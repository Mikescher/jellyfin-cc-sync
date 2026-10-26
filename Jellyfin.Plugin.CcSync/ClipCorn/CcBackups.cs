using System.Globalization;

namespace Jellyfin.Plugin.CcSync.ClipCorn;

/// <summary>
/// Rotating copies of <c>ClipCornUserData.db</c>, taken before every write session.
/// </summary>
public static class CcBackups
{
    private const string Prefix = "ClipCornUserData.";
    private const string Suffix = ".db";

    /// <summary>
    /// Copies the live user DB and keeps the newest <paramref name="keep"/> backups. Only called
    /// while no jClipCorn lock is held, so the file must not change during the copy; if it does,
    /// the backup is discarded and the write session must not start.
    /// </summary>
    public static async Task<string> CreateAsync(CcFiles files, string backupDirectory, int keep, DateTime nowUtc, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        var target = Path.Combine(backupDirectory, Prefix + nowUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + Suffix);

        var before = CcSqlite.ReadChangeCounter(files.UserDb);
        var input = new FileStream(files.UserDb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        await using (input.ConfigureAwait(false))
        {
            var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using (output.ConfigureAwait(false))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }

        if (before is null || CcSqlite.ReadChangeCounter(files.UserDb) != before || CcSqlite.ReadChangeCounter(target) != before)
        {
            File.Delete(target);
            throw new IOException("ClipCornUserData.db changed while it was backed up.");
        }

        Prune(backupDirectory, Math.Max(1, keep));
        return target;
    }

    public static IReadOnlyList<string> List(string backupDirectory)
        => Directory.Exists(backupDirectory)
            ? Directory.EnumerateFiles(backupDirectory, Prefix + "*" + Suffix).Order(StringComparer.Ordinal).ToList()
            : [];

    private static void Prune(string backupDirectory, int keep)
    {
        var all = List(backupDirectory);
        foreach (var old in all.Take(Math.Max(0, all.Count - keep)))
        {
            File.Delete(old);
        }
    }
}
