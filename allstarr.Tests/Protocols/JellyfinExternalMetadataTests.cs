using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed class JellyfinExternalMetadataTests
{
    [Theory]
    [InlineData("2024-02-29", null, "2024-02-29T00:00:00.0000000Z", 2024)]
    [InlineData("2024-02-29T18:23:45Z", null, "2024-02-29T00:00:00.0000000Z", 2024)]
    [InlineData("2024-02-29T23:23:45.123-05:00", null, "2024-02-29T00:00:00.0000000Z", 2024)]
    [InlineData("2024-07", null, "2024-07-01T00:00:00.0000000Z", 2024)]
    [InlineData("2024", null, "2024-01-01T00:00:00.0000000Z", 2024)]
    [InlineData("invalid", 2020, "2020-01-01T00:00:00.0000000Z", 2020)]
    [InlineData("2024-02-30", null, null, null)]
    [InlineData(null, null, null, null)]
    public void ReleaseMetadata_UsesProviderDateOnListsAndSingleItems(string? date, int? year, string? premiere, int? production)
    {
        var builder = new JellyfinResponseBuilder();
        var song = Track();
        song.ReleaseDate = date;
        song.Year = year;
        var album = new Album { Id = "ext-deezer-album-3", ReleaseDate = date, Year = year, ExternalProvider = "deezer" };
        var items = new[]
        {
            builder.ConvertSongToJellyfinItem(song), builder.ConvertAlbumToJellyfinItem(album),
            Item(builder.CreateSongResponse(song)), Item(builder.CreateAlbumResponse(album))
        };
        foreach (var item in items)
        {
            Assert.Equal(premiere, item.GetValueOrDefault("PremiereDate"));
            Assert.Equal(production, item.GetValueOrDefault("ProductionYear"));
            Assert.Equal(premiere != null, item.ContainsKey("PremiereDate"));
            Assert.Equal(production != null, item.ContainsKey("ProductionYear"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownFileFacts_AreEmittedOnListsSingleItemsAndPlaybackInfo(bool known)
    {
        var song = Track();
        if (known)
        {
            song.Size = 12_000_000;
            song.Bitrate = 960_000;
            song.Codec = "flac";
            song.SampleRate = 96_000;
            song.BitDepth = 24;
            song.Channels = 2;
        }
        var http = new DefaultHttpContext();
        http.Request.Path = "/Items";
        http.Request.QueryString = new("?Fields=MediaSources,MediaStreams,ParentId");
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http });
        var list = builder.ConvertSongToJellyfinItem(song);
        Assert.Equal(song.AlbumId, list["AlbumId"]);
        Assert.Equal(song.AlbumId, list["ParentId"]);
        Assert.Equal("Artist [D]", Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(list["AlbumArtists"]))["Name"]);
        foreach (var sources in new[] { list["MediaSources"], Item(builder.CreateSongResponse(song))["MediaSources"], builder.CreatePlaybackMediaSources(song) })
        {
            var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(sources));
            var stream = AudioStream(source);
            Assert.Equal(known ? 12_000_000L : null, source.GetValueOrDefault("Size"));
            Assert.Equal(known ? 960_000 : null, source.GetValueOrDefault("Bitrate"));
            foreach (var (key, value) in new[] { ("BitRate", 960_000), ("SampleRate", 96_000), ("BitDepth", 24), ("Channels", 2) })
            {
                Assert.Equal(known ? value : null, stream.GetValueOrDefault(key));
                Assert.Equal(known, stream.ContainsKey(key));
            }
            Assert.Equal(known, source.ContainsKey("Size"));
            Assert.Equal(known, source.ContainsKey("Bitrate"));
        }
        http.Request.QueryString = QueryString.Empty;
        var minimal = builder.ConvertSongToJellyfinItem(song);
        Assert.False(minimal.ContainsKey("MediaSources"));
        Assert.False(minimal.ContainsKey("MediaStreams"));
        Assert.False(minimal.ContainsKey("ParentId"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lists_ReadOnlyPreviousStreamFactsForTheSameViewerAndBackend(bool matchingBackend)
    {
        var user = Guid.NewGuid();
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal",
            new AllstarrPrincipal(user, "jellyfin", "backend", "principal", "User", false),
            "test", DateTimeOffset.UtcNow.AddMinutes(1), default, new ProtocolClientDescriptor("client", "device"));
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = context;
        http.Request.Path = "/Items";
        http.Request.QueryString = new("?Fields=MediaSources");
        var activity = new Mock<IPlaybackDeliveryActivitySource>(MockBehavior.Strict);
        activity.Setup(value => value.StreamFor(user, "device", "ext-deezer-song-1")).Returns(new PlaybackStreamSource(
            "deezer", "1", null, "jellyfin", matchingBackend ? "backend" : "other", true, DateTimeOffset.UtcNow, "cached")
        { Size = 12_000_000, Media = new("audio/flac", "flac", "flac", 960_000, 96_000, 24, 2) });
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http }, playbackActivity: activity.Object);
        var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(builder.ConvertSongToJellyfinItem(Track())["MediaSources"]));
        Assert.Equal(matchingBackend ? 12_000_000L : null, source.GetValueOrDefault("Size"));
        Assert.Equal(matchingBackend ? 960_000 : null, source.GetValueOrDefault("Bitrate"));
        var stream = AudioStream(source);
        foreach (var (key, value) in new[] { ("BitRate", 960_000), ("SampleRate", 96_000), ("BitDepth", 24), ("Channels", 2) })
        {
            Assert.Equal(matchingBackend ? value : null, stream.GetValueOrDefault(key));
            Assert.Equal(matchingBackend, stream.ContainsKey(key));
        }
        Assert.Equal(matchingBackend ? "flac" : null, stream.GetValueOrDefault("Codec"));
        activity.VerifyAll();
    }

    [Fact]
    public void ExactLengthAndDuration_ProvideAverageBitrateWithoutInventingAudioProperties()
    {
        var song = Track();
        song.Size = 12_000_000;
        var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(new JellyfinResponseBuilder().CreatePlaybackMediaSources(song)));
        Assert.Equal(960_000, source["Bitrate"]);
        var stream = AudioStream(source);
        Assert.False(stream.ContainsKey("BitRate"));
        Assert.False(stream.ContainsKey("SampleRate"));
        Assert.False(stream.ContainsKey("BitDepth"));
        Assert.False(stream.ContainsKey("Channels"));
        Assert.False(stream.ContainsKey("Codec"));
    }

    [Fact]
    public void ExactLengthWithoutDuration_DoesNotInventBitrate()
    {
        var song = Track();
        song.Size = 12_000_000;
        song.Duration = null;
        var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(new JellyfinResponseBuilder().CreatePlaybackMediaSources(song)));
        Assert.Equal(12_000_000L, source["Size"]);
        Assert.False(source.ContainsKey("Bitrate"));
    }

    [Fact]
    public void ProviderAlbumMapping_PreservesReleaseDate()
    {
        var metadata = new ProviderAlbumMetadata(new("deezer", ProviderResourceKind.Album, "3"), "Album",
            [new("Artist")], releaseDate: "2024-02-29");
        var album = ProtocolProviderGateway.Map(metadata);
        Assert.Equal("2024-02-29", album.ReleaseDate);
        var item = new JellyfinResponseBuilder().ConvertAlbumToJellyfinItem(album);
        Assert.Equal(2024, item["ProductionYear"]);
    }

    private static Dictionary<string, object?> AudioStream(Dictionary<string, object?> source)
    {
        var streams = Assert.IsType<Dictionary<string, object?>[]>(source["MediaStreams"]);
        Assert.Collection(streams,
            audio =>
            {
                Assert.Equal("Audio", audio["Type"]);
                Assert.Equal(0, audio["Index"]);
            },
            lyric =>
            {
                Assert.Equal("Lyric", lyric["Type"]);
                Assert.Equal(1, lyric["Index"]);
                Assert.True(Assert.IsType<bool>(lyric["IsExternal"]));
                foreach (var key in new[] { "Size", "BitRate", "SampleRate", "BitDepth", "Channels", "Codec", "TimeBase" })
                    Assert.False(lyric.ContainsKey(key));
            });
        Assert.Equal(0, source["DefaultAudioStreamIndex"]);
        return streams[0];
    }

    private static Dictionary<string, object?> Item(IActionResult result) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<JsonResult>(result).Value);

    private static Song Track() => new()
    {
        Id = "ext-deezer-song-1",
        ExternalProvider = "deezer",
        ExternalId = "1",
        Title = "Track",
        Album = "Album",
        AlbumId = "ext-deezer-album-3",
        Artist = "Artist",
        ArtistId = "ext-deezer-artist-2",
        AlbumArtist = "Artist",
        Duration = 100
    };
}
