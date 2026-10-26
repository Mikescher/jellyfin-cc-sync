using System.Text;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Sync;

/// <summary>
/// Names of the plugin's Jellyfin tags. Every tag starting with <see cref="Prefix"/>
/// (case-insensitive) belongs to the plugin.
/// </summary>
public static class TagSlug
{
    public const string Prefix = "jcc-";

    public const string RatingPrefix = Prefix + "rating-";

    public static bool IsOwned(string tag) => tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lower-case, whitespace runs and '-' runs become one '-', no leading or trailing '-'.
    /// jClipCorn restricts group names to <c>[A-Za-z0-9\-_ ]</c>, so nothing else needs escaping.
    /// </summary>
    public static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingDash = false;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c == '-')
            {
                pendingDash = builder.Length > 0;
                continue;
            }

            if (pendingDash)
            {
                builder.Append('-');
                pendingDash = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>The rating tag of a score, or null for "No rating".</summary>
    public static string? RatingTag(CcScore score) => score.IsRated ? RatingPrefix + score.Slug : null;

    /// <summary>The tag of a group, or null when the name has nothing left to slug.</summary>
    public static string? GroupTag(string group)
    {
        var slug = Slugify(group);
        return slug.Length == 0 ? null : Prefix + slug;
    }

    /// <summary>
    /// Groups whose tags coincide with another group's or with a rating tag. Such tags are simply
    /// shared; the result is only reported.
    /// </summary>
    public static IReadOnlyList<string> FindCollisions(IEnumerable<string> groupNames)
    {
        var warnings = new List<string>();
        var ratingTags = CcScore.All.Select(RatingTag).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var clash in groupNames
                     .Distinct(StringComparer.Ordinal)
                     .Select(name => (Name: name, Tag: GroupTag(name)))
                     .GroupBy(x => x.Tag ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var names = string.Join(", ", clash.Select(x => "\"" + x.Name + "\"").Order(StringComparer.Ordinal));

            if (clash.Key.Length == 0)
            {
                warnings.Add($"Group {names} has no usable tag name and is not tagged.");
                continue;
            }

            if (clash.Count() > 1)
            {
                warnings.Add($"Groups {names} share the tag {clash.Key}.");
            }

            if (clash.Key.StartsWith(RatingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(ratingTags.Contains(clash.Key)
                    ? $"Group {names} shares the tag {clash.Key} with a rating."
                    : $"Group {names} uses the rating tag prefix: {clash.Key}.");
            }
        }

        return warnings;
    }
}
