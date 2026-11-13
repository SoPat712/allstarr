using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Protocols;
using allstarr.Middleware;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_CapabilitiesAnnounceAlbumLyricsMetadataConditionalAndServingReads()
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/capabilities");
        response.EnsureSuccessStatusCode();
        var features = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["features"]!;
        foreach (var name in new[] { "v2Albums", "v2BulkLyrics", "v2TrackMetadata", "v2BatchConditional", "v2PlaybackServing" })
            Assert.True(features[name]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_PlaybackServingFactsFollowTheActiveStream(bool external)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = false;
        HttpResponseMessage HeldAudio() => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new V2HeldStream(release.Task)) { Headers = { ContentType = new("audio/flac") } }
        };
        using var factory = V2PlaybackFactory(audio: request => request?.Method == HttpMethod.Head ? V2AudioResponse(request) : HeldAudio(),
            source: () => opening
                ? new(HeldAudio(), new("fixture", new("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1), true, true,
                    new("audio/flac", "flac", "flac", 1000000, 96000, 24, 2), ProviderStreamRetryBehavior.DoNotRetry), "tidal", "served", IsCached: true)
                : V2ServiceAudio());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        if (external) await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var source = external ? "ext-deezer-song-known" : "native1";
        // Serving facts come with a stream link from resolve; reading the track itself never touches the audio.
        async Task<JsonNode> Resolve(string query = "")
        {
            using var response = await client.PostAsJsonAsync("/allstarr/v2/playback/resolve" + query, new { trackId = source, tier = "high", purpose = "prefetch" });
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            return JsonNode.Parse(text)!;
        }
        var unplayed = await Resolve();
        Assert.True(unplayed.AsObject().ContainsKey("serving"));
        Assert.Null(unplayed["serving"]);
        var playback = await ResolveV2Playback(client, source, "original");
        opening = true;
        using var request = new HttpRequestMessage(HttpMethod.Get, playback["url"]!.GetValue<string>());
        using var streaming = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        streaming.EnsureSuccessStatusCode();
        opening = false;
        try
        {
            var view = await Resolve();
            Assert.Equal(96000, view["source"]!["sampleRate"]!.GetValue<int>());
            var serving = view["serving"]!;
            Assert.Equal("flac", serving["codec"]!.GetValue<string>());
            Assert.Equal(external ? 1000000 : 2000000, serving["bitrate"]!.GetValue<int>());
            Assert.Equal(96000, serving["sampleRate"]!.GetValue<int>());
            Assert.Equal(24, serving["bitDepth"]!.GetValue<int>());
            Assert.Equal(external ? "tidal" : "jellyfin", serving["source"]!.GetValue<string>());
            Assert.Null((await Resolve("?deviceId=other-device"))["serving"]);
        }
        finally { release.TrySetResult(); }
        await streaming.Content.ReadAsByteArrayAsync();
        Assert.Null((await Resolve())["serving"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_AlbumIncludesTracksAndMoreByArtistInStandaloneAndBatchReads(bool external)
    {
        var source = external ? "ext-deezer-album-7" : "album1";
        var artist = external ? "ext-deezer-artist-9" : "artist1";
        var other = external ? "ext-deezer-album-8" : "album2";
        var gateway = V2BatchGateway();
        var album = new Album { Id = source, Title = "Library album", Artist = "Library artist", ArtistId = artist, Year = 2020, Songs = [V2BatchSong("known")] };
        gateway.Setup(item => item.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7")).ReturnsAsync(album);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "9", 20))
            .ReturnsAsync([album, new() { Id = other, Title = "Another album", Artist = album.Artist, ArtistId = artist }]);
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (request.RequestUri.AbsolutePath == "/UserViews") return Json(200, """{"Items":[]}""");
            Assert.Equal("/Users/listener/Items", request.RequestUri.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (query.GetValueOrDefault("includeItemTypes") == "MusicAlbum")
                return Json(200, query.ContainsKey("albumArtistIds")
                    ? """{"Items":[{"Id":"album1","Name":"Library album","Type":"MusicAlbum"},{"Id":"album2","Name":"Another album","Type":"MusicAlbum"}]}"""
                    : """{"Items":[{"Id":"album1","Name":"Library album","Type":"MusicAlbum","AlbumArtists":[{"Id":"artist1","Name":"Library artist"}],"ProductionYear":2020}]}""");
            return Json(200, V2LibrarySong);
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.AddSingleton<V2PlaybackErrors>();
            services.AddSingleton<ILogger<ClientApiMiddleware>>(provider => provider.GetRequiredService<V2PlaybackErrors>());
        });
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        if (external) await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var path = $"/allstarr/v2/albums/{V2Ids.Album(source)}?include=tracks&views=more-by-artist";
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text + "\n" + factory.Services.GetRequiredService<V2PlaybackErrors>().Last);
        var body = JsonNode.Parse(text)!;
        Assert.Equal(V2Ids.Album(source), body["album"]!["id"]!.GetValue<string>());
        Assert.Equal("Library album", body["album"]!["title"]!.GetValue<string>());
        Assert.Equal(external, body["album"]!["isExternal"]!.GetValue<bool>());
        Assert.Equal(V2Ids.Artist(artist), body["album"]!["artists"]![0]!["id"]!.GetValue<string>());
        Assert.Single(body["tracks"]!.AsArray());
        Assert.Equal(V2Ids.Album(other), Assert.Single(body["views"]!["moreByArtist"]!.AsArray())!["id"]!.GetValue<string>());
        var batch = await ReadV2Batch(client, path);
        Assert.Equal(200, batch["status"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(body, batch["body"]));
        Assert.Equal(response.Headers.ETag!.ToString(), batch["etag"]!.GetValue<string>());
        await AssertV2NotModified(client, path, response.Headers.ETag.ToString());
        var cached = await ReadV2Batch(client, path, response.Headers.ETag.ToString());
        Assert.Equal(304, cached["status"]!.GetValue<int>());
        Assert.False(cached.AsObject().ContainsKey("body"));
        using var plain = await client.GetAsync($"/allstarr/v2/albums/{source}");
        var summary = JsonNode.Parse(await plain.Content.ReadAsStringAsync())!;
        Assert.False(summary.AsObject().ContainsKey("tracks"));
        Assert.False(summary.AsObject().ContainsKey("views"));
    }

    [Fact]
    public async Task ClientApiV2_BulkLyricsKeepsFoundTracksAndReportsMissingIds()
    {
        var gateway = V2BatchGateway();
        var lyrics = new Mock<IProtocolLyricsResolver>();
        lyrics.Setup(item => item.GetRichLyricsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources?>()))
            .ReturnsAsync((ProtocolExecutionContext _, Song _, string key, ProtocolLyricsSources? _) =>
                new RichLyrics(new("fixture", "Fixture"), "line", [new(1000, 2000, key, "vocal", [])]));
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/UserViews" => Json(200, """{"Items":[]}"""),
            _ => throw new InvalidOperationException("Unexpected upstream request")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IProtocolLyricsResolver>();
            services.AddSingleton(lyrics.Object);
        });
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var first = await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var second = await SeedV2DetailsAsync(factory, "listener", "next", personal: true);
        var missing = V2Ids.Track(Guid.NewGuid());
        var path = $"/allstarr/v2/tracks?ids={V2Ids.TrackSource("ext-deezer-song-known")},{missing},{V2Ids.TrackSource("ext-deezer-song-next")}&views=lyrics&include=album";
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!;
        Assert.Equal(2, body["tracks"]!.AsArray().Count);
        var metadata = body["tracks"]![0]!;
        Assert.Equal(V2Ids.Artist("ext-deezer-artist-9"), metadata["artists"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("Stored album", metadata["album"]!["title"]!.GetValue<string>());
        Assert.Equal("flac", metadata["format"]!["container"]!.GetValue<string>());
        Assert.Equal(24, metadata["format"]!["bitDepth"]!.GetValue<int>());
        Assert.Equal(missing, Assert.Single(body["missing"]!.AsArray())!.GetValue<string>());
        foreach (var (recording, source) in new[] { (first, "known"), (second, "next") })
        {
            var id = V2Ids.Track(recording);
            Assert.Equal("ok", body["viewStatus"]![id]!["lyrics"]!.GetValue<string>());
            Assert.Equal("deezer:" + source, body["views"]![id]!["lyrics"]!["lines"]![0]!["text"]!.GetValue<string>());
            using var single = await client.GetAsync($"/allstarr/v2/tracks/{V2Ids.TrackSource("ext-deezer-song-" + source)}?views=lyrics&include=album");
            var track = JsonNode.Parse(await single.Content.ReadAsStringAsync())!;
            Assert.True(JsonNode.DeepEquals(track["views"]!["lyrics"], body["views"]![id]!["lyrics"]));
            Assert.NotNull(track["albums"]![V2Ids.Album("ext-deezer-album-7")]);
        }
        var batch = await ReadV2Batch(client, path);
        Assert.Equal(200, batch["status"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(body, batch["body"]));
        await AssertV2NotModified(client, path, response.Headers.ETag!.ToString());
        var cached = await ReadV2Batch(client, path, response.Headers.ETag!.ToString());
        Assert.Equal(304, cached["status"]!.GetValue<int>());
        Assert.False(cached.AsObject().ContainsKey("body"));
    }

    [Fact]
    public async Task ClientApiV2_TrackMetadataAndConditionalReadsAgreeInsideAndOutsideBatch()
    {
        var title = "Library song";
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, V2LibrarySong.Replace("Library song", title)));
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        const string path = "/allstarr/v2/tracks/native1?include=album";
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag!.ToString();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var track = body["track"]!;
        Assert.Equal("Library artist", track["artists"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Library album", track["album"]!["title"]!.GetValue<string>());
        Assert.Equal(200000, track["durationMs"]!.GetValue<long>());
        Assert.Equal("flac", track["format"]!["codec"]!.GetValue<string>());
        Assert.Equal(2000000, track["format"]!["bitrate"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(body, (await ReadV2Batch(client, path))["body"]));
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"other\", W/" + etag);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal("", await unchanged.Content.ReadAsStringAsync());
        Assert.Equal(etag, unchanged.Headers.ETag!.ToString());
        var cached = await ReadV2Batch(client, path, etag);
        Assert.Equal(304, cached["status"]!.GetValue<int>());
        Assert.False(cached.AsObject().ContainsKey("body"));
        title = "Updated song";
        var updated = await ReadV2Batch(client, path, etag);
        Assert.Equal(200, updated["status"]!.GetValue<int>());
        Assert.Equal(title, updated["body"]!["track"]!["title"]!.GetValue<string>());
        Assert.NotEqual(etag, updated["etag"]!.GetValue<string>());
        using var current = await client.GetAsync(path);
        Assert.Equal(current.Headers.ETag!.ToString(), updated["etag"]!.GetValue<string>());
        using var stale = new HttpRequestMessage(HttpMethod.Get, path);
        stale.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var refreshed = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal(current.Headers.ETag.ToString(), refreshed.Headers.ETag!.ToString());
        Assert.Equal(200, (await ReadV2Batch(client, path, "\"stale\""))["status"]!.GetValue<int>());
        Assert.Equal(304, (await ReadV2Batch(client, path, "*"))["status"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("/allstarr/v2/tracks?ids=native1&views=lyrics,lyrics", 400)]
    [InlineData("/allstarr/v2/tracks?ids=native1&views=playback", 400)]
    [InlineData("/allstarr/v2/tracks/native1?views=playback", 400)]
    [InlineData("/allstarr/v2/albums/album1?include=track", 400)]
    [InlineData("/allstarr/v2/albums/album1?views=more-by-artist,more-by-artist", 400)]
    [InlineData("/allstarr/v2/albums/trk_sbmF0aXZlMQ", 404)]
    [InlineData("/allstarr/v2/albums/unknown", 404)]
    public async Task ClientApiV2_AlbumAndBulkQueriesValidateInStandaloneAndBatchReads(string path, int status)
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync(path);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status, (await ReadV2Batch(client, path))["status"]!.GetValue<int>());
    }

    private static Mock<IProtocolProviderGateway> V2BatchGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["deezer"]);
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => V2BatchSong(id));
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => V2BatchSong(id));
        return gateway;
    }

    private static Song V2BatchSong(string id) => new()
    {
        Id = "ext-deezer-song-" + id,
        Title = "Stored song",
        Artist = "Stored artist",
        ArtistId = "ext-deezer-artist-9",
        Album = "Stored album",
        AlbumId = "ext-deezer-album-7",
        Duration = 200,
        Container = "flac",
        ExternalProvider = "deezer",
        ExternalId = id
    };

    private static async Task<JsonNode> ReadV2Batch(HttpClient client, string path, string? ifNoneMatch = null)
    {
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new { requests = new[] { new { id = "read", path, ifNoneMatch } } });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!["responses"]!["read"]!;
    }

    private static async Task AssertV2NotModified(HttpClient client, string path, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag!.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private sealed class V2HeldStream(Task release) : Stream
    {
        private bool started;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!started) { started = true; buffer.Span[0] = 0; return 1; }
            await release.WaitAsync(cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
