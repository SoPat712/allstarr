using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Capabilities;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class DurableRuntimeSettingsTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private Guid _tenantId;
    private Guid _userId;
    private FakeClock _clock = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
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

        var lyrics = await service.ApplyBatchAsync(_tenantId,
            [new("Providers:LyricsOrder", "spotify,lyricsplus,apple-download,lrclib")], "legacy-import");
        Assert.Equal("spotify,apple-download,lrclib", Assert.Single(lyrics.Settings).NormalizedValue);
    }

    [Fact]
    public async Task OptionalProviderSettings_AllowShippedEmptyDefaultsAndDurableDisable()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["AppleDownload:BaseUrl"] = string.Empty,
            ["AppleDownload:Quality"] = string.Empty,
            ["Deezer:Quality"] = string.Empty,
            ["Qobuz:Quality"] = string.Empty
        });

        var settings = await service.GetManyAsync(_tenantId, RuntimeSettingCatalog.Definitions.Keys);
        var bootstrap = settings["AppleDownload:BaseUrl"];
        Assert.Equal(RuntimeSettingOrigin.Bootstrap, bootstrap.Origin);
        Assert.Equal(string.Empty, bootstrap.Value);
        Assert.Equal(string.Empty, settings["Deezer:Quality"].Value);
        Assert.Equal(string.Empty, settings["Qobuz:Quality"].Value);

        var applied = await service.ApplyBatchAsync(_tenantId,
        [
            new("AppleDownload:BaseUrl", string.Empty),
            new("Deezer:Quality", string.Empty),
            new("Qobuz:Quality", string.Empty)
        ], "webui", _userId);
        Assert.All(applied.Settings, durable =>
        {
            Assert.Equal(RuntimeSettingOrigin.Durable, durable.Origin);
            Assert.Equal(string.Empty, durable.Value);
            Assert.Equal("", durable.NormalizedValue);
        });
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
            ["AppleDownload:BaseUrl"] = "http://compose-apple-gateway:8000",
            ["MULTI_PROVIDER_STREAMING_ORDER"] = "qobuz"
        }).Build();
        var signal = new RuntimeSettingsChangeSignal();
        var service = new DurableRuntimeSettingsService(_factory, configuration, _clock, signal);
        await service.ApplyBatchAsync(_tenantId,
        [
            new("Cache:SearchResultsMinutes", "15"), new("Deezer:Quality", "FLAC"),
            new("Providers:StreamingOrder", "deezer,qobuz"), new("Library:DownloadMode", "Album"),
            new("AppleDownload:BaseUrl", "http://apple-gateway.lan/base"),
            new("AppleDownload:Quality", "alac-24-96"),
            new("Qobuz:Quality", "FLAC_24_LOW"),
            new("Matching:LocalPreferencePercent", "11"),
            new("Matching:ExtensionPenaltyPercent", "4"),
            new("SpotifyApi:LyricsApiUrl", "http://spotify-lyrics:8080"),
            new("SpotifyImport:Playlists", "[[\"Discover Weekly\",\"source-id\",\"target-id\",\"last\",\"0 8 * * *\"]]")
        ], "webui", _userId);
        var cache = new CacheSettings(); var deezer = new DeezerSettings { Arl = "bootstrap-secret" };
        var qobuz = new QobuzSettings();
        var apple = new AppleDownloadSettings { BaseUrl = "http://compose-apple-gateway:8000" };
        var spotifyApi = new SpotifyApiSettings();
        var spotifyImport = new SpotifyImportSettings();
        var jellyfin = new JellyfinSettings(); var subsonic = new SubsonicSettings();
        var identity = new IdentityOptions { DefaultTenantId = _tenantId.ToString() };
        var matching = new TrackMatchPolicy();
        var projector = new DefaultTenantRuntimeSettingsProjector(service, signal, identity, configuration,
            Options.Create(cache), Options.Create(deezer), Options.Create(qobuz), Options.Create(apple),
            Options.Create(spotifyApi), Options.Create(spotifyImport),
            Options.Create(new MusicBrainzSettings()), Options.Create(new ScrobblingSettings()), Options.Create(jellyfin), Options.Create(subsonic),
            matching,
            NullLogger<DefaultTenantRuntimeSettingsProjector>.Instance);
        await projector.StartAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 50 && cache.SearchResultsMinutes != 15; attempt++) await Task.Delay(10);
        var migrated = await service.GetAsync(_tenantId, AudioQualityPolicy.SettingKey);
        Assert.Equal(RuntimeSettingOrigin.Durable, migrated.Origin);
        Assert.Equal("HiResLossless", migrated.Value);
        await service.ApplyBatchAsync(_tenantId,
            [new("Cache:SearchResultsMinutes", "22", 1), new(AudioQualityPolicy.SettingKey, "CdLossless", migrated.Revision)],
            "webui", _userId);
        for (var attempt = 0; attempt < 50 && (cache.SearchResultsMinutes != 22 || apple.Quality != "alac-16-44"); attempt++) await Task.Delay(10);
        await projector.StopAsync(CancellationToken.None);

        Assert.Equal(22, cache.SearchResultsMinutes); Assert.Equal("FLAC", deezer.Quality);
        Assert.Equal("bootstrap-secret", deezer.Arl);
        Assert.Equal("deezer,qobuz", configuration["MULTI_PROVIDER_STREAMING_ORDER"]);
        Assert.Equal("http://compose-apple-gateway:8000", apple.BaseUrl);
        Assert.Equal("alac-16-44", apple.Quality);
        Assert.Equal("FLAC_16", qobuz.Quality);
        Assert.Equal(0.11, matching.LocalPreferenceBoost);
        Assert.Equal(0.04, matching.ExtensionPreferencePenalty);
        Assert.Equal("http://spotify-lyrics:8080", spotifyApi.LyricsApiUrl);
        var importedPlaylist = Assert.Single(spotifyImport.Playlists);
        Assert.Equal("Discover Weekly", importedPlaylist.Name);
        Assert.Equal("target-id", importedPlaylist.JellyfinId);
        Assert.Equal(LocalTracksPosition.Last, importedPlaylist.LocalTracksPosition);
        var handler = new RecordingHandler();
        var discovery = new AppleDownloadEndpointDiscovery(
            new RecordingFactory(new HttpClient(handler)), Options.Create(apple));
        var discoveryResult = await discovery.DiscoverAsync();
        Assert.Equal(AppleDownloadEndpointState.Incompatible, discoveryResult.State);
        Assert.Equal("/api/capabilities", handler.RequestedPath);
        Assert.Equal(DownloadMode.Album, jellyfin.DownloadMode); Assert.Equal(DownloadMode.Album, subsonic.DownloadMode);
    }

    private DurableRuntimeSettingsService CreateService(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new(_factory, config, _clock, new RuntimeSettingsChangeSignal());
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
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
