using System.Collections.Concurrent;
using System.Net;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.SquidWTF;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class ProviderStatusManagerTests
{
    [Fact]
    public void SpotifyLyricsSidecar_DoesNotRequireDirectSpotifyApiMode()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>
            {
                ["MULTI_PROVIDER_LYRICS_ORDER"] = "spotify,lrclib"
            },
            spotifySettings: new SpotifyApiSettings
            {
                Enabled = false,
                SessionCookie = string.Empty,
                LyricsApiUrl = "http://lyrics-sidecar:8080"
            });

        var status = manager.GetStatus("spotify", ProviderCapabilities.Lyrics);

        Assert.Equal(ProviderConfigurationState.Configured, status.Configuration);
        Assert.Contains("spotify", manager.GetEnabledLyricsProviders());
    }

    [Fact]
    public void DisabledProvider_IsRemovedFromEveryCurrentCapabilityLane()
    {
        var manager = CreateManager(new Dictionary<string, string?>
        {
            ["MULTI_PROVIDER_METADATA_ORDER"] = "deezer",
            ["MULTI_PROVIDER_ENABLED_SEARCH"] = "deezer",
            ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "deezer",
            ["MULTI_PROVIDER_STREAMING_ORDER"] = "deezer",
            ["MULTI_PROVIDER_PLAYLIST_ORDER"] = "deezer",
            ["MULTI_PROVIDER_ENABLED_PLAYLIST"] = "deezer",
            ["MULTI_PROVIDER_LYRICS_ORDER"] = "deezer",
            ["MULTI_PROVIDER_DISABLED_PROVIDERS"] = "DeEzEr"
        });

        Assert.Empty(manager.GetEnabledSearchProviders());
        Assert.Empty(manager.GetEnabledDownloadProviders());
        Assert.Empty(manager.GetEnabledStreamingProviders());
        Assert.Empty(manager.GetEnabledPlaylistProviders());
        Assert.Empty(manager.GetEnabledLyricsProviders());
    }

    [Fact]
    public void SquidWtf_IsNeverAdvertisedOutsideMetadataEvenWhenLegacyOrdersContainIt()
    {
        var manager = CreateManager(new Dictionary<string, string?>
        {
            ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "squidwtf",
            ["MULTI_PROVIDER_STREAMING_ORDER"] = "squidwtf",
            ["MULTI_PROVIDER_PLAYLIST_ORDER"] = "squidwtf",
            ["MULTI_PROVIDER_ENABLED_PLAYLIST"] = "squidwtf"
        });

        Assert.Empty(manager.GetEnabledDownloadProviders());
        Assert.Empty(manager.GetEnabledStreamingProviders());
        Assert.Empty(manager.GetEnabledPlaylistProviders());
    }

    [Fact]
    public void PlaybackProviders_PreserveStreamingThenDownloadOrderWithoutDuplicates()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>
            {
                ["MULTI_PROVIDER_STREAMING_ORDER"] = "deezer,apple-download",
                ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "apple-download,deezer"
            },
            appleMusicSettings: new AppleDownloadSettings { BaseUrl = "http://apple-gateway" },
            deezerSettings: new DeezerSettings { Arl = "configured-arl" });

        Assert.Equal(["deezer", "apple-download"], manager.GetEnabledPlaybackProviders());
    }

    [Fact]
    public void StatusRead_IsPureAndDoesNotInventHealthOrTestTime()
    {
        var factory = new CountingHttpClientFactory();
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: factory);

        var status = manager.GetStatus("DeEzEr", "MeTaDaTa", "Account-A");

        Assert.Equal("deezer", status.Provider);
        Assert.Equal(ProviderCapabilities.Metadata, status.Capability);
        Assert.Equal("account-a", status.AccountKey);
        Assert.Equal(ProviderConfigurationState.NotRequired, status.Configuration);
        Assert.Equal(ProviderHealthState.Unknown, status.Health);
        Assert.Null(status.TestedAt);
        Assert.True(status.CanAttempt);
        Assert.False(status.IsReady);
        Assert.False(manager.IsProviderHealthy("deezer"));
        Assert.Empty(manager.GetStatusCache());
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public void MissingDeezerArl_DoesNotBlockPublicMetadata()
    {
        var manager = CreateManager(new Dictionary<string, string?>
        {
            ["MULTI_PROVIDER_METADATA_ORDER"] = "deezer",
            ["MULTI_PROVIDER_ENABLED_SEARCH"] = "deezer",
            ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "deezer",
            ["MULTI_PROVIDER_STREAMING_ORDER"] = "deezer"
        });

        Assert.Equal(["deezer"], manager.GetEnabledSearchProviders());
        Assert.Empty(manager.GetEnabledDownloadProviders());
        Assert.Empty(manager.GetEnabledStreamingProviders());

        var metadata = manager.GetStatus("deezer", ProviderCapabilities.Metadata);
        var download = manager.GetStatus("deezer", ProviderCapabilities.Download);
        Assert.Equal(ProviderConfigurationState.NotRequired, metadata.Configuration);
        Assert.True(metadata.CanAttempt);
        Assert.Equal(ProviderConfigurationState.NeedsConfiguration, download.Configuration);
        Assert.Equal("missing_deezer_arl", download.ReasonCode);
        Assert.False(download.CanAttempt);
    }

    [Fact]
    public async Task ExplicitProbe_ExposesTestingThenHealthy()
    {
        var handler = new BlockingHandler();
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler));

        var probe = manager.TestProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Metadata,
            "account-a");
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var testing = manager.GetStatus("deezer", ProviderCapabilities.Metadata, "account-a");
        Assert.Equal(ProviderHealthState.Testing, testing.Health);
        Assert.Null(testing.TestedAt);
        Assert.False(testing.IsReady);

        handler.Release.TrySetResult(true);
        var completed = await probe;

        Assert.Equal(ProviderHealthState.Healthy, completed.Health);
        Assert.NotNull(completed.TestedAt);
        Assert.True(completed.IsReady);
    }

    [Fact]
    public async Task ProbeObservations_AreIsolatedByAccountKey()
    {
        var handler = new QueuedResponseHandler(
            Json(HttpStatusCode.OK, "{\"id\":3135556}"),
            Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler));

        var first = await manager.TestProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Metadata,
            "account-a");
        var second = await manager.TestProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Metadata,
            "account-b");

        Assert.Equal(ProviderHealthState.Healthy, first.Health);
        Assert.Equal(ProviderHealthState.Degraded, second.Health);
        Assert.Equal(
            ProviderHealthState.Healthy,
            manager.GetStatus("deezer", ProviderCapabilities.Metadata, "account-a").Health);
        Assert.Equal(
            ProviderHealthState.Degraded,
            manager.GetStatus("deezer", ProviderCapabilities.Metadata, "account-b").Health);
    }

    [Fact]
    public async Task FailedDownloadProbe_DoesNotDegradeDeezerMetadata()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>
            {
                ["MULTI_PROVIDER_METADATA_ORDER"] = "deezer",
                ["MULTI_PROVIDER_ENABLED_SEARCH"] = "deezer",
                ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "deezer"
            },
            httpClientFactory: new HandlerHttpClientFactory(
                new QueuedResponseHandler(Json(HttpStatusCode.Unauthorized, "{}"))),
            deezerSettings: new DeezerSettings { Arl = "configured-arl" });

        var download = await manager.TestProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Download);

        Assert.Equal(ProviderHealthState.Degraded, download.Health);
        Assert.Empty(manager.GetEnabledDownloadProviders());
        Assert.Equal(["deezer"], manager.GetEnabledSearchProviders());

        var metadata = manager.GetStatus("deezer", ProviderCapabilities.Metadata);
        Assert.Equal(ProviderHealthState.Unknown, metadata.Health);
        Assert.True(metadata.CanAttempt);
    }

    [Fact]
    public async Task CompatibilityCache_ContainsOnlyCompletedCompatibilityProbe()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(
                new QueuedResponseHandler(Json(
                    HttpStatusCode.OK,
                    "{\"results\":{\"USER\":{\"USER_ID\":42}}}"))),
            deezerSettings: new DeezerSettings { Arl = "configured-arl" });

        Assert.Empty(manager.GetStatusCache());

        Assert.True(await manager.TestProviderConnectionAsync("deezer"));

        var cache = manager.GetStatusCache();
        var entry = Assert.Contains("deezer", cache);
        Assert.True(entry.IsHealthy);
        Assert.NotEqual(default, entry.TestedAt);
    }

    [Fact]
    public void PlaceholderCredentials_AreNotTreatedAsConfiguration()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            deezerSettings: new DeezerSettings { Arl = "your-deezer-arl-token" },
            qobuzSettings: new QobuzSettings
            {
                UserAuthToken = "your-qobuz-token",
                UserId = "your-qobuz-user-id"
            });

        Assert.Equal(
            ProviderConfigurationState.NeedsConfiguration,
            manager.GetStatus("deezer", ProviderCapabilities.Download).Configuration);
        Assert.Equal(
            ProviderConfigurationState.NeedsConfiguration,
            manager.GetStatus("qobuz", ProviderCapabilities.Download).Configuration);
    }

    [Fact]
    public async Task ManagedAccount_DoesNotBorrowAnotherGlobalCredential()
    {
        var http = new CountingHttpClientFactory();
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: http,
            deezerSettings: new DeezerSettings { Arl = "deployment-global-arl" });
        var accountId = Guid.CreateVersion7();

        var status = manager.GetManagedStatus(
            "deezer",
            ProviderCapabilities.Download,
            accountId,
            new Dictionary<string, string>());
        var tested = await manager.TestManagedProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Download,
            accountId,
            new Dictionary<string, string>());

        Assert.Equal(ProviderConfigurationState.NeedsConfiguration, status.Configuration);
        Assert.Equal("missing_provider_account_secret", status.ReasonCode);
        Assert.Equal(ProviderConfigurationState.NeedsConfiguration, tested.Configuration);
        Assert.Equal(0, http.CreateCount);
    }

    [Fact]
    public async Task ManagedLastFmAccount_UsesItsEncryptedAccountFieldsForProbe()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(
                new QueuedResponseHandler(Json(HttpStatusCode.OK, "{\"user\":{\"name\":\"listener\"}}"))));
        var accountId = Guid.CreateVersion7();
        var secrets = new Dictionary<string, string>
        {
            ["apikey"] = "api-key",
            ["sharedsecret"] = "shared-secret",
            ["sessionkey"] = "session-key"
        };

        var status = manager.GetManagedStatus("lastfm", ProviderCapabilities.Scrobbling, accountId, secrets);
        var tested = await manager.TestManagedProviderCapabilityAsync(
            "lastfm", ProviderCapabilities.Scrobbling, accountId, secrets);

        Assert.Equal(ProviderConfigurationState.Configured, status.Configuration);
        Assert.Equal(ProviderHealthState.Healthy, tested.Health);
        Assert.True(tested.IsReady);
    }

    [Fact]
    public async Task ManagedListenBrainzAccount_ValidatesItsOwnToken()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(
                new QueuedResponseHandler(Json(HttpStatusCode.OK, "{\"valid\":true,\"user_name\":\"listener\"}"))));
        var accountId = Guid.CreateVersion7();
        var secrets = new Dictionary<string, string> { ["token"] = "user-token" };

        var tested = await manager.TestManagedProviderCapabilityAsync(
            "listenbrainz", ProviderCapabilities.Scrobbling, accountId, secrets);

        Assert.Equal(ProviderHealthState.Healthy, tested.Health);
        Assert.True(tested.IsReady);
    }

    [Fact]
    public async Task SpotifyAccount_ProbesPlaylistAndLyricsSidecar()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(
                new QueuedResponseHandler(
                    SpotifyTotpSecrets(),
                    SpotifyTime(),
                    Json(HttpStatusCode.OK, "{\"accessToken\":\"access-token\"}"),
                    Json(HttpStatusCode.OK, "{\"error\":false,\"syncType\":\"LINE_SYNCED\",\"lines\":[]}"))),
            spotifySettings: new SpotifyApiSettings
            {
                Enabled = true,
                SessionCookie = "deployment-cookie",
                LyricsApiUrl = "http://lyrics-sidecar:8080"
            });
        var accountId = Guid.CreateVersion7();
        var secrets = new Dictionary<string, string> { ["sessioncookie"] = "account-cookie" };

        Assert.True(manager.CanTestCapability("spotify", ProviderCapabilities.Lyrics));
        Assert.True(await manager.TestManagedProviderConnectionAsync("spotify", accountId, secrets));
        Assert.Equal(
            ProviderHealthState.Healthy,
            manager.GetManagedStatus("spotify", ProviderCapabilities.Playlist, accountId, secrets).Health);
        Assert.Equal(
            ProviderHealthState.Healthy,
            manager.GetManagedStatus("spotify", ProviderCapabilities.Lyrics, accountId, secrets).Health);
    }

    [Fact]
    public async Task SpotifyPlaylistProbe_ReportsUnauthorizedWithoutExposingCookie()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(
                new QueuedResponseHandler(SpotifyTotpSecrets(), SpotifyTime(), Json(HttpStatusCode.Unauthorized, "{}"))),
            spotifySettings: new SpotifyApiSettings { Enabled = true, SessionCookie = "expired-cookie" });

        var result = await manager.TestProviderCapabilityAsync("spotify", ProviderCapabilities.Playlist);

        Assert.Equal(ProviderHealthState.Degraded, result.Health);
        Assert.Equal("provider_unauthorized", result.ReasonCode);
        Assert.DoesNotContain("expired-cookie", result.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpotifyPlaylistProbe_ReportsTotpTokenEndpointForbidden()
    {
        var blocked = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html>blocked by edge</html>", Encoding.UTF8, "text/html")
        };
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(new QueuedResponseHandler(SpotifyTotpSecrets(), SpotifyTime(), blocked)),
            spotifySettings: new SpotifyApiSettings { Enabled = true, SessionCookie = "valid-looking-cookie" });

        var result = await manager.TestProviderCapabilityAsync("spotify", ProviderCapabilities.Playlist);

        Assert.Equal(ProviderHealthState.Degraded, result.Health);
        Assert.Equal("provider_unauthorized", result.ReasonCode);
        Assert.DoesNotContain("valid-looking-cookie", result.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedAccountStatus_PreservesCapabilityFailureReason()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(
                new QueuedResponseHandler(Json(HttpStatusCode.Unauthorized, "{}"))));
        var accountId = Guid.CreateVersion7();
        var secrets = new Dictionary<string, string> { ["arl"] = "account-arl" };

        var tested = await manager.TestManagedProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Download,
            accountId,
            secrets);
        var current = manager.GetManagedStatus(
            "deezer",
            ProviderCapabilities.Download,
            accountId,
            secrets);

        Assert.Equal(ProviderHealthState.Degraded, tested.Health);
        Assert.Equal(ProviderConfigurationState.Configured, current.Configuration);
        Assert.Equal(ProviderHealthState.Degraded, current.Health);
        Assert.Equal("probe_failed", current.ReasonCode);
    }

    private static ProviderStatusManager CreateManager(
        IReadOnlyDictionary<string, string?> values,
        IHttpClientFactory? httpClientFactory = null,
        SpotifyApiSettings? spotifySettings = null,
        AppleDownloadSettings? appleMusicSettings = null,
        DeezerSettings? deezerSettings = null,
        QobuzSettings? qobuzSettings = null,
        SquidWtfEndpointCatalog? squidWtfCatalog = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new ProviderStatusManager(
            configuration,
            httpClientFactory ?? new CountingHttpClientFactory(),
            NullLogger<ProviderStatusManager>.Instance,
            Options.Create(spotifySettings ?? new SpotifyApiSettings()),
            Options.Create(appleMusicSettings ?? new AppleDownloadSettings()),
            Options.Create(deezerSettings ?? new DeezerSettings()),
            Options.Create(qobuzSettings ?? new QobuzSettings()),
            Options.Create(new SquidWTFSettings()),
            squidWtfCatalog ?? new SquidWtfEndpointCatalog([], []));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SpotifyTotpSecrets() => Json(
        HttpStatusCode.OK,
        System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { version = 1, secret = Enumerable.Range(1, 32).ToArray() }
        }));

    private static HttpResponseMessage SpotifyTime()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Date = DateTimeOffset.UtcNow;
        return response;
    }

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        public int CreateCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateCount++;
            throw new InvalidOperationException("A status read must not create an HTTP client.");
        }
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return Json(HttpStatusCode.OK, "{\"id\":3135556}");
        }
    }

    private sealed class QueuedResponseHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No fake provider response remains.");
            }

            return Task.FromResult(response);
        }
    }
}
