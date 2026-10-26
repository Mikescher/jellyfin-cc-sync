using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.CcSync;

/// <summary>
/// The plugin's own files. Deliberately not <c>Plugin.DataFolderPath</c>: that lives in the
/// versioned plugin folder and is discarded on every update.
/// </summary>
public sealed class PluginPaths
{
    public PluginPaths(IApplicationPaths applicationPaths)
        : this(Path.Combine(applicationPaths.DataPath, Plugin.PluginName))
    {
    }

    internal PluginPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public string StateDb => Path.Combine(DataDirectory, "state.db");

    public string SnapshotDirectory => Path.Combine(DataDirectory, "snapshots");

    public string BackupDirectory => Path.Combine(DataDirectory, "backups");
}
