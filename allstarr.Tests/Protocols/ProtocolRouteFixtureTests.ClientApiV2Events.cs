using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using allstarr.Core.ClientEvents;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_PingRequiresBearerAndReturnsNoContent()
    {
        using var factory = V2EventsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        using var anonymous = await client.SendAsync(new(HttpMethod.Head, "/allstarr/v2/ping"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var stream = await client.GetAsync("/allstarr/v2/events");
        await AssertClientApiError(stream, 401, "unauthorized");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var legacy = await client.SendAsync(new(HttpMethod.Head, "/allstarr/v2/ping"));
        Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.SendAsync(new(HttpMethod.Head, "/allstarr/v2/ping"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ClientApiV2_EventsSendNativePayloadsAndCommittedRecordingRenames()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-known", Title = "Stored song", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "known" });
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.AddHostedService(provider => provider.GetRequiredService<V2EventStream>());
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var recording = await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var hub = factory.Services.GetRequiredService<ClientEventHub>();
        using var stream = await OpenV2EventsAsync(client);
        hub.Publish(principal.UserId, ClientEvent.Keep("ext-deezer-song-known", "inProgress", .5));
        var track = (await V2FrameAsync(stream, "track.updated")).Data.GetProperty("track");
        Assert.Equal(V2Ids.Track(recording), track.GetProperty("id").GetString());
        Assert.Equal("Stored song", track.GetProperty("title").GetString());
        var keep = (await V2FrameAsync(stream, "keep.updated")).Data;
        Assert.Equal(V2Ids.Track(recording), keep.GetProperty("trackId").GetString());
        Assert.Equal("inProgress", keep.GetProperty("state").GetString());
        Assert.Equal(.5, keep.GetProperty("progress").GetDouble());
        Assert.False(keep.TryGetProperty("itemId", out _));
        hub.Publish(principal.UserId, ClientEvent.Match("ext-deezer-song-known", "accepted"));
        await V2FrameAsync(stream, "track.updated");
        Assert.Equal("accepted", (await V2FrameAsync(stream, "match.updated")).Data.GetProperty("state").GetString());
        hub.Publish(principal.UserId, ClientEvent.Lyrics("ext-deezer-song-known", "revision"));
        var lyrics = (await V2FrameAsync(stream, "lyrics.updated")).Data;
        Assert.Equal(V2Ids.Track(recording), lyrics.GetProperty("trackId").GetString());
        Assert.Equal(["trackId", "revision"], lyrics.EnumerateObject().Select(property => property.Name).ToArray());
        var mixId = Guid.NewGuid();
        hub.Publish(principal.UserId, ClientEvent.Mix(mixId));
        var mix = (await V2FrameAsync(stream, "mix.updated")).Data;
        Assert.Equal(V2Ids.Mix(mixId), mix.GetProperty("mixId").GetString());
        Assert.StartsWith("pl_", mix.GetProperty("playlistId").GetString());
        var row = new ClientLibraryAddition(Guid.NewGuid(), "ext-deezer-song-known", "track", "done", "");
        hub.Publish(principal.UserId, ClientEvent.LibraryAddition(row));
        Assert.Equal(JsonSerializer.Serialize(row, ClientApiContract.JsonOptions),
            (await V2FrameAsync(stream, "libraryAddition.updated")).Data.GetProperty("row").GetRawText());
        var identities = factory.Services.GetRequiredService<ITrackIdentityService>();
        var actor = new ProviderActorContext(ProviderActorKind.User, principal.UserId, new("jellyfin", "primary", "listener"));
        var other = (await identities.CreateRecordingAsync(actor, "rename")).Recording;
        var merged = await identities.MergeRecordingsAsync(actor, recording, other.Id, "rename");
        var renamed = (await V2FrameAsync(stream, "track.renamed")).Data;
        Assert.Equal(V2Ids.Track(merged.Id == recording ? other.Id : recording), renamed.GetProperty("old").GetString());
        Assert.Equal(V2Ids.Track(merged.Id), renamed.GetProperty("new").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_EventsReplayWithHeaderOrQueryThenContinueLive(bool query)
    {
        using var factory = V2EventsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var hub = factory.Services.GetRequiredService<ClientEventHub>();
        var first = await OpenV2EventsAsync(client);
        var owner = Assert.Single(hub.Listeners);
        hub.Publish(owner, ClientEvent.Releases(1));
        var cursor = (await V2FrameAsync(first, "releases.updated")).Id;
        first.Dispose();
        hub.Publish(owner, ClientEvent.Releases(2));
        hub.Publish(owner, ClientEvent.Releases(3));
        using var resumed = await OpenV2EventsAsync(client, query ? null : cursor, query ? cursor : "unknown-query");
        Assert.Equal(2, (await V2FrameAsync(resumed, "releases.updated")).Data.GetProperty("count").GetInt32());
        Assert.Equal(3, (await V2FrameAsync(resumed, "releases.updated")).Data.GetProperty("count").GetInt32());
        hub.Publish(owner, ClientEvent.Releases(4));
        Assert.Equal(4, (await V2FrameAsync(resumed, "releases.updated")).Data.GetProperty("count").GetInt32());
        using var unknown = await OpenV2EventsAsync(client, "previous-server");
        Assert.Equal("{}", (await V2FrameAsync(unknown, "resync")).Data.GetRawText());
        hub.Publish(owner, ClientEvent.Releases(5));
        Assert.Equal(5, (await V2FrameAsync(unknown, "releases.updated")).Data.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task ClientApiV2_EventsHeartbeatAndHttpCaps()
    {
        using var factory = V2EventsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.RemoveAll<ClientEventOptions>();
            services.AddSingleton(new ClientEventOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(30), ConnectionsPerUser = 1 });
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var stream = await OpenV2EventsAsync(client);
        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType!.MediaType);
        Assert.True(stream.Response.Headers.CacheControl!.NoStore);
        Assert.Empty(stream.Response.Content.Headers.ContentEncoding);
        Assert.Equal(": ping", await stream.FrameAsync());
        using var capped = await client.GetAsync("/allstarr/v2/events", HttpCompletionOption.ResponseHeadersRead);
        await AssertClientApiError(capped, 503, "unavailable");
    }

    private static ProtocolFactory V2EventsFactory(Func<HttpRequestMessage, HttpResponseMessage> responder,
        Action<IServiceCollection>? configure = null) => ClientApiFactory(responder, services =>
        {
            services.AddHostedService(provider => provider.GetRequiredService<V2EventStream>());
            configure?.Invoke(services);
        });

    private static async Task<ClientEventStream> OpenV2EventsAsync(HttpClient client, string? header = null, string? query = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/allstarr/v2/events" + (query == null ? "" : "?lastEventId=" + Uri.EscapeDataString(query)));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        if (header != null) request.Headers.TryAddWithoutValidation("Last-Event-ID", header);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = new ClientEventStream(response, new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)));
        Assert.Equal(": connected", await result.FrameAsync());
        return result;
    }

    private static async Task<(string Id, JsonElement Data)> V2FrameAsync(ClientEventStream stream, string expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var frame = await stream.FrameAsync();
            if (frame.StartsWith(':')) continue;
            var lines = frame.Split('\n');
            Assert.StartsWith("id: ", lines[0]);
            Assert.Equal("event: " + expected, lines[1]);
            Assert.StartsWith("data: ", lines[2]);
            using var document = JsonDocument.Parse(lines[2][6..]);
            return (lines[0][4..], document.RootElement.Clone());
        }
        throw new InvalidOperationException("The expected event was not received.");
    }

    private sealed class ClientEventStream(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public HttpResponseMessage Response => response;
        public async Task<string> FrameAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var lines = new List<string>();
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length == 0) return string.Join('\n', lines);
                lines.Add(line);
            }
            throw new EndOfStreamException();
        }
        public void Dispose() { response.Dispose(); reader.Dispose(); }
    }
}
