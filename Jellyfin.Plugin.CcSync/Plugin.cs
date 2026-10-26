using Jellyfin.Plugin.CcSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CcSync;

/// <summary>
/// Syncs one Jellyfin user's watch state, favourites and tags with a jClipCorn database.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginName = "jellyfin-cc-sync";

    public const string PluginGuid = "7c59bc79-ad24-4106-94b9-d13477e9a518";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded instance, for components the server does not inject into.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description =>
        "Syncs watch state, favourites and jcc-* tags with a jClipCorn database and writes Jellyfin watches back.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => Pages();

    internal static IEnumerable<PluginPageInfo> Pages()
    {
        var prefix = typeof(Plugin).Namespace + ".Configuration.";

        // The dashboard drawer keys its entries by plugin id, so only one page may be in the
        // menu; the others are reached through the tab strip the pages render.
        yield return new PluginPageInfo
        {
            Name = "CcSync",
            DisplayName = PluginName,
            EmbeddedResourcePath = prefix + "settingsPage.html",
            EnableInMainMenu = true,
            MenuIcon = "sync",
        };

        yield return Page("CcSyncStatus", prefix + "statusPage.html");
        yield return Page("CcSyncPending", prefix + "pendingPage.html");
        yield return Page("CcSyncHistory", prefix + "historyPage.html");

        yield return Page("CcSync.js", prefix + "settingsPage.js");
        yield return Page("CcSyncStatus.js", prefix + "statusPage.js");
        yield return Page("CcSyncPending.js", prefix + "pendingPage.js");
        yield return Page("CcSyncHistory.js", prefix + "historyPage.js");
        yield return Page("CcSyncCommon.js", prefix + "common.js");
    }

    private static PluginPageInfo Page(string name, string resource)
        => new() { Name = name, EmbeddedResourcePath = resource };
}
