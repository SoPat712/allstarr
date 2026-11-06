using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;

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
