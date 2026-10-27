namespace Jellyfin.Plugin.CcSync.Watch;

/// <summary>
/// In-memory playback per session and item (start time, furthest reported position), and a ledger
/// of recently recorded watches per user and item (ported from jellyfin-watchnotify). Lost on
/// restart; the start time then falls back to <c>stop - position</c>.
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
    public void Record(string key, Guid userId, Guid itemId, DateTime startUtc, long? positionTicks, TimeSpan coalesceWindow)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var position = Math.Max(positionTicks ?? 0, 0);
            if (_sessions.TryGetValue(key, out var entry) && now - entry.LastSeen < coalesceWindow)
            {
                _sessions[key] = entry with
                {
                    Start = startUtc < entry.Start ? startUtc : entry.Start,
                    LastSeen = now,
                    FurthestTicks = Math.Max(entry.FurthestTicks, position),
                };
                return;
            }

            _sessions[key] = new SessionEntry(startUtc, now, userId, itemId, position);
        }
    }

    /// <summary>
    /// The furthest position reported by the most recently active session of a user and item, or
    /// null when none is tracked.
    /// </summary>
    public long? FurthestPosition(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            SessionEntry? latest = null;
            foreach (var entry in _sessions.Values)
            {
                if (entry.UserId == userId && entry.ItemId == itemId && (latest is null || entry.LastSeen > latest.LastSeen))
                {
                    latest = entry;
                }
            }

            return latest?.FurthestTicks;
        }
    }

    /// <summary>
    /// Ends the watch of a user and item: drops every session tracked for them, so none of them
    /// counts towards a later watch. Returns the start of session <paramref name="key"/> if it is
    /// tracked, else the earliest tracked start, else null.
    /// </summary>
    public DateTime? EndWatch(Guid userId, Guid itemId, string? key = null)
    {
        lock (_lock)
        {
            DateTime? own = key is not null && _sessions.Remove(key, out var session) ? session.Start : null;
            DateTime? earliest = null;
            foreach (var (otherKey, entry) in _sessions.Where(kv => kv.Value.UserId == userId && kv.Value.ItemId == itemId).ToList())
            {
                _sessions.Remove(otherKey);
                if (earliest is null || entry.Start < earliest)
                {
                    earliest = entry.Start;
                }
            }

            return own ?? earliest;
        }
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

    private sealed record SessionEntry(DateTime Start, DateTime LastSeen, Guid UserId, Guid ItemId, long FurthestTicks);
}
