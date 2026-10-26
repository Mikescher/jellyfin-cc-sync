using Jellyfin.Plugin.CcSync.Sync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.CcSync.Tasks;

/// <summary>Retries pending writes; the trigger is editable under Dashboard → Scheduled Tasks.</summary>
public class FlushPendingTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly FlushEngine _engine;

    public FlushPendingTask(FlushEngine engine)
    {
        _engine = engine;
    }

    public string Name => "Write pending watches to jClipCorn";

    public string Key => "CcSyncFlush";

    public string Description => "Writes Jellyfin watches of the sync user into jClipCorn's viewed history while jClipCorn is not running.";

    public string Category => Plugin.PluginName;

    public bool IsHidden => false;

    public bool IsEnabled => true;

    // Runs every 5 minutes; the plugin's own history records what it did.
    public bool IsLogged => false;

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _engine.RunAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(5).Ticks };
    }
}
