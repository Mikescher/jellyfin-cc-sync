using System.Diagnostics;
using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Configuration;
using Jellyfin.Plugin.CcSync.State;
using Jellyfin.Plugin.CcSync.Watch;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CcSync.Sync;

public sealed record FlushResult(string Outcome, int Written, int Duplicate, int Failed, int Retrying, string Message);

/// <summary>Why the last flush could not start; kept so a long wait is recorded once, not every 5 minutes.</summary>
public sealed record FlushWait(string Reason, string Key, DateTime SinceUtc, DateTime LastCheckUtc);

/// <summary>
/// Writes pending updates into jClipCorn (<c>docs/sync-semantics.md</c> § Writing). Safe to call
/// concurrently and often: it returns at once when nothing is pending and waits for the gate.
/// </summary>
public sealed class FlushEngine : IDisposable
{
    public const string WaitKey = "flush-wait";

    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(15);

    private readonly PendingStore _pending;
    private readonly HistoryStore _history;
    private readonly RunStore _runs;
    private readonly SyncGate _gate;
    private readonly PluginPaths _paths;
    private readonly ILogger<FlushEngine> _logger;
    private readonly Lock _timerLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;

    public FlushEngine(PendingStore pending, HistoryStore history, RunStore runs, SyncGate gate, PluginPaths paths, ILogger<FlushEngine> logger)
    {
        _pending = pending;
        _history = history;
        _runs = runs;
        _gate = gate;
        _paths = paths;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Schedules a flush in 15 s; further requests within that time push it back.</summary>
    public void RequestSoon()
    {
        lock (_timerLock)
        {
            _timer ??= new Timer(_ => _ = RunInBackgroundAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public Task<FlushResult> RunAsync(CancellationToken cancellationToken) => RunAsync(Config, cancellationToken);

    public async Task<FlushResult> RunAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (_pending.Count(PendingStatus.Pending) == 0)
        {
            return new FlushResult(RunOutcomes.NothingToDo, 0, 0, 0, 0, "nothing pending");
        }

        using var gate = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);

        var pending = _pending.GetPending();
        if (pending.Count == 0)
        {
            return new FlushResult(RunOutcomes.NothingToDo, 0, 0, 0, 0, "nothing pending");
        }

        var now = DateTime.UtcNow;
        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, config.LockStaleMinutes));

        if (!ConfigValues.TryResolveTimeZone(config.ClipCornTimeZone, out var zone, out var zoneError))
        {
            return Wait(zoneError!, "config:timezone", HistoryKinds.RunFlush, now);
        }

        if (string.IsNullOrWhiteSpace(config.ClipCornDirectory) || !File.Exists(Path.Combine(config.ClipCornDirectory, CcFiles.UserDbName)))
        {
            return Wait($"ClipCornUserData.db not found in \"{config.ClipCornDirectory}\"", "config:directory", HistoryKinds.RunFlush, now);
        }

        var files = new CcFiles(config.ClipCornDirectory);
        var access = CcLockProbe.ProbeDirectory(files, staleAfter, now);
        if (access.Blocked)
        {
            return Wait(access.Describe(), "lock:" + LockKey(access), HistoryKinds.CcLocked, now);
        }

        _runs.RemoveValue(WaitKey);
        var run = _runs.Start(RunKinds.Flush, now);
        var stopwatch = Stopwatch.StartNew();
        var log = new List<HistoryEntry>();
        var summary = new FlushSummary { Pending = pending.Count };

        try
        {
            var result = await FlushAsync(config, zone, files, staleAfter, pending, run.Id, log, summary, cancellationToken).ConfigureAwait(false);
            summary.DurationMs = stopwatch.ElapsedMilliseconds;
            log.Add(RunEntry(run.Id, result, summary));
            _history.AddRange(log);
            _runs.Finish(run.Id, result.Outcome, summary, DateTime.UtcNow);
            _logger.LogInformation("Flush {Run}: {Outcome}, {Message}", run.Id, result.Outcome, result.Message);
            return result;
        }
        catch (Exception ex)
        {
            summary.AbortReason = ex.Message;
            summary.DurationMs = stopwatch.ElapsedMilliseconds;
            var result = new FlushResult(RunOutcomes.Aborted, 0, 0, 0, pending.Count, ex.Message);
            log.Add(RunEntry(run.Id, result, summary));
            _history.AddRange(log);
            _runs.Finish(run.Id, RunOutcomes.Aborted, summary, DateTime.UtcNow);
            _logger.LogError(ex, "Flush {Run} aborted", run.Id);
            return result;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_timerLock)
        {
            _timer?.Dispose();
        }

        _cts.Dispose();
    }

