using System.Diagnostics;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class SpicyLyricsHealthCapabilityAdapter(SpicyLyricsCapabilityAdapter lyrics) : IDisposable
{
    private readonly ProviderDiagnosticSamples samples = new();
    public void Dispose() => samples.Dispose();

    internal async Task<ProviderOutcome<ProviderHealthProbeResult>> CheckLyricsAsync(ProviderExecutionContext context,
        IReadOnlyList<ProviderDiagnosticSample>? recordings = null)
    {
        var started = Stopwatch.GetTimestamp();
        ProviderError? error = null;
        var result = await samples.CheckAsync($"spicylyrics:{context.Account?.AccountId}:{context.Account?.Revision}",
            recordings ?? ProviderDiagnosticSamples.Recordings("spicylyrics"), async (sample, token) =>
            {
                if (!SpotifyTrackReference.TryNormalize(sample.TrackId, out var id)) return ProviderSampleCheck.Missing;
                var outcome = await lyrics.FetchCoreAsync(ProviderDiagnosticSamples.WithCancellation(context, token),
                    new(Guid.NewGuid(), new("spicylyrics", ProviderResourceKind.Track, id), availabilityOnly: true),
                    LyricsCompletionMode.RequestBound, useCache: false);
                if (!outcome.IsSuccess)
                {
                    error = outcome.Error;
                    return new(false, error!.Code, Stop: error.Kind is ProviderErrorKind.AccountNeedsConfiguration or
                        ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.Forbidden or ProviderErrorKind.RateLimited);
                }
                return outcome.RequireValue().Availability == ProviderLyricsAvailabilityState.Available ? new(true) : ProviderSampleCheck.Missing;
            }, cancellationToken: context.CancellationToken);
        if (!result.Success && error != null && !result.SampleUnavailable)
            return ProviderOutcome<ProviderHealthProbeResult>.Failure(error);
        return ProviderOutcome<ProviderHealthProbeResult>.Success(new(result.Success ? ProviderProbeStatus.Healthy : ProviderProbeStatus.Unavailable,
            DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started), result.Success ? null : "sample-unavailable"));
    }
}
