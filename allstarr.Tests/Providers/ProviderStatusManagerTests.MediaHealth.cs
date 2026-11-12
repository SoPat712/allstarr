using allstarr.Core.Providers;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderStatusManagerTests
{
    [Theory]
    [InlineData("streaming")]
    [InlineData("download")]
    public async Task DeezerAccountProbeRetriesAColdResponseBeforeRecordingAnOutage(string capability)
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        var handler = new AppleProviderTestFactory.Handler(_ =>
            Json(HttpStatusCode.OK, ++calls == 1 ? "{}" : """{"results":{"USER":{"USER_ID":42}}}"""));
        using var services = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        var health = new ProviderRuntimeHealth();
        var manager = CreateManager(new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler), services: services, health: health);
        var account = Guid.NewGuid();
        var check = manager.TestManagedProviderCapabilityAsync("deezer", capability, account,
            new Dictionary<string, string> { ["arl"] = "fixture-cookie" });

        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimerAsync());
        Assert.Equal(1, calls);
        Assert.Empty(health.AlertSamples(ProviderRuntimeStatusKey.CreateManaged("deezer", capability, account), null));
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await check;

        Assert.Equal(2, calls);
        Assert.Equal(capability == "streaming" ? ProviderHealthState.Healthy : ProviderHealthState.Unknown, result.Health);
        Assert.DoesNotContain(health.AlertSamples(ProviderRuntimeStatusKey.CreateManaged("deezer", capability, account), null),
            sample => sample.Health == ProviderHealthState.Degraded);
    }

    [Fact]
    public async Task CancelingTheDeezerProbeRetryLeavesNoFailureEvidence()
    {
        var time = new ManualTimeProvider();
        using var services = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        var health = new ProviderRuntimeHealth();
        var manager = CreateManager(new Dictionary<string, string?>(), services: services, health: health,
            httpClientFactory: new HandlerHttpClientFactory(new AppleProviderTestFactory.Handler(_ => Json(HttpStatusCode.OK, "{}"))));
        using var cancellation = new CancellationTokenSource();
        var account = Guid.NewGuid();
        var check = manager.TestManagedProviderCapabilityAsync("deezer", "streaming", account,
            new Dictionary<string, string> { ["arl"] = "fixture-cookie" }, cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimerAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Null(health.Get(ProviderRuntimeStatusKey.CreateManaged("deezer", "streaming", account)));
    }

    [Theory]
    [InlineData("streaming")]
    [InlineData("download")]
    public async Task DeezerProbeRetriesKeepTheSelectedAccountsCredentials(string capability)
    {
        var time = new ManualTimeProvider();
        var cookies = new List<string>();
        var handler = new AppleProviderTestFactory.Handler(request =>
        {
            Assert.Equal("www.deezer.com", request.RequestUri!.Host);
            cookies.Add(Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Null(request.Headers.Authorization);
            return Json(HttpStatusCode.OK, cookies.Count % 2 == 1 ? "{}" : """{"results":{"USER":{"USER_ID":42}}}""");
        });
        using var services = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        var manager = CreateManager(new Dictionary<string, string?>(), services: services,
            deezerSettings: new() { Arl = "fixture-global-cookie" }, httpClientFactory: new HandlerHttpClientFactory(handler, "DeezerApi"));
        foreach (var cookie in new[] { "fixture-cookie-first", "fixture-cookie-second" })
        {
            var check = manager.TestManagedProviderCapabilityAsync("deezer", capability, Guid.NewGuid(),
                new Dictionary<string, string> { ["arl"] = cookie });
            Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimerAsync());
            time.Advance(TimeSpan.FromSeconds(1));
            var result = await check;
            Assert.Equal(capability == "streaming" ? ProviderHealthState.Healthy : ProviderHealthState.Unknown, result.Health);
        }
        Assert.Equal(new[] { "arl=fixture-cookie-first", "arl=fixture-cookie-first",
            "arl=fixture-cookie-second", "arl=fixture-cookie-second" }, cookies);
        var missing = await manager.TestManagedProviderCapabilityAsync("deezer", capability, Guid.NewGuid(),
            new Dictionary<string, string>());
        Assert.Equal(ProviderConfigurationState.NeedsConfiguration, missing.Configuration);
        Assert.Equal(4, cookies.Count);
    }

    [Theory]
    [InlineData("streaming")]
    [InlineData("download")]
    public void MediaStatusRequiresThreeRecentFailuresAndSuccessfulActivityRecoversIt(string capability)
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", capability);
        ProviderRuntimeStatus Check(bool passed) => new()
        {
            Provider = "deezer",
            Capability = capability,
            IsSupported = true,
            IsEnabled = true,
            Configuration = ProviderConfigurationState.Configured,
            Health = passed ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
            TestedAt = clock.UtcNow,
            ReasonCode = passed ? null : "probe_failed"
        };
        manager.RestoreCheckResult(key, null, Check(true));
        for (var count = 1; count <= 3; count++)
        {
            clock.UtcNow += TimeSpan.FromSeconds(1);
            manager.RestoreCheckResult(key, null, Check(false));
            Assert.Equal(count < 3 ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                manager.GetLatestCapabilityHealth(MediaContext(clock), capability == "streaming" ? ProviderCapabilityKind.Streaming : ProviderCapabilityKind.Download));
        }
        clock.UtcNow += TimeSpan.FromSeconds(1);
        health.Observe(MediaContext(clock), capability == "streaming" ? ProviderCapabilityKind.Streaming : ProviderCapabilityKind.Download,
            null, TimeSpan.FromMilliseconds(120));
        Assert.Equal(ProviderHealthState.Healthy,
            manager.GetLatestCapabilityHealth(MediaContext(clock), capability == "streaming" ? ProviderCapabilityKind.Streaming : ProviderCapabilityKind.Download));
    }

    [Fact]
    public void AnOldIsolatedMediaFailureIsNotACurrentOutage()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock);
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", "streaming");
        manager.RestoreCheckResult(key, null, new()
        {
            Provider = "deezer",
            Capability = "streaming",
            IsSupported = true,
            IsEnabled = true,
            Configuration = ProviderConfigurationState.Configured,
            Health = ProviderHealthState.Degraded,
            TestedAt = clock.UtcNow.AddHours(-1),
            ReasonCode = "probe_failed"
        });
        Assert.Equal(ProviderHealthState.Unknown, manager.GetLatestCapabilityHealth(MediaContext(clock), ProviderCapabilityKind.Streaming));
    }

    [Theory]
    [InlineData("streaming", false, false)]
    [InlineData("streaming", false, true)]
    [InlineData("streaming", true, false)]
    [InlineData("streaming", true, true)]
    [InlineData("download", false, false)]
    [InlineData("download", false, true)]
    [InlineData("download", true, false)]
    [InlineData("download", true, true)]
    public void ManualMediaTryReportsItsOutcomeWhileAutomaticHealthRequiresSustainedFailures(
        string capability, bool managed, bool previouslyHealthy)
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var health = new ProviderRuntimeHealth(clock: clock);
        var streaming = new Mock<IProviderStreamingCapability>();
        streaming.SetupGet(item => item.ProviderId).Returns("fixture");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        var download = new Mock<IProviderDownloadCapability>();
        download.SetupGet(item => item.ProviderId).Returns("fixture");
        download.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Download);
        var descriptor = new ProviderDescriptor("fixture", "Fixture", "Fixture", ProviderOrigin.Extension, "1", "1",
            [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Optional,
                "1", ["getStreamLease"], [ProviderAccountScope.Shared]),
            new(ProviderCapabilityKind.Download, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Optional,
                "1", ["download", "checkAvailability"], [ProviderAccountScope.Shared])], new(), entryPoint: "index.js");
        var registry = new ProviderRegistry([new(descriptor, [streaming.Object, download.Object])]);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health, providerRegistry: registry);
        var account = managed ? new ProviderAccountContext(Guid.NewGuid(), "fixture", ProviderAccountScope.Shared, 7) : null;
        var context = new ProviderExecutionContext(
            new(ProviderActorKind.Administrator, Guid.NewGuid(), new("jellyfin", "fixture", "admin")), "fixture", account,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, true, ["fixture"]), "playback", "media-health-test", clock.UtcNow.AddMinutes(1), default);
        var key = account == null ? ProviderRuntimeStatusKey.CreateAccountFree("fixture", capability) :
            ProviderRuntimeStatusKey.CreateManaged("fixture", capability, account.AccountId);
        var kind = capability == "streaming" ? ProviderCapabilityKind.Streaming : ProviderCapabilityKind.Download;
        ProviderRuntimeStatus Display() => account == null ? manager.GetAccountFreeStatus("fixture", capability) :
            manager.GetManagedStatus("fixture", capability, account.AccountId,
                new Dictionary<string, string> { ["arl"] = "fixture-cookie" }, account.Revision);
        if (previouslyHealthy)
            manager.RestoreCheckResult(key, account?.Revision, Display() with
            { Health = ProviderHealthState.Healthy, TestedAt = clock.UtcNow });

        for (var count = 1; count <= 3; count++)
        {
            clock.UtcNow += TimeSpan.FromSeconds(1);
            manager.RecordTryResult("fixture", capability, account?.AccountId, account?.Revision, false, 120, "TransientFailure");
            var displayed = Display();
            Assert.Equal(ProviderHealthState.Degraded, displayed.Health);
            Assert.Equal("transient-failure", displayed.ReasonCode);
            Assert.Equal(120, displayed.LatencyMilliseconds);
            Assert.Equal(count, health.Get(key, account?.Revision)!.ConsecutiveFailures);
            Assert.Equal(count == 3, displayed.CircuitOpen);
            var automatic = count == 3 ? ProviderHealthState.Degraded :
                previouslyHealthy ? ProviderHealthState.Healthy : ProviderHealthState.Unknown;
            Assert.Equal(automatic, manager.GetLatestCapabilityHealth(context, kind));
            Assert.Equal(automatic, manager.GetCurrentCheckHealth(new(key, account?.Revision, displayed)));
            Assert.Equal(ProviderHealthState.Degraded, manager.RecentAttempts(key, account?.Revision)[0].Status.Health);
        }

        clock.UtcNow += TimeSpan.FromSeconds(1);
        health.Observe(context, kind, null, TimeSpan.FromMilliseconds(80));
        Assert.Equal(ProviderHealthState.Healthy, Display().Health);
        Assert.Null(Display().ReasonCode);
        Assert.False(Display().CircuitOpen);
        Assert.Equal(ProviderHealthState.Healthy, manager.GetLatestCapabilityHealth(context, kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingAMediaResultDoesNotCountTheSameFailureTwice(bool tryResult)
    {
        var clock = new AdvancingMediaClock();
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", "streaming");
        manager.RestoreCheckResult(key, null, new()
        {
            Provider = "deezer",
            Capability = "streaming",
            IsSupported = true,
            IsEnabled = true,
            Configuration = ProviderConfigurationState.Configured,
            Health = ProviderHealthState.Healthy,
            TestedAt = clock.UtcNow
        });
        if (tryResult) manager.RecordTryResult("deezer", "streaming", null, null, false, 200, "probe_failed");
        else
        {
            var measured = manager.RecordStreamingMeasurement(
                ProviderCtsDiagnosticResult.Failure(503, "deezer", null, "resolve", "probe_failed"), null)!;
            manager.RestoreCheckResult(key, null, measured.Status);
        }
        Assert.Equal(1, health.Get(key)!.ConsecutiveFailures);
        Assert.Equal(ProviderHealthState.Healthy, health.Get(key)!.Health);
    }

    private sealed class AdvancingMediaClock : IPlatformClock
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public DateTimeOffset UtcNow => now += TimeSpan.FromMilliseconds(1);
    }

    private static ProviderExecutionContext MediaContext(FakeClock clock) => new(
        new(ProviderActorKind.Administrator, Guid.NewGuid(), new("jellyfin", "fixture", "admin")), "deezer", null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, true, ["deezer"]), "playback", "media-health-test", clock.UtcNow.AddMinutes(1), default);
}
