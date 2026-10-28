using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Storage;
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
    public async Task SharedStreamingAccountWorksForBothListeners()
    {
        var gateway = new GatewayHandler(Encoding.UTF8.GetBytes("progressive audio"), "audio/flac");
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        using var http = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(new StaticClientFactory(http), Options.Create(settings));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http, settings, discovery, AppleProviderTestFactory.Client());
        var owner = Context(Guid.CreateVersion7());
        var account = new ProviderAccountContext(Guid.CreateVersion7(), "apple-musickit", ProviderAccountScope.Shared,
            1, secretReferenceId: Guid.CreateVersion7());
        foreach (var listener in new[] { owner.Actor.EffectiveUserId!.Value, Guid.CreateVersion7() })
        {
            var context = new ProviderExecutionContext(new(ProviderActorKind.User, listener, owner.Actor.BackendPrincipal),
                "apple-musickit", account, new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                    ProviderExplicitContentPolicy.Allow, true, true, true, ["apple-musickit"]),
                "stream", "fixture", owner.Deadline, default);
            var result = await adapter.GetStreamLeaseAsync(context, new(new("apple-musickit", ProviderResourceKind.Track, "101")));
            Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        }
        Assert.Throws<UnauthorizedAccessException>(() => new ProviderExecutionContext(
            new(ProviderActorKind.User, Guid.CreateVersion7(), owner.Actor.BackendPrincipal), "apple-musickit", owner.Account,
            owner.Policy, "stream", "fixture", owner.Deadline, default));
    }

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
        var resolver = new ProviderDownloadArtifactResolver(store, new() { RootPath = root }, audioVerifier: FixtureAudioVerifier.Instance);
        var adapter = new AppleDownloadCapabilityAdapter(client, settings, discovery, resolver, 1024 * 1024, AppleProviderTestFactory.Client());
        var user = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        var workspace = await resolver.CreateWorkspaceAsync(new(
            user, job, AppleDownloadCapabilityAdapter.StableProviderId, null, "favorite:apple-track"));
        var context = Context(user);
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "101");

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
            uri.PathAndQuery == "/api/download/101?quality=alac-16-44");
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
        var resolver = new ProviderDownloadArtifactResolver(store, new() { RootPath = root }, audioVerifier: FixtureAudioVerifier.Instance);
        var adapter = new AppleDownloadCapabilityAdapter(client, settings, discovery, resolver, 1024, AppleProviderTestFactory.Client());
        var streaming = new AppleDownloadStreamingCapabilityAdapter(client, settings, discovery, AppleProviderTestFactory.Client());
        var user = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        var workspace = await resolver.CreateWorkspaceAsync(new(
            user, job, AppleDownloadCapabilityAdapter.StableProviderId, null, "favorite:bad-media"));
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "104");

        var outcome = await adapter.DownloadAsync(Context(user), new(
            track, job, workspace.Reference, ProviderAudioQuality.Any));
        var lease = (await streaming.GetStreamLeaseAsync(
            Context(user), new(track))).RequireValue();
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
        var resolver = new ProviderDownloadArtifactResolver(new MemoryStore(), new() { RootPath = root }, audioVerifier: FixtureAudioVerifier.Instance);
        var adapter = new AppleDownloadCapabilityAdapter(client, settings, discovery, resolver, 1024, AppleProviderTestFactory.Client());
        var user = Guid.CreateVersion7();
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "missing-route");

        var outcome = await adapter.CheckAvailabilityAsync(Context(user), new(track));

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
        var adapter = new AppleDownloadStreamingCapabilityAdapter(client, settings, discovery, AppleProviderTestFactory.Client());
        var user = Guid.CreateVersion7();
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId,
            ProviderResourceKind.Track,
            "101");

        var lease = (await adapter.GetStreamLeaseAsync(
            Context(user), new(track, ProviderAudioQuality.HighResolution) { PreferOriginalContainer = true })).RequireValue();
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
            uri.PathAndQuery == "/api/stream/101?quality=alac-24-96");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_UsesAudioFileAndPreservesByteRanges(bool preferOriginalContainer)
    {
        var audio = Encoding.UTF8.GetBytes("apple audio file");
        var gateway = new GatewayHandler(audio, "audio/flac")
        {
            AdvertiseAudioFile = true,
            AdvertiseStream = false
        };
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/", Quality = "alac-24-96" };
        using var http = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(new StaticClientFactory(http), Options.Create(settings));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http, settings, discovery, AppleProviderTestFactory.Client());
        var context = Context(Guid.CreateVersion7());
        var track = new ProviderExternalResourceId(AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "101");

        var lease = (await adapter.GetStreamLeaseAsync(context,
            new(track, ProviderAudioQuality.HighResolution, 4) { PreferOriginalContainer = preferOriginalContainer })).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        request.Headers.Range = new(4, 9);
        using var response = await lease.ProtectedResponseFactory!(request, CancellationToken.None);

        Assert.IsAssignableFrom<IProviderDownloadStreamingCapability>(adapter);
        Assert.True(lease.SupportsByteRanges);
        Assert.True(lease.SupportsSeeking);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 4-9/16", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal("bytes=4-9", gateway.AudioRange);
        Assert.Contains(gateway.Requests, uri => uri.PathAndQuery ==
            "/api/audio/101?quality=alac-24-96" + (preferOriginalContainer ? "&original_container=true" : string.Empty));
        Assert.DoesNotContain(gateway.Requests, uri => uri.AbsolutePath.StartsWith("/api/stream/", StringComparison.Ordinal));
        Assert.True((await adapter.ProbeStreamAsync(context, new(track))).RequireValue().Available);
    }

    [Theory]
    [InlineData("audio/flac")]
    [InlineData("audio/mp4")]
    public async Task Stream_PrefersAudioFileAndAcceptsItsActualMedia(string contentType)
    {
        var gateway = new GatewayHandler([1, 2, 3], contentType) { AdvertiseAudioFile = true };
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        using var http = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(new StaticClientFactory(http), Options.Create(settings));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http, settings, discovery, AppleProviderTestFactory.Client());
        var track = new ProviderExternalResourceId(AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "101");

        var lease = (await adapter.GetStreamLeaseAsync(Context(Guid.CreateVersion7()), new(track))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, CancellationToken.None);

        Assert.Equal("/api/audio/101", lease.ProtectedSourceUri.AbsolutePath);
        Assert.Equal("unknown", lease.Media.Codec);
        Assert.Equal("unknown", lease.Media.Container);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Stream_MissingAudioFileUsesFallbackEligibleTransportStatus()
    {
        var gateway = new GatewayHandler([], "audio/flac")
        {
            AdvertiseAudioFile = true,
            AudioStatus = HttpStatusCode.BadGateway,
            AudioError = "audio_artifact_missing"
        };
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        using var http = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(new StaticClientFactory(http), Options.Create(settings));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http, settings, discovery, AppleProviderTestFactory.Client());
        var track = new ProviderExternalResourceId(AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "101");
        var lease = (await adapter.GetStreamLeaseAsync(Context(Guid.CreateVersion7()), new(track))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => lease.ProtectedResponseFactory!(request, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "account_mismatch")]
    [InlineData(HttpStatusCode.GatewayTimeout, "audio_prepare_timeout")]
    [InlineData(HttpStatusCode.BadGateway, "wrapper_unavailable")]
    public async Task Stream_PreservesAccountAndTransientErrors(HttpStatusCode status, string detail)
    {
        var gateway = new GatewayHandler([], "audio/flac")
        {
            AdvertiseAudioFile = true,
            AudioStatus = status,
            AudioError = detail
        };
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        using var http = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(new StaticClientFactory(http), Options.Create(settings));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http, settings, discovery, AppleProviderTestFactory.Client());
        var track = new ProviderExternalResourceId(AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "101");
        var lease = (await adapter.GetStreamLeaseAsync(Context(Guid.CreateVersion7()), new(track))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, CancellationToken.None);

        Assert.Equal(status, response.StatusCode);
        Assert.Contains(detail, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Lyrics_ReturnsTimedGamdlArtifactWhenGatewayAdvertisesIt()
    {
        var gateway = new GatewayHandler([], "audio/flac") { AdvertiseLyrics = true };
        var settings = new AppleDownloadSettings { BaseUrl = "https://gateway.test/" };
        var client = new HttpClient(gateway);
        var discovery = new AppleDownloadEndpointDiscovery(
            new StaticClientFactory(client), Options.Create(settings));
        var adapter = new AppleDownloadLyricsCapabilityAdapter(client, settings, discovery, AppleProviderTestFactory.Client());
        var user = Guid.CreateVersion7();
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "103");

        var outcome = await adapter.FetchLyricsAsync(Context(user), new(
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
        var adapter = new AppleDownloadLyricsCapabilityAdapter(client, settings, discovery, AppleProviderTestFactory.Client());
        var track = new ProviderExternalResourceId(
            AppleDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "404");

        var outcome = await adapter.FetchLyricsAsync(
            Context(Guid.CreateVersion7()),
            new(Guid.CreateVersion7(), track, preferredFormat: ProviderLyricsFormat.LineTimed));

        Assert.True(outcome.IsSuccess, outcome.Error?.Kind.ToString());
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, outcome.Value!.Availability);
        Assert.DoesNotContain(gateway.Requests, uri =>
            uri.AbsolutePath.StartsWith("/api/download/", StringComparison.Ordinal) ||
            uri.AbsolutePath.StartsWith("/api/stream/", StringComparison.Ordinal));
    }

    private static ProviderExecutionContext Context(Guid user) => new(
        new ProviderActorContext(ProviderActorKind.User, user,
            new ProviderBackendPrincipal("jellyfin", "primary", "user")),
        AppleDownloadCapabilityAdapter.StableProviderId,
        account: new(Guid.Parse("11111111-1111-1111-1111-111111111111"), AppleMusicClient.ProviderId, ProviderAccountScope.Personal, 1, ownerUserId: user, secretReferenceId: Guid.NewGuid()),
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
        public bool AdvertiseStream { get; init; } = true;
        public bool AdvertiseAudioFile { get; init; }
        public HttpStatusCode AudioStatus { get; init; } = HttpStatusCode.OK;
        public string? AudioError { get; init; }
        public string? AudioRange { get; private set; }
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
                    {"sidecarApiVersion":"2.0","capabilities":[
                      {"id":"metadata-search-song","state":"supported"},
                      {"id":"metadata-song","state":"supported"}
                      {{(AdvertiseStream ? ",{\"id\":\"stream-audio-song\",\"state\":\"supported\"}" : string.Empty)}}
                      {{(AdvertiseAudioFile ? ",{\"id\":\"audio-file\",\"state\":\"supported\"}" : string.Empty)}}
                      {{(AdvertiseDownload ? ",{\"id\":\"download-audio-song\",\"state\":\"supported\"}" : string.Empty)}}
                      {{(AdvertiseLyrics ? ",{\"id\":\"synced-lyrics-artifact\",\"state\":\"supported\"}" : string.Empty)}}
                    ]}
                    """),
                "/api/health" => Json("{\"staged\":true,\"daemon_running\":true,\"wrapper_healthy\":true,\"logged_in\":true}"),
                "/api/me" => Json("{\"authenticated\":true}"),
                _ when path.StartsWith("/api/download/", StringComparison.Ordinal) => Audio(),
                _ when path.StartsWith("/api/stream/", StringComparison.Ordinal) => Audio(),
                _ when path.StartsWith("/api/audio/", StringComparison.Ordinal) => AudioFile(request),
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

        private HttpResponseMessage AudioFile(HttpRequestMessage request)
        {
            AudioRange = request.Headers.Range?.ToString();
            if (AudioError != null)
            {
                var failure = Json($$"""{"detail":"{{AudioError}}"}""");
                failure.StatusCode = AudioStatus;
                return failure;
            }
            var response = Audio();
            if (request.Headers.Range != null)
            {
                response.StatusCode = HttpStatusCode.PartialContent;
                response.Content.Headers.ContentRange = new(4, 9, audio.Length);
            }
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
        public Task<ProviderDownloadArtifactEntity?> FindByJobAsync(Guid jobId, string provider, CancellationToken token) =>
            Task.FromResult(Artifacts.SingleOrDefault(item =>
                item.DurableJobId == jobId && item.ProviderId == provider));
        public Task<bool> SupersedeAsync(VerifiedProviderDownloadArtifact expected, long revision, int attempt,
            DateTimeOffset now, CancellationToken token) => Task.FromResult(false);
        public Task<IReadOnlyList<ProviderDownloadWorkspaceEntity>> FindDiscardableWorkspacesAsync(Guid owner,
            Guid job, string? provider, CancellationToken token, ProviderDownloadWorkspaceRequest? legacyWorkspace = null) =>
            Task.FromResult<IReadOnlyList<ProviderDownloadWorkspaceEntity>>([]);
        public Task MarkPlacedAsync(Guid id, Guid managedId, CancellationToken token) => Task.CompletedTask;
    }
}
