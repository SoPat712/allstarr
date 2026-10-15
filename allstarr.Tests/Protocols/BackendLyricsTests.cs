using System.Text;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Models.Lyrics;

namespace allstarr.Tests;

public sealed class BackendLyricsTests
{
    [Fact]
    public void Jellyfin_TimedLyricsPreserveMetadataFractionsZeroAndOffset()
    {
        var lyrics = ParseJellyfin("""
            {
              "Metadata": {
                "Artist": "Fixture Artist",
                "Album": "Fixture Album",
                "Title": "Fixture Title",
                "Length": 1230000000,
                "Offset": 12500000,
                "IsSynced": true
              },
              "Lyrics": [
                { "Text": "zero", "Start": 0, "Cues": [] },
                { "Text": "fraction", "Start": 12500000 },
                { "Text": "whole", "Start": 25000000 }
              ]
            }
            """);

        Assert.NotNull(lyrics);
        Assert.Equal("backend", lyrics.Source);
        Assert.Equal("Fixture Artist", lyrics.ArtistName);
        Assert.Equal("Fixture Album", lyrics.AlbumName);
        Assert.Equal("Fixture Title", lyrics.TrackName);
        Assert.Equal(123, lyrics.Duration);
        Assert.Null(lyrics.PlainLyrics);
        Assert.Equal(
            "[offset:1250]\n[00:00.000]zero\n[00:01.250]fraction\n[00:02.500]whole",
            lyrics.SyncedLyrics);
    }

    [Fact]
    public void Jellyfin_NullIsSyncedInfersTimedOnlyFromValidNonblankStarts()
    {
        var lyrics = ParseJellyfin("""
            {
              "Metadata": { "Title": "Fixture", "IsSynced": null },
              "Lyrics": [
                { "Text": " ", "Start": null },
                { "Text": "valid", "Start": 10000000 }
              ]
            }
            """);

        Assert.NotNull(lyrics);
        Assert.Equal("Fixture", lyrics.TrackName);
        Assert.Null(lyrics.PlainLyrics);
        Assert.Equal("[00:01.000]valid", lyrics.SyncedLyrics);
    }

    [Fact]
    public void Jellyfin_ExplicitFalseRemainsPlainWithoutInventingTimestamps()
    {
        var lyrics = ParseJellyfin("""
            {
              "Metadata": { "Artist": "Artist", "Title": "Plain", "IsSynced": false },
              "Lyrics": [
                { "Text": "first", "Start": 0 },
                { "Text": "second", "Start": 10000000 }
              ]
            }
            """);

        Assert.NotNull(lyrics);
        Assert.Equal("first\nsecond", lyrics.PlainLyrics);
        Assert.Null(lyrics.SyncedLyrics);
    }

