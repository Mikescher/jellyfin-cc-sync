using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Sync;

public sealed record MatchReportEntry(string Name, string Kind, string? JfItemId, string? CcId, string? Reason, string? Path);

/// <summary>A report list, capped; <see cref="Total"/> is the full count.</summary>
public sealed record MatchList(int Total, IReadOnlyList<MatchReportEntry> Items)
{
    public const int Cap = 1000;

    public static MatchList Of(IEnumerable<MatchReportEntry> entries)
    {
        var all = entries.ToList();
        return new MatchList(all.Count, all.Take(Cap).ToList());
    }
}

/// <summary>The last sync's view of both sides, shown on the Status page.</summary>
public sealed record MatchReport(
    DateTime CreatedUtc,
    CcDatabaseInfo Database,
    int JfItems,
    int CcElements,
    int Matched,
    IReadOnlyDictionary<string, int> MatchedByKind,
    MatchList UnmatchedCc,
    MatchList UnmatchedJf,
    MatchList Ambiguous,
    MatchList KindMismatches,
    MatchList FallbackMatched,
    MatchList MatchedSeveralTimes,
    MatchList CcProblems,
    IReadOnlyList<string> TagWarnings)
{
    public static MatchReport From(CcLibrary library, MatchResult match, int jfItems, DateTime nowUtc)
    {
        static MatchReportEntry Jf(MatchIssue i) => new(i.Item.Name, i.Item.Kind.ToString(), i.Item.Id.ToString("N"), i.Element?.Id ?? i.Item.ClipCornId, i.Reason, i.Item.Path);
        static MatchReportEntry Cc(CcElement e, string? reason = null) => new(e.Name, e.Kind.ToString(), null, e.Id, reason ?? e.Problem, null);

        var groups = library.Movies.SelectMany(m => m.Groups).Concat(library.Series.SelectMany(s => s.Groups)).Distinct(StringComparer.Ordinal);

        return new MatchReport(
            nowUtc,
            library.Info,
            jfItems,
            library.Elements.Count(),
            match.Matched.Count,
            match.Matched.GroupBy(m => m.Item.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            MatchList.Of(match.UnmatchedCc.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => Cc(e, "no Jellyfin item"))),
            MatchList.Of(match.UnmatchedJf.Select(Jf)),
            MatchList.Of(match.Ambiguous.Select(Jf)),
            MatchList.Of(match.KindMismatches.Select(Jf)),
            MatchList.Of(match.FallbackMatched.Select(m => new MatchReportEntry(m.Item.Name, m.Item.Kind.ToString(), m.Item.Id.ToString("N"), m.Element.Id, "matched by " + m.Via, m.Item.Path))),
            MatchList.Of(match.MatchedSeveralTimes.Select(e => Cc(e, "matches several Jellyfin items; each is synced"))),
            MatchList.Of(library.Problems.Select(e => Cc(e))),
            TagSlug.FindCollisions(groups));
    }
}

/// <summary>A plan that was computed but not applied (dry run or change guard).</summary>
public sealed record SyncPreview(
    string PlanId,
    DateTime CreatedUtc,
    string Reason,
    string Fingerprint,
    string PlanHash,
    int ItemsChanged,
    int Unwatches,
    IReadOnlyList<PlannedChange> Changes);
