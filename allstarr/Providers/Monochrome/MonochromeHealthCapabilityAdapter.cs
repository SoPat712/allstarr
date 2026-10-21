using System.Diagnostics;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Monochrome;

public sealed class MonochromeHealthCapabilityAdapter(MonochromeStreamingCapabilityAdapter streaming) : IProviderHealthProbeCapability
{
    public string ProviderId => "monochrome";
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Health;

    public async Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(
        ProviderExecutionContext context, ProviderHealthProbeRequest request)
    {
        if (request.TargetCapability != ProviderCapabilityKind.Streaming)
            return ProviderOutcome<ProviderHealthProbeResult>.Failure(new(ProviderErrorKind.NotSupported));
        var started = Stopwatch.GetTimestamp();
        var result = await streaming.ProbeStreamAsync(context,
            new(new("monochrome", ProviderResourceKind.Track, MonochromeStreamingCapabilityAdapter.SampleTrackId)));
        if (!result.IsSuccess) return ProviderOutcome<ProviderHealthProbeResult>.Failure(result.Error!);
        return ProviderOutcome<ProviderHealthProbeResult>.Success(new(
            result.RequireValue().Available ? ProviderProbeStatus.Healthy : ProviderProbeStatus.Unavailable,
            DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started)));
    }
}