    private static string LockKey(CcAccessState access)
    {
        var holder = access.MainLock.Held ? access.MainLock : access.HistoryLock.Held ? access.HistoryLock : null;
        return holder is null
            ? "journal"
            : $"{holder.Host}|{holder.Pid}|{holder.AcquiredUtc:O}";
    }

    private async Task<FlushResult> FlushAsync(
        PluginConfiguration config,
        TimeZoneInfo zone,
        CcFiles files,
        TimeSpan staleAfter,
        IReadOnlyList<PendingUpdate> pending,
        string runId,
        List<HistoryEntry> log,
        FlushSummary summary,
        CancellationToken cancellationToken)
    {
        CcLibrary library;
        try
        {
            using var snapshot = await CcSnapshot.TakeAsync(files, _paths.SnapshotDirectory, true, cancellationToken).ConfigureAwait(false);
            library = CcReader.Read(snapshot.MainDb, snapshot.UserDb!, cancellationToken);
        }
        catch (Exception ex) when (ex is CcSnapshotException or CcSchemaException or CcReadException or IOException or UnauthorizedAccessException)
        {
            return Retry(pending, "reading jClipCorn failed: " + ex.Message, runId, log, summary, RunOutcomes.Aborted);
        }

        var lookup = new CcLookup(library);
        var requests = new List<CcWriteRequest>();
        var byId = pending.ToDictionary(p => p.Id);

        foreach (var update in pending)
        {
            var resolved = Resolve(update, library, lookup, zone, out var problem);
            if (resolved is null)
            {
                Fail(update, problem!, runId, log, summary);
                continue;
            }

            if (!string.Equals(update.CcId, resolved.ElementId, StringComparison.OrdinalIgnoreCase))
            {
                _pending.SetCcId(update.Id, resolved.ElementId);
                byId[update.Id] = update with { CcId = resolved.ElementId };
            }

            requests.Add(resolved);
        }

        if (requests.Count == 0)
        {
            return Done(summary);
        }

        string backup;
        try
        {
            backup = await CcBackups.CreateAsync(files, _paths.BackupDirectory, config.BackupCount, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            summary.Backup = Path.GetFileName(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Retry(Select(requests, byId), "backup failed: " + ex.Message, runId, log, summary, RunOutcomes.Aborted);
        }

        var before = CcLockProbe.ProbeDirectory(files, staleAfter, DateTime.UtcNow);
        if (before.Blocked)
        {
            return Retry(Select(requests, byId), "jClipCorn became busy before the write: " + before.Describe(), runId, log, summary, RunOutcomes.Waiting);
        }

        IReadOnlyList<CcWriteOutcome> outcomes;
        try
        {
            outcomes = CcWriter.Apply(
                files.UserDb,
                library.Info.MainDuuid,
                requests,
                config.ResetWatchLaterOnWatch,
                () => CcLockProbe.ProbeDirectory(files, staleAfter, DateTime.UtcNow, ownTransactionOn: files.UserDb) is { Blocked: true } a
                    ? "a lock appeared before the commit: " + a.Describe()
                    : null);
        }
        catch (CcWriteAbortedException ex)
        {
            return Retry(Select(requests, byId), ex.Message, runId, log, summary, RunOutcomes.Aborted);
        }

        var after = CcLockProbe.ProbeDirectory(files, staleAfter, DateTime.UtcNow);
        if (after.Blocked)
        {
            _logger.LogWarning("A jClipCorn lock appeared during the write: {State}", after.Describe());
            log.Add(new HistoryEntry
            {
                TsUtc = DateTime.UtcNow,
                RunId = runId,
                Kind = HistoryKinds.CcLocked,
                Detail = "appeared during the write (it was committed before): " + after.Describe(),
                Success = false,
            });
        }

        foreach (var outcome in outcomes)
        {
            Record(byId[outcome.PendingId], outcome, runId, log, summary);
        }

        return Done(summary);
    }

    private CcWriteRequest? Resolve(PendingUpdate update, CcLibrary library, CcLookup lookup, TimeZoneInfo zone, out string? problem)
    {
        problem = null;
        var kind = update.ItemKind switch
        {
            "Movie" => CcKind.Movie,
            "Episode" => CcKind.Episode,
            _ => (CcKind?)null,
        };

        if (kind is null)
        {
            problem = $"a {update.ItemKind} has no viewed history in jClipCorn";
            return null;
        }

        var id = update.CcId;
        if (id is null)
        {
            if (kind == CcKind.Episode)
            {
                problem = "the episode has no clipcorn id (episodes are matched by id only; is jClipCorn's NFO export on?)";
                return null;
            }

            var keys = PendingMatchKeys.Parse(update.MatchKeys);
            var found = lookup.FindMovie(
                keys?.ProviderIds.GetValueOrDefault("Tmdb"),
                keys?.ProviderIds.GetValueOrDefault("Imdb"),
                keys?.Name,
                keys?.Year);
            if (found.Match is null)
            {
                problem = "the movie has no clipcorn id and no unique fallback: " + found.Problem;
                return null;
            }

            id = found.Match.Id;
        }

        switch (library.Find(id))
        {
            case null:
                problem = $"jClipCorn has no element {id}";
                return null;
            case var element when element.Kind != kind:
                problem = $"clipcorn id {id} is a {element.Kind} in jClipCorn, not a {kind}";
                return null;
            case CcEpisode episode:
                return new CcWriteRequest(update.Id, episode.Id, CcKind.Episode, CcDateTime.FromUtc(update.WatchedAtUtc, zone), library.SeriesOf(episode)?.Id);
            case var element:
                return new CcWriteRequest(update.Id, element.Id, CcKind.Movie, CcDateTime.FromUtc(update.WatchedAtUtc, zone), null);
        }
    }

    private void Record(PendingUpdate update, CcWriteOutcome outcome, string runId, List<HistoryEntry> log, FlushSummary summary)
    {
        if (outcome.Result == CcWriteResult.Failed)
        {
            Fail(update, outcome.Note ?? "write failed", runId, log, summary);
            return;
        }

        var status = outcome.Result == CcWriteResult.Written ? PendingStatus.Written : PendingStatus.Duplicate;
        _pending.Finish(update.Id, status, DateTime.UtcNow, outcome.Entry, null);

        if (outcome.Result == CcWriteResult.Written)
        {
            summary.Written++;
        }
        else
        {
            summary.Duplicate++;
        }

        var details = new List<string> { $"#{update.Id}" };
        if (outcome.Result == CcWriteResult.Duplicate)
        {
            details.Add("an equal entry was already there");
        }

        if (outcome.WatchLaterRemoved)
        {
            details.Add("\"Watch later\" removed");
        }

        if (outcome.SeriesWatchLaterRemoved)
        {
            details.Add("\"Watch later\" removed from the series");
        }

        if (outcome.Note is not null)
        {
            details.Add(outcome.Note);
        }

        log.Add(Entry(update, runId, outcome.Result == CcWriteResult.Written ? HistoryKinds.PendingWritten : HistoryKinds.PendingDuplicate, string.Join(", ", details), true, outcome.Entry));
    }

    private void Fail(PendingUpdate update, string error, string runId, List<HistoryEntry> log, FlushSummary summary)
    {
        _pending.Finish(update.Id, PendingStatus.Failed, DateTime.UtcNow, null, error);
        summary.Failed++;
        log.Add(Entry(update, runId, HistoryKinds.PendingFailed, $"#{update.Id}: {error}", false, null));
    }

    /// <summary>Transient problem: every affected update stays pending; an error is recorded once per update until it changes.</summary>
    private FlushResult Retry(IEnumerable<PendingUpdate> updates, string error, string runId, List<HistoryEntry> log, FlushSummary summary, string outcome)
    {
        foreach (var update in updates)
        {
            summary.Retrying++;
            if (_pending.RecordAttempt(update.Id, DateTime.UtcNow, error) != error)
            {
                log.Add(Entry(update, runId, HistoryKinds.PendingFailed, $"#{update.Id} stays pending: {error}", false, null));
            }
        }

        summary.AbortReason = error;
        return new FlushResult(outcome, summary.Written, summary.Duplicate, summary.Failed, summary.Retrying, error);
    }

    private static FlushResult Done(FlushSummary summary)
        => new(RunOutcomes.Applied, summary.Written, summary.Duplicate, summary.Failed, summary.Retrying,
            $"{summary.Written} written, {summary.Duplicate} duplicate, {summary.Failed} failed");

    private FlushResult Wait(string reason, string key, string historyKind, DateTime nowUtc)
    {
        var previous = _runs.GetJson<FlushWait>(WaitKey);
        var same = previous is not null && previous.Key == key;
        _runs.SetJson(WaitKey, new FlushWait(reason, key, same ? previous!.SinceUtc : nowUtc, nowUtc));

        if (!same)
        {
            _history.Add(new HistoryEntry { TsUtc = nowUtc, Kind = historyKind, Detail = "writes wait: " + reason, Success = false });
            _logger.LogInformation("Pending jClipCorn writes wait: {Reason}", reason);
        }

        return new FlushResult(RunOutcomes.Waiting, 0, 0, 0, 0, reason);
    }

    private static IEnumerable<PendingUpdate> Select(IEnumerable<CcWriteRequest> requests, Dictionary<long, PendingUpdate> byId)
        => requests.Select(r => byId[r.PendingId]);

    private static HistoryEntry Entry(PendingUpdate update, string runId, string kind, string detail, bool success, string? entry) => new()
    {
        TsUtc = DateTime.UtcNow,
        RunId = runId,
        Kind = kind,
        JfItemId = update.JfItemId.ToString("N"),
        CcId = update.CcId,
        ItemName = update.ItemName,
        ItemKind = update.ItemKind,
        Field = entry is null ? null : "VIEWED_HISTORY",
        NewValue = entry,
        Detail = detail,
        Success = success,
    };

    private static HistoryEntry RunEntry(string runId, FlushResult result, FlushSummary summary) => new()
    {
        TsUtc = DateTime.UtcNow,
        RunId = runId,
        Kind = HistoryKinds.RunFlush,
        Detail = $"{result.Outcome}: {result.Message} ({summary.Pending} pending, {summary.DurationMs} ms"
            + (summary.Backup is null ? ")" : $", backup {summary.Backup})"),
        Success = result.Outcome is RunOutcomes.Applied or RunOutcomes.NothingToDo,
    };

    private async Task RunInBackgroundAsync()
    {
        try
        {
            await RunAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Debounced jClipCorn write failed");
        }
    }

    private sealed class FlushSummary
    {
        public int Pending { get; set; }

        public int Written { get; set; }

        public int Duplicate { get; set; }

        public int Failed { get; set; }

        public int Retrying { get; set; }

        public string? Backup { get; set; }

        public long DurationMs { get; set; }

        public string? AbortReason { get; set; }
    }
}
