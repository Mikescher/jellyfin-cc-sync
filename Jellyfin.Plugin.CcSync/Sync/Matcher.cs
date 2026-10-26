using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Sync;

/// <summary>What the sync needs to know about one Jellyfin item; built by <c>JfIndex</c>.</summary>
public sealed record JfItemInfo(
    Guid Id,
    CcKind Kind,
    string Name,
    int? Year,
    string? ClipCornId,
    string? Tmdb,
    string? Imdb,
    IReadOnlyList<string> Tags,
    string? Path = null,
    bool TagsLocked = false);

public sealed record MatchedItem(JfItemInfo Item, CcElement Element, string Via)
{
    public bool ByFallback => Via != Matcher.ById;
}

public sealed record MatchIssue(JfItemInfo Item, string Reason, CcElement? Element = null);

public sealed record MatchResult(
    IReadOnlyList<MatchedItem> Matched,
    IReadOnlyList<MatchIssue> UnmatchedJf,
    IReadOnlyList<MatchIssue> Ambiguous,
    IReadOnlyList<MatchIssue> KindMismatches,
    IReadOnlyList<CcElement> UnmatchedCc,
    IReadOnlyList<CcElement> MatchedSeveralTimes)
{
    public IEnumerable<MatchedItem> FallbackMatched => Matched.Where(m => m.ByFallback);
}

/// <summary>
/// Pairs Jellyfin items with jClipCorn elements (<c>docs/sync-semantics.md</c> § Matching): the
/// <c>clipcorn</c> provider id first, then — for movies and series without one — the fallback
/// chain, which must be unique on both sides.
/// </summary>
public static class Matcher
{
    public const string ById = "clipcorn id";

    public static MatchResult Match(CcLibrary library, IReadOnlyList<JfItemInfo> items)
    {
        var matched = new List<MatchedItem>();
        var unmatched = new List<MatchIssue>();
        var ambiguous = new List<MatchIssue>();
        var mismatches = new List<MatchIssue>();
        var idMatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needFallback = new List<JfItemInfo>();

        foreach (var item in items)
        {
            if (item.ClipCornId is null)
            {
                if (item.Kind is CcKind.Movie or CcKind.Series)
                {
                    needFallback.Add(item);
                }
                else
                {
                    unmatched.Add(new MatchIssue(item, $"the {item.Kind.ToString().ToLowerInvariant()} has no clipcorn id (no fallback for seasons and episodes)"));
                }

                continue;
            }

            var element = library.Find(item.ClipCornId);
            if (element is null)
            {
                unmatched.Add(new MatchIssue(item, $"clipcorn id {item.ClipCornId} does not exist in jClipCorn"));
            }
            else if (element.Kind != item.Kind)
            {
                mismatches.Add(new MatchIssue(item, $"clipcorn id {item.ClipCornId} is a {element.Kind} in jClipCorn", element));
            }
            else
            {
                matched.Add(new MatchedItem(item, element, ById));
                idMatched.Add(element.Id);
            }
        }

        var lookup = new CcLookup(library);
        var fallback = new List<(JfItemInfo Item, CcElement Element, string Via)>();
        foreach (var item in needFallback)
        {
            var (element, via, problem, isAmbiguous) = item.Kind == CcKind.Movie
                ? Unpack(lookup.FindMovie(item.Tmdb, item.Imdb, item.Name, item.Year, idMatched))
                : Unpack(lookup.FindSeries(item.Tmdb, item.Imdb, item.Name, item.Year, idMatched));

            if (element is not null)
            {
                fallback.Add((item, element, via!));
            }
            else if (isAmbiguous)
            {
                ambiguous.Add(new MatchIssue(item, problem!));
            }
            else
            {
                unmatched.Add(new MatchIssue(item, "no clipcorn id and " + problem));
            }
        }

        // Unique on the Jellyfin side too: two items without id that land on one element are both skipped.
        foreach (var group in fallback.GroupBy(f => f.Element.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                var (item, element, via) = group.Single();
                matched.Add(new MatchedItem(item, element, via));
            }
            else
            {
                ambiguous.AddRange(group.Select(f => new MatchIssue(
                    f.Item,
                    $"{f.Via} also matches {group.Count() - 1} other Jellyfin item(s) without clipcorn id",
                    f.Element)));
            }
        }

        var matchedIds = matched.Select(m => m.Element.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var several = matched
            .GroupBy(m => m.Element.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.First().Element)
            .ToList();

        return new MatchResult(
            matched,
            unmatched,
            ambiguous,
            mismatches,
            library.Elements.Where(e => !matchedIds.Contains(e.Id)).ToList(),
            several);
    }

    private static (CcElement? Element, string? Via, string? Problem, bool Ambiguous) Unpack<T>(FallbackResult<T> result)
        where T : CcElement
        => (result.Match, result.Via, result.Problem, result.Ambiguous);
}
