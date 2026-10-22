using System.Diagnostics;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Monochrome;

public sealed class MonochromeHealthCapabilityAdapter(MonochromeStreamingCapabilityAdapter streaming) : IProviderHealthProbeCapability
{
    private readonly ProviderDiagnosticSamples _diagnosticSamples = new();

    public string ProviderId => "monochrome";
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Health;

    public async Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(
        ProviderExecutionContext context, ProviderHealthProbeRequest request)
    {
        if (request.TargetCapability != ProviderCapabilityKind.Streaming)
            return ProviderOutcome<ProviderHealthProbeResult>.Failure(new(ProviderErrorKind.NotSupported));
        var started = Stopwatch.GetTimestamp();
        var result = await _diagnosticSamples.CheckAsync("monochrome:health", ProviderDiagnosticSamples.Recordings(ProviderId),
            async (sample, token) =>
            {
                var execution = ProviderDiagnosticSamples.WithCancellation(context, token);
                var match = sample.TrackId != null
                    ? ProviderOutcome<ProviderRecordingAvailability>.Success(new(new(ProviderId, ProviderResourceKind.Track, sample.TrackId)))
                    : sample.Isrc != null ? await streaming.ResolveRecordingAsync(execution,
                        new(sample.Query, sample.Title, [sample.Artist], sample.Isrc, sample.DurationSeconds * 1000, new Dictionary<string, string>()), ProviderAudioQuality.Lossless)
                    : await streaming.DiscoverSampleAsync(execution, sample.Title, ProviderAudioQuality.Lossless);
                if (!match.IsSuccess) return ProviderDiagnosticSamples.FromError(match.Error!);
                if (match.RequireValue().TrackId is not { } id) return ProviderSampleCheck.Missing;
                var probe = await streaming.ProbeStreamAsync(execution, new(id));
                if (!probe.IsSuccess) return ProviderDiagnosticSamples.FromError(probe.Error!);
                if (!probe.RequireValue().Available) return ProviderSampleCheck.Missing;
                return new(true);
            }, DiscoverAsync, context.CancellationToken);
        return ProviderOutcome<ProviderHealthProbeResult>.Success(new(
            result.Success ? ProviderProbeStatus.Healthy : ProviderProbeStatus.Unavailable,
            DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started), result.SampleUnavailable ? "sample-unavailable" : result.ReasonCode));
    }

    private static async IAsyncEnumerable<ProviderDiagnosticSample> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        foreach (var query in ProviderDiagnosticSamples.DiscoveryQueries("monochrome"))
        {
            token.ThrowIfCancellationRequested();
            yield return new(query, "", null, 200);
        }
    }
}
