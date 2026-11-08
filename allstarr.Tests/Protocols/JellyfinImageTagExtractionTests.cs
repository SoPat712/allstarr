using System.Text.Json;
using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinImageTagExtractionTests
{
    [Fact]
    public void ExtractImageTag_WithMatchingImageTagsObject_ReturnsRequestedTag()
    {
        using var document = JsonDocument.Parse("""
        {
          "ImageTags": {
            "Primary": "playlist-primary-tag",
            "Backdrop": "playlist-backdrop-tag"
          }
        }
        """);

        var imageTag = JellyfinController.ExtractImageTag(document.RootElement, "Primary");

        Assert.Equal("playlist-primary-tag", imageTag);
    }

    [Fact]
    public void ExtractImageTag_WithPrimaryImageTagFallback_ReturnsFallbackTag()
    {
        using var document = JsonDocument.Parse("""
        {
          "PrimaryImageTag": "primary-fallback-tag"
        }
        """);

        var imageTag = JellyfinController.ExtractImageTag(document.RootElement, "Primary");

        Assert.Equal("primary-fallback-tag", imageTag);
    }

    [Fact]
    public void ExtractImageTag_WithoutMatchingTag_ReturnsNull()
    {
        using var document = JsonDocument.Parse("""
        {
          "ImageTags": {
            "Backdrop": "playlist-backdrop-tag"
          }
        }
        """);

        var imageTag = JellyfinController.ExtractImageTag(document.RootElement, "Primary");

        Assert.Null(imageTag);
    }

    [Fact]
    public void AppleArtworkVariant_UsesRequestedCdnSizeOnlyForApple()
    {
        var source = new Uri(
            "https://is1-ssl.mzstatic.com/image/thumb/Music113/cover.jpg/1200x1200bb.jpg");

        Assert.Equal(
            "https://is1-ssl.mzstatic.com/image/thumb/Music113/cover.jpg/300x300bb.jpg",
            JellyfinController.SelectExternalArtworkVariant(source, "apple-download", 300, null).ToString());
        Assert.Same(
            source,
            JellyfinController.SelectExternalArtworkVariant(source, "deezer", 300, null));
        Assert.Same(
            source,
            JellyfinController.SelectExternalArtworkVariant(source, "apple-download", null, null));
    }

    [Theory]
    [InlineData("https://i.scdn.co/image/ab67616d00001e02eda9478c39a21e1cdc6609ca", 96, "https://i.scdn.co/image/ab67616d0000b273eda9478c39a21e1cdc6609ca")]
    [InlineData("https://i.scdn.co/image/ab67616d00001e02eda9478c39a21e1cdc6609ca", 600, "https://i.scdn.co/image/ab67616d000082c1eda9478c39a21e1cdc6609ca")]
    [InlineData("https://i.scdn.co/image/ab67616d00004851eda9478c39a21e1cdc6609ca", null, "https://i.scdn.co/image/ab67616d000082c1eda9478c39a21e1cdc6609ca")]
    [InlineData("https://i.scdn.co/image/ab67706c0000da84f0e6e2a1b0c2d3e4f5a6b7c8", 600, "https://i.scdn.co/image/ab67706c0000da84f0e6e2a1b0c2d3e4f5a6b7c8")]
    [InlineData("https://cdn-images.dzcdn.net/images/cover/0a1b2c/250x250-000000-80-0-0.jpg", 300, "https://cdn-images.dzcdn.net/images/cover/0a1b2c/600x600-000000-80-0-0.jpg")]
    [InlineData("https://cdn-images.dzcdn.net/images/cover/0a1b2c/250x250-000000-80-0-0.jpg", null, "https://cdn-images.dzcdn.net/images/cover/0a1b2c/1000x1000-000000-80-0-0.jpg")]
    public void SpotifyAndDeezerCovers_UseALargeEnoughSize(string source, int? width, string expected) =>
        Assert.Equal(expected, JellyfinController.SelectExternalArtworkVariant(new Uri(source), "spotify", width, null).ToString());
}
