namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

public enum CcKind
{
    Movie,
    Series,
    Season,
    Episode,
}

/// <summary>
/// An element read from both DB files. <see cref="Problem"/> is set when one of its JSON columns did
/// not parse; such an element is reported and skipped by the sync.
/// </summary>
public abstract record CcElement(string Id, string Name, CcScore Score, string? Problem)
{
    public abstract CcKind Kind { get; }
}

public sealed record CcMovie(
    string Id,
    string Name,
    string Zyklus,
    int ZyklusNumber,
    int? Year,
    CcOnlineRefs OnlineRefs,
    IReadOnlyList<string> Groups,
    CcViewedHistory History,
    CcTagList Tags,
    CcScore Score,
    string? Problem)
    : CcElement(Id, Name, Score, Problem)
{
    public override CcKind Kind => CcKind.Movie;
}

public sealed record CcSeries(
    string Id,
    string Name,
    CcOnlineRefs OnlineRefs,
    IReadOnlyList<string> Groups,
    CcTagList Tags,
    CcScore Score,
    string? Problem)
    : CcElement(Id, Name, Score, Problem)
{
    public override CcKind Kind => CcKind.Series;
}

public sealed record CcSeason(
    string Id,
    string SeriesId,
    string Name,
    int? Year,
    CcOnlineRefs OnlineRefs,
    CcScore Score,
    string? Problem)
    : CcElement(Id, Name, Score, Problem)
{
    public override CcKind Kind => CcKind.Season;
}

public sealed record CcEpisode(
    string Id,
    string SeasonId,
    int EpisodeNumber,
    string Name,
    CcViewedHistory History,
    CcTagList Tags,
    CcScore Score,
    string? Problem)
    : CcElement(Id, Name, Score, Problem)
{
    public override CcKind Kind => CcKind.Episode;
}

public sealed record CcGroup(string Name, string Parent);
