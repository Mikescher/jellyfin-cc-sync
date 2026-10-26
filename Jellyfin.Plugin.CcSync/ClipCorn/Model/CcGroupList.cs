using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

/// <summary>
/// A <c>GROUPS</c> column of a movie or series: a JSON array of group names
/// (<c>columnTypes/CCGroupList.java</c>). Like jClipCorn, names that fail
/// <c>^[A-Za-z0-9\-_ ]+$</c> are dropped silently.
/// </summary>
public static partial class CcGroupList
{
    public static bool TryParse(string? json, out IReadOnlyList<string> groups)
    {
        groups = [];

        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var list = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var name = element.GetString()!;
                if (IsValidName(name) && !list.Contains(name, StringComparer.Ordinal))
                {
                    list.Add(name);
                }
            }

            groups = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9\-_ ]+$")]
    private static partial Regex NamePattern();
}
