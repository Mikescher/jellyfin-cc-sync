namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

/// <summary>
/// Everything the sync needs from one jClipCorn database pair, read from a snapshot.
/// </summary>
public sealed class CcLibrary
{
    /// <summary>jClipCorn allows six levels; the walk stops there even if the table holds a cycle.</summary>
    private const int MaxGroupDepth = 6;

    private readonly Dictionary<string, CcElement> _byId;
    private readonly Dictionary<string, CcGroup> _groups;
    private readonly Dictionary<string, CcSeason> _seasons;
    private readonly Dictionary<string, CcSeries> _series;

    public CcLibrary(
        CcDatabaseInfo info,
        IReadOnlyList<CcMovie> movies,
        IReadOnlyList<CcSeries> series,
        IReadOnlyList<CcSeason> seasons,
        IReadOnlyList<CcEpisode> episodes,
        IReadOnlyList<CcGroup> groups)
    {
        Info = info;
        Movies = movies;
        Series = series;
        Seasons = seasons;
        Episodes = episodes;
        Groups = groups;

        _byId = new Dictionary<string, CcElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in movies.Concat<CcElement>(series).Concat(seasons).Concat(episodes))
        {
            // IDs are unique across tables in practice; a clash keeps the first and is visible in Problems.
            _byId.TryAdd(element.Id, element);
        }

        _groups = groups.GroupBy(g => g.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _seasons = seasons.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _series = series.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public CcDatabaseInfo Info { get; }

    public IReadOnlyList<CcMovie> Movies { get; }

    public IReadOnlyList<CcSeries> Series { get; }

    public IReadOnlyList<CcSeason> Seasons { get; }

    public IReadOnlyList<CcEpisode> Episodes { get; }

    public IReadOnlyList<CcGroup> Groups { get; }

    /// <summary>Gets the number of user-data rows without a main-DB element; jClipCorn ignores them too.</summary>
    public int OrphanUserRows { get; init; }

    public IEnumerable<CcElement> Elements => _byId.Values;

    public IEnumerable<CcElement> Problems => _byId.Values.Where(e => e.Problem is not null);

    public CcElement? Find(string id) => _byId.GetValueOrDefault(id);

    public CcSeason? SeasonOf(CcEpisode episode) => _seasons.GetValueOrDefault(episode.SeasonId);

    public CcSeries? SeriesOf(CcSeason season) => _series.GetValueOrDefault(season.SeriesId);

    public CcSeries? SeriesOf(CcEpisode episode) => SeasonOf(episode) is { } season ? SeriesOf(season) : null;

    /// <summary>The parent chain of a group, nearest first, without the group itself.</summary>
    public IReadOnlyList<string> AncestorsOf(string group)
    {
        var result = new List<string>();
        var current = group;
        for (var depth = 0; depth < MaxGroupDepth; depth++)
        {
            if (!_groups.TryGetValue(current, out var entry) || string.IsNullOrEmpty(entry.Parent)
                || entry.Parent == group || result.Contains(entry.Parent, StringComparer.Ordinal))
            {
                break;
            }

            result.Add(entry.Parent);
            current = entry.Parent;
        }

        return result;
    }
}

/// <summary>Version and pairing keys of both files.</summary>
public sealed record CcDatabaseInfo(int MainVersion, string MainDuuid, int UserVersion, string UserMainDuuid, int UserMainVersion, bool HistoryEnabled);
