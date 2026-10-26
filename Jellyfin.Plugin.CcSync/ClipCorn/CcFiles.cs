namespace Jellyfin.Plugin.CcSync.ClipCorn;

/// <summary>
/// File names below the configured <c>ClipCornDB</c> directory.
/// </summary>
public sealed class CcFiles
{
    public const string MainDbName = "ClipCornDB.db";
    public const string UserDbName = "ClipCornUserData.db";
    public const string HistoryDbName = "ClipCornHistory.db";
    public const string LockSuffix = ".~lock";
    public const string ProbeFileName = ".jellyfin-cc-sync-probe";

    public CcFiles(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public string MainDb => Path.Combine(Directory, MainDbName);

    public string UserDb => Path.Combine(Directory, UserDbName);

    public string HistoryDb => Path.Combine(Directory, HistoryDbName);

    public string MainLock => MainDb + LockSuffix;

    public string HistoryLock => HistoryDb + LockSuffix;

    /// <summary>
    /// Rollback journals (and, should one ever appear, WAL files) of all three databases. Their
    /// presence means a transaction is running or was interrupted.
    /// </summary>
    public IEnumerable<string> Journals =>
        new[] { MainDb, UserDb, HistoryDb }.SelectMany(db => new[] { db + "-journal", db + "-wal" });

    public IReadOnlyList<string> ExistingJournals() => Journals.Where(File.Exists).ToList();
}
