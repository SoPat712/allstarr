using System.Diagnostics;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.ArtistGrid;

public sealed class ArtistGridHealthCapabilityAdapter(
    ArtistGridMetadataCapabilityAdapter metadata,
    ArtistGridStreamingCapabilityAdapter streaming) : IProviderHealthProbeCapability
{
    private readonly ProviderDiagnosticSamples _diagnosticSamples = new();

    public string ProviderId => ArtistGridConstants.ProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Health;

    public async Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(
        ProviderExecutionContext context, ProviderHealthProbeRequest request)
    {
        if (request.TargetCapability is not (ProviderCapabilityKind.Metadata or ProviderCapabilityKind.Streaming))
            return ProviderOutcome<ProviderHealthProbeResult>.Failure(new(ProviderErrorKind.NotSupported));
        var started = Stopwatch.GetTimestamp();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = await _diagnosticSamples.CheckAsync($"artistgrid:{request.TargetCapability}",
            ProviderDiagnosticSamples.Recordings(ProviderId), async (sample, token) =>
            {
                var execution = ProviderDiagnosticSamples.WithCancellation(context, token);
                ProviderExternalResourceId? id = sample.TrackId == null ? null : new(ProviderId, ProviderResourceKind.Track, sample.TrackId);
                if (id == null)
                {
                    var tracks = await metadata.SearchTracksAsync(execution, new(sample.Query, new(3)));
                    if (!tracks.IsSuccess) return ProviderDiagnosticSamples.FromError(tracks.Error!);
                    id = tracks.RequireValue().Items.FirstOrDefault(track => !seen.Contains(track.Id.Value))?.Id;
                }
                if (sample.TrackId != null && request.TargetCapability == ProviderCapabilityKind.Metadata)
                {
                    var detail = await metadata.GetTrackAsync(execution, new(id!));
                    if (!detail.IsSuccess) return ProviderDiagnosticSamples.FromError(detail.Error!);
                }
                if (id == null || !seen.Add(id.Value)) return ProviderSampleCheck.Missing;
                if (request.TargetCapability == ProviderCapabilityKind.Streaming)
                {
                    var probe = await streaming.ProbeStreamAsync(execution, new(id));
                    if (!probe.IsSuccess) return ProviderDiagnosticSamples.FromError(probe.Error!);
                    if (!probe.RequireValue().Available) return ProviderSampleCheck.Missing;
                }
                return new(true);
            }, DiscoverAsync, context.CancellationToken);
        return ProviderOutcome<ProviderHealthProbeResult>.Success(new(
            result.Success ? ProviderProbeStatus.Healthy : ProviderProbeStatus.Unavailable,
            DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started), result.SampleUnavailable ? "sample-unavailable" : result.ReasonCode));

        async IAsyncEnumerable<ProviderDiagnosticSample> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var track in ArtistGridDiagnosticSamples.TracksAsync(metadata, ProviderDiagnosticSamples.WithCancellation(context, token)))
                yield return ProviderDiagnosticSamples.FromTrack(track);
        }
    }
}
