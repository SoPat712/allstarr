using System.Collections.Concurrent;
using System.Net;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.AppleMusic;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Operations;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed partial class ProviderStatusManagerTests
{
    [Fact]
    public async Task SpicyUpstreamAccessRestrictionInvalidatesPriorRecentHealthyEvidence()
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var source = new allstarr.Core.Providers.Lyrics.SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets());
        var health = new allstarr.Core.Health.ProviderRuntimeHealth();
        var observed = ObservedProviderCapabilities.Wrap<IProviderLyricsCapability>(source, health);
        var context = SpicyLyricsTestData.Context();
        var configuration = await ((allstarr.Core.Providers.Lyrics.IInternalLyricsCapability)source).CaptureSourceAsync(default);
        Assert.True((await observed.FetchLyricsAsync(context, SpicyLyricsTestData.Request())).IsSuccess);
        var key = ProviderRuntimeStatusKey.CreateManaged("spicylyrics", "lyrics", context.Account!.AccountId);
        Assert.Equal(ProviderHealthState.Healthy, health.GetLyrics(key, context.Account.Revision, configuration.ConfigurationId)!.Health);
        handler.Status = System.Net.HttpStatusCode.Forbidden;
        var denied = await observed.FetchLyricsAsync(context, SpicyLyricsTestData.Request());
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, denied.Error!.Kind);
        Assert.NotEqual(ProviderHealthState.Healthy, health.GetLyrics(key, context.Account.Revision, configuration.ConfigurationId)!.Health);
    }

    [Theory]
    [InlineData("apple-musickit")]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    public async Task DownloadReachabilityDoesNotVerifyMedia(string provider)
    {
        var discovery = new Mock<IAppleDownloadEndpointDiscovery>();
        discovery.Setup(item => item.DiscoverAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppleDownloadEndpointSnapshot(AppleDownloadEndpointState.Available, null, null, true,
                [new(ProviderCapabilities.Download, AppleDownloadCapabilityState.Available, null)]));
        var handler = new AppleProviderTestFactory.Handler(_ => Json(HttpStatusCode.OK,
            """{"results":{"USER":{"USER_ID":42}}}"""));
        var health = new ProviderRuntimeHealth();
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: new HandlerHttpClientFactory(handler),
            appleMusicSettings: new AppleDownloadSettings { BaseUrl = "https://download.example.test" },
            appleDownloadDiscovery: discovery.Object, health: health);
        var account = Guid.NewGuid();
        var secrets = new Dictionary<string, string>
        { ["musicusertoken"] = "fixture-token", ["arl"] = "fixture-cookie", ["userauthtoken"] = "fixture-token", ["userid"] = "fixture-user" };

        var result = await manager.TestManagedProviderCapabilityAsync(provider, "download", account, secrets);

        Assert.Equal(ProviderHealthState.Unknown, result.Health);
        Assert.Equal("download-not-verified", result.ReasonCode);
        Assert.False(result.HasCheckResult);
        Assert.Null(health.Get(ProviderRuntimeStatusKey.CreateManaged(provider, "download", account)));
    }

    [Theory]
    [InlineData("download-not-verified")]
    [InlineData("probe_not_available")]
    [InlineData("sample-unavailable")]
    [InlineData("not-found")]
    [InlineData("playlist-not-found")]
    public async Task NeutralProbeDoesNotRecordAFailure(string reason)
    {
        var download = new Mock<IProviderDownloadCapability>();
        download.SetupGet(item => item.ProviderId).Returns("fixture-extension");
        download.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Download);
        var probe = new Mock<IProviderHealthProbeCapability>();
        probe.SetupGet(item => item.ProviderId).Returns("fixture-extension");
        probe.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Health);
        probe.Setup(item => item.ProbeAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderHealthProbeRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderHealthProbeResult>.Success(new(ProviderProbeStatus.Degraded,
                DateTimeOffset.UtcNow, TimeSpan.Zero, reason)));
        var descriptor = new ProviderDescriptor("fixture-extension", "Fixture", "Fixture", ProviderOrigin.Extension, "1", "1",
            [new(ProviderCapabilityKind.Download, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                "1", ["download", "checkAvailability"]), new(ProviderCapabilityKind.Health, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["probeDownload"])], new(), entryPoint: "index.js", healthProbe: true);
        var registry = new ProviderRegistry([new(descriptor, [download.Object, probe.Object])]);
        var health = new ProviderRuntimeHealth();
        var manager = CreateManager(new Dictionary<string, string?>(), providerRegistry: registry, health: health);

        var result = await manager.TestAccountFreeProviderCapabilityAsync("fixture-extension", "download");

        Assert.Equal(ProviderHealthState.Unknown, result.Health);
        Assert.Equal(reason, result.ReasonCode);
        Assert.False(result.CircuitOpen);
        Assert.Null(health.Get(ProviderRuntimeStatusKey.CreateAccountFree("fixture-extension", "download")));
        Assert.Equal(result, manager.GetAccountFreeStatus("fixture-extension", "download"));

        var measurement = new ProviderCtsDiagnosticResult
        {
            Succeeded = true,
            DownloadVerified = true,
            ProviderId = "fixture-extension",
            SampleBytes = 4,
            MeasuredAt = DateTimeOffset.UtcNow,
            ClickToStreamMilliseconds = 12
        };
        var verified = manager.RecordDownloadVerification(measurement, null);
        Assert.NotNull(verified);
        var rechecked = await manager.TestAccountFreeProviderCapabilityAsync("fixture-extension", "download");
        Assert.Equal(ProviderHealthState.Healthy, rechecked.Health);
        Assert.Equal(measurement.MeasurementId, rechecked.VerificationMeasurementId);
        Assert.Equal("streaming-download", rechecked.VerificationMethod);
        Assert.Null(rechecked.ReasonCode);
        var restored = CreateManager(new Dictionary<string, string?>(), providerRegistry: registry);
        var saved = System.Text.Json.JsonSerializer.Deserialize<ProviderCheckResult>(System.Text.Json.JsonSerializer.Serialize(verified))!;
        restored.RestoreCheckResult(saved.Key, saved.Revision, saved.Status);
        Assert.Equal(rechecked, restored.GetAccountFreeStatus("fixture-extension", "download"));
        Assert.Null(manager.RecordDownloadVerification(new ProviderCtsDiagnosticResult
        { Succeeded = true, ProviderId = "fixture-extension", SampleBytes = 4 }, null));
        var accountId = Guid.NewGuid();
        var privateMeasurement = new ProviderCtsDiagnosticResult
        {
            Succeeded = true,
            DownloadVerified = true,
            ProviderId = "fixture-extension",
            ProviderAccountId = accountId,
            SampleBytes = 4,
            MeasuredAt = DateTimeOffset.UtcNow
        };
        manager.RecordDownloadVerification(privateMeasurement, 7);
        Assert.Equal(ProviderHealthState.Healthy, manager.GetManagedStatus("fixture-extension", "download", accountId,
            new Dictionary<string, string>(), 7).Health);
        Assert.Equal(ProviderHealthState.Unknown, manager.GetManagedStatus("fixture-extension", "download", accountId,
            new Dictionary<string, string>(), 8).Health);
        Assert.Equal(ProviderHealthState.Unknown, manager.GetManagedStatus("fixture-extension", "download", Guid.NewGuid(),
            new Dictionary<string, string>(), 7).Health);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(206, true)]
    [InlineData(503, false)]
    public async Task MonochromeConnectionProbeUsesStreamingFixture(int status, bool expected)
    {
        var requests = new List<string>();
        var handler = new AppleProviderTestFactory.Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/search/tracks") return Json(HttpStatusCode.OK,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    tracks = ProviderDiagnosticSamples.Recordings("monochrome")
                    .Select((sample, i) => new { id = (154008664334667776L + i).ToString(), isrc = sample.Isrc })
                }));
            var response = new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("fLaCfixture", Encoding.UTF8, "audio/flac") };
            if (status == 206)
            {
                response.Version = HttpVersion.Version20;
                response.Content = new ByteArrayContent([102]);
                response.Content.Headers.ContentType = new("application/octet-stream");
                response.Content.Headers.ContentRange = new(0, 0, 22349828);
                response.Headers.AcceptRanges.Add("bytes");
            }
            return response;
        });
        using var http = new HttpClient(handler);
        var registry = new ProviderRegistry([allstarr.Core.Providers.Monochrome.MonochromeProviderRegistration.CreateRegistration(new(http))]);
        var manager = CreateManager(new Dictionary<string, string?>(), providerRegistry: registry);
        Assert.True(manager.CanTestAccountFreeCapability("monochrome", "streaming"));
        var result = await manager.TestAccountFreeProviderCapabilityAsync("monochrome", "streaming");
        Assert.Equal(expected, result.Health == ProviderHealthState.Healthy);
        Assert.Equal(expected ? 1 : 3, requests.Count(item => item.StartsWith("/track/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ArtistGridConnectionProbeUsesTheRegisteredStreamingCheck()
    {
        var streaming = new Mock<IProviderStreamingCapability>();
        streaming.SetupGet(item => item.ProviderId).Returns("artistgrid");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        var health = new Mock<IProviderHealthProbeCapability>();
        health.SetupGet(item => item.ProviderId).Returns("artistgrid");
        health.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Health);
        health.Setup(item => item.ProbeAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderHealthProbeRequest>(request => request.TargetCapability == ProviderCapabilityKind.Streaming)))
            .ReturnsAsync(ProviderOutcome<ProviderHealthProbeResult>.Success(new(ProviderProbeStatus.Healthy, DateTimeOffset.UtcNow, TimeSpan.Zero)));
        var registry = new ProviderRegistry([new(new ProviderDescriptor("artistgrid", "ArtistGrid", "Fixture", ProviderOrigin.BuiltIn,
            "1", "1", [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                "1", ["getStreamLease", "probeStream"]), new(ProviderCapabilityKind.Health, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["probeStreaming"])], new(), healthProbe: true)
            { DefaultUseMode = ProviderUseMode.ManualOnly }, [streaming.Object, health.Object])]);
        var manager = CreateManager(new Dictionary<string, string?>(), providerRegistry: registry);
        Assert.Contains(manager.GetAllAccountFreeStatuses(), item => item.Provider == "artistgrid" && item.Capability == "streaming");
        Assert.True(manager.CanTestAccountFreeCapability("artistgrid", "streaming"));
        Assert.Equal(ProviderHealthState.Healthy, (await manager.TestAccountFreeProviderCapabilityAsync("artistgrid", "streaming")).Health);
        health.VerifyAll();
    }

    [Fact]
    public void RecordedCheckWinsTimestampTieWithPassiveObservation()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", ProviderCapabilities.Metadata);
        health.Record(key, null, true, 7, null);
        clock.UtcNow += TimeSpan.FromSeconds(1);
        var context = new ProviderExecutionContext(
            new ProviderActorContext(ProviderActorKind.Administrator, Guid.NewGuid(), new("jellyfin", "fixture", "admin")),
            "deezer", null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, true, true, ["deezer"]),
            "test", "status-test", clock.UtcNow.AddMinutes(1), default);
        health.Observe(context, ProviderCapabilityKind.Metadata, new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        Assert.Equal(ProviderHealthState.Healthy, health.Get(key)!.Health);
        manager.RestoreCheckResult(key, null, manager.GetAccountFreeStatus("deezer", "metadata") with
        {
            Health = ProviderHealthState.Degraded,
            ReasonCode = "timeout",
            TestedAt = clock.UtcNow
        });

        var status = manager.GetAccountFreeStatus("deezer", "metadata");
        Assert.Equal(ProviderHealthState.Degraded, status.Health);
        Assert.Equal("timeout", status.ReasonCode);
    }

    [Fact]
    public void RecordedPublicCheckCannotHideCurrentOpenCircuit()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", ProviderCapabilities.Metadata);
        health.Record(key, null, false, 4, "rate-limited", retryAfter: TimeSpan.FromMinutes(10));
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        manager.RestoreCheckResult(key, null, manager.GetAccountFreeStatus("deezer", ProviderCapabilities.Metadata) with
        {
            Health = ProviderHealthState.Healthy,
            TestedAt = clock.UtcNow
        });

        var status = manager.GetAccountFreeStatus("deezer", ProviderCapabilities.Metadata);

        Assert.True(health.GetRouteStatus("deezer", null, ProviderCapabilityKind.Metadata).CircuitOpen);
        Assert.True(status.CircuitOpen);
        Assert.False(status.CanAttempt);
    }

    [Fact]
    public void RecordedManagedCheckCannotHideCurrentOpenCircuitOrCrossCredentialRevision()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var accountId = Guid.CreateVersion7();
        var key = ProviderRuntimeStatusKey.CreateManaged("deezer", ProviderCapabilities.Streaming, accountId);
        var secrets = new Dictionary<string, string> { ["arl"] = "fixture-arl" };
        health.Record(key, 7, false, 4, "rate-limited", retryAfter: TimeSpan.FromMinutes(10));
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        manager.RestoreCheckResult(key, 7, manager.GetManagedStatus("deezer", ProviderCapabilities.Streaming,
            accountId, secrets, 7) with
        { Health = ProviderHealthState.Healthy, TestedAt = clock.UtcNow });

        var current = manager.GetManagedStatus("deezer", ProviderCapabilities.Streaming, accountId, secrets, 7);
        var revised = manager.GetManagedStatus("deezer", ProviderCapabilities.Streaming, accountId, secrets, 8);

        Assert.True(health.GetRouteStatus("deezer", accountId, ProviderCapabilityKind.Streaming).CircuitOpen);
        Assert.True(current.CircuitOpen);
        Assert.False(current.CanAttempt);
        Assert.False(revised.CircuitOpen);
        Assert.True(revised.CanAttempt);
    }

    [Fact]
    public void ExplicitSuccessClearsCurrentCircuitWithoutRestoringItForARevisionChange()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var accountId = Guid.CreateVersion7();
        var secrets = new Dictionary<string, string> { ["arl"] = "fixture-arl" };
        var key = ProviderRuntimeStatusKey.CreateManaged("deezer", ProviderCapabilities.Streaming, accountId);
        health.Record(key, 7, false, 4, "rate-limited", retryAfter: TimeSpan.FromMinutes(10));
        manager.RestoreCheckResult(key, 7, manager.GetManagedStatus("deezer", ProviderCapabilities.Streaming,
            accountId, secrets, 7) with
        { Health = ProviderHealthState.Degraded, TestedAt = clock.UtcNow });

        var paused = manager.GetManagedStatus("deezer", ProviderCapabilities.Streaming, accountId, secrets, 7);
        var evidence = manager.GetCurrentRuntimeEvidence(key, 7, paused);
        Assert.Equal(clock.UtcNow.AddMinutes(10), evidence.AutomaticRetryAt);
        Assert.Equal(1, evidence.FailureCount);
        Assert.Equal("rate-limited", evidence.AutomaticPauseReasonCode);

        manager.RecordTryResult("deezer", ProviderCapabilities.Streaming, accountId, 7, true, 12, null);

        var recovered = manager.GetManagedStatus("deezer", ProviderCapabilities.Streaming, accountId, secrets, 7);
        Assert.False(recovered.CircuitOpen);
        Assert.Equal(ProviderRuntimeEvidenceSnapshot.Empty,
            manager.GetCurrentRuntimeEvidence(key, 7, recovered));
        Assert.False(manager.GetManagedStatus("deezer", ProviderCapabilities.Streaming, accountId, secrets, 8).CircuitOpen);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void StatusUsesNewestEvidenceAfterRoutingSampleExpires(bool observationIsNewer, bool observationHealthy)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", ProviderCapabilities.Metadata);
        var baseline = manager.GetAccountFreeStatus("deezer", ProviderCapabilities.Metadata);
        var older = clock.UtcNow;
        var newer = older.AddMinutes(1);
        var recorded = baseline with
        {
            Health = observationHealthy ? ProviderHealthState.Degraded : ProviderHealthState.Healthy,
            TestedAt = observationIsNewer ? older : newer,
            LatencyMilliseconds = 20,
            ReasonCode = observationHealthy ? "timeout" : null
        };
        manager.RestoreCheckResult(key, null, recorded);
        clock.UtcNow = observationIsNewer ? newer : older;
        health.Record(key, null, observationHealthy, 10, observationHealthy ? null : "transient-failure");
        clock.UtcNow = newer.AddMinutes(6);

        var status = manager.GetAccountFreeStatus("deezer", ProviderCapabilities.Metadata);

        Assert.Null(health.Get(key));
        Assert.Equal(observationIsNewer == observationHealthy ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
            status.Health);
        Assert.Equal(newer, status.TestedAt);
        Assert.Equal(observationIsNewer ? 10 : 20, status.LatencyMilliseconds);
        Assert.Equal(observationIsNewer ? observationHealthy ? null : "transient-failure" : recorded.ReasonCode, status.ReasonCode);
        Assert.False(status.CircuitOpen);
        Assert.Equal(allstarr.Core.Routing.ProviderRouteHealthState.Unknown,
            health.GetRouteStatus("deezer", null, ProviderCapabilityKind.Metadata).State);
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("NotFound")]
    [InlineData("no_results")]
    [InlineData("incompatible-media")]
    [InlineData("IncompatibleMedia")]
    [InlineData("INCOMPATIBLE_MEDIA")]
    [InlineData("not-supported")]
    [InlineData("NotSupported")]
    [InlineData("canceled")]
    [InlineData("cancelled")]
    [InlineData("busy")]
    [InlineData("temporary_storage_full")]
    [InlineData("TemporaryStorageFull")]
    [InlineData("invalid_input")]
    [InlineData("needs_artist")]
    [InlineData("needs-artist")]
    [InlineData("NeedsArtist")]
    public void TryResourceAndLocalOutcomesDoNotRecordHealth(string reason)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", ProviderCapabilities.Metadata);
        manager.RecordTryResult("deezer", "metadata", null, null, false, 10, reason);
        Assert.Null(health.Get(key));
        manager.RecordTryResult("deezer", "metadata", null, null, true, 7, null);
        var success = health.Get(key);
        manager.RestoreCheckResult(key, null, manager.GetAccountFreeStatus("deezer", "metadata"));
        clock.UtcNow += TimeSpan.FromSeconds(1);
        manager.RecordTryResult("deezer", "metadata", null, null, false, 10, reason);
        Assert.Same(success, health.Get(key));
        Assert.Equal(ProviderHealthState.Healthy, manager.GetAccountFreeStatus("deezer", "metadata").Health);
        Assert.Equal(success!.ObservedAt, manager.GetAccountFreeStatus("deezer", "metadata").TestedAt);
    }

    [Theory]
    [InlineData("transient-failure", "transient-failure")]
    [InlineData("TransientFailure", "transient-failure")]
    [InlineData("permanent_failure", "permanent-failure")]
    [InlineData("timeout", "timeout")]
    [InlineData("Unauthorized", "unauthorized")]
    [InlineData("RateLimited", "rate-limited")]
    [InlineData("provider-contract-changed", "provider-contract-changed")]
    public void TryProviderFailureRecordsImmediateDegradedHealth(string reason, string normalized)
    {
        var health = new ProviderRuntimeHealth();
        var manager = CreateManager(new Dictionary<string, string?>(), health: health);
        manager.RecordTryResult("deezer", "metadata", null, null, true, 7, null);
        manager.RecordTryResult("deezer", "metadata", null, null, false, 10, reason);
        var status = manager.GetAccountFreeStatus("deezer", "metadata");
        Assert.Equal(ProviderHealthState.Degraded, status.Health);
        Assert.Equal(normalized, status.ReasonCode);
        Assert.Equal(10, status.LatencyMilliseconds);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("playlist")]
    [InlineData("streaming")]
    [InlineData("download")]
    public async Task QobuzProbe_UsesTheExactSelectedAccount(string capability)
    {
        var calls = 0;
        var testsFavorites = capability is "streaming" or "download";
        using var handler = new AppleProviderTestFactory.Handler(request =>
        {
            calls++;
            Assert.Equal("www.qobuz.com", request.RequestUri!.Host);
            Assert.Equal(testsFavorites ? "/api.json/0.2/favorite/getUserFavorites" : "/api.json/0.2/track/search",
                request.RequestUri.AbsolutePath);
            if (testsFavorites)
                Assert.Contains("user_id=selected-user", request.RequestUri.Query);
            Assert.Equal("selected-token", request.Headers.GetValues("X-User-Auth-Token").Single());
            return Json(HttpStatusCode.OK, "{\"tracks\":{\"items\":[{\"id\":42}]}}");
        });
        var manager = CreateManager(new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler),
            qobuzSettings: new QobuzSettings { UserAuthToken = "other-token", UserId = "other-user" });

        var status = await manager.TestManagedProviderCapabilityAsync("qobuz", capability, Guid.NewGuid(),
            new Dictionary<string, string> { ["userAuthToken"] = "selected-token", ["userId"] = "selected-user" });

        Assert.True(manager.CanTestCapability("qobuz", capability));
        Assert.Equal(capability == "download" ? ProviderHealthState.Unknown : ProviderHealthState.Healthy, status.Health);
        Assert.NotNull(status.TestedAt);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("playlist")]
    public async Task QobuzCatalogWithoutAccountSecretsNeedsConfigurationBeforeProbing(string capability)
    {
        var http = new CountingHttpClientFactory();
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: http,
            qobuzSettings: new QobuzSettings { UserAuthToken = "other-token", UserId = "other-user" });
        var status = await manager.TestManagedProviderCapabilityAsync("qobuz", capability, Guid.NewGuid(),
            new Dictionary<string, string>());

        Assert.Equal(ProviderConfigurationState.NeedsConfiguration, status.Configuration);
        Assert.Equal("missing_provider_account_secret", status.ReasonCode);
        Assert.Equal(ProviderHealthState.Unknown, status.Health);
        Assert.Null(status.TestedAt);
        Assert.False(status.CanAttempt);
        Assert.False(manager.CanTestAccountFreeCapability("qobuz", capability));
        Assert.Throws<InvalidOperationException>(() => manager.GetAccountFreeStatus("qobuz", capability));
        Assert.DoesNotContain("qobuz", manager.GetEnabledSearchProviders());
        Assert.Equal(0, http.CreateCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task LrclibProbe_DistinguishesReachableCatalogFromUpstreamFailure(HttpStatusCode response, bool healthy)
    {
        var handler = new RepeatingResponseHandler(_ => Json(response, "{\"plainLyrics\":\"Words\"}"));
        var manager = CreateManager(new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler));

        var status = await manager.TestAccountFreeProviderCapabilityAsync("lrclib", "lyrics");

        Assert.True(manager.CanTestAccountFreeCapability("lrclib", "lyrics"));
        Assert.Equal(healthy, status.Health == ProviderHealthState.Healthy);
        Assert.NotNull(status.TestedAt);
        Assert.Equal(response == HttpStatusCode.OK ? 1 : response == HttpStatusCode.NotFound ? 12 : 3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("lrclib.net", request.Host);
            Assert.Contains(request.AbsolutePath, new[] { "/api/get", "/api/search" });
        });
        if (response == HttpStatusCode.NotFound) Assert.Equal("sample-unavailable", status.ReasonCode);
    }

    [Theory]
    [InlineData("lrcred", "{\"hits\":[]}", "lrc.red")]
    [InlineData("genius", "{\"lyrics\":\"\"}", "fetch-genius.samidy.workers.dev")]
    [InlineData("unison", "{\"success\":false,\"code\":\"NOT_FOUND\"}", "unison.boidu.dev")]
    public async Task PublicTimedLyricsProbeNeedsNoAccount(string provider, string body, string host)
    {
        var handler = new RepeatingResponseHandler(request => Json(
            provider == "lrcred" && request.RequestUri!.AbsolutePath.EndsWith(".ttml", StringComparison.Ordinal)
                ? HttpStatusCode.NotFound : HttpStatusCode.OK, body));
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: new HandlerHttpClientFactory(handler));
        Assert.True(manager.CanTestAccountFreeCapability(provider, "lyrics"));
        Assert.Equal(ProviderConfigurationState.NotRequired, manager.GetAccountFreeStatus(provider, "lyrics").Configuration);
        var status = await manager.TestAccountFreeProviderCapabilityAsync(provider, "lyrics");
        Assert.Equal(ProviderHealthState.Unknown, status.Health);
        Assert.Equal("sample-unavailable", status.ReasonCode);
        Assert.All(handler.Requests, request => Assert.Equal(host, request.Host));
        Assert.InRange(handler.Requests.Count, 8, 16);
    }

    [Fact]
    public async Task PaxsenixProbeResolvesSampleIsrcWithoutAnAccount()
    {
        var handler = new QueuedResponseHandler(Json(HttpStatusCode.OK,
            """{"type":"Line","content":[{}],"lrc":"[00:01]Never gonna give you up"}"""));
        var apple = AppleProviderTestFactory.Client(new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            Assert.Equal("/v1/catalog/us/songs", request.RequestUri!.AbsolutePath);
            Assert.Contains("filter[isrc]=USQX91300108", Uri.UnescapeDataString(request.RequestUri.Query));
            Assert.False(request.Headers.Contains("Cookie"));
            return Json(HttpStatusCode.OK,
                """{"data":[{"id":"1558533900","attributes":{"name":"Get Lucky","artistName":"Daft Punk","durationInMillis":369000}}]}""");
        })));
        var manager = CreateManager(new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler), services:
            Mock.Of<IServiceProvider>(provider => provider.GetService(typeof(AppleMusicClient)) == apple));

        Assert.True(manager.CanTestAccountFreeCapability("paxsenix", "lyrics"));
        Assert.Equal(ProviderConfigurationState.NotRequired,
            manager.GetAccountFreeStatus("paxsenix", "lyrics").Configuration);
        var status = await manager.TestAccountFreeProviderCapabilityAsync("paxsenix", "lyrics");

        Assert.Equal(ProviderHealthState.Healthy, status.Health);
        var uri = Assert.Single(handler.Requests);
        Assert.Equal("lyrics.paxsenix.org", uri.Host);
        Assert.Equal("/apple-music/lyrics", uri.AbsolutePath);
        Assert.Equal("?id=1558533900", uri.Query);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaxsenixProbeReportsUpstreamBusyOrTimedOutWithinDiagnosticBudget(bool hang)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var client = new HttpClient(new PaxsenixProbeHandler(hang));
        var apple = AppleProviderTestFactory.Client(new HttpClient(new AppleProviderTestFactory.Handler(request =>
            Json(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? "{\"results\":{\"songs\":{\"data\":[]}}}"
                : "{\"data\":[{\"id\":\"987654\",\"attributes\":{\"name\":\"Never Gonna Give You Up\",\"artistName\":\"Rick Astley\"}}]}"))));
        var adapter = new allstarr.Core.Providers.Lyrics.PaxsenixCapabilityAdapter(client, null,
            new allstarr.Core.Providers.Lyrics.PaxsenixRequestPacer(clock, TimeSpan.Zero), clock,
            new AppleMusicKitMetadataCapabilityAdapter(apple));
        var services = new ServiceCollection().AddSingleton(apple).AddSingleton(adapter).BuildServiceProvider();
        var manager = CreateManager(new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(new PaxsenixProbeHandler(hang)), services: services);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var pending = manager.TestAccountFreeProviderCapabilityAsync("paxsenix", "lyrics");
        if (hang)
        {
            await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8));
            clock.Advance(TimeSpan.FromSeconds(8));
        }
        var status = await pending;
        Assert.Equal(hang ? ProviderHealthState.Unknown : ProviderHealthState.Degraded, status.Health);
        Assert.Equal("lyrics-upstream-busy-or-timed-out", status.ReasonCode);
        Assert.InRange(started.ElapsedMilliseconds, 0, 21000);
        if (hang) Assert.Null(status.LatencyMilliseconds);
        else Assert.InRange(status.LatencyMilliseconds!.Value, 0, 20500);
        Assert.Equal(status.ReasonCode, manager.GetAccountFreeStatus("paxsenix", "lyrics").ReasonCode);
    }

    private sealed class PaxsenixProbeHandler(bool hang) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (hang) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.ServiceUnavailable);
        }
    }

    [Fact]
    public async Task PaxsenixProbeReportsMissingAppleWebToken()
    {
        using var http = new HttpClient(new QueuedResponseHandler(Json(HttpStatusCode.ServiceUnavailable, "{}")));
        var apple = new AppleMusicClient(http, new AppleWebTokenProvider(http),
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>());
        var manager = CreateManager(new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(new AppleProviderTestFactory.Handler(_ =>
                throw new InvalidOperationException("Unexpected lyrics read."))), services:
            Mock.Of<IServiceProvider>(provider => provider.GetService(typeof(AppleMusicClient)) == apple));
        var status = await manager.TestAccountFreeProviderCapabilityAsync("paxsenix", "lyrics");
        Assert.Equal(ProviderHealthState.Degraded, status.Health);
        Assert.Equal("apple-web-token-unavailable", status.ReasonCode);
    }

    [Theory]
    [InlineData("paxsenix", "Paxsenix", "lyrics")]
    [InlineData("genius", "Genius", "genius")]
    public async Task PublicLyricsSchemaIncludesSourcesWithoutAccountFields(string id, string name, string icon)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var controller = new allstarr.Controllers.AdminUiController(new ConfigurationBuilder().Build(),
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new MusicBrainzSettings()), null!, CreateManager(new Dictionary<string, string?>()),
            new ProviderAccountOptions(), null!)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services }
            }
        };

        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.GetSchema());
        var schema = Assert.IsType<allstarr.Models.Admin.AdminUiSchemaResponse>(result.Value);
        var provider = Assert.Single(schema.Providers, item => item.Id == id);
        Assert.Equal(name, provider.Name);
        Assert.Equal(icon, provider.Icon);
        Assert.Equal(new[] { "lyrics" }, provider.Categories);
        Assert.Equal("built_in", provider.ConnectionKind);
        Assert.Empty(provider.AccountSettings);
        Assert.Empty(provider.ConfigSchema);
    }

    [Fact]
    public async Task PublicHealthProbesBypassTheLyricsCache()
    {
        var handler = new QueuedResponseHandler(
            Json(HttpStatusCode.OK, "{\"success\":true,\"data\":{\"lyrics\":\"Fixture\",\"format\":\"plain\"}}"),
            Json(HttpStatusCode.TooManyRequests, "{}"),
            Json(HttpStatusCode.TooManyRequests, "{}"),
            Json(HttpStatusCode.TooManyRequests, "{}"));
        var adapter = new allstarr.Core.Providers.Lyrics.UnisonCapabilityAdapter(new HttpClient(handler), new TestMemoryApplicationCache());
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: new HandlerHttpClientFactory(handler),
            services: Mock.Of<IServiceProvider>(provider => provider.GetService(typeof(allstarr.Core.Providers.Lyrics.UnisonCapabilityAdapter)) == adapter));
        Assert.Equal(ProviderHealthState.Healthy, (await manager.TestAccountFreeProviderCapabilityAsync("unison", "lyrics")).Health);
        var second = await manager.TestAccountFreeProviderCapabilityAsync("unison", "lyrics");
        Assert.Equal(ProviderHealthState.Degraded, second.Health);
        Assert.Equal("rate-limited", second.ReasonCode);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public void LyricsPlusAndMusixmatchWorkWithoutAccounts()
    {
        var manager = CreateManager(new Dictionary<string, string?>
        { ["MULTI_PROVIDER_LYRICS_ORDER"] = "lyricsplus,musixmatch,lrclib" });
        Assert.Equal(new[] { "lyricsplus", "musixmatch", "lrclib" }, manager.GetEnabledLyricsProviders());
        Assert.Equal(ProviderConfigurationState.NotRequired,
            manager.GetAccountFreeStatus("lyricsplus", "lyrics").Configuration);
        Assert.Equal(ProviderConfigurationState.NotRequired, manager.GetAccountFreeStatus("musixmatch", "lyrics").Configuration);
        var account = Guid.NewGuid();
        Assert.Equal(ProviderConfigurationState.NeedsConfiguration,
            manager.GetManagedStatus("musixmatch", "lyrics", account, new Dictionary<string, string>()).Configuration);
        Assert.Equal(ProviderConfigurationState.Configured,
            manager.GetManagedStatus("musixmatch", "lyrics", account,
                new Dictionary<string, string> { ["userToken"] = "synthetic-desktop-token" }).Configuration);
    }

    [Theory]
    [InlineData("lyricsplus", true)]
    [InlineData("lyricsplus", false)]
    [InlineData("musixmatch", true)]
    [InlineData("musixmatch", false)]
    public async Task NewLyricsProbesReuseTheirNativeTransportAndSafeOutcomes(string provider, bool healthy)
    {
        var handler = new RepeatingResponseHandler(request => Json(
            provider == "lyricsplus" && !healthy ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK,
            provider == "lyricsplus" ? "{\"type\":\"None\",\"lyrics\":[{\"text\":\"Words\"}]}" :
                healthy ? MusixmatchProbeBody(request.RequestUri!) : "{\"message\":{\"header\":{\"status_code\":401},\"body\":{}}}"));
        var manager = CreateManager(new Dictionary<string, string?> { ["LyricsPlus:BaseUrl"] = "http://lyrics:3000/prefix" },
            httpClientFactory: new HandlerHttpClientFactory(handler));
        var status = provider == "lyricsplus"
            ? await manager.TestAccountFreeProviderCapabilityAsync(provider, "lyrics")
            : await manager.TestManagedProviderCapabilityAsync(provider, "lyrics", Guid.NewGuid(),
                new Dictionary<string, string> { ["userToken"] = "synthetic-desktop-token" });
        Assert.Equal(healthy, status.Health == ProviderHealthState.Healthy);
        Assert.Equal(healthy ? 1 : 3, handler.Requests.Count);
        var request = handler.Requests[0];
        Assert.Equal(provider == "lyricsplus" ? "lyrics" : "apic-appmobile.musixmatch.com", request.Host);
        if (provider == "musixmatch")
        {
            Assert.Contains("usertoken=synthetic-desktop-token", request.Query, StringComparison.Ordinal);
            Assert.Equal("/ws/1.1/macro.subtitles.get", request.AbsolutePath);
        }
        if (!healthy) Assert.Equal(provider == "lyricsplus" ? "rate-limited" : "account-needs-reauthentication", status.ReasonCode);
    }

    [Fact]
    public async Task MusixmatchAccountFreeProbeSharesTheAutomaticToken()
    {
        var handler = new RepeatingResponseHandler(request => Json(HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath.EndsWith("token.get", StringComparison.Ordinal)
                ? """{"message":{"header":{"status_code":200},"body":{"user_token":"automatic-fixture-token"}}}"""
                : MusixmatchProbeBody(request.RequestUri)));
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: new HandlerHttpClientFactory(handler));

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(ProviderHealthState.Healthy,
                (await manager.TestAccountFreeProviderCapabilityAsync("musixmatch", "lyrics")).Health);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("/ws/1.1/token.get", handler.Requests[0].AbsolutePath);
        Assert.All(handler.Requests, request => Assert.Equal("apic-appmobile.musixmatch.com", request.Host));
        Assert.All(handler.Requests.Skip(1), request => Assert.Contains("usertoken=automatic-fixture-token", request.Query));
    }

    [Theory]
    [InlineData("renew")]
    [InlineData("captcha")]
    public async Task MusixmatchProbeRejectsInnerAuthorizationHints(string hint)
    {
        var payload = "{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"macro_calls\":{\"matcher.track.get\":{\"message\":{\"header\":{\"status_code\":200,\"hint\":\"" + hint + "\"},\"body\":[]}}}}}}";
        var handler = new RepeatingResponseHandler(_ => Json(HttpStatusCode.OK, payload));
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: new HandlerHttpClientFactory(handler));
        var status = await manager.TestManagedProviderCapabilityAsync("musixmatch", "lyrics", Guid.NewGuid(),
            new Dictionary<string, string> { ["userToken"] = "synthetic-desktop-token" });
        Assert.Equal(ProviderHealthState.Degraded, status.Health);
        Assert.Equal("account-needs-reauthentication", status.ReasonCode);
    }

    [Fact]
    public void AppleMediaReadinessUsesBackgroundDiscoveryEvenWithoutAnAccountTest()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var health = new ProviderRuntimeHealth(clock: clock);
        health.AppleDownloadSnapshot = new(AppleDownloadEndpointState.NeedsAuthentication,
            "endpoint_authentication_required", "2.0.0", false, []);
        var manager = CreateManager(new Dictionary<string, string?>(),
            appleMusicSettings: new AppleDownloadSettings { BaseUrl = "http://apple-gateway" }, health: health);
        Assert.All(manager.GetAppleDownloadEndpointStatuses(), status =>
        {
            Assert.Equal(ProviderHealthState.Degraded, status.Health);
            Assert.Equal("apple-download-sign-in-required", status.ReasonCode);
            Assert.Equal(clock.UtcNow, status.TestedAt);
        });
        Assert.Equal(ProviderHealthState.Unknown,
            manager.GetAccountFreeStatus("apple-musickit", ProviderCapabilities.Metadata).Health);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveExtensionCapabilitiesReceiveReadinessChecks(bool succeeds)
    {
        var implementation = new Mock<IProviderMetadataCapability>();
        implementation.SetupGet(item => item.ProviderId).Returns("fixture-extension");
        implementation.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        implementation.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(succeeds ? ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture-extension",
                [new(new("fixture-extension", ProviderResourceKind.Track, "track"), "Get Lucky", [new("Daft Punk")])])) :
                ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(new(ProviderErrorKind.TransientFailure)));
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(
            "fixture-extension", "Fixture", "Fixture extension", ProviderOrigin.Extension, "1", "1.0",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1.0", ["searchTracks", "getTrack"], [])],
            new ProviderPermissionDescriptor(), entryPoint: "index.js"), [implementation.Object])]);
        var manager = CreateManager(new Dictionary<string, string?>(), providerRegistry: registry);

        var before = Assert.Single(manager.GetAllAccountFreeStatuses(),
            item => item.Provider == "fixture-extension" && item.Capability == "metadata");
        var after = await manager.TestAccountFreeProviderCapabilityAsync("fixture-extension", "metadata");

        Assert.True(manager.CanTestCapability("fixture-extension", "metadata"));
        Assert.False(before.IsReady);
        Assert.Equal(succeeds, after.IsReady);
        Assert.Equal(succeeds ? ProviderHealthState.Healthy : ProviderHealthState.Degraded, after.Health);
        implementation.VerifyAll();
    }

    [Fact]
    public void SpotifyLyricsRequiresAnExactManagedAccount()
    {
        var manager = CreateManager(new Dictionary<string, string?>(),
            spotifySettings: new SpotifyApiSettings { Enabled = false, SessionCookie = "deployment-cookie" });
        Assert.Throws<InvalidOperationException>(() => manager.GetAccountFreeStatus("spotify", ProviderCapabilities.Lyrics));
        Assert.DoesNotContain("spotify", manager.GetEnabledLyricsProviders());
        var id = Guid.NewGuid();
        Assert.Equal(ProviderConfigurationState.NeedsConfiguration,
            manager.GetManagedStatus("spotify", ProviderCapabilities.Lyrics, id, new Dictionary<string, string>()).Configuration);
        Assert.Equal(ProviderConfigurationState.Configured,
            manager.GetManagedStatus("spotify", ProviderCapabilities.Lyrics, id,
                new Dictionary<string, string> { ["sessioncookie"] = "account-cookie" }).Configuration);
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
    public void UnknownProvider_IsNeverAdvertisedWhenOrdersContainIt()
    {
        var manager = CreateManager(new Dictionary<string, string?>
        {
            ["MULTI_PROVIDER_METADATA_ORDER"] = "unknown-provider",
            ["MULTI_PROVIDER_ENABLED_SEARCH"] = "unknown-provider",
            ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "unknown-provider",
            ["MULTI_PROVIDER_STREAMING_ORDER"] = "unknown-provider",
            ["MULTI_PROVIDER_PLAYLIST_ORDER"] = "unknown-provider",
            ["MULTI_PROVIDER_ENABLED_PLAYLIST"] = "unknown-provider"
        });

        Assert.Empty(manager.GetEnabledSearchProviders());
        Assert.Empty(manager.GetEnabledDownloadProviders());
        Assert.Empty(manager.GetEnabledStreamingProviders());
        Assert.Empty(manager.GetEnabledPlaylistProviders());
    }

    [Fact]
    public void PlaybackProviders_RequireSelectedAccountsEvenWhenGlobalSettingsExist()
    {
        var manager = CreateManager(
            new Dictionary<string, string?>
            {
                ["MULTI_PROVIDER_STREAMING_ORDER"] = "deezer,apple-download",
                ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "apple-download,deezer"
            },
            appleMusicSettings: new AppleDownloadSettings { BaseUrl = "http://apple-gateway" },
            deezerSettings: new DeezerSettings { Arl = "configured-arl" });

        Assert.Empty(manager.GetEnabledPlaybackProviders());
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
        Assert.DoesNotContain(statuses, item => item.Provider == "qobuz");
        Assert.Contains(statuses, item => item.Provider == "apple-musickit" && item.Capability == ProviderCapabilities.Playlist);
        Assert.DoesNotContain(statuses, item => item.Capability == ProviderCapabilities.Scrobbling);
        Assert.DoesNotContain(statuses, item => item.Provider == "apple-musickit" && item.Capability == ProviderCapabilities.Download);
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
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

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
            Json(HttpStatusCode.OK, "{\"data\":[{\"id\":3135556}]}"),
            Json(HttpStatusCode.ServiceUnavailable, "{}"),
            Json(HttpStatusCode.ServiceUnavailable, "{}"),
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

    [Theory]
    [InlineData("multi-scrobbler")]
    [InlineData("koito")]
    public void CompatibleScrobbleTarget_NeedsAnExplicitEndpoint(string providerId)
    {
        var manager = CreateManager(new Dictionary<string, string?>());
        var status = manager.GetManagedStatus(providerId, ProviderCapabilities.Scrobbling,
            Guid.CreateVersion7(), new Dictionary<string, string> { ["token"] = "fixture-token" });
        Assert.Equal(ProviderConfigurationState.NeedsConfiguration, status.Configuration);
        Assert.False(status.IsReady);
    }

    [Theory]
    [InlineData("listenbrainz")]
    [InlineData("multi-scrobbler")]
    [InlineData("koito")]
    public async Task ManagedListenBrainzAccount_ValidatesItsOwnToken(string providerId)
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
            providerId, ProviderCapabilities.Scrobbling, accountId, secrets);

        Assert.Equal(ProviderHealthState.Healthy, tested.Health);
        Assert.True(tested.IsReady);
        Assert.Equal("https://koito.example/apis/listenbrainz/1/validate-token",
            Assert.Single(handler.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task SpotifyAccount_ProbesPlaylistAndNativeLyrics()
    {
        var handler = new QueuedResponseHandler(
            SpotifyTotpSecrets(), SpotifyTime(), Json(HttpStatusCode.OK, "{\"accessToken\":\"access-token\"}"),
            SpotifyTotpSecrets(), SpotifyTime(), Json(HttpStatusCode.OK, "{\"accessToken\":\"access-token\"}"),
            Json(HttpStatusCode.OK, "{\"lyrics\":{\"syncType\":\"UNSYNCED\",\"lines\":[{\"words\":\"Synthetic words\"}]}}"));
        var manager = CreateManager(new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler),
            spotifySettings: new SpotifyApiSettings { Enabled = true, SessionCookie = "deployment-cookie" });
        var accountId = Guid.CreateVersion7();
        var secrets = new Dictionary<string, string> { ["sessioncookie"] = "account-cookie" };
        Assert.True(manager.CanTestCapability("spotify", ProviderCapabilities.Lyrics));
        Assert.True(await manager.TestManagedProviderConnectionAsync("spotify", accountId, secrets));
        Assert.Equal(ProviderHealthState.Healthy,
            manager.GetManagedStatus("spotify", ProviderCapabilities.Playlist, accountId, secrets).Health);
        Assert.Equal(ProviderHealthState.Healthy,
            manager.GetManagedStatus("spotify", ProviderCapabilities.Lyrics, accountId, secrets).Health);
        Assert.Equal("spclient.wg.spotify.com", handler.Requests.Last().Host);
        Assert.Contains("/color-lyrics/v2/track/", handler.Requests.Last().AbsolutePath, StringComparison.Ordinal);
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

    [Fact]
    public async Task MissingCatalogClient_RemainsUncheckedWithoutBlockingRetry()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var discovery = new Mock<IAppleDownloadEndpointDiscovery>();
        discovery.Setup(item => item.DiscoverAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppleDownloadEndpointSnapshot(
                AppleDownloadEndpointState.Unreachable,
                "endpoint_unreachable",
                null,
                false,
                [new AppleDownloadCapabilityStatus(
                    ProviderCapabilities.Metadata,
                    AppleDownloadCapabilityState.Degraded,
                    "endpoint_unreachable")]));
        var manager = CreateManager(
            new Dictionary<string, string?>
            {
                ["MULTI_PROVIDER_METADATA_ORDER"] = "apple-download,deezer",
                ["MULTI_PROVIDER_ENABLED_SEARCH"] = "apple-download,deezer"
            },
            appleMusicSettings: new AppleDownloadSettings { BaseUrl = "http://apple-gateway" },
            appleDownloadDiscovery: discovery.Object,
            clock: clock);

        var failed = await manager.TestAccountFreeProviderCapabilityAsync(
            "apple-download",
            ProviderCapabilities.Metadata);
        clock.UtcNow += TimeSpan.FromHours(1);

        Assert.Equal(ProviderHealthState.Unknown, failed.Health);
        Assert.Equal("probe_not_available", failed.ReasonCode);
        Assert.Equal(["apple-musickit", "deezer"], manager.GetEnabledSearchProviders());
        Assert.Equal(ProviderHealthState.Unknown,
            manager.GetAccountFreeStatus("apple-download", ProviderCapabilities.Metadata).Health);
    }

    [Fact]
    public async Task AppleCatalog_RemainsIndependentOfOptionalMediaDiscovery()
    {
        var discovery = new Mock<IAppleDownloadEndpointDiscovery>();
        discovery.Setup(item => item.DiscoverAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppleDownloadEndpointSnapshot(AppleDownloadEndpointState.Unreachable,
                "endpoint_unreachable", null, false, []));
        var manager = CreateManager(new Dictionary<string, string?>(),
            appleMusicSettings: new AppleDownloadSettings { BaseUrl = "http://apple-gateway" },
            appleDownloadDiscovery: discovery.Object);
        await manager.TestManagedProviderCapabilityAsync("apple-download", ProviderCapabilities.Streaming,
            Guid.CreateVersion7(), new Dictionary<string, string> { ["musicusertoken"] = "fixture-token" });
        foreach (var capability in new[] { ProviderCapabilities.Metadata, ProviderCapabilities.Playlist })
        {
            var status = manager.GetAccountFreeStatus("apple-download", capability);
            Assert.Equal("apple-musickit", status.Provider);
            Assert.True(status.IsSupported);
            Assert.Equal(ProviderConfigurationState.NotRequired, status.Configuration);
            Assert.Equal(ProviderHealthState.Unknown, status.Health);
            Assert.Null(status.ReasonCode);
        }
        discovery.Verify(item => item.DiscoverAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AppleCatalogProbe_UsesBoundedPublicSearchWithoutPersonalCredentials()
    {
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            Assert.Equal("/v1/catalog/us/search", request.RequestUri!.AbsolutePath);
            Assert.Contains("types=songs&limit=1", request.RequestUri.Query);
            Assert.False(request.Headers.Contains("Cookie"));
            return Json(HttpStatusCode.OK, """{"results":{"songs":{"data":[]}}}""");
        }));
        var client = AppleProviderTestFactory.Client(http);
        var manager = CreateManager(new Dictionary<string, string?>(), services:
            Mock.Of<IServiceProvider>(provider => provider.GetService(typeof(AppleMusicClient)) == client));
        var status = await manager.TestManagedProviderCapabilityAsync("apple-musickit", ProviderCapabilities.Metadata,
            Guid.CreateVersion7(), new Dictionary<string, string> { ["musicusertoken"] = "fixture-token" });
        Assert.Equal(ProviderHealthState.Healthy, status.Health);
        Assert.Equal(ProviderConfigurationState.Configured, status.Configuration);
    }

    [Fact]
    public async Task AppleWebTokenFailure_AppearsOnTheSelectedAccountStatus()
    {
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        var tokens = new AppleWebTokenProvider(http);
        await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(() => tokens.GetAsync(default));
        var manager = CreateManager(new Dictionary<string, string?>(), services:
            Mock.Of<IServiceProvider>(provider => provider.GetService(typeof(AppleWebTokenProvider)) == tokens));
        var accountId = Guid.CreateVersion7();
        var status = manager.GetManagedStatus("apple-musickit", ProviderCapabilities.Playlist, accountId,
            new Dictionary<string, string> { ["musicusertoken"] = "fixture-token" });
        Assert.Equal("apple-musickit", status.Provider);
        Assert.Equal(ProviderConfigurationState.Configured, status.Configuration);
        Assert.Equal(ProviderHealthState.Degraded, status.Health);
        Assert.Equal("apple-web-token-unavailable", status.ReasonCode);
        Assert.Equal(tokens.ObservedAt, status.TestedAt);
    }

    [Theory]
    [InlineData("paxsenix")]
    [InlineData("lrclib")]
    [InlineData("lyricsplus")]
    [InlineData("musixmatch")]
    [InlineData("spotify")]
    public async Task SourcesWithoutStreaming_IgnoreOldPlaybackEvidenceAndRejectPlaybackProbes(string provider)
    {
        var http = new CountingHttpClientFactory();
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: http);
        var account = Guid.NewGuid();
        var key = ProviderRuntimeStatusKey.CreateManaged(provider, "streaming", account);
        manager.RestoreCheckResult(key, 1, new ProviderRuntimeStatus
        {
            Provider = provider,
            Capability = "streaming",
            IsSupported = true,
            IsEnabled = true,
            Configuration = ProviderConfigurationState.NotRequired,
            Health = ProviderHealthState.Degraded,
            TestedAt = DateTimeOffset.UtcNow,
            ReasonCode = "probe_failed"
        });
        manager.RecordTryResult(provider, "streaming", account, 1, false, 1, "timeout");

        var unsupported = manager.GetManagedStatus(provider, "streaming", account, new Dictionary<string, string>(), 1);
        Assert.False(unsupported.IsSupported);
        Assert.Equal(ProviderHealthState.Unknown, unsupported.Health);
        Assert.Null(unsupported.TestedAt);
        Assert.False(manager.CanTestCapability(provider, "streaming"));
        var tested = await manager.TestManagedProviderCapabilityAsync(provider, "streaming", account, new Dictionary<string, string>());
        Assert.Null(tested.TestedAt);
        Assert.DoesNotContain(manager.GetAllManagedStatuses(provider, account, new Dictionary<string, string>(), 1),
            item => item.Capability is "streaming" or "download");
        Assert.DoesNotContain(manager.GetAllAccountFreeStatuses().Where(item => item.Provider == provider),
            item => item.Capability is "streaming" or "download");
        Assert.Equal(0, http.CreateCount);
    }

    [Fact]
    public void LyricsTryFailure_PreservesTheSpecificUpstreamReason()
    {
        var manager = CreateManager(new Dictionary<string, string?>());
        manager.RecordTryResult("paxsenix", "lyrics", null, null, false, 4, "lyrics-upstream-busy-or-timed-out");
        var status = manager.GetAccountFreeStatus("paxsenix", "lyrics");
        Assert.Equal(ProviderHealthState.Degraded, status.Health);
        Assert.Equal("lyrics-upstream-busy-or-timed-out", status.ReasonCode);
    }

    private static ProviderStatusManager CreateManager(
        IReadOnlyDictionary<string, string?> values,
        IHttpClientFactory? httpClientFactory = null,
        SpotifyApiSettings? spotifySettings = null,
        AppleDownloadSettings? appleMusicSettings = null,
        DeezerSettings? deezerSettings = null,
        QobuzSettings? qobuzSettings = null,
        IProviderRegistry? providerRegistry = null,
        IAppleDownloadEndpointDiscovery? appleDownloadDiscovery = null,
        IPlatformClock? clock = null,
        IServiceProvider? services = null,
        ProviderRuntimeHealth? health = null)
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
            health: health,
            appleDownloadDiscovery: appleDownloadDiscovery,
            services: services ?? (providerRegistry == null
                ? null
                : new ServiceCollection().AddSingleton(providerRegistry)
                    .AddSingleton(Mock.Of<allstarr.Core.Routing.IProviderRouteAccountResolver>())
                    .AddSingleton<ProviderDiagnosticExecutionFactory>().AddSingleton<ProviderTryOperations>()
                    .BuildServiceProvider()),
            clock: clock);
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
            return Json(HttpStatusCode.OK, "{\"data\":[{\"id\":3135556}]}");
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

    private sealed class FakeClock(DateTimeOffset utcNow) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
