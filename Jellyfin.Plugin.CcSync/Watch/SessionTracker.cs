namespace Jellyfin.Plugin.CcSync.Watch;

/// <summary>
/// In-memory playback start times per session and item, and a ledger of recently recorded watches
/// per user and item (ported from jellyfin-watchnotify). Lost on restart; the start time then
/// falls back to <c>stop - position</c>.
/// </summary>
public sealed class SessionTracker
{
    private const int SessionMaxAgeHours = 24;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _claimed = new(StringComparer.Ordinal);

    public static string SessionKey(string sessionId, Guid itemId) => sessionId + "|" + itemId.ToString("N");

    public static string DedupKey(Guid userId, Guid itemId) => userId.ToString("N") + "|" + itemId.ToString("N");

    /// <summary>
    /// Folds an event into the session entry. An entry whose last activity lies within
    /// <paramref name="coalesceWindow"/> keeps its earliest start, so a seek that restarts the
    /// stream does not reset it; a longer gap starts a new watch.
    /// </summary>
    public void Record(string key, Guid userId, Guid itemId, DateTime startUtc, TimeSpan coalesceWindow)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_sessions.TryGetValue(key, out var entry) && now - entry.LastSeen < coalesceWindow)
            {
                _sessions[key] = entry with
                {
                    Start = startUtc < entry.Start ? startUtc : entry.Start,
                    LastSeen = now,
                };
                return;
            }

            _sessions[key] = new SessionEntry(startUtc, now, userId, itemId);
        }
    }

    public bool TryPopStart(string key, out DateTime startUtc)
    {
        lock (_lock)
        {
            if (_sessions.Remove(key, out var entry))
            {
                startUtc = entry.Start;
                return true;
            }
        }

        startUtc = default;
        return false;
    }

    /// <summary>
    /// Pops the earliest start recorded for a user and item in any session, for the paths that
    /// learn about a finished watch without knowing its session.
    /// </summary>
    public bool TryPopStartForItem(Guid userId, Guid itemId, out DateTime startUtc)
    {
        lock (_lock)
        {
            string? match = null;
            foreach (var (key, entry) in _sessions)
            {
                if (entry.UserId == userId && entry.ItemId == itemId
                    && (match is null || entry.Start < _sessions[match].Start))
                {
                    match = key;
                }
            }

            if (match is not null && _sessions.Remove(match, out var found))
            {
                startUtc = found.Start;
                return true;
            }
        }

        startUtc = default;
        return false;
    }

    /// <summary>Claims a watch; false when the same key was claimed within <paramref name="ttl"/>.</summary>
    public bool TryClaim(string key, TimeSpan ttl)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_claimed.TryGetValue(key, out var last) && now - last < ttl)
            {
                return false;
            }

            _claimed[key] = now;
            return true;
        }
    }

    public bool IsClaimed(string key, TimeSpan ttl)
    {
        lock (_lock)
        {
            return _claimed.TryGetValue(key, out var last) && DateTime.UtcNow - last < ttl;
        }
    }

    /// <summary>Drops stale entries so neither map grows without bound.</summary>
    public void Collect(TimeSpan dedupTtl)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            foreach (var key in _sessions
                         .Where(kv => now - kv.Value.LastSeen > TimeSpan.FromHours(SessionMaxAgeHours))
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _sessions.Remove(key);
            }

            foreach (var key in _claimed
                         .Where(kv => now - kv.Value > dedupTtl)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _claimed.Remove(key);
            }
        }
    }

    private sealed record SessionEntry(DateTime Start, DateTime LastSeen, Guid UserId, Guid ItemId);
}
