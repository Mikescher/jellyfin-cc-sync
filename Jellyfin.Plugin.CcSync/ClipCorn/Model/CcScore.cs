namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

/// <summary>
/// jClipCorn's user score (<c>columnTypes/CCUserScore.java</c>). The stored value is not the rank:
/// "Average" is stored as 7 and ranks between "Not recommended" (2) and "Recommended" (3).
/// </summary>
public sealed record CcScore(int Stored, int Rank, string Label, string Slug)
{
    public const int NoRatingValue = 6;

    public static IReadOnlyList<CcScore> All { get; } =
    [
        new(0, 1, "Crap", "crap"),
        new(1, 2, "Bad", "bad"),
        new(2, 3, "Not recommended", "not-recommended"),
        new(7, 4, "Average", "average"),
        new(3, 5, "Recommended", "recommended"),
        new(4, 6, "Favourite", "favourite"),
        new(5, 7, "Masterpiece", "masterpiece"),
        new(NoRatingValue, 0, "No rating", string.Empty),
    ];

    public static CcScore NoRating { get; } = All.Single(s => s.Stored == NoRatingValue);

    public bool IsRated => Stored != NoRatingValue;

    public static CcScore? FromStored(int stored) => All.FirstOrDefault(s => s.Stored == stored);
}
