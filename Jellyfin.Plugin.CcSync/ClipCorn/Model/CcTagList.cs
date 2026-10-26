using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

/// <summary>
/// The user-data <c>TAGS</c> column (<c>columnTypes/CCTagList.java</c>): a set of tag indexes,
/// stored as a compact JSON array sorted ascending.
/// </summary>
public sealed class CcTagList
{
    public const int WatchLater = 3;

    /// <summary>The highest tag index jClipCorn knows; an unknown index makes it read the list as empty.</summary>
    public const int MaxIndex = 8;

    private readonly SortedSet<int> _tags;

    private CcTagList(SortedSet<int> tags)
    {
        _tags = tags;
    }

    public static CcTagList Empty { get; } = new([]);

    public IReadOnlyCollection<int> Tags => _tags;

    public static bool TryParse(string? json, out CcTagList tags)
    {
        tags = Empty;

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

            var set = new SortedSet<int>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number
                    || !element.TryGetInt32(out var index)
                    || index < 0 || index > MaxIndex)
                {
                    return false;
                }

                set.Add(index);
            }

            tags = new CcTagList(set);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool Contains(int tag) => _tags.Contains(tag);

    public CcTagList Without(int tag)
    {
        if (!_tags.Contains(tag))
        {
            return this;
        }

        var set = new SortedSet<int>(_tags);
        set.Remove(tag);
        return new CcTagList(set);
    }

    public string ToJson()
        => "[" + string.Join(',', _tags.Select(t => t.ToString(CultureInfo.InvariantCulture))) + "]";

    public override string ToString() => ToJson();
}