    [Fact]
    public void Jellyfin_ExplicitTrueWithMissingStartIsUnavailable()
    {
        var lyrics = ParseJellyfin("""
            {
              "Metadata": { "Title": "Broken", "IsSynced": true },
              "Lyrics": [{ "Text": "missing start" }]
            }
            """);

        Assert.Null(lyrics);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Metadata\":{},\"Lyrics\":[]}")]
    [InlineData("{\"Metadata\":{},\"Lyrics\":\"wrong type\"}")]
    [InlineData("{\"Metadata\":{\"IsSynced\":true},\"Lyrics\":[{\"Text\":5,\"Start\":0}]}")]
    [InlineData("{\"Metadata\":{\"IsSynced\":\"yes\"},\"Lyrics\":[{\"Text\":\"bad\",\"Start\":0}]}")]
    public void Jellyfin_EmptyAndMalformedPayloadsReturnNull(string payload)
    {
        Assert.Null(ParseJellyfin(payload));
    }

    [Fact]
    public void SubsonicJson_TimedLyricsPreserveMetadataFractionsZeroAndOffset()
    {
        var lyrics = ParseSubsonic("""
            {
              "subsonic-response": {
                "status": "ok",
                "version": "1.16.1",
                "lyricsList": {
                  "structuredLyrics": [{
                    "displayArtist": "Fixture Artist",
                    "displayTitle": "Fixture Title",
                    "lang": "en",
                    "synced": true,
                    "offset": -1250,
                    "line": [
                      { "start": 0, "value": "zero" },
                      { "start": 1250, "value": "fraction" }
                    ]
                  }]
                }
              }
            }
            """, json: true);

        Assert.NotNull(lyrics);
        Assert.Equal("backend", lyrics.Source);
        Assert.Equal("Fixture Artist", lyrics.ArtistName);
        Assert.Equal("Fixture Title", lyrics.TrackName);
        Assert.Equal(
            "[offset:-1250]\n[00:00.000]zero\n[00:01.250]fraction",
            lyrics.SyncedLyrics);
        Assert.Null(lyrics.PlainLyrics);
    }

    [Fact]
    public void SubsonicJson_ChoosesFirstValidTimedEntryWithoutMergingLayers()
    {
        var lyrics = ParseSubsonic("""
            {"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[
              {"displayTitle":"first","synced":true,"line":[{"start":0,"value":"first"}]},
              {"displayTitle":"second","synced":true,"line":[{"start":1000,"value":"second"}]}
            ]}}}
            """, json: true);

        Assert.NotNull(lyrics);
        Assert.Equal("first", lyrics.TrackName);
        Assert.Equal("[00:00.000]first", lyrics.SyncedLyrics);
    }

    [Fact]
    public void SubsonicJson_SkipsMalformedTimedCandidateForAnotherValidTimedEntry()
    {
        var lyrics = ParseSubsonic("""
            {"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[
              {"displayTitle":"broken","synced":true,"line":[{"value":"missing start"}]},
              {"displayArtist":"Artist","displayTitle":"valid","synced":true,"line":[{"start":2500,"value":"valid"}]}
            ]}}}
            """, json: true);

        Assert.NotNull(lyrics);
        Assert.Equal("Artist", lyrics.ArtistName);
        Assert.Equal("valid", lyrics.TrackName);
        Assert.Equal("[00:02.500]valid", lyrics.SyncedLyrics);
    }

    [Fact]
    public void SubsonicJson_RetainsFirstValidPlainFallbackWhenTimedEntriesFail()
    {
        var lyrics = ParseSubsonic("""
            {"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[
              {"displayArtist":"Plain Artist","displayTitle":"Plain","synced":false,"line":[{"value":"plain one"},{"value":"plain two"}]},
              {"displayTitle":"broken timed","synced":true,"line":[{"value":"missing start"}]}
            ]}}}
            """, json: true);

        Assert.NotNull(lyrics);
        Assert.Equal("Plain Artist", lyrics.ArtistName);
        Assert.Equal("Plain", lyrics.TrackName);
        Assert.Equal("plain one\nplain two", lyrics.PlainLyrics);
        Assert.Null(lyrics.SyncedLyrics);
    }

    [Fact]
    public void SubsonicJson_PlainLyricsDoNotInventTimestamps()
    {
        var lyrics = ParseSubsonic("""
            {"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[
              {"displayTitle":"plain","synced":false,"line":[{"value":"first"},{"value":"second"}]}
            ]}}}
            """, json: true);

        Assert.NotNull(lyrics);
        Assert.Equal("first\nsecond", lyrics.PlainLyrics);
        Assert.Null(lyrics.SyncedLyrics);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("not-json")]
    [InlineData("{\"subsonic-response\":{\"status\":\"failed\"}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\"}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"lyricsList\":{\"structuredLyrics\":[{\"synced\":\"true\",\"line\":[]}]}}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"lyricsList\":{\"structuredLyrics\":[{\"synced\":true,\"line\":[{\"start\":-1,\"value\":\"negative\"}]}]}}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"lyricsList\":{\"structuredLyrics\":[{\"synced\":true,\"line\":[{\"start\":86400001,\"value\":\"too late\"}]}]}}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"lyricsList\":{\"structuredLyrics\":[{\"synced\":false,\"line\":[{\"value\":5}]}]}}}")]
    public void SubsonicJson_FailedMalformedAndOutOfRangePayloadsReturnNull(string payload)
    {
        Assert.Null(ParseSubsonic(payload, json: true));
    }

    [Fact]
    public void SubsonicJson_WhitespaceOnlyTextIsUnavailable()
    {
        var lyrics = ParseSubsonic("""
            {"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[
              {"synced":false,"line":[{"value":"  "},{"value":"\t"}]}
            ]}}}
            """, json: true);

        Assert.Null(lyrics);
    }

    [Fact]
    public void SubsonicXml_TimedLyricsPreserveMetadataFractionsZeroAndOffset()
    {
        var lyrics = ParseSubsonic("""
            <subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1">
              <lyricsList>
                <structuredLyrics displayArtist="Fixture Artist" displayTitle="Fixture Title" lang="en" synced="true" offset="-1250">
                  <line start="0">zero</line>
                  <line start="1250">fraction</line>
                </structuredLyrics>
              </lyricsList>
            </subsonic-response>
            """, json: false);

        Assert.NotNull(lyrics);
        Assert.Equal("Fixture Artist", lyrics.ArtistName);
        Assert.Equal("Fixture Title", lyrics.TrackName);
        Assert.Equal(
            "[offset:-1250]\n[00:00.000]zero\n[00:01.250]fraction",
            lyrics.SyncedLyrics);
    }

    [Fact]
    public void SubsonicXml_PlainLyricsDoNotInventTimestamps()
    {
        var lyrics = ParseSubsonic("""
            <subsonic-response status="ok">
              <lyricsList>
                <structuredLyrics displayArtist="Artist" displayTitle="Plain" synced="false">
                  <line>first</line><line>second</line>
                </structuredLyrics>
              </lyricsList>
            </subsonic-response>
            """, json: false);

        Assert.NotNull(lyrics);
        Assert.Equal("Artist", lyrics.ArtistName);
        Assert.Equal("Plain", lyrics.TrackName);
        Assert.Equal("first\nsecond", lyrics.PlainLyrics);
        Assert.Null(lyrics.SyncedLyrics);
    }

    [Fact]
    public void SubsonicXml_DtdAndExternalEntitiesAreRejected()
    {
        var body = """
            <?xml version="1.0"?>
            <!DOCTYPE subsonic-response [<!ENTITY unsafe SYSTEM "file:///etc/passwd">]>
            <subsonic-response status="ok"><lyricsList><structuredLyrics synced="false"><line>&unsafe;</line></structuredLyrics></lyricsList></subsonic-response>
            """;

        Assert.Null(ParseSubsonic(body, json: false));
    }

    [Theory]
    [InlineData("<subsonic-response status=\"failed\" />")]
    [InlineData("<subsonic-response status=\"ok\"><lyricsList></subsonic-response>")]
    [InlineData("<subsonic-response status=\"ok\"><lyricsList><structuredLyrics synced=\"true\"><line start=\"-1\">bad</line></structuredLyrics></lyricsList></subsonic-response>")]
    public void SubsonicXml_FailedMalformedAndOutOfRangePayloadsReturnNull(string payload)
    {
        Assert.Null(ParseSubsonic(payload, json: false));
    }

    [Fact]
    public void Subsonic_BodyIsBoundedAndDialectMustMatch()
    {
        var oversized = "{\"subsonic-response\":{\"status\":\"ok\",\"lyricsList\":{\"structuredLyrics\":[{\"synced\":false,\"line\":[{\"value\":\"" +
            new string('x', 2 * 1024 * 1024 + 100) + "\"}]}]}}}";

        Assert.Null(ParseSubsonic(oversized, json: true));
        Assert.Null(ParseSubsonic("<subsonic-response status=\"ok\" />", json: true));
        Assert.Null(ParseSubsonic("{\"subsonic-response\":{\"status\":\"ok\"}}", json: false));
    }

    [Theory]
    [InlineData('x', 2097153)]
    [InlineData('界', 700000)]
    public void JellyfinBodyIsBoundedByUtf8Bytes(char character, int count)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Metadata = new { IsSynced = false },
            Lyrics = new[] { new { Text = new string(character, count) } }
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Null(ParseJellyfin(payload));
    }

    private static LyricsInfo? ParseJellyfin(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return BackendLyrics.ParseJellyfin(document.RootElement);
    }

    private static LyricsInfo? ParseSubsonic(string payload, bool json) =>
        BackendLyrics.ParseSubsonic(Encoding.UTF8.GetBytes(payload), json);
}
