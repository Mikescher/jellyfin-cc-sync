using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

public sealed record CcOnlineRef(string Type, string Id, string Description);

/// <summary>
/// The main-DB <c>ONLINEREF</c> column (<c>columnTypes/CCOnlineReferenceList.java</c>):
/// <c>[{"t":"tmdb","id":"movie/16869"},{"t":"imdb","id":"tt0361748","d":"…"}]</c>.
/// </summary>
public sealed partial class CcOnlineRefs
{
    private CcOnlineRefs(IReadOnlyList<CcOnlineRef> refs)
    {
        Refs = refs;
    }

    public static CcOnlineRefs Empty { get; } = new([]);

    public IReadOnlyList<CcOnlineRef> Refs { get; }

    public static bool TryParse(string? json, out CcOnlineRefs refs)
    {
        refs = Empty;

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

            var list = new List<CcOnlineRef>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !TryGetString(element, "t", out var type)
                    || !TryGetString(element, "id", out var id))
                {
                    return false;
                }

                TryGetString(element, "d", out var description);
                list.Add(new CcOnlineRef(type.ToLowerInvariant(), id, description));
            }

            refs = new CcOnlineRefs(list);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The TMDB number for <paramref name="kind"/> (<c>movie</c> or <c>tv</c>), taken from the first
    /// matching reference without a description; descriptions mark secondary references.
    /// </summary>
    public string? Tmdb(string kind)
    {
        var prefix = kind + "/";
        var id = Primary("tmdb", r => r.StartsWith(prefix, StringComparison.Ordinal) && TmdbPattern().IsMatch(r));
        return id?[prefix.Length..];
    }

    public string? Imdb() => Primary("imdb", r => ImdbPattern().IsMatch(r));

    private string? Primary(string type, Func<string, bool> valid)
    {
        var candidates = Refs.Where(r => r.Type == type && valid(r.Id)).ToList();
        return (candidates.FirstOrDefault(r => r.Description.Length == 0) ?? candidates.FirstOrDefault())?.Id;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    [GeneratedRegex("^(movie|tv)/[0-9]+$")]
    private static partial Regex TmdbPattern();

    [GeneratedRegex("^tt[0-9]+$")]
    private static partial Regex ImdbPattern();
}
