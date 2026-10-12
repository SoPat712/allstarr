using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderStreamOpenerTests
{
    private readonly ProtocolExecutionContext _context = new(
        ProtocolKind.Jellyfin, "backend", "principal", null, "stream-test",
        DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    [Fact]
    public async Task MissingRouteReportsThatNoSourceIsAvailable()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(_context, "deezer", "42", ProviderAudioQuality.Lossless, null, false))
            .ReturnsAsync((ProtocolProviderStream?)null);

        var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .OpenAsync(new DefaultHttpContext(), _context, "deezer", "42", ProviderAudioQuality.Lossless);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task UpstreamFailureKeepsItsStatusAndForwardsRangeAndHead()
    {
        var upstream = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(_context, "deezer", "42", ProviderAudioQuality.Any, "bytes=10-", true))
            .ReturnsAsync(new ProtocolProviderStream(upstream, Lease(), "deezer"));
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Head;
        http.Request.Headers.Range = "bytes=10-";

        var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .OpenAsync(http, _context, "deezer", "42", ProviderAudioQuality.Any);

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        gateway.VerifyAll();
    }

    private static ProviderStreamLease Lease() => new(
        "lease",
        new Uri("https://media.example.test/track"),
        DateTimeOffset.UtcNow.AddMinutes(1),
        supportsByteRanges: true,
        supportsSeeking: true,
        new ProviderMediaFormat("audio/flac", "flac", "flac"),
        ProviderStreamRetryBehavior.DoNotRetry);
}
