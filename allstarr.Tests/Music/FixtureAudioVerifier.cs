using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Tests;

// Routing and ownership fixtures use opaque payloads; media-integrity fixtures use real audio.
internal sealed class FixtureAudioVerifier : IProviderAudioVerifier
{
    internal static FixtureAudioVerifier Instance { get; } = new();
    public Task<ProviderOutcome<TrackMediaFacts>> VerifyPrefixAsync(HttpResponseMessage response,
        ProviderMediaFormat expected, double? recordingSeconds, CancellationToken token,
        Func<long, long?, CancellationToken, Task<HttpResponseMessage>>? openRange = null) =>
        Task.FromResult(ProviderOutcome<TrackMediaFacts>.Success(PrefetchedStream.Facts(response.Content) ??
            new(expected, response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength)));
    public async Task<TrackMediaFacts> VerifyFileAsync(string path, ProviderMediaFormat expected,
        double? recordingSeconds, CancellationToken token, bool derivedDuration = false) =>
        await AudioMediaFacts.ReadFileAsync(path, token) ?? new(expected, new FileInfo(path).Length);
}
