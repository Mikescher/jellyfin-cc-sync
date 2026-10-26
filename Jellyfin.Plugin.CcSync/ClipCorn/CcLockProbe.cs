using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.CcSync.ClipCorn;

public enum CcLockFormat
{
    Absent,
    Json,
    BarePid,
    Malformed,
    Unreadable,
}

/// <summary>What one <c>.~lock</c> file says.</summary>
public sealed record CcLockStatus(
    string Path,
    CcLockFormat Format,
    bool Held,
    string? Host,
    long? Pid,
    DateTime? AcquiredUtc,
    DateTime? HeartbeatUtc,
    DateTime? ModifiedUtc,
    string Description)
{
    public bool Present => Format != CcLockFormat.Absent;

    /// <summary>Gets a value indicating whether the file exists but no longer counts as held.</summary>
    public bool Stale => Present && !Held;
}

/// <summary>Lock and journal state of the whole directory.</summary>
public sealed record CcAccessState(CcLockStatus MainLock, CcLockStatus HistoryLock, IReadOnlyList<string> Journals)
{
    public bool Blocked => MainLock.Held || HistoryLock.Held || Journals.Count > 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (MainLock.Held)
        {
            parts.Add(MainLock.Description);
        }

        if (HistoryLock.Held)
        {
            parts.Add(HistoryLock.Description);
        }

        parts.AddRange(Journals.Select(j => $"{System.IO.Path.GetFileName(j)} exists (a transaction is running or was interrupted)"));
        return parts.Count == 0 ? "not locked" : string.Join("; ", parts);
    }
}

/// <summary>
/// Reads jClipCorn's lock files (<c>util/filesystem/FileLockManager.java</c>). The plugin never
/// creates, rewrites or deletes them.
/// </summary>
public static class CcLockProbe
{
    public static CcAccessState ProbeDirectory(CcFiles files, TimeSpan staleAfter, DateTime nowUtc)
        => new(Probe(files.MainLock, staleAfter, nowUtc), Probe(files.HistoryLock, staleAfter, nowUtc), files.ExistingJournals());

    public static CcLockStatus Probe(string path, TimeSpan staleAfter, DateTime nowUtc)
    {
        string content;
        DateTime? modified = null;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
            modified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Evaluate(path, null, null, staleAfter, nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only "not found" counts as absent; a lock we cannot read is assumed to be held.
            return new CcLockStatus(path, CcLockFormat.Unreadable, true, null, null, null, null, modified, $"{System.IO.Path.GetFileName(path)} cannot be read ({ex.Message})");
        }

        return Evaluate(path, content, modified, staleAfter, nowUtc);
    }

    /// <summary>
    /// The rule: a JSON lock with a readable heartbeat <c>u</c> is held while the heartbeat is younger
    /// than <paramref name="staleAfter"/>; anything else present is held while the file's mtime is.
    /// </summary>
    public static CcLockStatus Evaluate(string path, string? content, DateTime? modifiedUtc, TimeSpan staleAfter, DateTime nowUtc)
    {
        var name = System.IO.Path.GetFileName(path);

        if (content is null)
        {
            return new CcLockStatus(path, CcLockFormat.Absent, false, null, null, null, null, null, $"{name} absent");
        }

        var text = content.Trim().TrimStart('﻿');
        var format = CcLockFormat.Malformed;
        string? host = null;
        long? pid = null;
        DateTime? acquired = null;
        DateTime? heartbeat = null;

        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    format = CcLockFormat.Json;
                    host = ReadString(doc.RootElement, "host");
                    pid = long.TryParse(ReadString(doc.RootElement, "pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
                    acquired = ParseInstant(ReadString(doc.RootElement, "c"));
                    heartbeat = ParseInstant(ReadString(doc.RootElement, "u"));
                }
            }
            catch (JsonException)
            {
                format = CcLockFormat.Malformed;
            }
        }
        else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var barePid))
        {
            format = CcLockFormat.BarePid;
            pid = barePid;
        }

        var reference = heartbeat ?? modifiedUtc;
        var held = reference is null || nowUtc - reference.Value < staleAfter;

        var holder = string.IsNullOrEmpty(host) ? "jClipCorn" : $"jClipCorn on {host}";
        if (pid is not null)
        {
            holder += $" (pid {pid})";
        }

        var basis = heartbeat is not null ? "heartbeat" : "file modified";
        var age = reference is null ? "age unknown" : $"{basis} {FormatAge(nowUtc - reference.Value)}";
        var description = held
            ? $"{name} held by {holder}, {age}"
            : $"{name} is stale: {holder} was not shut down cleanly ({age})";

        return new CcLockStatus(path, format, held, host, pid, acquired, heartbeat, modifiedUtc, description);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static DateTime? ParseInstant(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            return "in the future";
        }

        return age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds} s ago"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : $"{(int)age.TotalHours} h {age.Minutes} min ago";
    }
}
