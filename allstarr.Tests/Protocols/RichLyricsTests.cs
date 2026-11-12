using System.Text.Json;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Tests;

public sealed class RichLyricsTests
{
    [Fact]
    public void CleanupPreservesNativeWordsAndNormalizesMixedWords()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo
        {
            PlainLyrics = "Привет мир\nНello   wοrld\n  It’s ‘our`\u200b\u200c\u200d\u2060\ufeff song  "
        })!;
        Assert.Equal(new[] { "Привет мир", "Hello world", " It's 'our' song " }, rich.Lines.Select(line => line.Text));
    }

    [Fact]
    public void CleanupDropsMarkersAndEdgeMusicWithoutMovingWordsOrLines()
    {
        var lyrics = new LyricsInfo
        {
            SyncedLyrics = "[00:00]♫ …\n[00:01][Verse 2]\n[00:02]Нello world\n[00:03](Hook)\n[00:04]♪♫…\n[00:05][Bridge: Artist]\n[00:06]Last\n[00:07]♪",
            TimedLines = [new("♫ …", TimeSpan.Zero), new("[Verse 2]", TimeSpan.FromSeconds(1)),
                new("Нello   world", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1),
                    [new("Н", TimeSpan.FromSeconds(2)), new("ello  ", TimeSpan.FromMilliseconds(2200)),
                     new(" world", TimeSpan.FromMilliseconds(2500))]),
                new("(Hook)", TimeSpan.FromSeconds(3)), new("♪♫…", TimeSpan.FromSeconds(4)),
                new("[Bridge: Artist]", TimeSpan.FromSeconds(5)), new("Last", TimeSpan.FromSeconds(6)),
                new("♪", TimeSpan.FromSeconds(7))]
        };
        var rich = RichLyricsMapper.Map(lyrics)!;
        Assert.Equal(new[] { "Hello world", "♪", "Last" }, rich.Lines.Select(line => line.Text));
        Assert.Equal(new long?[] { 2000, 4000, 6000 }, rich.Lines.Select(line => line.StartMs));
        Assert.Equal(new[] { "H", "ello ", "world" }, rich.Lines[0].Words.Select(word => word.Text));
        Assert.Equal(new long[] { 2000, 2200, 2500 }, rich.Lines[0].Words.Select(word => word.StartMs));
        Assert.Equal(3000, rich.Lines[0].EndMs);
        using var jellyfin = JsonDocument.Parse(new JellyfinLyricsProtocolAdapter().Shape(lyrics).Body);
        Assert.Equal(new[] { "Hello world", "♪", "Last" }, jellyfin.RootElement.GetProperty("Lyrics")
            .EnumerateArray().Select(line => line.GetProperty("Text").GetString()));
        Assert.Equal(new[] { 2000L, 4000L, 6000L }, SubsonicStructuredLyricsMapper.Map(new Song(), lyrics)!.Lines
            .Select(line => line.StartMilliseconds));
        Assert.Equal("Нello   world", lyrics.TimedLines[2].Text);
    }

    [Fact]
    public void SegmentGapsKeepNativeWordsSeparateFromLatinWords()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo
        {
            SyncedLyrics = "[00:01]Привет Hello",
            TimedLines = [new("Привет Hello", TimeSpan.FromSeconds(1), segments:
                [new("Привет", TimeSpan.FromSeconds(1)), new("Hello", TimeSpan.FromSeconds(2))])]
        })!;
        Assert.Equal(new[] { "Привет", "Hello" }, rich.Lines[0].Words.Select(word => word.Text));
    }

    [Fact]
    public void ConsecutiveMusicLinesCollapseAndKeepTheFirstTimestamp()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo
        {
            SyncedLyrics = "[00:00]♪\n[00:01]First\n[00:02]♫…\n[00:03]♪♪\n[00:04]Last\n[00:05]…"
        })!;
        Assert.Equal(new[] { "First", "♪", "Last" }, rich.Lines.Select(line => line.Text));
        Assert.Equal(new long?[] { 1000, 2000, 4000 }, rich.Lines.Select(line => line.StartMs));
    }

    [Fact]
    public void CleanupOfEnhancedLrcKeepsCuePositionsAfterRemovingInvisibleCharacters()
    {
        var lyrics = new LyricsInfo { SyncedLyrics = "[00:01][Chorus]\n[00:02]<00:02>Нe\u200bllo  <00:03>world\n[00:04](yeah)" };
        var rich = RichLyricsMapper.Map(lyrics)!;
        Assert.Equal(new[] { "Hello world", "(yeah)" }, rich.Lines.Select(line => line.Text));
        Assert.Equal(new RichLyricsWord(2000, 3000, "Hello "), rich.Lines[0].Words[0]);
        Assert.Equal(3000, rich.Lines[0].Words[1].StartMs);
    }

    [Fact]
    public void BackgroundWordsOverlapWithoutEnteringTheLeadLine()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo
        {
            Source = "lyricsplus",
            SyncedLyrics = "[00:01.000]Hello echo world",
            TimedLines = [new("Hello echo world", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
                [new("Hello ", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500)),
                 new("echo ", TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(500), true),
                 new("world", TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500))])]
        })!;
        Assert.Equal("word", rich.Format);
        Assert.Equal(new RichLyricsSource("lyricsplus", "LyricsPlus"), rich.Source);
        Assert.Collection(rich.Lines,
            line =>
            {
                Assert.Equal((1000L, 3000L, "Hello world", "lead"), (line.StartMs, line.EndMs, line.Text, line.Part));
                Assert.Equal(new[] { "Hello ", "world" }, line.Words.Select(word => word.Text));
                Assert.Equal(2500L, line.Words[1].EndMs);
            },
            line =>
            {
                Assert.Equal((1100L, 1600L, "echo", "background"), (line.StartMs, line.EndMs, line.Text, line.Part));
                Assert.Equal(new RichLyricsWord(1100, 1600, "echo "), Assert.Single(line.Words));
            });
    }

    [Fact]
    public void MissingLineDurationUsesItsOwnWordsBeforeLaterVocals()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo
        {
            Duration = 120,
            SyncedLyrics = "[00:01.000]Lead echo\n[00:04.000]Next",
            TimedLines = [new("Lead echo", TimeSpan.FromSeconds(1), segments:
                [new("Lead", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500)),
                 new("echo ", TimeSpan.FromMilliseconds(1200), TimeSpan.FromSeconds(2), true),
                 new("yes", TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(500), true)]),
                new("Next", TimeSpan.FromSeconds(4))]
        })!;
        Assert.Equal(1500L, rich.Lines[0].EndMs);
        Assert.Equal("Lead", rich.Lines[0].Text);
        Assert.Equal(3200L, rich.Lines[1].EndMs);
        Assert.Equal("background", rich.Lines[1].Part);
        Assert.Equal(new[] { 3200L, 2000L }, rich.Lines[1].Words.Select(word => word.EndMs!.Value));
    }

    [Fact]
    public void MissingEndsUseTheNextWordThenTheLineEnd()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo
        {
            SyncedLyrics = "[00:01.000]First second\n[00:04.000]Next",
            TimedLines = [new("First second", TimeSpan.FromSeconds(1), segments:
                [new("First ", TimeSpan.FromSeconds(1)), new("second", TimeSpan.FromSeconds(2))]),
                new("Next", TimeSpan.FromSeconds(4))]
        })!;
        Assert.Equal(2000L, rich.Lines[0].Words[0].EndMs);
        Assert.Equal(4000L, rich.Lines[0].Words[1].EndMs);
        Assert.Null(rich.Lines[1].EndMs);
    }

    [Fact]
    public void LineLrcKeepsMillisecondsAndEmptyWords()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo { Source = "lrclib", SyncedLyrics = "[00:01.234]First\n[00:02.500]Last" })!;
        Assert.Equal("line", rich.Format);
        Assert.Equal(new[] { 1234L, 2500L }, rich.Lines.Select(line => line.StartMs!.Value));
        Assert.All(rich.Lines, line => Assert.Empty(line.Words));
        Assert.Null(rich.Lines[1].EndMs);
    }

    [Fact]
    public void PlainTextHasOneUntimedLinePerTextLine()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo { Source = "kept", PlainLyrics = "First\r\n\r\nLast" })!;
        Assert.Equal("plain", rich.Format);
        Assert.Equal(new[] { "First", "", "Last" }, rich.Lines.Select(line => line.Text));
        Assert.All(rich.Lines, line =>
        {
            Assert.Null(line.StartMs);
            Assert.Null(line.EndMs);
            Assert.Empty(line.Words);
        });
        var json = JsonSerializer.Serialize(rich);
        Assert.Contains("\"startMs\":null", json);
        Assert.DoesNotContain("voice", json);
        Assert.DoesNotContain("singer", json);
        Assert.Contains("\"source\":{\"id\":\"kept\",\"name\":\"Kept lyrics\"}", json);
    }

    [Fact]
    public void EmptyLyricsReturnNull()
    {
        Assert.Null(RichLyricsMapper.Map(null));
        Assert.Null(RichLyricsMapper.Map(new LyricsInfo { PlainLyrics = "  " }));
    }

    [Fact]
    public void EnhancedLrcRetainsWordsForOlderCachedLyrics()
    {
        var rich = RichLyricsMapper.Map(new LyricsInfo { SyncedLyrics = "[00:01.000]<00:01.000>Hello <00:01.500>world<00:02.000>" })!;
        Assert.Equal("word", rich.Format);
        Assert.Equal("Hello world", Assert.Single(rich.Lines).Text);
        Assert.Equal(new RichLyricsWord(1500, 2000, "world"), rich.Lines[0].Words[1]);
    }

    [Fact]
    public void WordFixtureKeepsExactProtocolBodies()
    {
        var lyrics = new LyricsInfo
        {
            Source = "spicylyrics",
            ArtistName = "Artist",
            AlbumName = "Album",
            TrackName = "Fixture",
            Duration = 120,
            SyncedLyrics = "[00:01.000]<00:01.000>Hello <00:01.500>world<00:02.000>",
            TimedLines = [new("Hello world", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
                [new("Hello ", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500)),
                 new("world", TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(500))], voice: "v2", singer: "Performer")]
        };
        _ = RichLyricsMapper.Map(lyrics);
        Assert.Equal("""{"Metadata":{"Artist":"Artist","Album":"Album","Title":"Fixture","Length":1200000000,"IsSynced":true},"Lyrics":[{"Text":"Hello world","Start":10000000,"Cues":[{"Position":0,"EndPosition":6,"Start":10000000,"End":15000000},{"Position":6,"EndPosition":11,"Start":15000000,"End":20000000}]}]}""",
            new JellyfinLyricsProtocolAdapter().Shape(lyrics).Body);
        var subsonic = SubsonicStructuredLyricsMapper.Map(new Song { Artist = "Artist", Title = "Fixture" }, lyrics);
        var response = new SubsonicResponseBuilder().CreateLyricsBySongIdResponse("json", subsonic);
        Assert.Equal("""{"subsonic-response":{"status":"ok","version":"1.16.1","lyricsList":{"structuredLyrics":[{"displayArtist":"Artist","displayTitle":"Fixture","lang":"xxx","offset":0,"synced":true,"line":[{"start":1000,"value":"Hello world"}]}]}}}""",
            JsonSerializer.Serialize(Assert.IsType<JsonResult>(response).Value));
        var xml = Assert.IsType<ContentResult>(new SubsonicResponseBuilder().CreateLyricsBySongIdResponse("xml", subsonic));
        Assert.Equal("""
            <subsonic-response status="ok" version="1.16.1" xmlns="http://subsonic.org/restapi">
              <lyricsList>
                <structuredLyrics displayArtist="Artist" displayTitle="Fixture" lang="xxx" offset="0" synced="true">
                  <line start="1000">Hello world</line>
                </structuredLyrics>
              </lyricsList>
            </subsonic-response>
            """, xml.Content);
    }
}
