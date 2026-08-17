using System.Net;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Providers.AppleDownload;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class AppleDownloadCapabilityAdapterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-apple-capability", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Download_UsesDiscoveredGatewayAndResolvesHostOwnedArtifact()
    {
        var audio = Encoding.UTF8.GetBytes("fake flac audio bytes");
        var gateway = new GatewayHandler(audio, "audio/flac");
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/", Quality = "alac-16-44" };
        var client = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(
            new StaticClientFactory(client), Options.Create(settings));
        var store = new MemoryStore();
        var resolver = new ProviderDownloadArtifactResolver(store, new() { RootPath = root });
        var adapter = new AppleDownloadCapabilityAdapter(client, settings, discovery, resolver, 1024 * 1024);
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        var workspace = await resolver.CreateWorkspaceAsync(new(
            tenant, user, job, AppleDownloadCapabilityAdapter.StableProviderId, null, "favorite:apple-track"));
        var context = Context(tenant, user);
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "apple/track 1");

        var availability = await adapter.CheckAvailabilityAsync(context, new(track));
        var outcome = await adapter.DownloadAsync(context, new(
            track, job, workspace.Reference, ProviderAudioQuality.Lossless));

        Assert.True(availability.IsSuccess);
        Assert.Equal(ProviderDownloadAvailabilityState.Available, availability.RequireValue().State);
        var output = outcome.RequireValue();
        Assert.EndsWith(".flac", output.ArtifactId, StringComparison.Ordinal);
        Assert.DoesNotContain("apple/track", output.ArtifactId, StringComparison.Ordinal);
        Assert.Equal(audio.Length, output.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant(), output.Sha256);
        Assert.Equal("flac", output.Media.Codec);

        var verified = await resolver.ResolveAsync(workspace.Reference, output);
        Assert.Equal(audio, await File.ReadAllBytesAsync(verified.SourcePath));
        Assert.Equal(ProviderDownloadArtifactState.Verified, verified.State);
        Assert.Single(store.Artifacts);
        Assert.Contains(gateway.Requests, uri =>
            uri.PathAndQuery == "/api/download/apple%2Ftrack%201?quality=alac-16-44");
    }

    [Theory]
    [InlineData(ProviderAudioQuality.Any, "alac-16-44", "alac-16-44")]
    [InlineData(ProviderAudioQuality.HighResolution, "alac-16-44", "alac-16-44")]
    [InlineData(ProviderAudioQuality.HighResolution, "alac-24-96", "alac-24-96")]
    [InlineData(ProviderAudioQuality.Lossless, "alac-24-96", "alac-16-44")]
    [InlineData(ProviderAudioQuality.Lossy, "alac-16-44", "aac-320")]
    [InlineData(ProviderAudioQuality.Lossy, "aac-96", "aac-96")]
    [InlineData(ProviderAudioQuality.DataSaver, "alac-24-192", "aac-96")]
    public void Quality_UsesConfiguredQualityOrAnAppropriateLowerClientTier(
        ProviderAudioQuality requested,
        string configured,
        string expected)
    {
        Assert.Equal(expected, AppleDownloadCapabilityAdapter.Quality(requested, configured));
    }

    [Fact]
    public async Task DownloadAndStream_RejectUnrecognizedMedia()
    {
        var gateway = new GatewayHandler(Encoding.UTF8.GetBytes("not audio"), "text/html");
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        var client = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(
            new StaticClientFactory(client), Options.Create(settings));
        var store = new MemoryStore();
        var resolver = new ProviderDownloadArtifactResolver(store, new() { RootPath = root });
        var adapter = new AppleDownloadCapabilityAdapter(client, settings, discovery, resolver, 1024);
        var streaming = new AppleDownloadStreamingCapabilityAdapter(client, settings, discovery);
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        var workspace = await resolver.CreateWorkspaceAsync(new(
            tenant, user, job, AppleDownloadCapabilityAdapter.StableProviderId, null, "favorite:bad-media"));
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "bad-media");

        var outcome = await adapter.DownloadAsync(Context(tenant, user), new(
            track, job, workspace.Reference, ProviderAudioQuality.Any));
        var lease = (await streaming.GetStreamLeaseAsync(
            Context(tenant, user), new(track))).RequireValue();
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, outcome.Error!.Kind);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            lease.ProtectedResponseFactory!(streamRequest, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, workspace.Reference.WorkspaceId)));
        Assert.Empty(store.Artifacts);
    }

    [Fact]
    public async Task Availability_DoesNotAdvertiseDownloadWhenManifestOmitsRoute()
    {
        var gateway = new GatewayHandler([], "audio/flac") { AdvertiseDownload = false };
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        var client = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(
            new StaticClientFactory(client), Options.Create(settings));
        var resolver = new ProviderDownloadArtifactResolver(new MemoryStore(), new() { RootPath = root });
        var adapter = new AppleDownloadCapabilityAdapter(client, settings, discovery, resolver, 1024);
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "missing-route");

        var outcome = await adapter.CheckAvailabilityAsync(Context(tenant, user), new(track));

        Assert.Equal(ProviderDownloadAvailabilityState.Unavailable, outcome.RequireValue().State);
    }

    [Fact]
    public async Task Stream_UsesTheProgressiveGatewayWithoutAdvertisingRanges()
    {
        var audio = Encoding.UTF8.GetBytes("progressive apple audio");
        var gateway = new GatewayHandler(audio, "audio/flac");
        var settings = new AppleDownloadSettings
        {
            BaseUrl = "https://gateway.test/",
            Quality = "alac-24-96"
        };
        var client = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(
            new StaticClientFactory(client), Options.Create(settings));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(client, settings, discovery);
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId,
            ProviderResourceKind.Track,
            "apple/track 1");

        var lease = (await adapter.GetStreamLeaseAsync(
            Context(tenant, user), new(track))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, CancellationToken.None);

        Assert.False(lease.SupportsByteRanges);
        Assert.False(lease.SupportsSeeking);
        Assert.Equal(ProviderStreamRetryBehavior.RetrySameLeaseOnce, lease.RetryBehavior);
        Assert.Equal("audio/flac", lease.Media.MimeType);
        Assert.Equal("flac", lease.Media.Codec);
        Assert.Null(lease.Media.BitDepth);
        Assert.Null(lease.Media.SampleRate);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(audio, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains(gateway.Requests, uri =>
            uri.PathAndQuery == "/api/stream/apple%2Ftrack%201?quality=aac-320");
    }

    [Fact]
    public async Task Lyrics_ReturnsTimedGamdlArtifactWhenGatewayAdvertisesIt()
    {
        var gateway = new GatewayHandler([], "audio/flac") { AdvertiseLyrics = true };
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        var client = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(
            new StaticClientFactory(client), Options.Create(settings));
        var adapter = new AppleDownloadLyricsCapabilityAdapter(client, settings, discovery);
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "103");

        var outcome = await adapter.FetchLyricsAsync(Context(tenant, user), new(
            Guid.CreateVersion7(), track, preferredFormat: ProviderLyricsFormat.LineTimed));

        Assert.True(outcome.IsSuccess, outcome.Error?.Kind.ToString());
        Assert.Equal("GAMDL", outcome.Value!.Source);
        Assert.Equal(ProviderLyricsFormat.LineTimed, outcome.Value.Format);
        Assert.Equal("[00:01.00]Fixture lyrics\n", outcome.Value.Content);
        Assert.DoesNotContain(gateway.Requests, uri =>
            uri.AbsolutePath.StartsWith("/api/download/", StringComparison.Ordinal) ||
            uri.AbsolutePath.StartsWith("/api/stream/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lyrics_MissingCachedArtifactDoesNotTriggerMediaDownload()
    {
        var gateway = new GatewayHandler([], "audio/flac")
        {
            AdvertiseLyrics = true,
            MissingLyrics = true
        };
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        var client = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(
            new StaticClientFactory(client), Options.Create(settings));
        var adapter = new AppleDownloadLyricsCapabilityAdapter(client, settings, discovery);
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "missing");

        var outcome = await adapter.FetchLyricsAsync(
            Context(Guid.CreateVersion7(), Guid.CreateVersion7()),
            new(Guid.CreateVersion7(), track, preferredFormat: ProviderLyricsFormat.LineTimed));

        Assert.True(outcome.IsSuccess, outcome.Error?.Kind.ToString());
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, outcome.Value!.Availability);
        Assert.DoesNotContain(gateway.Requests, uri =>
            uri.AbsolutePath.StartsWith("/api/download/", StringComparison.Ordinal) ||
            uri.AbsolutePath.StartsWith("/api/stream/", StringComparison.Ordinal));
    }

    private static ProviderExecutionContext Context(Guid tenant, Guid user) => new(
        new ProviderActorContext(tenant, ProviderActorKind.User, user,
            new ProviderBackendPrincipal("jellyfin", "primary", "user")),
        AppleDownloadCapabilityAdapter.StableProviderId,
        account: null,
        library: null,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: true,
            allowSharedAccount: false,
            allowManagedDownloads: true,
            [AppleDownloadCapabilityAdapter.StableProviderId]),
        "download-test",
        "correlation-test",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None,
        "favorite:test");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class StaticClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class GatewayHandler(byte[] audio, string contentType) : HttpMessageHandler
    {
        public bool AdvertiseDownload { get; init; } = true;
        public bool AdvertiseLyrics { get; init; }
        public bool MissingLyrics { get; init; }
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response = path switch
            {
                "/api/capabilities" => Json($$"""
                    {"sidecarApiVersion":"1.0","capabilities":[
                      {"id":"metadata-search-song","state":"supported"},
                      {"id":"metadata-song","state":"supported"},
                      {"id":"stream-audio-song","state":"supported"}
                      {{(AdvertiseDownload ? ",{\"id\":\"download-audio-song\",\"state\":\"supported\"}" : string.Empty)}}
                      {{(AdvertiseLyrics ? ",{\"id\":\"synced-lyrics-artifact\",\"state\":\"supported\"}" : string.Empty)}}
                    ]}
                    """),
                "/api/health" => Json("{\"staged\":true,\"daemon_running\":true,\"wrapper_healthy\":true,\"logged_in\":true}"),
                "/api/me" => Json("{\"authenticated\":true}"),
                _ when path.StartsWith("/api/download/", StringComparison.Ordinal) => Audio(),
                _ when path.StartsWith("/api/stream/", StringComparison.Ordinal) => Audio(),
                _ when path.StartsWith("/api/lyrics/", StringComparison.Ordinal) => MissingLyrics
                    ? new(HttpStatusCode.NotFound)
                    : Json("{\"source\":\"GAMDL\",\"format\":\"LineTimed\",\"content\":\"[00:01.00]Fixture lyrics\\n\"}"),
                _ => new(HttpStatusCode.NotFound)
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private HttpResponseMessage Audio()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
            response.Content.Headers.ContentType = new(contentType);
            return response;
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class MemoryStore : IProviderDownloadArtifactStore
    {
        public List<ProviderDownloadWorkspaceEntity> Workspaces { get; } = [];
        public List<ProviderDownloadArtifactEntity> Artifacts { get; } = [];
        public Task<ProviderDownloadWorkspaceEntity> CreateWorkspaceAsync(ProviderDownloadWorkspaceEntity value, CancellationToken token)
        {
            var existing = Workspaces.SingleOrDefault(item => item.WorkspaceId == value.WorkspaceId);
            if (existing != null) return Task.FromResult(existing);
            Workspaces.Add(value);
            return Task.FromResult(value);
        }
        public Task<ProviderDownloadWorkspaceEntity?> GetWorkspaceAsync(string id, CancellationToken token) =>
            Task.FromResult(Workspaces.SingleOrDefault(item => item.WorkspaceId == id));
        public Task<ProviderDownloadArtifactEntity> AddVerifiedAsync(ProviderDownloadArtifactEntity value, CancellationToken token)
        {
            var existing = Artifacts.SingleOrDefault(item =>
                item.WorkspaceRecordId == value.WorkspaceRecordId && item.ProviderArtifactId == value.ProviderArtifactId);
            if (existing != null) return Task.FromResult(existing);
            Artifacts.Add(value);
            return Task.FromResult(value);
        }
        public Task<ProviderDownloadArtifactEntity?> FindByJobAsync(Guid tenantId, Guid jobId, string provider, CancellationToken token) =>
            Task.FromResult(Artifacts.SingleOrDefault(item =>
                item.TenantId == tenantId && item.DurableJobId == jobId && item.ProviderId == provider));
        public Task MarkPlacedAsync(Guid id, Guid managedId, CancellationToken token) => Task.CompletedTask;
    }
}
