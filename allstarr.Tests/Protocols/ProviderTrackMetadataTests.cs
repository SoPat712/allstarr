using System.Text.Json;
using System.Xml.Linq;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class ProviderTrackMetadataTests
{
    [Fact]
    public void DescriptiveFieldsAndSuppliedZeroGainSurviveBothProtocols()
    {
        var song = Track();
        song.Upc = "123456789012";
        song.Label = "Example Records";
        song.Copyright = "2020 Example";
        song.Composer = "Composer";
        song.Contributors = ["producer: Producer", "lyricist: Lyricist"];
        song.Genres = ["Pop", "Electronic"];
        song.ReplayGain = new() { TrackGain = 0, AlbumGain = -6.5, TrackPeak = 0.98 };
        var item = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<JsonResult>(new JellyfinResponseBuilder().CreateSongResponse(song)).Value);
        Assert.Equal(0d, item["NormalizationGain"]);
        Assert.Equal(new[] { "Pop", "Electronic" }, Assert.IsType<string[]>(item["Genres"]));
        Assert.Equal("123456789012", Assert.IsType<Dictionary<string, string>>(item["ProviderIds"])["UPC"]);
        Assert.Equal(3, Assert.IsType<object[]>(item["People"]).Length);
        var subsonic = new SubsonicResponseBuilder();
        var json = subsonic.ConvertSongToJson(song);
        Assert.Equal("123456789012", json["upc"]);
        Assert.Equal(0d, Assert.IsType<Dictionary<string, double>>(json["replayGain"])["trackGain"]);
        var xml = subsonic.ConvertSongToXml(song, XNamespace.None);
        Assert.Equal("-6.5", xml.Element("replayGain")!.Attribute("albumGain")!.Value);
        Assert.Equal(new[] { "composer", "producer", "lyricist" }, xml.Elements("contributors").Select(element => element.Attribute("role")!.Value));
    }

    [Fact]
    public void UnknownTrackFactsAreAbsentInBothProtocols()
    {
        var song = Track();
        var jellyfin = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<JsonResult>(new JellyfinResponseBuilder().CreateSongResponse(song)).Value);
        foreach (var key in new[] { "NormalizationGain", "PremiereDate", "ProductionYear", "Size", "Bitrate" })
            Assert.False(jellyfin.ContainsKey(key), key);
        var subsonic = new SubsonicResponseBuilder();
        var json = subsonic.ConvertSongToJson(song);
        var xml = subsonic.ConvertSongToXml(song, XNamespace.None);
        foreach (var key in new[] { "year", "bitRate", "size", "samplingRate", "bitDepth", "channelCount", "replayGain" })
        {
            Assert.False(json.ContainsKey(key), key);
            Assert.Null(xml.Attribute(key));
            Assert.Null(xml.Element(key));
        }
    }

    [Theory]
    [InlineData("artistgrid")]
    [InlineData("apple")]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    [InlineData("spotify")]
    [InlineData("monochrome")]
    [InlineData("spotiflac-amazon")]
    [InlineData("spotiflac-tidal-web")]
    [InlineData("spotiflac-apple-music")]
    [InlineData("spotiflac-deezer")]
    [InlineData("spotiflac-qobuz-web")]
    [InlineData("spotiflac-soundcloud")]
    [InlineData("spotiflac-jiosaavn")]
    [InlineData("spotiflac-youtube-music")]
    [InlineData("spotiflac-spotify-web")]
    [InlineData("spotiflac-melon")]
    [InlineData("spotiflac-bugs")]
    [InlineData("spotiflac-genie")]
    public void KnownMediaFactsReachBothProtocolSongShapes(string provider)
    {
        var song = Track();
        song.ExternalProvider = provider;
        song.Container = "wav";
        song.Codec = "pcm_s24le";
        song.SampleRate = 44100;
        song.BitDepth = 24;
        song.Channels = 2;
        song.Bitrate = 2116800;
        song.Size = 30645098;
        song.ReleaseDate = "2020-07-17";
        song.Year = 2020;
        var jellyfin = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<JsonResult>(new JellyfinResponseBuilder().CreateSongResponse(song)).Value);
        Assert.Equal("wav", jellyfin["Container"]);
        Assert.Equal(30645098L, jellyfin["Size"]);
        Assert.Equal(2116800, jellyfin["Bitrate"]);
        var subsonic = new SubsonicResponseBuilder();
        var json = subsonic.ConvertSongToJson(song);
        var xml = subsonic.ConvertSongToXml(song, XNamespace.None);
        foreach (var (key, expected) in new (string, object)[]
        {
            ("suffix", "wav"), ("contentType", "audio/wav"), ("bitRate", 2116),
            ("size", 30645098L), ("samplingRate", 44100), ("bitDepth", 24), ("channelCount", 2), ("year", 2020)
        })
        {
            Assert.Equal(expected, json[key]);
            Assert.Equal(Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture), xml.Attribute(key)?.Value);
        }
    }

    private static Song Track() => new()
    {
        Id = "ext-artistgrid-song-example",
        ExternalProvider = "artistgrid",
        ExternalId = "example",
        Title = "Communicate",
        Artist = "Ariana Grande",
        Album = "Positions",
        Duration = 115
    };
}
