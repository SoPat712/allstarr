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
}
