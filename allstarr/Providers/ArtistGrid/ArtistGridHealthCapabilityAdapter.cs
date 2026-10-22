using System.Diagnostics;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.ArtistGrid;

public sealed class ArtistGridHealthCapabilityAdapter(
    ArtistGridMetadataCapabilityAdapter metadata,
    ArtistGridStreamingCapabilityAdapter streaming) : IProviderHealthProbeCapability
{
    public string ProviderId => ArtistGridConstants.ProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Health;

    public async Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(
        ProviderExecutionContext context, ProviderHealthProbeRequest request)
    {
        if (request.TargetCapability is not (ProviderCapabilityKind.Metadata or ProviderCapabilityKind.Streaming))
            return ProviderOutcome<ProviderHealthProbeResult>.Failure(new(ProviderErrorKind.NotSupported));
        var started = Stopwatch.GetTimestamp();
        var index = await metadata.SearchArtistsAsync(context, new("*", new(100)));
        if (!index.IsSuccess) return ProviderOutcome<ProviderHealthProbeResult>.Failure(index.Error!);
        ProviderError? failure = null;
        await foreach (var track in ArtistGridDiagnosticSamples.TracksAsync(metadata, context))
        {
            if (request.TargetCapability == ProviderCapabilityKind.Streaming)
            {
                var result = await streaming.ProbeStreamAsync(context, new(track.Id));
                if (!result.IsSuccess)
                {
                    failure = result.Error;
                    if (failure!.Kind is ProviderErrorKind.NotFound or ProviderErrorKind.IncompatibleMedia) continue;
                    return ProviderOutcome<ProviderHealthProbeResult>.Failure(failure);
                }
                if (!result.RequireValue().Available) continue;
            }
            return ProviderOutcome<ProviderHealthProbeResult>.Success(new(
                ProviderProbeStatus.Healthy, DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started)));
        }
        return ProviderOutcome<ProviderHealthProbeResult>.Failure(failure ?? new(ProviderErrorKind.NotFound));
    }
}
