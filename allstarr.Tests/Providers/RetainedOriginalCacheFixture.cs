using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

internal sealed class RetainedOriginalCacheFixture : IAsyncDisposable
{
    private readonly SqliteTestDatabase database;
    public readonly Guid Account = Guid.CreateVersion7();
    public readonly ManualTimeProvider Clock = new(DateTimeOffset.UtcNow);
    public readonly ProviderExternalResourceId Track = new("deezer", ProviderResourceKind.Track, "original-track");
    public readonly byte[] Bytes = Enumerable.Range(0, 65_536).Select(value => (byte)(value % 251)).ToArray();
    public readonly SubsonicSettings Settings;
    public readonly IConfiguration Configuration;
    public readonly Factory Contexts;
    public readonly EfDownloadedSongMappingStore Mappings;
    public readonly LocalLibraryService Library;
    public readonly PlaybackDeliveryActivityStore Activity = new();
    public int LeaseCalls;
    public ManagedTrackCacheService Cache { get; private set; }
    public string Root => Path.GetDirectoryName(database.DatabasePath)!;
    public long MaximumBytes = 16 * 1024 * 1024;
    public DownloadedSongMappingScope OriginalScope => new(Account, ProviderAudioQuality.Any,
        DownloadedSongRepresentation.Original, 7);
    public ProtocolExecutionContext Context => new(ProtocolKind.Jellyfin, "fixture", "listener",
        new(Guid.Parse("20000000-0000-0000-0000-000000000001"), "jellyfin", "fixture", "listener", "Listener", false),
        "original-cache", Clock.GetUtcNow().AddMinutes(5), default, new("fixture", "device"))
    { IsOriginalDownload = true, RetainOriginalMedia = true };

    private RetainedOriginalCacheFixture(SqliteTestDatabase database, StorageMode mode)
    {
        this.database = database;
        Contexts = new(database.Options);
        Mappings = new(Contexts);
        Settings = new() { StorageMode = mode, CacheDurationHours = 72 };
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = Root }).Build();
        Library = new(Configuration, new NoNetworkFactory(), Options.Create(Settings), Mappings,
            NullLogger<LocalLibraryService>.Instance);
        Cache = CreateCache();
    }

    public static async Task<RetainedOriginalCacheFixture> CreateAsync(StorageMode mode = StorageMode.Cache) =>
        new(await SqliteTestDatabase.CreateAsync(), mode);

    private ManagedTrackCacheService CreateCache() => new(Configuration, Options.Create(Settings), Library,
        NullLogger<ManagedTrackCacheService>.Instance, timeProvider: Clock, mappings: Mappings,
        audioMaximumBytes: () => MaximumBytes, audioVerifier: FixtureAudioVerifier.Instance);

    public async Task RestartCacheAsync()
    {
        await Cache.DisposeAsync();
        Cache = CreateCache();
    }

    public HttpResponseMessage Response(byte[]? bytes = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? Bytes) };
        response.Content.Headers.ContentType = new("audio/flac");
        return response;
    }

    public ProtocolProviderStream Stream(HttpResponseMessage response, bool ranges = true, long revision = 7) =>
        new(response, new("original", new Uri("https://media.example.test/original"), Context.Deadline,
                ranges, ranges, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry),
            Track.ProviderId, Track.Value, Account, ServingAccountRevision: revision);

    public Task WrapAsync(ProtocolProviderStream stream, ProtocolExecutionContext? context = null, bool headOnly = false) =>
        Cache.WrapAsync(stream, context ?? Context, Track.ProviderId, Track.Value, headOnly,
            () => Task.FromResult<Song?>(new() { Title = "Original track", Artist = "Artist", Album = "Album" }), default);

    public Task<ProtocolProviderStream?> OpenAsync(DownloadedSongMappingScope? scope = null, long revision = 7,
        bool retained = true) => Cache.TryOpenAsync(Track, scope ?? OriginalScope, default, revision,
            originalDownload: true, retainOriginalMedia: retained);

    public async Task<string> SeedPlaybackAsync(byte[] bytes)
    {
        var path = Path.Combine(Root, "cache", "converted.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        await Library.RegisterDownloadedSongAsync(new(Account, ProviderAudioQuality.Any), new()
        { ExternalProvider = Track.ProviderId, ExternalId = Track.Value, Title = "Playback", Artist = "Artist", Album = "Album" }, path);
        return path;
    }

    public ProtocolProviderGateway Gateway(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> open,
        bool supportsRanges = true)
    {
        var source = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        source.SetupGet(item => item.ProviderId).Returns(Track.ProviderId);
        source.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        var lease = new ProviderStreamLease("selected-original", new Uri("https://media.example.test/original"), Context.Deadline,
            supportsRanges, supportsRanges, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry,
            (request, token) => open(request, token));
        source.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns((ProviderExecutionContext _, ProviderStreamLeaseRequest request) =>
            {
                Assert.Equal(ProviderAudioQuality.Any, request.RequestedQuality);
                Interlocked.Increment(ref LeaseCalls);
                return Task.FromResult(ProviderOutcome<ProviderStreamLease>.Success(lease));
            });
        var descriptor = new ProviderDescriptor(Track.ProviderId, "Fixture", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getStreamLease"])], new());
        var registry = new ProviderRegistry([new(descriptor, [source.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderStreamingCapability>(request,
                [new(0, descriptor, descriptor.Capabilities.Single(), source.Object,
                    new(request.Actor, Track.ProviderId, new(Account, Track.ProviderId, ProviderAccountScope.Shared, 7,
                        secretReferenceId: Guid.NewGuid()), request.Policy, request.OperationId, request.CorrelationId,
                        request.Deadline, request.CancellationToken), Track)],
                new(request.CorrelationId, ProviderCapabilityKind.Streaming, Track.ProviderId, Account, [])));
        return new(router.Object, registry, new NoNetworkFactory(), Configuration, managedTrackCache: Cache,
            playbackActivity: Activity, timeProvider: Clock, audioVerifier: FixtureAudioVerifier.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await Cache.DisposeAsync();
        Activity.Dispose();
        await database.DisposeAsync();
    }

    internal sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }

    private sealed class NoNetworkFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoNetworkHandler());
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fixture does not permit network access.");
    }
}
