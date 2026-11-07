using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Providers.Lyrics;
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

        Assert.Equal("""{"IsFavorite":true,"ItemId":"fixture-item","Key":"fixture-item","PlaybackPositionTicks":0,"PlayCount":0,"Played":false}""", favorite.Body);
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
            SyncedLyrics = "[ar:Fixture Artist]\n[00:01.25]first\n[00:02.125]second",
            TimedLines = [new("Structured line without words", TimeSpan.FromSeconds(10))]
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
        Assert.All(root.GetProperty("Lyrics").EnumerateArray(), line => Assert.Empty(line.GetProperty("Cues").EnumerateArray()));
    }

    [Fact]
    public void LyricsAdapter_PrefersStructuredWordsAndIndexesTheEmittedTextInUtf16()
    {
        var response = new JellyfinLyricsProtocolAdapter().Shape(new LyricsInfo
        {
            SyncedLyrics = "[00:01.000]old text",
            TimedLines = [new("  café 😀 世界  ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3),
                [new("  café ", TimeSpan.FromTicks(10_001_234), TimeSpan.FromMilliseconds(400)),
                 new("😀 ", TimeSpan.FromSeconds(2)), new("世界  ", TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500))])]
        });
        using var document = JsonDocument.Parse(response.Body);
        var line = Assert.Single(document.RootElement.GetProperty("Lyrics").EnumerateArray());
        var text = line.GetProperty("Text").GetString()!;
        Assert.Equal("  café 😀 世界  ", text);
        var cues = line.GetProperty("Cues");
        Assert.Equal(3, cues.GetArrayLength());
        Assert.Equal("  café ", text[cues[0].GetProperty("Position").GetInt32()..cues[0].GetProperty("EndPosition").GetInt32()]);
        Assert.Equal(10_001_234, cues[0].GetProperty("Start").GetInt64());
        Assert.Equal(14_001_234, cues[0].GetProperty("End").GetInt64());
        Assert.Equal(7, cues[1].GetProperty("Position").GetInt32());
        Assert.Equal(10, cues[1].GetProperty("EndPosition").GetInt32());
        Assert.Equal("😀 ", text[7..10]);
        Assert.Equal(30_000_000, cues[1].GetProperty("End").GetInt64());
        Assert.Equal(35_000_000, cues[2].GetProperty("End").GetInt64());
    }

    [Theory]
    [InlineData("apple")]
    [InlineData("amll")]
    public void LyricsAdapter_TimedTextReturnsSeparateVocalLines(string source)
    {
        const string xml = """<tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div><p begin="1s" end="4s"><span begin="1s" end="2s">café </span><span ttm:role="x-bg" begin="1.5s" end="2.5s">echo </span><span begin="2s" end="3s">世界</span></p><p begin="5s" end="6s">next line</p></div></body></tt>""";
        var parsed = TtmlLyricsParser.Parse(xml, source).RequireValue();
        var lyrics = LyricsVersions.Map(parsed, source, 6);
        lyrics.SyncedLyrics = xml;
        var response = new JellyfinLyricsProtocolAdapter().Shape(lyrics);
        using var document = JsonDocument.Parse(response.Body);
        var lines = document.RootElement.GetProperty("Lyrics");
        Assert.Equal(3, lines.GetArrayLength());
        Assert.Equal("café 世界", lines[0].GetProperty("Text").GetString());
        Assert.Equal(2, lines[0].GetProperty("Cues").GetArrayLength());
        Assert.Equal(5, lines[0].GetProperty("Cues")[1].GetProperty("Position").GetInt32());
        Assert.Equal("echo", lines[1].GetProperty("Text").GetString());
        Assert.Equal(15_000_000, lines[1].GetProperty("Start").GetInt64());
        var backgroundCue = Assert.Single(lines[1].GetProperty("Cues").EnumerateArray());
        Assert.Equal(0, backgroundCue.GetProperty("Position").GetInt32());
        Assert.Equal(4, backgroundCue.GetProperty("EndPosition").GetInt32());
        Assert.Equal(15_000_000, backgroundCue.GetProperty("Start").GetInt64());
        Assert.Equal(25_000_000, backgroundCue.GetProperty("End").GetInt64());
        Assert.Equal("next line", lines[2].GetProperty("Text").GetString());
        Assert.Empty(lines[2].GetProperty("Cues").EnumerateArray());
        Assert.DoesNotContain("<tt", response.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LyricsAdapter_PreservesBackgroundLinesAndIndexesEachLinesWordCues(bool vocalParts)
    {
        var lead = new ProviderLyricsTimedLine("café 世界", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
            [new("café ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
             new("世界", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))]);
        var background = new ProviderLyricsTimedLine("oh yes", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.5),
            [new("oh ", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.25)),
             new("yes", TimeSpan.FromSeconds(1.75), TimeSpan.FromSeconds(0.25))], isBackground: true);
        var response = new JellyfinLyricsProtocolAdapter().Shape(new LyricsInfo
        {
            SyncedLyrics = "[00:01.000]café oh yes 世界",
            TimedLines = [new("café oh yes 世界", lead.Start, lead.Duration,
                [lead.Segments![0],
                 new("oh ", background.Start, TimeSpan.FromSeconds(0.25), true),
                 new("yes ", TimeSpan.FromSeconds(1.75), TimeSpan.FromSeconds(0.25), true),
                 lead.Segments[1]], vocalParts: vocalParts ? [background, lead] : null)]
        });
        using var document = JsonDocument.Parse(response.Body);
        var lines = document.RootElement.GetProperty("Lyrics");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("café 世界", lines[0].GetProperty("Text").GetString());
        Assert.Equal("oh yes", lines[1].GetProperty("Text").GetString());
        Assert.Equal(10_000_000, lines[0].GetProperty("Start").GetInt64());
        Assert.Equal(15_000_000, lines[1].GetProperty("Start").GetInt64());
        var expectedWords = new[] { new[] { "café ", "世界" }, new[] { "oh ", "yes" } };
        var expectedStarts = new[] { new[] { 10_000_000L, 20_000_000L }, new[] { 15_000_000L, 17_500_000L } };
        var expectedEnds = new[] { new[] { 20_000_000L, 30_000_000L }, new[] { 17_500_000L, 20_000_000L } };
        for (var index = 0; index < lines.GetArrayLength(); index++)
        {
            var text = lines[index].GetProperty("Text").GetString()!;
            var cues = lines[index].GetProperty("Cues");
            Assert.Equal(2, cues.GetArrayLength());
            for (var word = 0; word < cues.GetArrayLength(); word++)
            {
                var cue = cues[word];
                Assert.Equal(expectedWords[index][word], text[cue.GetProperty("Position").GetInt32()..cue.GetProperty("EndPosition").GetInt32()]);
                Assert.Equal(expectedStarts[index][word], cue.GetProperty("Start").GetInt64());
                Assert.Equal(expectedEnds[index][word], cue.GetProperty("End").GetInt64());
            }
        }
    }

    [Fact]
    public void LyricsAdapter_InfersWordEndsWithinEachVocalPart()
    {
        var lyrics = new LyricsInfo
        {
            SyncedLyrics = "[00:01.000]first words",
            TimedLines = [new("first words", TimeSpan.FromSeconds(1), segments:
                    [new("first ", TimeSpan.FromSeconds(1)), new("words", TimeSpan.FromSeconds(2))]),
                new("echo", TimeSpan.FromSeconds(1.5), segments: [new("echo", TimeSpan.FromSeconds(1.5))], isBackground: true),
                new("again", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1),
                    [new("again", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1))], isBackground: true),
                new("next", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1),
                    [new("next", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1))])]
        };
        using var document = JsonDocument.Parse(new JellyfinLyricsProtocolAdapter().Shape(lyrics).Body);
        var lines = document.RootElement.GetProperty("Lyrics");
        Assert.Equal(4, lines.GetArrayLength());
        Assert.Equal(50_000_000, lines[0].GetProperty("Cues")[1].GetProperty("End").GetInt64());
        Assert.Equal(30_000_000, lines[1].GetProperty("Cues")[0].GetProperty("End").GetInt64());
        Assert.Equal("[00:01.000]<00:01.000>first <00:02.000>words<00:05.000>\n[00:01.500]<00:01.500>echo<00:03.000>\n[00:03.000]<00:03.000>again<00:04.000>\n[00:05.000]<00:05.000>next<00:06.000>",
            LyricsTiming.SidecarContent(lyrics));
    }

    [Fact]
    public void LyricsAdapter_ParsesStoredTimedTextWithoutStructuredLines()
    {
        var response = new JellyfinLyricsProtocolAdapter().Shape(new LyricsInfo
        {
            SyncedLyrics = """<tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="1s" end="3s"><span begin="1s" end="2s">café </span><span begin="2s" end="3s">世界</span></p></div></body></tt>"""
        });
        using var document = JsonDocument.Parse(response.Body);
        var line = Assert.Single(document.RootElement.GetProperty("Lyrics").EnumerateArray());
        Assert.Equal("café 世界", line.GetProperty("Text").GetString());
        Assert.Equal(2, line.GetProperty("Cues").GetArrayLength());
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
