using Jellyfin.Plugin.CcSync.Api;
using Jellyfin.Plugin.CcSync.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CcSync.ClipCorn;

/// <summary>
/// "Test ClipCorn access": everything the sync and the writer will need, checked once and explained.
/// Reads only snapshots; the only file it writes in the directory is the probe file.
/// </summary>
public sealed class CcProbe
{
    private readonly PluginPaths _paths;
    private readonly ILogger<CcProbe> _logger;

    public CcProbe(PluginPaths paths, ILogger<CcProbe> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<ProbeResponse> RunAsync(string directory, int lockStaleMinutes, string timeZone, CancellationToken cancellationToken)
    {
        var checks = new List<ProbeCheck>();

        void Add(string name, string level, string message) => checks.Add(new ProbeCheck { Name = name, Level = level, Message = message });

        if (ConfigValues.TryResolveTimeZone(timeZone, out var zone, out var zoneError))
        {
            Add("Time zone", "ok", $"{zone.Id}, currently UTC{FormatOffset(zone.GetUtcOffset(DateTime.UtcNow))}");
        }
        else
        {
            Add("Time zone", "error", zoneError!);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            Add("Directory", "error", "No jClipCorn directory is configured.");
            return Finish(checks);
        }

        if (!Directory.Exists(directory))
        {
            Add("Directory", "error", $"{directory} does not exist inside the Jellyfin process (check the bind mount or volume).");
            return Finish(checks);
        }

        var entries = Directory.EnumerateFileSystemEntries(directory).Count();
        if (entries == 0)
        {
            Add("Directory", "error", $"{directory} exists but is empty. Is the share mounted?");
            return Finish(checks);
        }

        Add("Directory", "ok", $"{directory} ({entries} entries)");

        var files = new CcFiles(directory);
        var mainReadable = CheckReadable(files.MainDb, "Main database", Add);
        var userReadable = CheckReadable(files.UserDb, "User database", Add);
        CheckWritable(files, Add);

        var access = CcLockProbe.ProbeDirectory(files, ConfigValues.LockStaleAfter(lockStaleMinutes), DateTime.UtcNow);
        foreach (var status in new[] { access.MainLock, access.HistoryLock })
        {
            var level = status.Present ? "warn" : "ok";
            var message = status.Held ? status.Description + ". Reads work; writes wait until jClipCorn is closed."
                : status.Stale ? status.Description + ". Writes are allowed."
                : status.Description + ".";
            Add("Lock", level, message);
        }

        if (access.Journals.Count > 0)
        {
            Add("Journal", "warn", string.Join(", ", access.Journals.Select(Path.GetFileName)) + " exists: a transaction is running or was interrupted. Writes wait.");
        }

        if (!mainReadable || !userReadable)
        {
            return Finish(checks);
        }

        try
        {
            using var snapshot = await CcSnapshot.TakeAsync(files, _paths.SnapshotDirectory, true, cancellationToken).ConfigureAwait(false);
            var library = CcReader.Read(snapshot.MainDb, snapshot.UserDb!, cancellationToken);
            var info = library.Info;

            Add("Schema", "ok", $"Main schema {info.MainVersion}, user data schema {info.UserVersion}, user data belongs to this database ({info.MainDuuid}). History triggers {(info.HistoryEnabled ? "on" : "off")}.");

            var counts = $"{library.Movies.Count} movies, {library.Series.Count} series, {library.Seasons.Count} seasons, {library.Episodes.Count} episodes, {library.Groups.Count} groups";
            if (library.Movies.Count == 0 && library.Episodes.Count == 0)
            {
                Add("Content", "error", counts + ". A database without movies and episodes aborts every sync.");
            }
            else
            {
                Add("Content", "ok", counts + (library.OrphanUserRows > 0 ? $"; {library.OrphanUserRows} user rows without element (ignored)" : string.Empty) + ".");
            }

            var problems = library.Problems.ToList();
            if (problems.Count > 0)
            {
                var sample = string.Join("; ", problems.Take(3).Select(p => $"{p.Kind} \"{p.Name}\": {p.Problem}"));
                Add("Content", "warn", $"{problems.Count} elements have unreadable values and are skipped: {sample}{(problems.Count > 3 ? " …" : string.Empty)}");
            }
        }
        catch (Exception ex) when (ex is CcSnapshotException or CcSchemaException or CcReadException or IOException or UnauthorizedAccessException)
        {
            Add("Schema", "error", ex.Message);
        }

        return Finish(checks);
    }

    private ProbeResponse Finish(List<ProbeCheck> checks)
    {
        var response = new ProbeResponse { Checks = checks, Ok = checks.All(c => c.Level != "error") };
        _logger.LogInformation(
            "jClipCorn access test: {Result}",
            string.Join(" | ", checks.Select(c => $"{c.Name} {c.Level}: {c.Message}")));
        return response;
    }

    private static bool CheckReadable(string path, string name, Action<string, string, string> add)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[16];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read < 16 || !buffer.AsSpan().SequenceEqual("SQLite format 3\0"u8))
            {
                add(name, "error", $"{Path.GetFileName(path)} is not an SQLite database.");
                return false;
            }

            add(name, "ok", $"{Path.GetFileName(path)} readable ({stream.Length / 1024.0 / 1024.0:0.0} MiB).");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            add(name, "error", $"{Path.GetFileName(path)} cannot be read: {ex.Message}");
            return false;
        }
    }

    private static void CheckWritable(CcFiles files, Action<string, string, string> add)
    {
        var probe = Path.Combine(files.Directory, CcFiles.ProbeFileName);
        try
        {
            File.WriteAllText(probe, "jellyfin-cc-sync write test");
            File.Delete(probe);
            add("Write access", "ok", "The directory is writable (needed for ClipCornUserData.db-journal).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            add("Write access", "error", $"Cannot create files in the directory, so watches cannot be written back: {ex.Message}");
        }
    }

    private static string FormatOffset(TimeSpan offset)
        => (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hh\\:mm", System.Globalization.CultureInfo.InvariantCulture);
}
