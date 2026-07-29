using System.Reflection;
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

        var imageTag = InvokeExtractImageTag(document.RootElement, "Primary");

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

        var imageTag = InvokeExtractImageTag(document.RootElement, "Primary");

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

        var imageTag = InvokeExtractImageTag(document.RootElement, "Primary");

        Assert.Null(imageTag);
    }

    private static string? InvokeExtractImageTag(JsonElement item, string imageType)
    {
        var method = typeof(JellyfinController).GetMethod(
            "ExtractImageTag",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (string?)method!.Invoke(null, new object?[] { item, imageType });
    }
}
