using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Sync;

/// <summary>The sync user's current values for the fields the plugin owns.</summary>
public sealed record JfUserData(bool Played, int PlayCount, DateTime? LastPlayedDate, bool IsFavorite)
{
    public static JfUserData Default { get; } = new(false, 0, null, false);
}

public sealed record FieldChange<T>(T Old, T New);

public sealed record PlannedChange(
    Guid ItemId,
    string Name,
    CcKind Kind,
    string? CcId,
    FieldChange<bool>? Played,
    FieldChange<int>? PlayCount,
    FieldChange<DateTime?>? LastPlayed,
    FieldChange<bool>? Favorite,
    IReadOnlyList<string> AddTags,
    IReadOnlyList<string> RemoveTags,
    bool LockTags = false)
{
    public bool HasPlayedChange => Played is not null || PlayCount is not null || LastPlayed is not null;

    public bool HasUserDataChange => HasPlayedChange || Favorite is not null;

    public bool HasTagChange => AddTags.Count > 0 || RemoveTags.Count > 0 || LockTags;

    public bool Unwatches => Played is { Old: true, New: false };
}

public sealed record SyncPlan(IReadOnlyList<PlannedChange> Changes)
{
    public int Unwatches => Changes.Count(c => c.Unwatches);
}

/// <summary>Desired vs actual: only differences become changes.</summary>
public static class SyncPlanner
{
    /// <param name="desired">Desired state per item.</param>
    /// <param name="actual">The sync user's current values.</param>
    /// <param name="lockTags">Add <c>MetadataField.Tags</c> to the locked fields of items that carry plugin tags.</param>
    public static SyncPlan Plan(IReadOnlyList<DesiredItem> desired, IReadOnlyDictionary<Guid, JfUserData> actual, bool lockTags = false)
    {
        var changes = new List<PlannedChange>();

        foreach (var d in desired)
        {
            var current = actual.GetValueOrDefault(d.Item.Id) ?? JfUserData.Default;

            var played = d.Played is { } p && p != current.Played ? new FieldChange<bool>(current.Played, p) : null;
            var playCount = d.PlayCount is { } c && c != current.PlayCount ? new FieldChange<int>(current.PlayCount, c) : null;
            var lastPlayed = d.OwnsLastPlayed && !SameSecond(d.LastPlayed, current.LastPlayedDate)
                ? new FieldChange<DateTime?>(AsUtc(current.LastPlayedDate), d.LastPlayed)
                : null;
            var favorite = d.Favorite is { } f && f != current.IsFavorite ? new FieldChange<bool>(current.IsFavorite, f) : null;

            IReadOnlyList<string> add = [];
            IReadOnlyList<string> remove = [];
            var lockNow = false;
            if (d.Tags is { } wanted)
            {
                var owned = d.Item.Tags.Where(TagSlug.IsOwned).ToList();
                add = wanted.Where(t => !owned.Contains(t, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
                remove = owned.Where(t => !wanted.Contains(t)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                lockNow = lockTags && wanted.Count > 0 && !d.Item.TagsLocked;
            }

            var change = new PlannedChange(d.Item.Id, d.Item.Name, d.Item.Kind, d.Element?.Id, played, playCount, lastPlayed, favorite, add, remove, lockNow);
            if (change.HasUserDataChange || change.HasTagChange)
            {
                changes.Add(change);
            }
        }

        return new SyncPlan(changes);
    }

    /// <summary>Equal when both are null or both truncate to the same second.</summary>
    public static bool SameSecond(DateTime? a, DateTime? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return AsUtc(a)!.Value.Ticks / TimeSpan.TicksPerSecond == AsUtc(b)!.Value.Ticks / TimeSpan.TicksPerSecond;
    }

    // Jellyfin's dates come back from the database without a kind; they are UTC.
    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
    };
}
