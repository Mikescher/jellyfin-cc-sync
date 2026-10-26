using Jellyfin.Plugin.CcSync.Sync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.CcSync.Tasks;

/// <summary>jClipCorn → Jellyfin; the trigger is editable under Dashboard → Scheduled Tasks.</summary>
public class SyncFromClipCornTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly SyncEngine _engine;

    public SyncFromClipCornTask(SyncEngine engine)
    {
        _engine = engine;
    }

    public string Name => "Sync jClipCorn → Jellyfin";

    public string Key => "CcSyncFromClipCorn";

    public string Description => "Sets the sync user's watched state, play count, last-played date, favourites and the jcc-* tags from jClipCorn.";

    public string Category => Plugin.PluginName;

    public bool IsHidden => false;

    public bool IsEnabled => true;

    // The plugin's own history records every run.
    public bool IsLogged => false;

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _engine.RunAsync(SyncMode.Normal, null, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks };
    }
}
