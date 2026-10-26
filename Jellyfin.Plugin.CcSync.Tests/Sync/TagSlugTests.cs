using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Sync;

namespace Jellyfin.Plugin.CcSync.Tests.Sync;

public class TagSlugTests
{
    [Theory]
    [InlineData("Star Wars", "star-wars")]
    [InlineData("MCU", "mcu")]
    [InlineData("  Star   Wars ", "star-wars")]
    [InlineData("A - B", "a-b")]
    [InlineData("--x--", "x")]
    [InlineData("Lord_of_the_Rings", "lord_of_the_rings")]
    [InlineData("Phase 4", "phase-4")]
    [InlineData("-", "")]
    public void Slugifies(string value, string expected)
    {
        Assert.Equal(expected, TagSlug.Slugify(value));
    }

    [Fact]
    public void BuildsRatingTags()
    {
        Assert.Equal("jcc-rating-masterpiece", TagSlug.RatingTag(CcScore.FromStored(5)!));
        Assert.Equal("jcc-rating-not-recommended", TagSlug.RatingTag(CcScore.FromStored(2)!));
        Assert.Equal("jcc-rating-average", TagSlug.RatingTag(CcScore.FromStored(7)!));
        Assert.Null(TagSlug.RatingTag(CcScore.NoRating));
    }

    [Fact]
    public void BuildsGroupTags()
    {
        Assert.Equal("jcc-star-wars", TagSlug.GroupTag("Star Wars"));
        Assert.Null(TagSlug.GroupTag("- -"));
    }

    [Theory]
    [InlineData("jcc-mcu", true)]
    [InlineData("JCC-Rating-Bad", true)]
    [InlineData("jcc-", true)]
    [InlineData("jccmcu", false)]
    [InlineData("mcu", false)]
    [InlineData("x-jcc-mcu", false)]
    public void OwnsEveryJccTag(string tag, bool owned)
    {
        Assert.Equal(owned, TagSlug.IsOwned(tag));
    }

    [Fact]
    public void ReportsCollisions()
    {
        var warnings = TagSlug.FindCollisions(["Star Wars", "star-wars", "MCU", "Rating Favourite", "Rating Things", "- -", "MCU"]);

        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("\"Star Wars\", \"star-wars\"", StringComparison.Ordinal) && w.Contains("jcc-star-wars", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("\"Rating Favourite\"", StringComparison.Ordinal) && w.Contains("with a rating", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("\"Rating Things\"", StringComparison.Ordinal) && w.Contains("prefix", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("\"- -\"", StringComparison.Ordinal) && w.Contains("no usable tag", StringComparison.Ordinal));
    }

    [Fact]
    public void NoCollisionsForDistinctGroups()
    {
        Assert.Empty(TagSlug.FindCollisions(["MCU", "DCEU", "Star Wars"]));
    }
}
