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
    public void LyricsAdapter_PreservesPascalCaseSyncedResponseAndReadsFractionsAsWritten()
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
        Assert.Equal(1_230_000_000L, root.GetProperty("Metadata").GetProperty("Length").GetInt64());
        Assert.Equal("first", root.GetProperty("Lyrics")[0].GetProperty("Text").GetString());
        Assert.Equal(12_500_000, root.GetProperty("Lyrics")[0].GetProperty("Start").GetInt64());
        // ".125" is 125 ms; it used to be read as hundredths and land a second late.
        Assert.Equal(21_250_000, root.GetProperty("Lyrics")[1].GetProperty("Start").GetInt64());
    }

    [Fact]
    public void LyricsAdapter_EnhancedTextKeepsSpacesAndUsesCharacterRangesForCues()
    {
        var response = new JellyfinLyricsProtocolAdapter().Shape(new LyricsInfo
        {
            SyncedLyrics = "[offset:100]\n[00:01.00]<00:01.00>t'he 'a <00:02.125>last<00:03.00>\n[00:04.00]next"
        });

        using var document = JsonDocument.Parse(response.Body);
        var line = document.RootElement.GetProperty("Lyrics")[0];
        Assert.Equal("t'he 'a last", line.GetProperty("Text").GetString());
        Assert.Equal(9_000_000L, line.GetProperty("Start").GetInt64());
        var cues = line.GetProperty("Cues");
        Assert.Equal(2, cues.GetArrayLength());
        Assert.Equal(0, cues[0].GetProperty("Position").GetInt32());
        Assert.Equal(8, cues[0].GetProperty("EndPosition").GetInt32());
        Assert.Equal(9_000_000L, cues[0].GetProperty("Start").GetInt64());
        Assert.Equal(20_250_000L, cues[0].GetProperty("End").GetInt64());
        Assert.Equal(8, cues[1].GetProperty("Position").GetInt32());
        Assert.Equal(12, cues[1].GetProperty("EndPosition").GetInt32());
        Assert.Equal(29_000_000L, cues[1].GetProperty("End").GetInt64());
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\u2800")]
    public void LyricsAdapter_ConvertedSidecarsReturnOrdinarySpacesAndKeepJoinedSyllables(string gap)
    {
        var response = new JellyfinLyricsProtocolAdapter().Shape(new LyricsInfo
        {
            SyncedLyrics = $"[00:01.00][00:05.00]<00:01.00>😀one{gap}<00:02.00>to<00:02.50>gether\n[00:08.00]next"
        });

        using var document = JsonDocument.Parse(response.Body);
        var lines = document.RootElement.GetProperty("Lyrics");
        Assert.Equal("😀one together", lines[0].GetProperty("Text").GetString());
        Assert.DoesNotContain("\u2800", lines[0].GetProperty("Text").GetString()!);
        Assert.Equal(6, lines[0].GetProperty("Cues")[0].GetProperty("EndPosition").GetInt32());
        Assert.Equal(60_000_000L, lines[1].GetProperty("Cues")[1].GetProperty("Start").GetInt64());
        Assert.Equal(80_000_000L, lines[1].GetProperty("Cues")[2].GetProperty("End").GetInt64());
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
