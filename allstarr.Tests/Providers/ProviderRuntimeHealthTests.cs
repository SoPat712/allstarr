using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using HealthState = allstarr.Services.Common.ProviderHealthState;

namespace allstarr.Tests;

public sealed class ProviderRuntimeHealthTests
{
    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);
    private readonly Guid _account = Guid.CreateVersion7();
    private ProviderRuntimeStatusKey Key(string provider = "deezer", string capability = "streaming", Guid? account = null) =>
        ProviderRuntimeStatusKey.CreateManaged(provider, capability, account ?? _account);
    private ProviderRuntimeHealth Store() => new(clock: _clock);

    [Fact]
    public void ObservationsSeparateProviderAccountCapabilityAndRevision()
    {
        var store = Store();
        store.Record(Key(), 2, true, 12, null);
        store.Record(Key(capability: "playlist"), 2, false, 30, "unauthorized");
        store.Record(Key(account: Guid.NewGuid()), 2, false, 30, "timeout");
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 2)!.Health);
        Assert.Equal(HealthState.Degraded, store.Get(Key(capability: "playlist"), 2)!.Health);
        Assert.Null(store.Get(Key(), 3));
        Assert.Null(store.Get(Key(provider: "qobuz"), 2));
        Assert.Null(store.Get(ProviderRuntimeStatusKey.CreateAccountFree("deezer", "streaming")));
    }

    [Fact]
    public async Task ConcurrentFailuresAreAtomicAndOldRevisionCannotOverwriteNewCredentials()
    {
        var store = Store();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => store.Record(Key(), 1, false, 1, "timeout"))));
        var failed = store.Get(Key(), 1)!;
        Assert.Equal(3, failed.ConsecutiveFailures);
        Assert.True(store.IsCircuitOpen(failed));
        store.Record(Key(), 2, true, 7, null);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => store.Record(Key(), 1, false, 1, "timeout"))));
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 2)!.Health);
        Assert.Null(store.Get(Key(), 1));
    }

    [Fact]
    public void CooldownExpiresAndSuccessfulRetryClosesCircuit()
    {
        var store = Store();
        for (var i = 0; i < 3; i++) store.Record(Key(), 1, false, 3, "timeout");
        Assert.True(store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).CircuitOpen);
        _clock.UtcNow += TimeSpan.FromSeconds(61);
        var route = store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming);
        Assert.False(route.CircuitOpen);
        Assert.Equal(ProviderRouteHealthState.Unknown, route.State);
        Assert.Equal(HealthState.Degraded, store.Get(Key(), 1)!.Health);
        store.Record(Key(), 1, true, 10, null);
        Assert.Equal(ProviderRouteHealthState.Healthy, store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).State);
        Assert.Equal(0, store.Get(Key(), 1)!.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(ProviderErrorKind.NotFound)]
    [InlineData(ProviderErrorKind.NotSupported)]
    [InlineData(ProviderErrorKind.IncompatibleMedia)]
    [InlineData(ProviderErrorKind.Forbidden)]
    [InlineData(ProviderErrorKind.Canceled)]
    public void ResourceAndPolicyFailuresAreNotOutages(ProviderErrorKind kind)
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, new(kind), TimeSpan.Zero);
        Assert.Null(store.Get(Key()));
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unauthorized)]
    [InlineData(ProviderErrorKind.AccountNeedsReauthentication)]
    [InlineData(ProviderErrorKind.AccountNeedsConfiguration)]
    public void CredentialsFailureImmediatelyOpensExactRevisionCircuit(ProviderErrorKind kind)
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, new(kind), TimeSpan.FromMilliseconds(2));
        var route = store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming);
        Assert.True(route.CircuitOpen);
        Assert.Equal(4, route.AccountRevision);
    }

    [Fact]
    public void RateLimitHonorsRetryAfterAndRestartHasNoHydratedState()
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, new(ProviderErrorKind.RateLimited, TimeSpan.FromMinutes(10)), TimeSpan.Zero);
        _clock.UtcNow += TimeSpan.FromMinutes(9);
        Assert.True(store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).CircuitOpen);
        Assert.Null(Store().Get(Key()));
        _clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.False(store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).CircuitOpen);
    }

    [Fact]
    public void CanceledProbeCannotRestoreOverAConcurrentObservation()
    {
        var store = Store();
        store.Record(Key(), 4, false, 1, "timeout");
        var probe = store.BeginProbe(Key(), 4);
        Assert.Equal(HealthState.Testing, store.Get(Key(), 4)!.Health);
        store.Record(Key(), 5, true, 1, null);
        store.CancelProbe(probe);
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 5)!.Health);
        Assert.Null(store.BeginProbe(Key(), 4));
    }

    [Fact]
    public void CanceledProbeRestoresPreviousObservationAndExpiredStateIsUnknown()
    {
        var store = Store();
        store.Record(Key(), 4, true, 7, null);
        var original = store.Get(Key());
        var probe = store.BeginProbe(Key(), 4);
        store.CancelProbe(probe);
        Assert.Same(original, store.Get(Key()));
        _clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.Null(store.Get(Key()));
    }

    [Fact]
    public void RawDiagnosticTextNeverEntersStatus()
    {
        var store = Store();
        store.Record(Key(), 1, false, 1, "https://private.invalid?token=secret");
        Assert.Equal("probe_failed", store.Get(Key())!.ReasonCode);
    }

    private ProviderExecutionContext Context() => new(
        new ProviderActorContext(ProviderActorKind.Administrator, Guid.Parse("00000000-0000-0000-0000-000000000001"),
            new ProviderBackendPrincipal("jellyfin", "fixture", "admin")), "deezer",
        new ProviderAccountContext(_account, "deezer", ProviderAccountScope.Shared, 4),
        new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow, true, true, true, ["deezer"]),
        "test", "health-test", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = now; }
}
