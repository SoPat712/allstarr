using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class MediaCapacityTests
{
    [Fact]
    public async Task LoweringCapacityPreservesExistingLeasesAndRejectsNewWork()
    {
        var capacity = 2;
        var gate = new BoundedOperationGate(() => capacity);
        var one = await gate.TryEnterAsync();
        var two = await gate.TryEnterAsync();
        Assert.NotNull(one);
        Assert.NotNull(two);
        capacity = 1;
        Assert.Null(await gate.TryEnterAsync());
        one.Dispose();
        one.Dispose();
        Assert.Null(await gate.TryEnterAsync());
        two.Dispose();
        using var next = await gate.TryEnterAsync();
        Assert.NotNull(next);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public void ListenerAdmissionUsesVerifiedIdentityRatherThanDeviceOrOriginalPurpose(ProtocolKind protocol)
    {
        var config = new ConfigurationManager { [MediaCapacityLimits.StreamsKey] = "1" };
        var admission = new ProviderStreamAdmission(new(config));
        var principal = new AllstarrPrincipal(Guid.NewGuid(), protocol.ToString().ToLowerInvariant(), "primary", "listener", "Listener", false);
        var first = Context(protocol, principal, "one");
        var lease = admission.TryEnter(first);
        Assert.NotNull(lease);
        Assert.Null(admission.TryEnter(Context(protocol, principal, "two") with { IsOriginalDownload = true }));
        using var other = admission.TryEnter(Context(protocol, principal with { BackendPrincipalId = "other", UserId = Guid.NewGuid() }, "one"));
        using var backend = admission.TryEnter(Context(protocol, principal with { BackendInstanceId = "other" }, "one"));
        Assert.NotNull(other);
        Assert.NotNull(backend);
        lease.Dispose();
        lease.Dispose();
        using var replacement = admission.TryEnter(first);
        Assert.NotNull(replacement);
    }

    [Fact]
    public async Task ConversionRefusalNeverCallsSpawnAndCancellationOrFailureReleasesThePermit()
    {
        var config = new ConfigurationManager { [MediaCapacityLimits.ConversionsKey] = "1" };
        var conversions = new MediaConversionGate(new(config));
        var calls = 0;
        var held = await conversions.EnterAsync(default);
        await Assert.ThrowsAsync<LocalCapacityException>(() => conversions.RunAsync(() => { calls++; return Task.FromResult(1); }, default));
        Assert.Equal(0, calls);
        held.Dispose();
        await Assert.ThrowsAsync<IOException>(() => conversions.RunAsync<int>(() => throw new IOException(), default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversions.EnterAsync(cancellation.Token).AsTask());
        Assert.Equal(1, await conversions.RunAsync(() => Task.FromResult(1), default));
    }

    private static ProtocolExecutionContext Context(ProtocolKind kind, AllstarrPrincipal principal, string device) =>
        new(kind, principal.BackendInstanceId, principal.BackendPrincipalId, principal, "fixture", DateTimeOffset.UtcNow.AddMinutes(1),
            default, new("fixture", device));
}
