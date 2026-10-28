using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task PlaybackTimingSeparatesFailedAndSuccessfulSourcePhases(ProtocolKind protocol)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var timing = new PlaybackStartTimings(clock);
        timing.Enter(); timing.Enable();
        using var activity = new PlaybackDeliveryActivityStore();
        var first = Streaming("deezer", (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(30));
            return Task.FromResult(AudioResponse(HttpStatusCode.BadGateway));
        });
        var second = Streaming("qobuz", (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            return Task.FromResult(AudioResponse());
        });
        var (gateway, _) = FailoverGateway(first, second, activity, clock: clock);
        using var response = (await gateway.OpenStreamAsync(DownloadStartContext(protocol, "Balanced", clock),
            "allstarr", "recording", ProviderAudioQuality.Any, null))!.Response;
        var sample = timing.Snapshot("completed");
        Assert.Equal("qobuz", sample.Provider);
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Route);
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Lease && phase.Provider == "deezer");
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Source && phase.Provider == "deezer" && phase.ElapsedMs == 30);
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Source && phase.Provider == "qobuz" && phase.ElapsedMs == 50);
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Verify && phase.Provider == "qobuz");
    }
}
