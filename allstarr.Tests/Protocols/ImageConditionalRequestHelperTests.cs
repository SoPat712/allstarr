using Microsoft.AspNetCore.Http;
using allstarr.Services.Common;

namespace allstarr.Tests;

public class ImageConditionalRequestHelperTests
{
    [Fact]
    public void ComputeStrongETag_SamePayload_ReturnsStableQuotedHash()
    {
        var payload = new byte[] { 1, 2, 3, 4 };

        var first = ImageConditionalRequestHelper.ComputeStrongETag(payload);
        var second = ImageConditionalRequestHelper.ComputeStrongETag(payload);

        Assert.Equal(first, second);
        Assert.StartsWith("\"", first);
        Assert.EndsWith("\"", first);
    }

    [Fact]
    public void MatchesIfNoneMatch_WithExactMatch_ReturnsTrue()
    {
        var headers = new HeaderDictionary
        {
            ["If-None-Match"] = "\"ABC123\""
        };

        Assert.True(ImageConditionalRequestHelper.MatchesIfNoneMatch(headers, "\"ABC123\""));
    }

    [Fact]
    public void MatchesIfNoneMatch_WithMultipleValues_ReturnsTrueForMatchingEntry()
    {
        var headers = new HeaderDictionary
        {
            ["If-None-Match"] = "\"stale\", \"fresh\""
        };

        Assert.True(ImageConditionalRequestHelper.MatchesIfNoneMatch(headers, "\"fresh\""));
    }

    [Fact]
    public void MatchesIfNoneMatch_WithWildcard_ReturnsTrue()
    {
        var headers = new HeaderDictionary
        {
            ["If-None-Match"] = "*"
        };

        Assert.True(ImageConditionalRequestHelper.MatchesIfNoneMatch(headers, "\"anything\""));
    }

    [Fact]
    public void MatchesIfNoneMatch_WithoutMatch_ReturnsFalse()
    {
        var headers = new HeaderDictionary
        {
            ["If-None-Match"] = "\"ABC123\""
        };

        Assert.False(ImageConditionalRequestHelper.MatchesIfNoneMatch(headers, "\"XYZ789\""));
    }
}
