using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Configuration;
using Jellyfin.Plugin.CcSync.Jellyfin;
using Jellyfin.Plugin.CcSync.State;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CcSync.Sync;

public enum SyncMode
{
    /// <summary>Scheduled or "Run now": guards apply.</summary>
    Normal,

    /// <summary>Forced dry run: plan and store, apply nothing.</summary>
    Preview,

    /// <summary>Apply the stored preview once with the guard lifted; refused when it is stale.</summary>
    Apply,
}

public sealed record SyncRunResult(string Outcome, string Message, string? RunId, int ItemsChanged, int Unwatched);

/// <summary>
/// jClipCorn → Jellyfin (<c>PLAN.md</c> § 4.2). Reads only a snapshot; writes only the sync user's
/// data and the <c>jcc-*</c> tags of items in the selected libraries.
/// </summary>
public sealed class SyncEngine
{
    private static readonly JsonSerializerOptions HashOptions = new() { WriteIndented = false };

    private readonly SyncGate _gate;
    private readonly PendingStore _pending;
    private readonly HistoryStore _history;
    private readonly RunStore _runs;
    private readonly PluginPaths _paths;
    private readonly JfIndex _index;
    private readonly JfApplier _applier;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ActivityMirror _activity;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        SyncGate gate,
        PendingStore pending,
        HistoryStore history,
        RunStore runs,
        PluginPaths paths,
        JfIndex index,
        JfApplier applier,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ActivityMirror activity,
        ILogger<SyncEngine> logger)
    {
        _gate = gate;
        _pending = pending;
        _history = history;
        _runs = runs;
        _paths = paths;
        _index = index;
        _applier = applier;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _activity = activity;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The fingerprint of the jClipCorn files and the pending updates a plan was built from.</summary>
    public static string Fingerprint(CcFiles files, IEnumerable<PendingUpdate> pending)
    {
        static string Stamp(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{CcSqlite.ReadChangeCounter(path)}")
                : "missing";
        }

        var text = $"{Stamp(files.MainDb)}|{Stamp(files.UserDb)}|" + string.Join(',', pending.Select(p => p.Id.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    public async Task<SyncRunResult> RunAsync(SyncMode mode, string? planId, CancellationToken cancellationToken)
    {
        var config = Config;
        if (mode == SyncMode.Normal && !config.SyncEnabled)
        {
            return new SyncRunResult(RunOutcomes.NothingToDo, "sync is disabled in the settings", null, 0, 0);
        }

        using var gate = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);

        var run = _runs.Start(RunKinds.Sync, DateTime.UtcNow);
        var stopwatch = Stopwatch.StartNew();
        var summary = new SyncSummary { Mode = mode.ToString() };
        var log = new List<HistoryEntry>();

        SyncRunResult result;
        try
        {
            result = await RunInGateAsync(config, mode, planId, run.Id, summary, log, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new SyncRunResult(RunOutcomes.Aborted, "cancelled", run.Id, summary.ItemsChanged, summary.Unwatched);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync {Run} failed", run.Id);
            result = new SyncRunResult(RunOutcomes.Aborted, ex.Message, run.Id, summary.ItemsChanged, summary.Unwatched);
        }

        summary.DurationMs = stopwatch.ElapsedMilliseconds;
        if (result.Outcome == RunOutcomes.Aborted)
        {
            summary.AbortReason = result.Message;
        }

        log.Add(new HistoryEntry
        {
            TsUtc = DateTime.UtcNow,
            RunId = run.Id,
            Kind = HistoryKinds.RunSync,
            Detail = $"{result.Outcome} ({mode}): {result.Message} ({summary.DurationMs} ms)",
            Success = result.Outcome is not RunOutcomes.Aborted,
        });
        _history.AddRange(log);
        _runs.Finish(run.Id, result.Outcome, summary, DateTime.UtcNow);
        _logger.LogInformation("Sync {Run} ({Mode}): {Outcome}, {Message}", run.Id, mode, result.Outcome, result.Message);

        switch (result.Outcome)
        {
            case RunOutcomes.Aborted:
                _activity.Report("sync from jClipCorn aborted", result.Message, LogLevel.Error);
                break;
            case RunOutcomes.Guarded:
                _activity.Report("sync from jClipCorn waits for confirmation", result.Message + " — see the plugin's Status page.", LogLevel.Warning);
                break;
            case RunOutcomes.Applied when summary.Errors > 0:
                _activity.Report($"sync from jClipCorn: {summary.Errors} change(s) failed", result.Message, LogLevel.Warning);
                break;
        }

        return result with { RunId = run.Id };
    }

    private async Task<SyncRunResult> RunInGateAsync(
        PluginConfiguration config,
        SyncMode mode,
        string? planId,
        string runId,
        SyncSummary summary,
        List<HistoryEntry> log,
        CancellationToken cancellationToken)
    {
        SyncRunResult Abort(string reason) => new(RunOutcomes.Aborted, reason, runId, 0, 0);

        if (JfItems.SyncUserId(config) is not { } userId || _userManager.GetUserById(userId) is not { } user)
        {
            return Abort("no sync user is configured, or the user no longer exists");
        }

        if (!ConfigValues.TryResolveTimeZone(config.ClipCornTimeZone, out var zone, out var zoneError))
        {
            return Abort(zoneError!);
        }

        if (!ConfigValues.TryParseTimeOfDay(config.DateOnlyTimeOfDay, out var dateOnlyTime))
        {
            return Abort($"\"{config.DateOnlyTimeOfDay}\" is not a time of day (HH:mm)");
        }

        if (string.IsNullOrWhiteSpace(config.ClipCornDirectory))
        {
            return Abort("no jClipCorn directory is configured");
        }

        var files = new CcFiles(config.ClipCornDirectory);
        var fingerprint = Fingerprint(files, _pending.GetPending());

        CcLibrary library;
        try
        {
            using var snapshot = await CcSnapshot.TakeAsync(files, _paths.SnapshotDirectory, true, cancellationToken).ConfigureAwait(false);
            library = CcReader.Read(snapshot.MainDb, snapshot.UserDb!, cancellationToken);
        }
        catch (Exception ex) when (ex is CcSnapshotException or CcSchemaException or CcReadException or IOException or UnauthorizedAccessException)
        {
            return Abort("reading jClipCorn failed, Jellyfin left untouched: " + ex.Message);
        }

        if (library.Movies.Count == 0 && library.Episodes.Count == 0)
        {
            return Abort("the jClipCorn database has no movies and no episodes; refusing to sync against it");
        }

        // Taken inside the gate: no pending update can be written until this run is done.
        var t0 = DateTime.UtcNow;
        var pending = _pending.GetPending();

        var index = _index.Build(config);
        var match = Matcher.Match(library, index.Items);
        var report = MatchReport.From(library, match, index.Items.Count, DateTime.UtcNow);
        _runs.SetJson(RunStore.MatchReportKey, report);
        summary.Matched = match.Matched.Count;
        summary.UnmatchedJf = match.UnmatchedJf.Count + match.Ambiguous.Count + match.KindMismatches.Count;
        summary.Skipped = library.Problems.Count();

        var options = SyncOptions.From(config, zone, dateOnlyTime);
        var desired = DesiredStateBuilder.Build(
            library,
            match,
            index.Items,
            pending.Select(p => new PendingOverlay(p.JfItemId, p.CcId, p.WatchedAtUtc)).ToList(),
            options);

        var actual = _userDataManager.GetUserDataBatch(index.Instances, user)
            .ToDictionary(kv => kv.Key, kv => new JfUserData(kv.Value.Played, kv.Value.PlayCount, kv.Value.LastPlayedDate, kv.Value.IsFavorite));

        var plan = SyncPlanner.Plan(desired, actual, config.LockTagsField);
        summary.Planned = plan.Changes.Count;

        if (plan.Changes.Count == 0)
        {
            _runs.RemoveValue(RunStore.PreviewPlanKey);
            return new SyncRunResult(RunOutcomes.NothingToDo, $"{match.Matched.Count} items matched, everything in line", runId, 0, 0);
        }

        if (mode == SyncMode.Apply)
        {
            var stored = _runs.GetJson<SyncPreview>(RunStore.PreviewPlanKey);
            if (stored is null || stored.PlanId != planId)
            {
                return Abort("the preview to apply is gone or was replaced; open the Status page again");
            }

            if (stored.Fingerprint != fingerprint)
            {
                return Abort("jClipCorn or the pending updates changed since the preview; run a new preview");
            }
        }
        else if (GuardReason(config, mode, plan) is { } reason)
        {
            StorePreview(plan, reason, fingerprint, runId, log);
            summary.Guard = reason;
            summary.ItemsChanged = 0;
            var outcome = mode == SyncMode.Preview || config.DryRun ? RunOutcomes.DryRun : RunOutcomes.Guarded;
            return new SyncRunResult(outcome, $"{plan.Changes.Count} item(s) would change, {plan.Unwatches} un-watched: {reason}", runId, 0, 0);
        }

        foreach (var change in plan.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A watch that arrived after the pending snapshot is not in this plan's overlay.
            var applyUserData = !(change.HasUserDataChange && _pending.HasPendingCreatedAfter(change.ItemId, t0));
            if (!applyUserData)
            {
                summary.SkippedNewWatch++;
                if (!change.HasTagChange)
                {
                    continue;
                }
            }

            try
            {
                var applied = await _applier.ApplyAsync(change, user, applyUserData, cancellationToken).ConfigureAwait(false);
                if (applied.Problem is not null)
                {
                    summary.Errors++;
                    log.Add(Entry(change, runId, HistoryKinds.JfPlayed, null, null, null, applied.Problem, false));
                    continue;
                }

                summary.ItemsChanged++;
                log.AddRange(Entries(change, runId, string.Empty, applied.UserDataApplied, applied.TagsApplied));
                if (applied.UserDataApplied && change.Unwatches)
                {
                    summary.Unwatched++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summary.Errors++;
                _logger.LogError(ex, "Applying the change to {Item} failed", change.Name);
                log.Add(Entry(change, runId, change.HasUserDataChange ? HistoryKinds.JfPlayed : HistoryKinds.JfTagAdded, null, null, null, "failed: " + ex.Message, false));
            }
        }

        _runs.RemoveValue(RunStore.PreviewPlanKey);
        return new SyncRunResult(
            RunOutcomes.Applied,
            $"{summary.ItemsChanged} item(s) changed, {summary.Unwatched} un-watched, {summary.Errors} error(s), {summary.SkippedNewWatch} skipped for a new watch",
            runId,
            summary.ItemsChanged,
            summary.Unwatched);
    }

    private string? GuardReason(PluginConfiguration config, SyncMode mode, SyncPlan plan)
    {
        if (mode == SyncMode.Preview)
        {
            return "preview requested";
        }

        if (config.DryRun)
        {
            return "dry-run mode is on";
        }

        if (!_runs.Recent(RunKinds.Sync, 200).Any(r => r.Outcome == RunOutcomes.Applied))
        {
            return "first sync: confirm the preview";
        }

        if (plan.Changes.Count > config.MaxChangesPerRun)
        {
            return $"more than {config.MaxChangesPerRun} items would change";
        }

        if (plan.Unwatches > config.MaxUnwatchPerRun)
        {
            return $"more than {config.MaxUnwatchPerRun} items would be un-watched";
        }

        return null;
    }

    private void StorePreview(SyncPlan plan, string reason, string fingerprint, string runId, List<HistoryEntry> log)
    {
        var planHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan.Changes, HashOptions)))[..16];
        var previous = _runs.GetJson<SyncPreview>(RunStore.PreviewPlanKey);

        var preview = new SyncPreview(
            Guid.NewGuid().ToString("N")[..12],
            DateTime.UtcNow,
            reason,
            fingerprint,
            planHash,
            plan.Changes.Count,
            plan.Unwatches,
            plan.Changes);
        _runs.SetJson(RunStore.PreviewPlanKey, preview);

        // The same plan again (dry-run mode every 30 minutes) is not recorded again.
        if (previous?.PlanHash != planHash)
        {
            foreach (var change in plan.Changes)
            {
                log.AddRange(Entries(change, runId, HistoryKinds.PlannedPrefix, true, true));
            }
        }
    }

    private static IEnumerable<HistoryEntry> Entries(PlannedChange change, string runId, string prefix, bool userData, bool tags)
    {
        if (userData && change.HasPlayedChange)
        {
            var fields = new List<string>();
            var oldValues = new List<string>();
            var newValues = new List<string>();
            if (change.Played is { } p)
            {
                fields.Add("Played");
                oldValues.Add(p.Old ? "played" : "unplayed");
                newValues.Add(p.New ? "played" : "unplayed");
            }

            if (change.PlayCount is { } c)
            {
                fields.Add("PlayCount");
                oldValues.Add(c.Old.ToString(CultureInfo.InvariantCulture) + "×");
                newValues.Add(c.New.ToString(CultureInfo.InvariantCulture) + "×");
            }

            if (change.LastPlayed is { } l)
            {
                fields.Add("LastPlayedDate");
                oldValues.Add(FormatDate(l.Old));
                newValues.Add(FormatDate(l.New));
            }

            yield return Entry(change, runId, prefix + HistoryKinds.JfPlayed, string.Join(", ", fields), string.Join(", ", oldValues), string.Join(", ", newValues), null, true);
        }

        if (userData && change.Favorite is { } f)
        {
            yield return Entry(change, runId, prefix + HistoryKinds.JfFavorite, "IsFavorite", f.Old ? "true" : "false", f.New ? "true" : "false", null, true);
        }

        if (!tags)
        {
            yield break;
        }

        foreach (var tag in change.AddTags)
        {
            yield return Entry(change, runId, prefix + HistoryKinds.JfTagAdded, "Tags", null, tag, null, true);
        }

        foreach (var tag in change.RemoveTags)
        {
            yield return Entry(change, runId, prefix + HistoryKinds.JfTagRemoved, "Tags", tag, null, null, true);
        }

        if (change.LockTags)
        {
            yield return Entry(change, runId, prefix + HistoryKinds.JfTagAdded, "LockedFields", null, "Tags", "tags field locked", true);
        }
    }

    private static HistoryEntry Entry(PlannedChange change, string runId, string kind, string? field, string? oldValue, string? newValue, string? detail, bool success) => new()
    {
        TsUtc = DateTime.UtcNow,
        RunId = runId,
        Kind = kind,
        JfItemId = change.ItemId.ToString("N"),
        CcId = change.CcId,
        ItemName = change.Name,
        ItemKind = change.Kind.ToString(),
        Field = field,
        OldValue = oldValue,
        NewValue = newValue,
        Detail = detail,
        Success = success,
    };

    private static string FormatDate(DateTime? value)
        => value is null ? "∅" : value.Value.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private sealed class SyncSummary
    {
        public string Mode { get; set; } = string.Empty;

        public int Matched { get; set; }

        public int UnmatchedJf { get; set; }

        public int Skipped { get; set; }

        public int Planned { get; set; }

        public int ItemsChanged { get; set; }

        public int Unwatched { get; set; }

        public int SkippedNewWatch { get; set; }

        public int Errors { get; set; }

        public string? Guard { get; set; }

        public long DurationMs { get; set; }

        public string? AbortReason { get; set; }
    }
}
