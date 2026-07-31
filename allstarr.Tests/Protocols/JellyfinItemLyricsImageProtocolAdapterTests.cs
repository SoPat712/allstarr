using System.Text.Json;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Models.Lyrics;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class JellyfinItemLyricsImageProtocolAdapterTests
{
    [Fact]
    public void InteractionAdapter_ShapesFavoriteCapabilitiesAndInstantMixResponses()
    {
        var adapter = new JellyfinInteractionProtocolAdapter();

        var favorite = adapter.ShapeFavorite("fixture-item", true);
        var mix = adapter.ShapeInstantMix([
            new Dictionary<string, object?> { ["Id"] = "mix-1" }
        ]);

        Assert.Equal("{\"IsFavorite\":true,\"ItemId\":\"fixture-item\",\"Key\":\"fixture-item\"}", favorite.Body);
        Assert.Equal(StatusCodes.Status204NoContent, adapter.ShapeCapabilitiesStatus(StatusCodes.Status200OK));
        Assert.Equal(StatusCodes.Status401Unauthorized, adapter.ShapeCapabilitiesStatus(StatusCodes.Status401Unauthorized));
        Assert.Equal(
            "{\"Items\":[{\"Id\":\"mix-1\"}],\"TotalRecordCount\":1,\"StartIndex\":0}",
            mix.Body);
        Assert.False(adapter.CanRunOptionalUserWork(null));
    }

    [Fact]
    public void LyricsAdapter_PreservesPascalCaseSyncedResponseAndCurrentTickConversion()
    {
        var adapter = new JellyfinLyricsProtocolAdapter();

        var response = adapter.Shape(new LyricsInfo
        {
            ArtistName = "Fixture Artist",
            AlbumName = "Fixture Album",
            TrackName = "Fixture Song",
            Duration = 123,
            SyncedLyrics = "[ar:Fixture Artist]\n[00:01.25]first\n[00:02.125]second"
        });

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        Assert.True(root.GetProperty("Metadata").GetProperty("IsSynced").GetBoolean());
        Assert.Equal("first", root.GetProperty("Lyrics")[0].GetProperty("Text").GetString());
        Assert.Equal(12_500_000, root.GetProperty("Lyrics")[0].GetProperty("Start").GetInt64());
        Assert.Equal(32_500_000, root.GetProperty("Lyrics")[1].GetProperty("Start").GetInt64());
    }

    [Fact]
    public void LyricsAdapter_PlainResponseDoesNotInventStartTimestamps()
    {
        var adapter = new JellyfinLyricsProtocolAdapter();

        var response = adapter.Shape(new LyricsInfo
        {
            TrackName = "Fixture Song",
            PlainLyrics = " first \r\nsecond"
        });

        using var document = JsonDocument.Parse(response.Body);
        var lines = document.RootElement.GetProperty("Lyrics");
        Assert.Equal(["first", "second"], lines.EnumerateArray()
            .Select(line => line.GetProperty("Text").GetString()!).ToArray());
        Assert.All(lines.EnumerateArray(), line => Assert.False(line.TryGetProperty("Start", out _)));
    }

    [Fact]
    public void ImageAdapter_ReturnsBodyAndStableEtagThenHonorsConditionalRequest()
    {
        var adapter = new JellyfinImageProtocolAdapter();
        var bytes = new byte[] { 1, 2, 3 };
        var headers = new HeaderDictionary();

        var first = adapter.Shape(bytes, "image/jpeg", headers);
        headers["If-None-Match"] = first.ETag;
        var conditional = adapter.Shape(bytes, "image/jpeg", headers);

        Assert.Equal(StatusCodes.Status200OK, first.StatusCode);
        Assert.Equal(bytes, first.Body);
        Assert.Equal(StatusCodes.Status304NotModified, conditional.StatusCode);
        Assert.Null(conditional.Body);
        Assert.Equal(first.ETag, conditional.ETag);
    }
}
