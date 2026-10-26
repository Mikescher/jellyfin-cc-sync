using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CcSync.State;

/// <summary>
/// Copies failures, aborts and guard stops into Jellyfin's activity log when
/// <c>WriteToActivityLog</c> is on; routine runs stay in the plugin's own history.
/// </summary>
public sealed class ActivityMirror
{
    private readonly IActivityManager _activityManager;
    private readonly ILogger<ActivityMirror> _logger;

    public ActivityMirror(IActivityManager activityManager, ILogger<ActivityMirror> logger)
    {
        _activityManager = activityManager;
        _logger = logger;
    }

    public void Report(string title, string overview, LogLevel severity)
    {
        if (Plugin.Instance?.Configuration.WriteToActivityLog != true)
        {
            return;
        }

        var entry = new ActivityLog(Plugin.PluginName + ": " + title, "CcSync", Guid.Empty)
        {
            ShortOverview = overview.Length > 500 ? overview[..500] : overview,
            LogSeverity = severity,
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await _activityManager.CreateAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write an activity log entry");
            }
        });
    }
}
