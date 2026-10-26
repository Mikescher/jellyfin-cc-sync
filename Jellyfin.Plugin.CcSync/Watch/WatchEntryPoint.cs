using Jellyfin.Plugin.CcSync.Configuration;
using Jellyfin.Plugin.CcSync.Jellyfin;
using Jellyfin.Plugin.CcSync.State;
using Jellyfin.Plugin.CcSync.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CcSync.Watch;

/// <summary>
/// Turns watches of the sync user into pending updates (rules: <c>docs/sync-semantics.md</c> §
/// Watches). Never waits for the sync gate.
/// </summary>
public sealed class WatchEntryPoint : IHostedService, IDisposable
{
    /// <summary>
    /// The server saves user data before it raises PlaybackStopped, so the user-data fallback waits
    /// this long for the richer stop event to claim the watch first.
    /// </summary>
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CollectInterval = TimeSpan.FromMinutes(30);

    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly SessionTracker _tracker;
    private readonly PendingStore _pending;
    private readonly HistoryStore _history;
    private readonly RunStore _runs;
    private readonly FlushEngine _flush;
    private readonly ILogger<WatchEntryPoint> _logger;
    private readonly CancellationTokenSource _cts = new();

    private Task? _collector;

    public WatchEntryPoint(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        SessionTracker tracker,
        PendingStore pending,
        HistoryStore history,
        RunStore runs,
        FlushEngine flush,
        ILogger<WatchEntryPoint> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _tracker = tracker;
        _pending = pending;
        _history = history;
        _runs = runs;
        _flush = flush;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var aborted = _runs.AbortDangling(DateTime.UtcNow);
            if (aborted > 0)
            {
                _logger.LogWarning("Marked {Count} run(s) cut off by the last server stop as aborted", aborted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the plugin state database");
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _userDataManager.UserDataSaved += OnUserDataSaved;

        _collector = Task.Run(() => CollectLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _userDataManager.UserDataSaved -= OnUserDataSaved;

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_collector is not null)
        {
            try
            {
                await _collector.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    public void Dispose() => _cts.Dispose();

    private static bool IsWatchable(BaseItem? item) => item is Movie or Episode;

    private static Guid ResolveUserId(PlaybackProgressEventArgs e)
        => e.Users.FirstOrDefault()?.Id ?? e.Session?.UserId ?? Guid.Empty;

    private static bool IsSyncUser(PlaybackProgressEventArgs e, Guid syncUser)
        => e.Users.Any(u => u.Id == syncUser) || e.Session?.UserId == syncUser;

    private static string ResolveSessionId(PlaybackProgressEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Session?.Id))
        {
            return e.Session.Id;
        }

        return !string.IsNullOrEmpty(e.PlaySessionId) ? e.PlaySessionId : ResolveUserId(e).ToString("N");
    }

    private async Task CollectLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CollectInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            _tracker.Collect(TimeSpan.FromMinutes(Config.DedupMinutes));
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => RecordActivity(e, "start");

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) => RecordActivity(e, "progress");

    private void RecordActivity(PlaybackProgressEventArgs e, string what)
    {
        try
        {
            if (!IsWatchable(e.Item) || JfItems.SyncUserId(Config) is not { } syncUser || !IsSyncUser(e, syncUser))
            {
                return;
            }

            _tracker.Record(
                SessionTracker.SessionKey(ResolveSessionId(e), e.Item.Id),
                syncUser,
                e.Item.Id,
                DateTime.UtcNow,
                TimeSpan.FromMinutes(Config.StartCoalesceMinutes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback {What}", what);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var config = Config;
            if (!IsWatchable(e.Item) || JfItems.SyncUserId(config) is not { } syncUser || !IsSyncUser(e, syncUser))
            {
                return;
            }

            if (!WatchRules.IsWatched(e.PlayedToCompletion, e.PlaybackPositionTicks, e.Item.RunTimeTicks, config.WatchedThreshold))
            {
                _logger.LogDebug(
                    "Stop of {Item} at {Percent:P0} is below the watched threshold",
                    e.Item.Name,
                    WatchRules.Fraction(e.PlaybackPositionTicks, e.Item.RunTimeTicks));
                return;
            }

            var endUtc = DateTime.UtcNow;
            if (!_tracker.TryPopStart(SessionTracker.SessionKey(ResolveSessionId(e), e.Item.Id), out var startUtc)
                && !_tracker.TryPopStartForItem(syncUser, e.Item.Id, out startUtc))
            {
                startUtc = WatchRules.EstimateStart(endUtc, e.PlaybackPositionTicks);
            }

            Complete(config, e.Item, syncUser, startUtc, PendingSource.Playback);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback stop");
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            // Import is what the plugin's own writes (and NFO imports) use.
            if (e.SaveReason == UserDataSaveReason.Import || !IsWatchable(e.Item) || e.UserData?.Played != true)
            {
                return;
            }

            var config = Config;
            if (JfItems.SyncUserId(config) != e.UserId)
            {
                return;
            }

            switch (e.SaveReason)
            {
                case UserDataSaveReason.PlaybackFinished:
                    _ = Task.Run(() => RunFallbackAsync(e, _cts.Token), CancellationToken.None);
                    break;

                case UserDataSaveReason.TogglePlayed when config.ManualMarkCreatesPending:
                    Complete(config, e.Item, e.UserId, DateTime.UtcNow, PendingSource.Manual);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle user data save");
        }
    }

    /// <summary>
    /// For clients that report a natural end of file as a stop without position or completion flag.
    /// Runs late so a well-behaved stop event claims the watch first.
    /// </summary>
    private async Task RunFallbackAsync(UserDataSaveEventArgs e, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FallbackDelay, cancellationToken).ConfigureAwait(false);

            var config = Config;
            if (_tracker.IsClaimed(SessionTracker.DedupKey(e.UserId, e.Item.Id), TimeSpan.FromMinutes(config.DedupMinutes)))
            {
                return;
            }

            var endUtc = DateTime.UtcNow;
            if (!_tracker.TryPopStartForItem(e.UserId, e.Item.Id, out var startUtc))
            {
                startUtc = WatchRules.EstimateStart(endUtc, e.UserData?.PlaybackPositionTicks);
            }

            Complete(config, e.Item, e.UserId, startUtc, PendingSource.PlaybackFallback);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle the user-data watch fallback");
        }
    }

    private void Complete(PluginConfiguration config, BaseItem item, Guid userId, DateTime watchedAtUtc, string source)
    {
        if (!config.WriteBackEnabled)
        {
            return;
        }

        if (!JfItems.InLibraries(_libraryManager, item, JfItems.LibraryIds(config)))
        {
            _logger.LogDebug("Watch of {Item} ignored: not in the selected libraries", item.Name);
            return;
        }

        if (!_tracker.TryClaim(SessionTracker.DedupKey(userId, item.Id), TimeSpan.FromMinutes(config.DedupMinutes)))
        {
            _logger.LogDebug("Watch of {Item} ignored: already recorded within the dedup window", item.Name);
            return;
        }

        var now = DateTime.UtcNow;
        var name = JfItems.DisplayName(item);
        var kind = JfItems.Kind(item)!;
        var ccId = JfItems.ProviderId(item, JfItems.ClipCornProvider);
        var watchedAt = WatchRules.TruncateToSeconds(watchedAtUtc);

        var pending = _pending.Add(now, source, item.Id, name, kind, ccId, PendingMatchKeys.From(item).ToJson(), watchedAt);

        _history.Add(new HistoryEntry
        {
            TsUtc = now,
            Kind = HistoryKinds.PendingCreated,
            JfItemId = item.Id.ToString("N"),
            CcId = ccId,
            ItemName = name,
            ItemKind = kind,
            NewValue = StateDb.FormatTime(watchedAt),
            Detail = $"#{pending.Id}, source {source}, watched at {watchedAt:yyyy-MM-dd HH:mm:ss} UTC" + (ccId is null ? ", no clipcorn id" : string.Empty),
        });

        _logger.LogInformation("Watch of {Item} queued for jClipCorn as pending update #{Id} ({Source})", name, pending.Id, source);
        _flush.RequestSoon();
    }
}
