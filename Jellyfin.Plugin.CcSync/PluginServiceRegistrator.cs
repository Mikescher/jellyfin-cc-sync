using Jellyfin.Plugin.CcSync.Api;
using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.Jellyfin;
using Jellyfin.Plugin.CcSync.State;
using Jellyfin.Plugin.CcSync.Sync;
using Jellyfin.Plugin.CcSync.Watch;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CcSync;

/// <summary>
/// Registers the plugin's services with the server's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PluginPaths>();
        serviceCollection.AddSingleton<CcProbe>();
        serviceCollection.AddSingleton<StateDb>();
        serviceCollection.AddSingleton<PendingStore>();
        serviceCollection.AddSingleton<HistoryStore>();
        serviceCollection.AddSingleton<RunStore>();
        serviceCollection.AddSingleton<SessionTracker>();
        serviceCollection.AddSingleton<SyncGate>();
        serviceCollection.AddSingleton<FlushEngine>();
        serviceCollection.AddSingleton<JfIndex>();
        serviceCollection.AddSingleton<JfApplier>();
        serviceCollection.AddSingleton<SyncEngine>();
        serviceCollection.AddSingleton<StatusService>();
        serviceCollection.AddHostedService<WatchEntryPoint>();
    }
}
