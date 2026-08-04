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

        var status = manager.GetAccountFreeStatus("spotify", ProviderCapabilities.Lyrics);

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

        Assert.Equal(["apple-download"], manager.GetEnabledPlaybackProviders());
    }

    [Fact]
    public void ManagedStatusRead_IsPureAndDoesNotInventHealthOrTestTime()
    {
        var factory = new CountingHttpClientFactory();
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: factory);

        var status = manager.GetManagedStatus(
            "DeEzEr",
            "MeTaDaTa",
            Guid.CreateVersion7(),
            new Dictionary<string, string>());

        Assert.Equal("deezer", status.Provider);
        Assert.Equal(ProviderCapabilities.Metadata, status.Capability);
        Assert.Equal(ProviderConfigurationState.NotRequired, status.Configuration);
        Assert.Equal(ProviderHealthState.Unknown, status.Health);
        Assert.Null(status.TestedAt);
        Assert.True(status.CanAttempt);
        Assert.False(status.IsReady);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public void MissingManagedDeezerSecret_DoesNotBlockPublicMetadata()
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

        var metadata = manager.GetAccountFreeStatus("deezer", ProviderCapabilities.Metadata);
        var download = manager.GetManagedStatus(
            "deezer",
            ProviderCapabilities.Download,
            Guid.CreateVersion7(),
            new Dictionary<string, string>());
        Assert.Equal(ProviderConfigurationState.NotRequired, metadata.Configuration);
        Assert.True(metadata.CanAttempt);
        Assert.Equal(ProviderConfigurationState.NeedsConfiguration, download.Configuration);
        Assert.Equal("missing_provider_account_secret", download.ReasonCode);
        Assert.False(download.CanAttempt);
    }

    [Fact]
    public void AccountFreeProjection_ExcludesAccountRequiredCapabilities()
    {
        var statuses = CreateManager(new Dictionary<string, string?>()).GetAllAccountFreeStatuses();

        Assert.Contains(statuses, item => item.Provider == "deezer" && item.Capability == ProviderCapabilities.Metadata);
        Assert.Contains(statuses, item => item.Provider == "qobuz" && item.Capability == ProviderCapabilities.Metadata);
        Assert.Contains(statuses, item => item.Provider == "apple-download" && item.Capability == ProviderCapabilities.Download);
        Assert.DoesNotContain(statuses, item => item.Capability is ProviderCapabilities.Playlist or ProviderCapabilities.Scrobbling);
        Assert.DoesNotContain(statuses, item =>
            (item.Provider is "deezer" or "qobuz") &&
            (item.Capability is ProviderCapabilities.Streaming or ProviderCapabilities.Download));
    }

    [Fact]
    public async Task ExplicitProbe_ExposesTestingThenHealthy()
    {
        var handler = new BlockingHandler();
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler));

        var accountId = Guid.CreateVersion7();
        var probe = manager.TestManagedProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Metadata,
            accountId,
            new Dictionary<string, string>());
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var testing = manager.GetManagedStatus(
            "deezer", ProviderCapabilities.Metadata, accountId, new Dictionary<string, string>());
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
    public async Task ProbeObservations_AreIsolatedByProviderAccount()
    {
        var handler = new QueuedResponseHandler(
            Json(HttpStatusCode.OK, "{\"id\":3135556}"),
            Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler));

        var accountA = Guid.CreateVersion7();
        var accountB = Guid.CreateVersion7();
        var first = await manager.TestManagedProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Metadata,
            accountA,
            new Dictionary<string, string>());
        var second = await manager.TestManagedProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Metadata,
            accountB,
            new Dictionary<string, string>());

        Assert.Equal(ProviderHealthState.Healthy, first.Health);
        Assert.Equal(ProviderHealthState.Degraded, second.Health);
        Assert.Equal(
            ProviderHealthState.Healthy,
            manager.GetManagedStatus("deezer", ProviderCapabilities.Metadata, accountA, new Dictionary<string, string>()).Health);
        Assert.Equal(
            ProviderHealthState.Degraded,
            manager.GetManagedStatus("deezer", ProviderCapabilities.Metadata, accountB, new Dictionary<string, string>()).Health);
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

        var download = await manager.TestManagedProviderCapabilityAsync(
            "deezer",
            ProviderCapabilities.Download,
            Guid.CreateVersion7(),
            new Dictionary<string, string> { ["arl"] = "configured-arl" });

        Assert.Equal(ProviderHealthState.Degraded, download.Health);
        Assert.Empty(manager.GetEnabledDownloadProviders());
        Assert.Equal(["deezer"], manager.GetEnabledSearchProviders());

        var metadata = manager.GetAccountFreeStatus("deezer", ProviderCapabilities.Metadata);
        Assert.Equal(ProviderHealthState.Unknown, metadata.Health);
        Assert.True(metadata.CanAttempt);
    }

    [Fact]
    public void DeploymentCredentials_DoNotEnableAccountRequiredCapabilities()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            deezerSettings: new DeezerSettings { Arl = "deployment-arl" },
            qobuzSettings: new QobuzSettings
            {
                UserAuthToken = "deployment-token",
                UserId = "deployment-user"
            });

        Assert.Empty(manager.GetEnabledDownloadProviders());
        Assert.False(manager.CanTestAccountFreeCapability("deezer", ProviderCapabilities.Download));
        Assert.Throws<InvalidOperationException>(() =>
            manager.GetAccountFreeStatus("deezer", ProviderCapabilities.Download));
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
        var handler = new QueuedResponseHandler(
            Json(HttpStatusCode.OK, "{\"valid\":true,\"user_name\":\"listener\"}"));
        var manager = CreateManager(
            new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler));
        var accountId = Guid.CreateVersion7();
        var secrets = new Dictionary<string, string>
        {
            ["token"] = "user-token",
            ["baseUrl"] = "https://koito.example/apis/listenbrainz/1"
        };

        var tested = await manager.TestManagedProviderCapabilityAsync(
            "listenbrainz", ProviderCapabilities.Scrobbling, accountId, secrets);

        Assert.Equal(ProviderHealthState.Healthy, tested.Health);
        Assert.True(tested.IsReady);
        Assert.Equal("https://koito.example/apis/listenbrainz/1/validate-token",
            Assert.Single(handler.Requests).AbsoluteUri);
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

        var result = await manager.TestManagedProviderCapabilityAsync(
            "spotify",
            ProviderCapabilities.Playlist,
            Guid.CreateVersion7(),
            new Dictionary<string, string> { ["sessioncookie"] = "expired-cookie" });

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

        var result = await manager.TestManagedProviderCapabilityAsync(
            "spotify",
            ProviderCapabilities.Playlist,
            Guid.CreateVersion7(),
            new Dictionary<string, string> { ["sessioncookie"] = "valid-looking-cookie" });

        Assert.Equal(ProviderHealthState.Degraded, result.Health);
        Assert.Equal("provider_forbidden", result.ReasonCode);
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
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No fake provider response remains.");
            }

            return Task.FromResult(response);
        }
    }
}
