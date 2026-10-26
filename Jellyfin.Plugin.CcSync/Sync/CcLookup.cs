using System.Globalization;
using System.Text;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Sync;

/// <summary>A fallback match and the key that produced it.</summary>
public sealed record FallbackResult<T>(T? Match, string? Via, string? Problem, bool Ambiguous = false)
    where T : CcElement;

/// <summary>
/// The jClipCorn side of fallback matching (<c>docs/sync-semantics.md</c> § Matching): movies by
/// TMDB → IMDB → normalised name + year, series by TMDB → IMDB → normalised name (+ year of the
/// earliest season). A key that finds more than one element is ambiguous and ends the chain.
/// </summary>
public sealed class CcLookup
{
    private readonly ILookup<string, CcMovie> _movieTmdb;
    private readonly ILookup<string, CcMovie> _movieImdb;
    private readonly ILookup<string, CcMovie> _movieName;
    private readonly ILookup<string, CcSeries> _seriesTmdb;
    private readonly ILookup<string, CcSeries> _seriesImdb;
    private readonly ILookup<string, CcSeries> _seriesName;
    private readonly Dictionary<string, int> _seriesYear;

    public CcLookup(CcLibrary library)
    {
        _movieTmdb = library.Movies.Where(m => m.OnlineRefs.Tmdb("movie") is not null).ToLookup(m => m.OnlineRefs.Tmdb("movie")!);
        _movieImdb = library.Movies.Where(m => m.OnlineRefs.Imdb() is not null).ToLookup(m => m.OnlineRefs.Imdb()!, StringComparer.OrdinalIgnoreCase);
        _movieName = library.Movies.Where(m => m.Year is not null).ToLookup(m => NameYearKey(m.Name, m.Year!.Value));
        _seriesTmdb = library.Series.Where(s => s.OnlineRefs.Tmdb("tv") is not null).ToLookup(s => s.OnlineRefs.Tmdb("tv")!);
        _seriesImdb = library.Series.Where(s => s.OnlineRefs.Imdb() is not null).ToLookup(s => s.OnlineRefs.Imdb()!, StringComparer.OrdinalIgnoreCase);
        _seriesName = library.Series.ToLookup(s => Normalize(s.Name));
        _seriesYear = library.Seasons
            .Where(s => s.Year is not null)
            .GroupBy(s => s.SeriesId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(s => s.Year!.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Case-folded, diacritics removed, every run of punctuation and whitespace one space.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        var space = false;
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                space = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <param name="exclude">Element ids that are already matched by clipcorn id and are not offered again.</param>
    public FallbackResult<CcMovie> FindMovie(string? tmdb, string? imdb, string? name, int? year, IReadOnlySet<string>? exclude = null)
    {
        var chain = new List<(string Via, IEnumerable<CcMovie> Candidates)>();
        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            chain.Add(("tmdb " + tmdb, _movieTmdb[tmdb.Trim()]));
        }

        if (!string.IsNullOrWhiteSpace(imdb))
        {
            chain.Add(("imdb " + imdb, _movieImdb[imdb.Trim()]));
        }

        if (year is > 0 && Normalize(name).Length > 0)
        {
            chain.Add(($"name \"{name}\" + year {year}", _movieName[NameYearKey(name!, year.Value)]));
        }

        return Walk(chain, exclude);
    }

    public FallbackResult<CcSeries> FindSeries(string? tmdb, string? imdb, string? name, int? year, IReadOnlySet<string>? exclude = null)
    {
        var chain = new List<(string Via, IEnumerable<CcSeries> Candidates)>();
        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            chain.Add(("tmdb " + tmdb, _seriesTmdb[tmdb.Trim()]));
        }

        if (!string.IsNullOrWhiteSpace(imdb))
        {
            chain.Add(("imdb " + imdb, _seriesImdb[imdb.Trim()]));
        }

        var normalized = Normalize(name);
        if (normalized.Length > 0)
        {
            var byName = _seriesName[normalized];
            if (year is > 0)
            {
                byName = byName.Where(s => !_seriesYear.TryGetValue(s.Id, out var first) || first == year);
            }

            chain.Add(($"name \"{name}\"" + (year is > 0 ? $" + year {year}" : string.Empty), byName));
        }

        return Walk(chain, exclude);
    }

    private static FallbackResult<T> Walk<T>(List<(string Via, IEnumerable<T> Candidates)> chain, IReadOnlySet<string>? exclude)
        where T : CcElement
    {
        foreach (var (via, candidates) in chain)
        {
            var found = candidates.Where(c => exclude is null || !exclude.Contains(c.Id)).Take(2).ToList();
            if (found.Count == 1)
            {
                return new FallbackResult<T>(found[0], via, null);
            }

            if (found.Count > 1)
            {
                return new FallbackResult<T>(null, via, $"{via} matches several jClipCorn elements", Ambiguous: true);
            }
        }

        return new FallbackResult<T>(null, null, chain.Count == 0 ? "no TMDB, IMDB, name or year to match by" : "no jClipCorn element matches");
    }

    private static string NameYearKey(string name, int year) => Normalize(name) + "|" + year.ToString(CultureInfo.InvariantCulture);
}
