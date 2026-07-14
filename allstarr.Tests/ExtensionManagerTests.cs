using allstarr.Services.Common;

namespace allstarr.Tests;

public class ExtensionManagerTests
{
    [Fact]
    public void ParseRepositoryList_DoesNotAddAThirdPartyRegistryByDefault()
    {
        Assert.Empty(ExtensionManager.ParseRepositoryList(null));
        Assert.Empty(ExtensionManager.ParseRepositoryList("  "));
    }

    [Fact]
    public void ParseRepositoryList_ReturnsOnlyExplicitlyConfiguredRegistries()
    {
        var repositories = ExtensionManager.ParseRepositoryList(
            "https://one.example/registry.json, https://two.example/registry.json");

        Assert.Equal(
            ["https://one.example/registry.json", "https://two.example/registry.json"],
            repositories);
    }

    [Fact]
    public void ParseStoreRegistry_SupportsExtensionsWrapperAndSnakeCaseFields()
    {
        const string json = """
        {
          "extensions": [
            {
              "id": "amazon-music",
              "name": "amazon-music",
              "display_name": "Amazon Music",
              "description": "Amazon playlist and metadata provider",
              "download_url": "https://example.test/amazon.zip",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "version": "1.2.3",
              "type": ["metadata", "playlist"]
            }
          ]
        }
        """;

        var items = ExtensionManager.ParseStoreRegistry(json, "https://example.test/registry.json");

        var item = Assert.Single(items);
        Assert.Equal("amazon-music", item.Id);
        Assert.Equal("Amazon Music", item.DisplayName);
        Assert.Equal("https://example.test/amazon.zip", item.DownloadUrl);
        Assert.Equal(new string('a', 64), item.Sha256);
        Assert.Equal(new[] { "metadata", "playlist" }, item.Types);
        Assert.Equal("https://example.test/registry.json", item.RepoUrl);
    }

    [Fact]
    public void ParseStoreRegistry_SupportsRootArrayAndCamelCaseFields()
    {
        const string json = """
        [
          {
            "id": "deezer-fast",
            "displayName": "Deezer Fast",
            "downloadUrl": "https://example.test/deezer.zip",
            "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "capabilities": "metadata,playlist"
          }
        ]
        """;

        var items = ExtensionManager.ParseStoreRegistry(json);

        var item = Assert.Single(items);
        Assert.Equal("deezer-fast", item.Id);
        Assert.Equal("Deezer Fast", item.DisplayName);
        Assert.Equal(new[] { "metadata", "playlist" }, item.Types);
    }

    [Fact]
    public void ParseStoreRegistry_SkipsItemsWithoutDownloadUrl()
    {
        const string json = """
        { "items": [ { "id": "broken", "displayName": "Broken" } ] }
        """;

        var items = ExtensionManager.ParseStoreRegistry(json);

        Assert.Empty(items);
    }
}
