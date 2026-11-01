using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed partial class VirtualPlaylistProtocolAdapterTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public VirtualPlaylistProtocolAdapterTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private const string NativeAudioKeys = "Name ServerId Id Etag DateCreated CanDelete CanDownload HasLyrics Container SortName PremiereDate ExternalUrls MediaSources Path EnableMediaSourceDisplay ChannelId Taglines Genres RunTimeTicks PlayAccess ProductionYear IndexNumber ParentIndexNumber RemoteTrailers ProviderIds IsFolder ParentId Type People Studios GenreItems LocalTrailerCount UserData SpecialFeatureCount DisplayPreferencesId Tags PrimaryImageAspectRatio Artists ArtistItems Album AlbumId AlbumPrimaryImageTag AlbumArtist AlbumArtists MediaStreams ImageTags BackdropImageTags ImageBlurHashes Chapters LocationType MediaType LockedFields LockData PlaylistItemId";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PlaylistAudio_MatchesNativeKeysAndTypesWithoutProviderReads(bool cached, bool musiver)
    {
        var song = PlaylistSong(cached);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "1")).ReturnsAsync(song);
        var model = Model() with
        {
            CreatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            ProjectionMode = PlaylistProjectionMode.Resolved,
            Tracks = Enumerable.Range(0, 100).Select(index => new VirtualPlaylistTrack(index, song.Id, song.Title,
                song.Artist, song.Album, song.AlbumArtist, 180_000, null, TrackMatchState.Accepted,
                RouteKind: TrackRouteKind.External, RouteProviderId: "deezer", RouteExternalId: "1")).ToArray()
        };
        var query = musiver ? "?Fields=DateCreated,SortName,MediaSources,Path,Genres,ProviderIds,ParentId,People,PrimaryImageAspectRatio,MediaStreams&Limit=100&StartIndex=0" :
            "?EnableImageTypes=Primary,Backdrop,Banner,Thumb&Fields=PrimaryImageAspectRatio,MediaStreams&Limit=100&StartIndex=0&UserId=user-1";
        var http = new DefaultHttpContext();
        http.Request.Path = musiver ? "/Items" : $"/Playlists/{ProtocolId}/Items";
        http.Request.QueryString = new(query + (musiver ? "&ParentId=" + ProtocolId : ""));
        http.Items[JellyfinResponseBuilder.ServerIdItemKey] = "library-server";
        var accessor = new HttpContextAccessor { HttpContext = http };
        var builder = new JellyfinResponseBuilder(httpContexts: accessor);
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(new StubVirtualizationService(model),
            new StubJellyfinMutationResolver(null), responseBuilder: builder, providerGateway: gateway.Object, httpContexts: accessor);
        var watch = Stopwatch.StartNew();
        JsonElement item = default;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var result = Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(Context(ProtocolKind.Jellyfin), ProtocolId, null, http.Request.Query, default));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal(100, json.RootElement.GetProperty("Items").GetArrayLength());
            item = json.RootElement.GetProperty("Items")[0].Clone();
        }
        watch.Stop();
        var elapsed = watch.Elapsed.TotalMilliseconds / 10;
        _output.WriteLine($"100 playlist items: {elapsed:F2} ms (cached={cached}, musiver={musiver})");
        var expected = NativeAudioKeys.Split(' ').Where(key => cached || key != "Container").Order().ToArray();
        var actual = item.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.True(expected.SequenceEqual(actual), $"100 playlist items: {elapsed:F2} ms; missing: {string.Join(',', expected.Except(actual))}; extra: {string.Join(',', actual.Except(expected))}");
        var arrays = "ExternalUrls MediaSources Taglines Genres RemoteTrailers People Studios GenreItems Tags Artists ArtistItems AlbumArtists MediaStreams BackdropImageTags Chapters LockedFields".Split(' ');
        var objects = "ProviderIds UserData ImageTags ImageBlurHashes".Split(' ');
        var numbers = "RunTimeTicks ProductionYear IndexNumber ParentIndexNumber LocalTrailerCount SpecialFeatureCount PrimaryImageAspectRatio".Split(' ');
        var booleans = "CanDelete CanDownload HasLyrics EnableMediaSourceDisplay IsFolder LockData".Split(' ');
        foreach (var key in expected)
            Assert.Equal(key == "ChannelId" ? JsonValueKind.Null : arrays.Contains(key) ? JsonValueKind.Array : objects.Contains(key) ? JsonValueKind.Object :
                numbers.Contains(key) ? JsonValueKind.Number : booleans.Contains(key) ? item.GetProperty(key).GetBoolean() ? JsonValueKind.True : JsonValueKind.False : JsonValueKind.String, item.GetProperty(key).ValueKind);
        Assert.Equal(song.AlbumId, item.GetProperty("ParentId").GetString());
        Assert.Equal(model.CreatedAt.UtcDateTime.ToString("O"), item.GetProperty("DateCreated").GetString());
        Assert.Equal(song.ArtistId, item.GetProperty("People")[0].GetProperty("Id").GetString());
        var single = JsonSerializer.SerializeToElement(Assert.IsType<JsonResult>(builder.CreateSongResponse(song)).Value);
        Assert.Equal(single.GetProperty("MediaSources").GetRawText(), item.GetProperty("MediaSources").GetRawText());
        var source = item.GetProperty("MediaSources")[0];
        var stream = source.GetProperty("MediaStreams")[0];
        foreach (var key in new[] { "Codec", "BitRate", "Channels", "SampleRate", "DisplayTitle" })
            Assert.Equal(cached, stream.TryGetProperty(key, out _));
        Assert.Equal(cached, source.TryGetProperty("Size", out _));
        Assert.Equal(cached, source.TryGetProperty("Bitrate", out _));
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void PlaylistNormalization_UsesOnlyKnownGains()
    {
        var song = PlaylistSong(false);
        song.ReplayGain = new() { TrackGain = -3, AlbumGain = -6.5, TrackPeak = 0.98 };
        var item = new JellyfinResponseBuilder().ConvertPlaylistSongToJellyfinItem(song, null, null);
        Assert.Equal(-3d, item["NormalizationGain"]);
        Assert.Equal(-6.5d, item["AlbumNormalizationGain"]);
        foreach (var key in new[] { "AlbumGain", "TrackPeak", "AlbumPeak" }) Assert.False(item.ContainsKey(key));
    }

    [Fact]
    public void PlaylistPeople_UseTheStoredAlbumArtistIdentity()
    {
        var song = PlaylistSong(false);
        song.AlbumArtist = "Host";
        song.AlbumArtists = [new Artist { Id = "ext-deezer-artist-host", Name = "Host" }];
        var builder = new JellyfinResponseBuilder();
        var item = JsonSerializer.SerializeToElement(builder.ConvertPlaylistSongToJellyfinItem(song, null, DateTimeOffset.UnixEpoch));
        Assert.Equal(new[] { song.ArtistId, "ext-deezer-artist-host" },
            item.GetProperty("People").EnumerateArray().Select(person => person.GetProperty("Id").GetString()));
        song.AlbumArtists.Clear();
        item = JsonSerializer.SerializeToElement(builder.ConvertPlaylistSongToJellyfinItem(song, null, DateTimeOffset.UnixEpoch));
        Assert.Equal(song.ArtistId, Assert.Single(item.GetProperty("People").EnumerateArray()).GetProperty("Id").GetString());
    }

    [Fact]
    public void ExternalArtworkTags_AreStableVersionedAndShareTheAlbumTag()
    {
        var builder = new JellyfinResponseBuilder();
        var song = PlaylistSong(false);
        var first = builder.ConvertSongToJellyfinItem(song);
        var second = builder.ConvertSongToJellyfinItem(song);
        var album = builder.ConvertAlbumToJellyfinItem(new Album { Id = song.AlbumId!, ExternalProvider = "deezer" });
        var artist = builder.ConvertArtistToJellyfinItem(new Artist { Id = song.ArtistId!, ExternalProvider = "deezer" });
        foreach (var item in new[] { first, album, artist })
            Assert.Equal(item["Id"] + "-art-v3", Assert.IsType<Dictionary<string, string>>(item["ImageTags"])["Primary"]);
        Assert.Equal(first["AlbumPrimaryImageTag"], Assert.IsType<Dictionary<string, string>>(album["ImageTags"])["Primary"]);
        Assert.Equal(Assert.IsType<Dictionary<string, string>>(first["ImageTags"]), Assert.IsType<Dictionary<string, string>>(second["ImageTags"]));
    }

    private static Song PlaylistSong(bool cached) => new()
    {
        Id = "ext-deezer-song-1",
        ExternalProvider = "deezer",
        ExternalId = "1",
        Title = "Track",
        Artist = "Artist",
        Artists = ["Artist"],
        ArtistIds = ["ext-deezer-artist-2"],
        ArtistId = "ext-deezer-artist-2",
        Album = "Album",
        AlbumId = "ext-deezer-album-3",
        AlbumArtist = "Artist",
        Duration = 180,
        Year = 2024,
        Track = 1,
        DiscNumber = 1,
        Container = cached ? "flac" : null,
        Codec = cached ? "flac" : null,
        Size = cached ? 12_000_000 : null,
        Bitrate = cached ? 960_000 : null,
        Channels = cached ? 2 : null,
        SampleRate = cached ? 96_000 : null
    };
}
