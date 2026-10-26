namespace Jellyfin.Plugin.CcSync.Tests;

public class PluginPagesTests
{
    [Fact]
    public void EveryPageResourceIsEmbedded()
    {
        var embedded = typeof(Plugin).Assembly.GetManifestResourceNames();

        foreach (var page in Plugin.Pages())
        {
            Assert.Contains(page.EmbeddedResourcePath, embedded);
        }
    }

    [Fact]
    public void OnlyOnePageIsInTheMenu()
    {
        Assert.Single(Plugin.Pages(), p => p.EnableInMainMenu);
    }
}
