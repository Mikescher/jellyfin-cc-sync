using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcSchemaTests
{
    [Theory]
    [InlineData(38, true)]
    [InlineData(39, true)]
    [InlineData(37, false)]
    [InlineData(40, false)]
    public void AcceptsMainVersions(int mainVersion, bool usable)
    {
        var problems = CcSchema.Check(new CcDatabaseInfo(mainVersion, "d", 4, "d", 38, true));
        Assert.Equal(usable, problems.Count == 0);
    }

    [Fact]
    public void RejectsOtherUserDataVersions()
        => Assert.NotEmpty(CcSchema.Check(new CcDatabaseInfo(39, "d", 3, "d", 39, true)));

    [Fact]
    public void RejectsUserDataWrittenForANewerMainSchema()
        => Assert.NotEmpty(CcSchema.Check(new CcDatabaseInfo(38, "d", 4, "d", 39, true)));
}
