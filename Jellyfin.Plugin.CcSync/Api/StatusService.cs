using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.Configuration;
using Jellyfin.Plugin.CcSync.Jellyfin;
using Jellyfin.Plugin.CcSync.State;
using Jellyfin.Plugin.CcSync.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CcSync.Api;

/// <summary>Builds the Status page's view: configuration warnings, lock state, runs, match summary.</summary>
public sealed class StatusService
{
    private readonly RunStore _runs;
    private readonly PendingStore _pending;
    private readonly IUserManager _userManager;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<StatusService> _logger;

    public StatusService(RunStore runs, PendingStore pending, IUserManager userManager, IConfigurationManager configurationManager, ILogger<StatusService> logger)
    {
        _runs = runs;
        _pending = pending;
        _userManager = userManager;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    public StatusResponse Build(PluginConfiguration config)
    {
        var warnings = new List<ProbeCheck>();
        void Warn(string level, string name, string message) => warnings.Add(new ProbeCheck { Level = level, Name = name, Message = message });

        var syncUser = JfItems.SyncUserId(config);
        if (syncUser is null)
        {
            Warn("error", "Sync user", "No sync user is selected; nothing is synced and no watch is written back.");
        }
        else if (_userManager.GetUserById(syncUser.Value) is null)
        {
            Warn("error", "Sync user", "The selected sync user no longer exists.");
        }
        else if (NfoUserId() is { } nfoUser && Guid.TryParse(nfoUser, out var nfo) && nfo == syncUser)
        {
            Warn("warn", "NFO settings", "Dashboard → NFO Settings saves the sync user's watch data to NFO files, i.e. Jellyfin writes into jClipCorn's NFOs on every watch. Clear that setting.");
        }

        if (!ConfigValues.TryResolveTimeZone(config.ClipCornTimeZone, out _, out var zoneError))
        {
            Warn("error", "Time zone", zoneError!);
        }

        if (!ConfigValues.TryParseTimeOfDay(config.DateOnlyTimeOfDay, out _))
        {
            Warn("error", "Date-only time", $"\"{config.DateOnlyTimeOfDay}\" is not a time of day.");
        }

        var directoryExists = !string.IsNullOrWhiteSpace(config.ClipCornDirectory) && Directory.Exists(config.ClipCornDirectory);
        if (string.IsNullOrWhiteSpace(config.ClipCornDirectory))
        {
            Warn("error", "Directory", "No jClipCorn directory is configured.");
        }
        else if (!directoryExists)
        {
            Warn("error", "Directory", $"{config.ClipCornDirectory} does not exist inside the Jellyfin process.");
        }

        if (!config.SyncEnabled)
        {
            Warn("warn", "Sync", "jClipCorn → Jellyfin is switched off.");
        }

        if (config.DryRun)
        {
            Warn("warn", "Dry run", "Dry-run mode is on: syncs only store a preview.");
        }

        if (!config.WriteBackEnabled)
        {
            Warn("warn", "Write-back", "Watches are not queued for jClipCorn; the next sync reverts them in Jellyfin.");
        }

        var response = new StatusResponse
        {
            Warnings = warnings.OrderBy(w => w.Level == "error" ? 0 : 1).ToList(),
            Directory = config.ClipCornDirectory,
            DirectoryExists = directoryExists,
            LastSync = _runs.Last(RunKinds.Sync),
            LastFlush = _runs.Last(RunKinds.Flush),
            FlushWait = _runs.GetJson<FlushWait>(FlushEngine.WaitKey),
            PendingCounts = _pending.CountsByStatus(),
            DryRun = config.DryRun,
        };

        if (directoryExists)
        {
            var access = CcLockProbe.ProbeDirectory(new CcFiles(config.ClipCornDirectory), TimeSpan.FromMinutes(Math.Max(1, config.LockStaleMinutes)), DateTime.UtcNow);
            response.MainLock = access.MainLock;
            response.HistoryLock = access.HistoryLock;
            response.Journals = access.Journals.Select(Path.GetFileName).OfType<string>().ToList();
            response.Blocked = access.Blocked;
        }

        if (_runs.GetJson<MatchReport>(RunStore.MatchReportKey) is { } report)
        {
            response.Match = new MatchSummary
            {
                CreatedUtc = report.CreatedUtc,
                Database = report.Database,
                JfItems = report.JfItems,
                CcElements = report.CcElements,
                Matched = report.Matched,
                MatchedByKind = report.MatchedByKind,
                UnmatchedCc = report.UnmatchedCc.Total,
                UnmatchedJf = report.UnmatchedJf.Total,
                Ambiguous = report.Ambiguous.Total,
                KindMismatches = report.KindMismatches.Total,
                FallbackMatched = report.FallbackMatched.Total,
                MatchedSeveralTimes = report.MatchedSeveralTimes.Total,
                CcProblems = report.CcProblems.Total,
                TagWarnings = report.TagWarnings,
            };
        }

        if (_runs.GetJson<SyncPreview>(RunStore.PreviewPlanKey) is { } preview)
        {
            response.Preview = new PreviewSummary
            {
                PlanId = preview.PlanId,
                CreatedUtc = preview.CreatedUtc,
                Reason = preview.Reason,
                ItemsChanged = preview.ItemsChanged,
                Unwatches = preview.Unwatches,
            };
        }

        return response;
    }

    private string? NfoUserId()
    {
        try
        {
            return (_configurationManager.GetConfiguration("xbmcmetadata") as XbmcMetadataOptions)?.UserId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the NFO settings");
            return null;
        }
    }
}
