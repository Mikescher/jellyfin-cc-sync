using System.Globalization;

namespace Jellyfin.Plugin.CcSync.ClipCorn;

public sealed class CcSnapshotException(string message) : Exception(message);

/// <summary>
/// A private local copy of the jClipCorn files, taken without any SQLite lock on the originals so a
/// running jClipCorn never sees <c>SQLITE_BUSY</c> from us. Deleted on dispose.
/// </summary>
public sealed class CcSnapshot : IDisposable
{
    private const int Attempts = 3;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(6);

    private CcSnapshot(string directory, string mainDb, string? userDb, string sourceStamp)
    {
        Directory = directory;
        MainDb = mainDb;
        UserDb = userDb;
        SourceStamp = sourceStamp;
    }

    public string Directory { get; }

    public string MainDb { get; }

    public string? UserDb { get; }

    /// <summary>
    /// Gets size, mtime and header change counter of every source as verified for this copy; equal
    /// stamps mean equal content as far as SQLite's commit counter can tell.
    /// </summary>
    public string SourceStamp { get; }

    /// <summary>
    /// Copies the main DB (and the user DB) into a new folder below <paramref name="snapshotRoot"/>.
    /// A copy counts only if no journal existed and every source kept its size, mtime and header
    /// change counter while it was copied; otherwise it is retried and finally refused.
    /// </summary>
    public static async Task<CcSnapshot> TakeAsync(
        CcFiles files,
        string snapshotRoot,
        bool includeUserData,
        CancellationToken cancellationToken,
        TimeSpan? retryDelay = null)
    {
        RemoveLeftovers(snapshotRoot);

        var directory = Path.Combine(
            snapshotRoot,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(directory);

        var sources = includeUserData ? new[] { files.MainDb, files.UserDb } : new[] { files.MainDb };
        string? lastProblem = null;

        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (attempt > 1)
                {
                    await Task.Delay(retryDelay ?? DefaultRetryDelay, cancellationToken).ConfigureAwait(false);
                }

                var (problem, stamps) = await TryCopyAsync(files, sources, directory, cancellationToken).ConfigureAwait(false);
                lastProblem = problem;
                if (lastProblem is null)
                {
                    return new CcSnapshot(
                        directory,
                        Path.Combine(directory, CcFiles.MainDbName),
                        includeUserData ? Path.Combine(directory, CcFiles.UserDbName) : null,
                        string.Join('|', stamps.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Length}:{s.Modified.Ticks}:{s.Counter}"))));
                }
            }
        }
        catch
        {
            TryDelete(directory);
            throw;
        }

        TryDelete(directory);
        throw new CcSnapshotException($"No stable copy after {Attempts} attempts: {lastProblem}");
    }

    public void Dispose() => TryDelete(Directory);

    private static async Task<(string? Problem, List<FileStamp> Stamps)> TryCopyAsync(CcFiles files, string[] sources, string directory, CancellationToken cancellationToken)
    {
        var journals = files.ExistingJournals();
        if (journals.Count > 0)
        {
            return ($"{Path.GetFileName(journals[0])} exists, a write is in progress", []);
        }

        var before = sources.Select(Stamp).ToList();

        foreach (var source in sources)
        {
            var target = Path.Combine(directory, Path.GetFileName(source));
            var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var after = sources.Select(Stamp).ToList();
        journals = files.ExistingJournals();

        for (var i = 0; i < sources.Length; i++)
        {
            var copy = Stamp(Path.Combine(directory, Path.GetFileName(sources[i])));
            if (before[i] != after[i] || copy.Length != before[i].Length || copy.Counter != before[i].Counter)
            {
                return ($"{Path.GetFileName(sources[i])} changed while it was copied", before);
            }

            if (before[i].Counter is null)
            {
                return ($"{Path.GetFileName(sources[i])} is not an SQLite database", before);
            }
        }

        return (journals.Count > 0 ? $"{Path.GetFileName(journals[0])} appeared while copying" : null, before);
    }

    private static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc, CcSqlite.ReadChangeCounter(path));
    }

    private static void RemoveLeftovers(string snapshotRoot)
    {
        if (!System.IO.Directory.Exists(snapshotRoot))
        {
            return;
        }

        foreach (var dir in new DirectoryInfo(snapshotRoot).EnumerateDirectories())
        {
            if (DateTime.UtcNow - dir.LastWriteTimeUtc > LeftoverAge)
            {
                TryDelete(dir.FullName);
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retried by RemoveLeftovers on a later run.
        }
    }

    private readonly record struct FileStamp(long Length, DateTime Modified, uint? Counter);
}
