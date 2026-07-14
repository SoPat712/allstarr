using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Core.Identity;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class DurableRuntimeSettingsTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "allstarr-tests", $"runtime-settings-{Guid.NewGuid():N}.db");
    private TestFactory _factory = null!;
    private Guid _tenantId;
    private Guid _userId;
    private FakeClock _clock = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var options = new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite($"Data Source={_path}").Options;
        _factory = new(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        _tenantId = Guid.CreateVersion7(); _userId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-07-13T12:00:00Z"); _clock = new(now);
        db.Tenants.Add(new() { Id = _tenantId, Slug = "settings", Name = "Settings", CreatedAt = now });
        db.Users.Add(new() { Id = _userId, TenantId = _tenantId, DisplayName = "Admin", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Get_ReportsBootstrapFallbackThenTypedDurableOverride()
    {
        var service = CreateService(new Dictionary<string, string?> { ["Cache:SearchResultsMinutes"] = "7" });
        var fallback = await service.GetAsync(_tenantId, "Cache:SearchResultsMinutes");
        Assert.Equal(RuntimeSettingOrigin.Bootstrap, fallback.Origin);
        Assert.Equal(7, fallback.Value);
        Assert.Null(fallback.Revision);

        var applied = await service.ApplyBatchAsync(_tenantId,
            [new("Cache:SearchResultsMinutes", "12")], "webui", _userId);
        var persisted = Assert.Single(applied.Settings);
        Assert.Equal(RuntimeSettingOrigin.Durable, persisted.Origin);
        Assert.Equal(12, persisted.Value);
        Assert.Equal(1, persisted.Revision);
        Assert.Equal(1, applied.ChangeVersion);
        await using var db = await _factory.CreateDbContextAsync();
        var audit = await db.AuditEvents.SingleAsync();
        Assert.Equal("runtime-settings.batch-apply", audit.Action);
        Assert.DoesNotContain("12", audit.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyBatch_IsAtomicAndRejectsDeploymentOrSecretKeys()
    {
        var service = CreateService([]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(_tenantId,
            [new("Cache:LyricsDays", "20"), new("Jellyfin:ApiKey", "do-not-store")], "legacy-import"));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.TenantRuntimeSettings.ToListAsync());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(_tenantId,
            [new("Cache:LyricsDays", "0")], "webui"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(_tenantId,
            [new("Library:PlaylistsDirectory", "../outside")], "webui"));
        Assert.Empty(await db.TenantRuntimeSettings.ToListAsync());
    }

    [Fact]
    public async Task ApplyBatch_UsesCreateOnlyAndOptimisticRevisionContracts()
    {
        var service = CreateService([]);
        var created = await service.ApplyBatchAsync(_tenantId, [new("MusicBrainz:Enabled", "false")], "webui", _userId);
        Assert.Equal(1, Assert.Single(created.Settings).Revision);
        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() => service.ApplyBatchAsync(_tenantId,
            [new("MusicBrainz:Enabled", "true")], "webui", _userId));
        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() => service.ApplyBatchAsync(_tenantId,
            [new("MusicBrainz:Enabled", "true", 99)], "webui", _userId));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        var updated = await service.ApplyBatchAsync(_tenantId,
            [new("MusicBrainz:Enabled", "true", 1)], "webui", _userId);
        var value = Assert.Single(updated.Settings);
        Assert.Equal(2, value.Revision); Assert.Equal(true, value.Value);
        Assert.Equal(_clock.UtcNow, value.UpdatedAt);
    }

    [Fact]
    public async Task ProviderLists_AreNormalizedAndDuplicateIdsAreRejected()
    {
        var service = CreateService([]);
        var result = await service.ApplyBatchAsync(_tenantId,
            [new("Providers:StreamingOrder", " Deezer, QOBUZ ")], "legacy-import");
        Assert.Equal("deezer,qobuz", Assert.Single(result.Settings).NormalizedValue);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(_tenantId,
            [new("Providers:DownloadOrder", "deezer,DEEZER")], "legacy-import"));
    }

    [Fact]
    public async Task StageBatch_LeavesCommitAndChangePublicationToOuterTransaction()
    {
        var service = CreateService([]);
        await using var db = await _factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var staged = await service.StageBatchAsync(db, _tenantId,
            [new("SpotifyApi:Enabled", "true")], "legacy-import", _userId);
        Assert.Single(staged);
        await db.SaveChangesAsync(); await transaction.RollbackAsync();
        Assert.False(await db.TenantRuntimeSettings.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task DefaultTenantProjector_AppliesDurableOptionsAndRoutingWithoutTouchingSecrets()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:SearchResultsMinutes"] = "1",
            ["Deezer:Arl"] = "bootstrap-secret",
            ["MULTI_PROVIDER_STREAMING_ORDER"] = "qobuz"
        }).Build();
        var signal = new RuntimeSettingsChangeSignal();
        var service = new DurableRuntimeSettingsService(_factory, configuration, _clock, signal);
        await service.ApplyBatchAsync(_tenantId,
        [
            new("Cache:SearchResultsMinutes", "15"), new("Deezer:Quality", "MP3_320"),
            new("Providers:StreamingOrder", "deezer,qobuz"), new("Library:DownloadMode", "Album"),
            new("AppleDownload:BaseUrl", "http://apple-gateway.lan/base"),
            new("AppleDownload:Quality", "alac-24-96")
        ], "webui", _userId);
        var cache = new CacheSettings(); var deezer = new DeezerSettings { Arl = "bootstrap-secret" };
        var apple = new AppleDownloadSettings();
        var jellyfin = new JellyfinSettings(); var subsonic = new SubsonicSettings();
        var identity = new IdentityOptions { DefaultTenantId = _tenantId.ToString() };
        var projector = new DefaultTenantRuntimeSettingsProjector(service, signal, identity, configuration,
            Options.Create(cache), Options.Create(deezer), Options.Create(new QobuzSettings()), Options.Create(new SquidWTFSettings()),
            Options.Create(apple), Options.Create(new SpotifyApiSettings()), Options.Create(new SpotifyImportSettings()),
            Options.Create(new MusicBrainzSettings()), Options.Create(new ScrobblingSettings()), Options.Create(jellyfin), Options.Create(subsonic),
            NullLogger<DefaultTenantRuntimeSettingsProjector>.Instance);
        await projector.StartAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 50 && cache.SearchResultsMinutes != 15; attempt++) await Task.Delay(10);
        await service.ApplyBatchAsync(_tenantId, [new("Cache:SearchResultsMinutes", "22", 1)], "webui", _userId);
        for (var attempt = 0; attempt < 50 && cache.SearchResultsMinutes != 22; attempt++) await Task.Delay(10);
        await projector.StopAsync(CancellationToken.None);

        Assert.Equal(22, cache.SearchResultsMinutes); Assert.Equal("MP3_320", deezer.Quality);
        Assert.Equal("bootstrap-secret", deezer.Arl);
        Assert.Equal("deezer,qobuz", configuration["MULTI_PROVIDER_STREAMING_ORDER"]);
        Assert.Equal("http://apple-gateway.lan/base", apple.BaseUrl);
        Assert.Equal("alac-24-96", apple.Quality);
        var handler = new RecordingHandler();
        var discovery = new AppleDownloadEndpointDiscovery(
            new RecordingFactory(new HttpClient(handler)), Options.Create(apple));
        var discoveryResult = await discovery.DiscoverAsync();
        Assert.Equal(AppleDownloadEndpointState.Incompatible, discoveryResult.State);
        Assert.Equal("/base/api/capabilities", handler.RequestedPath);
        Assert.Equal(DownloadMode.Album, jellyfin.DownloadMode); Assert.Equal(DownloadMode.Album, subsonic.DownloadMode);
    }

    private DurableRuntimeSettingsService CreateService(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new(_factory, config, _clock, new RuntimeSettingsChangeSignal());
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = now; }
    private sealed class RecordingFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? RequestedPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
