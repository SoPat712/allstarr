using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Providers.Monochrome;
using allstarr.Core.Routing;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class MonochromeDiagnosticTests
{
    [Theory]
    [InlineData("audio", true, null)]
    [InlineData("html", false, "IncompatibleMedia")]
    [InlineData("oversized", false, "IncompatibleMedia")]
    [InlineData("wrong-range", false, "IncompatibleMedia")]
    [InlineData("unavailable", false, "TransientFailure")]
    public async Task Measurement_UsesTheRealAdapterWithRangeResponses(string responseKind, bool succeeded, string? reason)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("tracks.monochrome.st", request.RequestUri!.Host);
            Assert.Equal($"/track/{MonochromeStreamingCapabilityAdapter.SampleTrackId}", request.RequestUri.AbsolutePath);
            var range = request.Headers.Range!;
            requests.Add(range.ToString());
            var end = range.Ranges.Single().To!.Value;
            var response = new HttpResponseMessage(responseKind == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.PartialContent)
            {
                Version = HttpVersion.Version20,
                Content = new ByteArrayContent(Enumerable.Repeat((byte)102, (int)end + 1).ToArray())
            };
            response.Content.Headers.ContentType = new(responseKind == "html" ? "text/html" : "application/octet-stream");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(responseKind == "wrong-range" ? 1 : 0,
                responseKind == "wrong-range" ? end + 1 : end,
                responseKind == "oversized" ? 2L * 1024 * 1024 * 1024 + 1 : 22349828);
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        }));
        var registry = new ProviderRegistry([MonochromeProviderRegistration.CreateRegistration(new(http))]);
        using var selector = new ProviderCtsTrackSelector(new ProviderCheckServiceTests.Factory(database.Options));
        var runner = new ProviderCtsDiagnosticRunner(registry, Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict),
            selector, Mock.Of<IProviderOutcomeObserver>());
        var actor = new ProviderActorContext(ProviderActorKind.Administrator, Guid.NewGuid(), new("jellyfin", "fixture", "admin"));

        var result = await runner.MeasureAsync(actor, "monochrome", null, ProviderAudioQuality.HighResolution,
            "range-measurement", householdCheck: true);

        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(succeeded ? new[] { "bytes=0-0", "bytes=0-65535" } : ["bytes=0-0"], requests);
        if (!succeeded) return;
        Assert.Equal(65536, result.SampleBytes);
        Assert.Equal("application/octet-stream", result.ContentType);
        Assert.Equal("byte-range", result.SeekRung);
        Assert.Equal("flac", result.Media!.Codec);
        Assert.Null(result.Media.Bitrate);
        Assert.Null(result.Media.SampleRate);
        Assert.Null(result.Media.Channels);
        Assert.NotNull(result.QualityDowngradeReason);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
