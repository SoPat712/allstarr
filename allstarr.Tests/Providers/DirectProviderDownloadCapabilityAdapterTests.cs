using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Providers.Qobuz;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Deezer;
using allstarr.Services.Local;
using allstarr.Services.Qobuz;
using allstarr.Services.Subsonic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace allstarr.Tests;

public sealed class DirectProviderDownloadCapabilityAdapterTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "allstarr-direct-downloads", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Deezer_DecryptsRetriesAndReusesTheHostOwnedArtifact()
    {
        var trackId = "42";
        var plain = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        var handler = new DeezerHandler(EncryptDeezer(plain, trackId));
        var client = new HttpClient(handler);
        var service = DeezerService(client);
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var adapter = new DeezerDownloadCapabilityAdapter(
            client,
            new RawSecretAccessor("""{"arl":"selected-arl","arlFallback":null}"""),
            service,
            resolver,
            configuredQuality: "FLAC",
            maximumArtifactBytes: 1024 * 1024);
        var (context, workspace, job) = await SetupAsync(resolver, DeezerDownloadCapabilityAdapter.StableProviderId);
        var track = new ProviderExternalResourceId(
            DeezerDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, trackId);
        var progress = new InlineProgress<ProviderDownloadProgress>();

        var availability = await adapter.CheckAvailabilityAsync(context, new(track));
        var first = (await adapter.DownloadAsync(context, new(
            track, job, workspace.Reference, ProviderAudioQuality.Lossless), progress)).RequireValue();
        var second = (await adapter.DownloadAsync(context, new(
            track, job, workspace.Reference, ProviderAudioQuality.Lossless))).RequireValue();

        Assert.Equal(ProviderDownloadAvailabilityState.Available, availability.RequireValue().State);
        Assert.Equal(first, second);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant(), first.Sha256);
        Assert.Equal("flac", first.Media.Codec);
        var verified = await resolver.ResolveAsync(workspace.Reference, first);
        Assert.Equal(plain, await File.ReadAllBytesAsync(verified.SourcePath));
        Assert.Single(store.Artifacts);
        Assert.Equal(3, handler.MediaRequests);
        Assert.Contains(handler.Requests, request =>
            request.Host == "www.deezer.com" && request.Cookie == "arl=selected-arl");
        Assert.Contains(ProviderDownloadProgressStage.Resolving, progress.Values.Select(item => item.Stage));
        Assert.Contains(ProviderDownloadProgressStage.Transferring, progress.Values.Select(item => item.Stage));
        Assert.Contains(ProviderDownloadProgressStage.Verifying, progress.Values.Select(item => item.Stage));
        Assert.Equal(ProviderDownloadProgressStage.Completed, progress.Values[^1].Stage);
    }

    [Fact]
    public async Task Qobuz_UsesSelectedAccountAndPreservesSignedFlacFacts()
    {
        var audio = Encoding.UTF8.GetBytes("fixture qobuz flac bytes");
        var handler = new QobuzHandler(audio);
        var client = new HttpClient(handler);
        var factory = Factory(client);
        var bundle = new Mock<QobuzBundleService>(
            factory, NullLogger<QobuzBundleService>.Instance)
        { CallBase = false };
        bundle.Setup(item => item.GetAppIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("123456789");
        bundle.Setup(item => item.GetSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["fixture-signing-secret"]);
        var service = QobuzService(factory, bundle.Object);
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var adapter = new QobuzDownloadCapabilityAdapter(
            client,
            new RawSecretAccessor("""{"userAuthToken":"selected-token","userId":"selected-user"}"""),
            service,
            resolver,
            configuredQuality: "FLAC_24_LOW",
            maximumArtifactBytes: 1024 * 1024);
        var (context, workspace, job) = await SetupAsync(resolver, QobuzDownloadCapabilityAdapter.StableProviderId);
        var track = new ProviderExternalResourceId(
            QobuzDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "77");

        var output = (await adapter.DownloadAsync(context, new(
            track, job, workspace.Reference, ProviderAudioQuality.HighResolution))).RequireValue();

        Assert.Equal("flac", output.Media.Codec);
        Assert.Equal(24, output.Media.BitDepth);
        Assert.Equal(96_000, output.Media.SampleRate);
        Assert.Contains(handler.Requests, request =>
            request.Host == "www.qobuz.com" && request.UserAuthToken == "selected-token");
        var verified = await resolver.ResolveAsync(workspace.Reference, output);
        Assert.Equal(audio, await File.ReadAllBytesAsync(verified.SourcePath));
        Assert.Single(store.Artifacts);
    }

    [Fact]
    public async Task DeezerStream_DecryptsIncrementallyAndDoesNotAdvertiseRanges()
    {
        const string trackId = "42";
        var plain = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
        var handler = new DeezerHandler(
            EncryptDeezer(plain, trackId),
            failFirstMediaRequest: false,
            invalidFirstMediaResponse: true);
        var client = new HttpClient(handler);
        var service = DeezerService(client);
        var adapter = new DeezerStreamingCapabilityAdapter(
            client,
            new RawSecretAccessor("""{"arl":"selected-arl","arlFallback":null}"""),
            service,
            configuredQuality: "FLAC");
        var resolver = Resolver(new MemoryStore());
        var (context, _, _) = await SetupAsync(resolver, DeezerDownloadCapabilityAdapter.StableProviderId);
        var track = new ProviderExternalResourceId(
            DeezerDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, trackId);

        var lease = (await adapter.GetStreamLeaseAsync(
            context, new(track, ProviderAudioQuality.Lossless))).RequireValue();

        Assert.False(lease.SupportsByteRanges);
        Assert.False(lease.SupportsSeeking);
        Assert.Equal(ProviderStreamRetryBehavior.RefreshLease, lease.RetryBehavior);
        using (var invalidRequest = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                lease.ProtectedResponseFactory!(invalidRequest, CancellationToken.None));
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri))
        using (var response = await lease.ProtectedResponseFactory!(request, CancellationToken.None))
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            var prefix = new byte[17];
            Assert.Equal(prefix.Length, await stream.ReadAsync(prefix));
            Assert.Equal(plain[..prefix.Length], prefix);
            Assert.Equal(2048, handler.MediaBytesRead);
            Assert.True(handler.MediaBytesRead < plain.Length);

            var remainder = new byte[2048 - prefix.Length];
            Assert.Equal(remainder.Length, await stream.ReadAsync(remainder));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                stream.ReadExactlyAsync(new byte[1], canceled.Token).AsTask());
        }

        using var completeRequest = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var completeResponse = await lease.ProtectedResponseFactory!(
            completeRequest, CancellationToken.None);
        Assert.Equal(plain, await completeResponse.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task QobuzStream_PreservesARealUpstreamByteRange()
    {
        var audio = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        var handler = new QobuzHandler(audio, invalidFirstMediaResponse: true);
        var client = new HttpClient(handler);
        var factory = Factory(client);
        var bundle = new Mock<QobuzBundleService>(
            factory, NullLogger<QobuzBundleService>.Instance)
        { CallBase = false };
        bundle.Setup(item => item.GetAppIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync("123456789");
        bundle.Setup(item => item.GetSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["fixture-signing-secret"]);
        var adapter = new QobuzStreamingCapabilityAdapter(
            client,
            new RawSecretAccessor("""{"userAuthToken":"selected-token","userId":"selected-user"}"""),
            QobuzService(factory, bundle.Object),
            configuredQuality: "FLAC_24_LOW");
        var resolver = Resolver(new MemoryStore());
        var (context, _, _) = await SetupAsync(resolver, QobuzDownloadCapabilityAdapter.StableProviderId);
        var track = new ProviderExternalResourceId(
            QobuzDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "77");
        var lease = (await adapter.GetStreamLeaseAsync(
            context, new(track, ProviderAudioQuality.HighResolution, rangeStart: 10))).RequireValue();
        using (var invalidRequest = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                lease.ProtectedResponseFactory!(invalidRequest, CancellationToken.None));
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        request.Headers.Range = new RangeHeaderValue(10, 19);

        using var response = await lease.ProtectedResponseFactory!(request, CancellationToken.None);

        Assert.True(lease.SupportsByteRanges);
        Assert.True(lease.SupportsSeeking);
        Assert.Equal(ProviderStreamRetryBehavior.RefreshLease, lease.RetryBehavior);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-19/64", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(audio[10..20], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes=10-19", handler.MediaRange);
    }

    [Fact]
    public async Task InvalidAccountSecretStopsBeforeAnyProviderRequest()
    {
        var handler = new DeezerHandler([]);
        var client = new HttpClient(handler);
        var resolver = Resolver(new MemoryStore());
        var adapter = new DeezerDownloadCapabilityAdapter(
            client,
            new RawSecretAccessor("{}"),
            DeezerService(client),
            resolver,
            configuredQuality: null,
            maximumArtifactBytes: 1024);
        var (context, _, _) = await SetupAsync(resolver, DeezerDownloadCapabilityAdapter.StableProviderId);
        var track = new ProviderExternalResourceId(
            DeezerDownloadCapabilityAdapter.StableProviderId, ProviderResourceKind.Track, "missing-account");

        var outcome = await adapter.CheckAvailabilityAsync(context, new(track));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, outcome.Error!.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(ProviderAudioQuality.Lossy, "FLAC", "MP3_320")]
    [InlineData(ProviderAudioQuality.Lossless, "MP3_128", "MP3_128")]
    public void DeezerQuality_DoesNotExceedTheConfiguredCeiling(
        ProviderAudioQuality requested,
        string configured,
        string expected) =>
        Assert.Equal(expected, DeezerDownloadCapabilityAdapter.Quality(requested, configured));

    [Theory]
    [InlineData(ProviderAudioQuality.Lossy, "FLAC_24_HIGH", "MP3_320")]
    [InlineData(ProviderAudioQuality.Lossless, "FLAC_24_HIGH", "FLAC_16")]
    [InlineData(ProviderAudioQuality.HighResolution, "FLAC_24_LOW", "FLAC_24_LOW")]
    [InlineData(ProviderAudioQuality.Lossless, "MP3_320", "MP3_320")]
    public void QobuzQuality_DoesNotExceedTheConfiguredCeiling(
        ProviderAudioQuality requested,
        string configured,
        string expected) =>
        Assert.Equal(expected, QobuzDownloadCapabilityAdapter.Quality(requested, configured));

    private ProviderDownloadArtifactResolver Resolver(MemoryStore store) =>
        new(store, new() { RootPath = root, MaximumArtifactBytes = 1024 * 1024 });

    private async Task<(ProviderExecutionContext Context, ProviderDownloadWorkspace Workspace, Guid Job)>
        SetupAsync(ProviderDownloadArtifactResolver resolver, string providerId)
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        var context = new ProviderExecutionContext(
            new ProviderActorContext(tenant, ProviderActorKind.User, user,
                new ProviderBackendPrincipal("jellyfin", "primary", "user")),
            providerId,
            new ProviderAccountContext(
                account,
                providerId,
                ProviderAccountScope.User,
                revision: 1,
                tenantId: tenant,
                ownerUserId: user,
                secretReferenceId: Guid.CreateVersion7()),
            library: null,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(
                    ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, allowTranscode: false),
                ProviderExplicitContentPolicy.Allow,
                allowFallback: false,
                allowSharedAccount: false,
                allowManagedDownloads: true,
                [providerId]),
            "direct-download-test",
            "direct-download-correlation",
            DateTimeOffset.UtcNow.AddMinutes(2),
            CancellationToken.None,
            "direct-download-idempotency");
        var workspace = await resolver.CreateWorkspaceAsync(new(
            tenant, user, job, providerId, account, "direct-download-idempotency"));
        return (context, workspace, job);
    }

    private DeezerDownloadService DeezerService(HttpClient client)
    {
        var configuration = Configuration();
        return new(
            Factory(client),
            configuration,
            Mock.Of<ILocalLibraryService>(),
            Mock.Of<IMusicMetadataService>(),
            Options.Create(new SubsonicSettings()),
            Options.Create(new DeezerSettings { Quality = "FLAC", MinRequestIntervalMs = 0 }),
            Mock.Of<IServiceProvider>(),
            NullLogger<DeezerDownloadService>.Instance);
    }

    private QobuzDownloadService QobuzService(
        IHttpClientFactory factory,
        QobuzBundleService bundle) => new(
        factory,
        Configuration(),
        Mock.Of<ILocalLibraryService>(),
        Mock.Of<IMusicMetadataService>(),
        bundle,
        Options.Create(new SubsonicSettings()),
        Options.Create(new QobuzSettings { Quality = "FLAC_24_LOW", MinRequestIntervalMs = 0 }),
        Mock.Of<IServiceProvider>(),
        NullLogger<QobuzDownloadService>.Instance);

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Library:DownloadPath"] = root
        })
        .Build();

    private static IHttpClientFactory Factory(HttpClient client)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        return factory.Object;
    }

    private static byte[] EncryptDeezer(byte[] plain, string trackId)
    {
        var output = plain.ToArray();
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(trackId))).ToLowerInvariant();
        const string secret = "g4el58wc0zvf9na1";
        var key = Enumerable.Range(0, 16)
            .Select(index => (byte)(hash[index] ^ hash[index + 16] ^ secret[index]))
            .ToArray();
        for (var offset = 0; offset + 2048 <= output.Length; offset += 6144)
        {
            var cipher = new CbcBlockCipher(new BlowfishEngine());
            cipher.Init(true, new ParametersWithIV(
                new KeyParameter(key), [0, 1, 2, 3, 4, 5, 6, 7]));
            for (var block = offset; block < offset + 2048; block += cipher.GetBlockSize())
                cipher.ProcessBlock(output, block, output, block);
        }
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class RawSecretAccessor(string json) : IProviderAccountSecretAccessor
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes(json);
        public Task<T> UseAsync<T>(
            ProviderAccountContext account,
            Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken) => operation(bytes);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class DeezerHandler(
        byte[] encrypted,
        bool failFirstMediaRequest = true,
        bool invalidFirstMediaResponse = false) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];
        public int MediaRequests { get; private set; }
        public long MediaBytesRead { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new(
                request.RequestUri!.Host,
                request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null,
                null));
            var uri = request.RequestUri!;
            HttpResponseMessage response;
            if (uri.Host == "www.deezer.com")
            {
                response = Json("""
                    {"results":{"checkForm":"form","USER":{"OPTIONS":{"license_token":"license"}}}}
                    """);
            }
            else if (uri.Host == "api.deezer.com")
            {
                response = Json("""
                    {"track_token":"track-token","title":"Fixture","artist":{"name":"Artist"}}
                    """);
            }
            else if (uri.Host == "media.deezer.com")
            {
                response = Json("""
                    {"data":[{"media":[{"format":"FLAC","sources":[{"url":"https://cdn.deezer.test/audio/42"}]}]}]}
                    """);
            }
            else if (uri.Host == "cdn.deezer.test")
            {
                MediaRequests++;
                response = invalidFirstMediaResponse && MediaRequests == 1
                    ? Audio(encrypted, "text/html")
                    : failFirstMediaRequest && MediaRequests == 1
                    ? new(HttpStatusCode.ServiceUnavailable)
                    : Audio(new TrackingReadStream(
                        encrypted, count => MediaBytesRead += count), "application/octet-stream");
            }
            else
            {
                response = new(HttpStatusCode.NotFound);
            }
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class QobuzHandler(
        byte[] audio,
        bool invalidFirstMediaResponse = false) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];
        public string? MediaRange { get; private set; }
        private int mediaRequests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new(
                request.RequestUri!.Host,
                null,
                request.Headers.TryGetValues("X-User-Auth-Token", out var tokens)
                    ? tokens.Single()
                    : null));
            var uri = request.RequestUri!;
            var response = uri.Host switch
            {
                "www.qobuz.com" => Json("""
                    {"url":"https://cdn.qobuz.test/audio/77","mime_type":"audio/flac","bit_depth":24,"sampling_rate":96,"sample":false}
                    """),
                "cdn.qobuz.test" => QobuzAudio(request),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private HttpResponseMessage QobuzAudio(HttpRequestMessage request)
        {
            if (invalidFirstMediaResponse && ++mediaRequests == 1)
                return Audio(audio, "text/html");
            MediaRange = request.Headers.Range?.ToString();
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            if (range?.From is not long from || range.To is not long to)
                return Audio(audio, "audio/flac");
            var response = Audio(audio[(int)from..((int)to + 1)], "audio/flac");
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new(from, to, audio.Length);
            return response;
        }
    }

    private sealed record RequestSnapshot(string Host, string? Cookie, string? UserAuthToken);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Audio(byte[] value, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(value)
        };
        response.Content.Headers.ContentType = new(contentType);
        return response;
    }

    private static HttpResponseMessage Audio(Stream value, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(value)
        };
        response.Content.Headers.ContentType = new(contentType);
        return response;
    }

    private sealed class TrackingReadStream(byte[] value, Action<int> observed)
        : MemoryStream(value, writable: false)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Read(buffer.Span);
            observed(count);
            return ValueTask.FromResult(count);
        }
    }

    private sealed class MemoryStore : IProviderDownloadArtifactStore
    {
        public List<ProviderDownloadWorkspaceEntity> Workspaces { get; } = [];
        public List<ProviderDownloadArtifactEntity> Artifacts { get; } = [];

        public Task<ProviderDownloadWorkspaceEntity> CreateWorkspaceAsync(
            ProviderDownloadWorkspaceEntity value,
            CancellationToken token)
        {
            var existing = Workspaces.SingleOrDefault(item => item.WorkspaceId == value.WorkspaceId);
            if (existing != null) return Task.FromResult(existing);
            Workspaces.Add(value);
            return Task.FromResult(value);
        }

        public Task<ProviderDownloadWorkspaceEntity?> GetWorkspaceAsync(
            string id,
            CancellationToken token) =>
            Task.FromResult(Workspaces.SingleOrDefault(item => item.WorkspaceId == id));

        public Task<ProviderDownloadArtifactEntity> AddVerifiedAsync(
            ProviderDownloadArtifactEntity value,
            CancellationToken token)
        {
            var existing = Artifacts.SingleOrDefault(item =>
                item.WorkspaceRecordId == value.WorkspaceRecordId &&
                item.ProviderArtifactId == value.ProviderArtifactId);
            if (existing != null) return Task.FromResult(existing);
            Artifacts.Add(value);
            return Task.FromResult(value);
        }

        public Task<ProviderDownloadArtifactEntity?> FindByJobAsync(
            Guid tenantId,
            Guid jobId,
            string provider,
            CancellationToken token) =>
            Task.FromResult(Artifacts.SingleOrDefault(item =>
                item.TenantId == tenantId && item.DurableJobId == jobId && item.ProviderId == provider));

        public Task MarkPlacedAsync(Guid id, Guid managedId, CancellationToken token) => Task.CompletedTask;
    }
}
