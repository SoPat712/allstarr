using allstarr.Models.Subsonic;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class JellyfinAudioShapeTests
{
    internal const string RequestedFields =
        "DateCreated,SortName,MediaSources,Path,Genres,ProviderIds,ParentId,People,PrimaryImageAspectRatio,MediaStreams";
    private const string ServerId = "c17d351d3af24c678a6d8049c212d522";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalAudio_MatchesLibraryItemKeys(bool includeFields)
    {
        var http = new DefaultHttpContext();
        if (includeFields) http.Request.QueryString = new QueryString("?Fields=" + RequestedFields);
        http.Items[JellyfinResponseBuilder.ServerIdItemKey] = ServerId;
        var builder = new JellyfinResponseBuilder(Options.Create(new JellyfinSettings
        {
            DeviceId = "client-device"
        }), new HttpContextAccessor { HttpContext = http });

        var item = builder.ConvertSongToJellyfinItem(Track());
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Protocols", "jellyfin-audio-shape.json")));
        var shape = fixture.RootElement.GetProperty(includeFields ? "feishin_fields" : "no_fields");
        AssertKeys(shape.GetProperty("ItemKeys"), item.Keys, "ImageBlurHashes");
        Assert.Equal(ServerId, item["ServerId"]);
        Assert.IsType<long>(item["RunTimeTicks"]);
        Assert.False(Assert.IsType<bool>(item["IsFolder"]));
        Assert.Equal("Audio", item["Type"]);
        Assert.Equal("Audio", item["MediaType"]);
        Assert.False(item.ContainsKey("PlaylistItemId"));
        Assert.Null(item["ChannelId"]);

        if (!includeFields) return;
        var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(item["MediaSources"]));
        // The eventual source size and bitrate are unknown until playback opens it.
        // Keep playback URLs for clients that open streams from item lists.
        AssertKeys(shape.GetProperty("MediaSourceKeys"),
            source.Keys.Except(["DirectStreamUrl", "TranscodingUrl"]), "Size", "Bitrate");
        var streams = Assert.IsType<Dictionary<string, object?>[]>(source["MediaStreams"]);
        var stream = Assert.Single(streams, entry => Equals(entry["Type"], "Audio"));
        Assert.Single(streams, entry => Equals(entry["Type"], "Lyric"));
        AssertKeys(shape.GetProperty("AudioStreamKeys"), stream.Keys, "BitRate", "Codec", "SampleRate", "Channels", "BitDepth", "ChannelLayout", "TimeBase", "DisplayTitle");
        Assert.Same(source["MediaStreams"], item["MediaStreams"]);
        Assert.Empty(Assert.IsType<object[]>(item["People"]));
    }

    [Theory]
    [InlineData("?Fields=MediaStreams", true, false)]
    [InlineData("?Fields=MediaSources", false, true)]
    [InlineData("?Fields=mediastreams&Fields=Genres", true, false)]
    public void ExternalAudio_RequestedStreamFieldsAreIndependent(string query, bool streams, bool sources)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http });
        var item = builder.ConvertSongToJellyfinItem(Track());
        Assert.Equal(streams, item.ContainsKey("MediaStreams"));
        Assert.Equal(sources, item.ContainsKey("MediaSources"));
        Assert.Equal(query.Contains("Genres"), item.ContainsKey("GenreItems"));
    }

    [Fact]
    public void ExternalAudio_UnknownValuesAreAbsentExceptChannelId()
    {
        var item = new JellyfinResponseBuilder().ConvertSongToJellyfinItem(new Song
        {
            Id = "ext-deezer-song-1",
            Title = "Track",
            IsLocal = false,
            ExternalProvider = "deezer"
        });
        foreach (var key in new[] { "PremiereDate", "ProductionYear", "IndexNumber", "AlbumId", "AlbumPrimaryImageTag", "ServerId" })
            Assert.False(item.ContainsKey(key), key);
        Assert.Null(item["ChannelId"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalContainers_UseTheSameFieldSelection(bool includeFields)
    {
        var http = new DefaultHttpContext();
        if (includeFields) http.Request.QueryString = new QueryString("?Fields=Genres,ProviderIds,SortName,DateCreated");
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http });
        var playlist = new ExternalPlaylist { Id = "ext-deezer-playlist-1", Provider = "deezer", ExternalId = "1" };
        var items = new[]
        {
            builder.ConvertAlbumToJellyfinItem(new Album { Id = "ext-deezer-album-1", ExternalProvider = "deezer" }),
            builder.ConvertArtistToJellyfinItem(new Artist { Id = "ext-deezer-artist-1", ExternalProvider = "deezer" }),
            builder.ConvertPlaylistToJellyfinItem(playlist),
            builder.ConvertPlaylistToAlbumItem(playlist)
        };
        foreach (var item in items)
        {
            foreach (var field in new[] { "Genres", "GenreItems", "ProviderIds" })
                Assert.Equal(includeFields, item.ContainsKey(field));
            foreach (var field in new[] { "BasicSyncInfo", "ImageBlurHashes", "ParentLogoItemId", "ParentBackdropItemId", "PlaylistItemId" })
                Assert.False(item.ContainsKey(field));
        }
    }

    [Fact]
    public void PlaybackInfoSources_AreAvailableWithoutListingFields()
    {
        var builder = new JellyfinResponseBuilder();
        Assert.False(builder.ConvertSongToJellyfinItem(Track()).ContainsKey("MediaSources"));
        var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(builder.CreatePlaybackMediaSources(Track())));
        Assert.Equal("ext-deezer-song-1", source["Id"]);
        Assert.Equal("/Audio/ext-deezer-song-1/stream?static=true", source["DirectStreamUrl"]);
        Assert.Equal("/Audio/ext-deezer-song-1/universal?container=flac&audioCodec=flac", source["TranscodingUrl"]);
        Assert.False(Assert.IsType<bool>(source["RequiresOpening"]));
        Assert.True(Assert.IsType<bool>(source["SupportsDirectStream"]));
    }

    [Theory]
    [InlineData("/Items/ext-deezer-song-1", "")]
    [InlineData("/Items/ext-deezer-song-1", "?Fields=Genres")]
    [InlineData("/Users/listener/Items/ext-deezer-song-1", "")]
    [InlineData("/Users/listener/Items/ext-deezer-song-1", "?Fields=Genres")]
    public void SingleAudio_IgnoresFieldSelection(string path, string query)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http });
        var item = builder.ConvertSongToJellyfinItem(Track());
        foreach (var field in new[] { "ProviderIds", "Overview", "MediaSources", "MediaStreams", "Genres", "Path" })
            Assert.True(item.ContainsKey(field), field);
        Assert.False(item.ContainsKey("PlaylistItemId"));
    }

    [Fact]
    public void SingleItemResponses_IncludeOptionalFieldsWithoutRequestContext()
    {
        var builder = new JellyfinResponseBuilder();
        var responses = new[]
        {
            builder.CreateSongResponse(Track()),
            builder.CreateAlbumResponse(new Album { Id = "ext-deezer-album-1", ExternalProvider = "deezer" }),
            builder.CreateArtistResponse(new Artist { Id = "ext-deezer-artist-1", ExternalProvider = "deezer" }, []),
            builder.CreatePlaylistAsAlbumResponse(new ExternalPlaylist
                { Id = "ext-deezer-playlist-1", Provider = "deezer", ExternalId = "1" }, [Track()])
        };
        foreach (var response in responses)
        {
            var item = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<JsonResult>(response).Value);
            Assert.True(item.ContainsKey("ProviderIds"));
            Assert.True(item.ContainsKey("Genres"));
            Assert.False(item.ContainsKey("PlaylistItemId"));
        }
    }

    [Theory]
    [InlineData("/Items")]
    [InlineData("/Users/listener/Items")]
    [InlineData("/Playlists/playlist/Items")]
    [InlineData("/Albums")]
    [InlineData("/Artists")]
    [InlineData("/Items/song/InstantMix")]
    [InlineData("/Items/Latest")]
    [InlineData("/Users/listener/Items/Latest")]
    [InlineData("/UserItems/Resume")]
    [InlineData("/Search")]
    [InlineData("/Shows/NextUp")]
    public void AudioListings_SelectOptionalFields(string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http });
        var item = builder.ConvertSongToJellyfinItem(Track());
        foreach (var field in new[] { "ProviderIds", "Overview", "MediaSources", "MediaStreams", "ParentId", "Genres", "Path" })
            Assert.False(item.ContainsKey(field), field);
        http.Request.QueryString = new QueryString("?Fields=ProviderIds,MediaSources,ParentId");
        item = builder.ConvertSongToJellyfinItem(Track());
        Assert.True(item.ContainsKey("ProviderIds"));
        var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(item["MediaSources"]));
        Assert.Equal("/Audio/ext-deezer-song-1/stream?static=true", source["DirectStreamUrl"]);
    }

    private static void AssertKeys(JsonElement expected, IEnumerable<string> actual, params string[] unavailable) =>
        Assert.Equal(expected.EnumerateArray().Select(value => value.GetString()!)
            .Except(unavailable).Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    private static Song Track() => new()
    {
        Id = "ext-deezer-song-1",
        Title = "Track",
        Artist = "Artist",
        Album = "Album",
        ArtistId = "ext-deezer-artist-2",
        AlbumId = "ext-deezer-album-3",
        IsLocal = false,
        ExternalProvider = "deezer",
        ExternalId = "1",
        Duration = 180,
        Track = 1,
        Year = 2020,
        Genre = "Electronic"
    };
}
