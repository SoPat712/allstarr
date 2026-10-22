using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Monochrome;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class MonochromeSampleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Health_RemovedRecordingsUseAnotherSampleOrDiscovery(int missing)
    {
        var searches = 0;
        var media = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/search/tracks")
            {
                searches++;
                var tracks = searches <= missing ? Array.Empty<object>() : ProviderDiagnosticSamples.Recordings("monochrome")
                    .Select((sample, i) => (object)new { id = (154008664334667776L + i).ToString(), isrc = sample.Isrc }).ToArray();
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { tracks })) };
            }
            media++;
            return new(HttpStatusCode.OK) { Content = new StringContent("fLaC", System.Text.Encoding.UTF8, "audio/flac") };
        }));
        var probe = new MonochromeHealthCapabilityAdapter(new(http));
        var result = await probe.ProbeAsync(Context(), new(ProviderCapabilityKind.Streaming));
        Assert.Equal(ProviderProbeStatus.Healthy, result.RequireValue().Status);
        Assert.Equal(missing + 1, searches);
        Assert.Equal(1, media);
    }

    [Fact]
    public async Task Health_OutageKeepsSourceReasonAfterThreeSearches()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.ServiceUnavailable); }));
        var result = await new MonochromeHealthCapabilityAdapter(new(http)).ProbeAsync(Context(), new(ProviderCapabilityKind.Streaming));
        Assert.Equal(ProviderProbeStatus.Unavailable, result.RequireValue().Status);
        Assert.Equal("transient-failure", result.RequireValue().SafeCode);
        Assert.Equal(3, calls);
    }

    private static ProviderExecutionContext Context() => new(new(ProviderActorKind.PublicRead, null), "monochrome", null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, false, ["monochrome"]), "sample-check", "sample-check", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
