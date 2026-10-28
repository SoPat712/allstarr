using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderStreamOpenerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(300000000)]
    public async Task OriginalDownload_IgnoresDeliveryProfileLimitsAndOffsets(long startTimeTicks)
    {
        var bytes = ((byte[])[73, 68, 51, 4, 0, 0, 0, 0, 0, 0]).Concat(Flac()).ToArray();
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        upstream.Content.Headers.ContentType = new("audio/flac");
        upstream.Headers.Add("X-Allstarr-Duration-Seconds", "2.5");
        var delivery = new ProviderAudioDeliveryRequest
        {
            Profiles = [new(["mp3"], ["mp3"], ["http"])],
            MaxBitrate = 64000,
            StartTimeTicks = startTimeTicks
        };
        var context = _context with { IsOriginalDownload = true, AudioDelivery = delivery };
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(
                It.Is<ProtocolExecutionContext>(execution => execution.IsOriginalDownload && execution.AudioDelivery == null),
                "deezer", "42", ProviderAudioQuality.Any, null, false))
            .ReturnsAsync(new ProtocolProviderStream(upstream, SequentialLease(), "deezer", "42"));

        var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .OpenAsync(new DefaultHttpContext(), context, "deezer", "42", delivery.Quality, delivery);
        using var stream = Assert.IsType<FileStreamResult>(result).FileStream;
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);

        Assert.Equal(bytes, output.ToArray());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SourceInspection_UsesReadOnlyHeadAndDisposesItsBody()
    {
        using var body = new MemoryStream([1, 2, 3]);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(
                It.Is<ProtocolExecutionContext>(context => context.IsReadOnly && context.AudioDelivery == null),
                "deezer", "42", ProviderAudioQuality.Any, null, true))
            .ReturnsAsync(new ProtocolProviderStream(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) },
                Lease(), "deezer", "42"));

        var decision = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .InspectAsync(_context, "deezer", "42", new());

        Assert.True(decision.Supported);
        Assert.Equal("flac", decision.Media!.Codec);
        Assert.False(body.CanRead);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IncompatibleAudio_DisposesTheSourceBeforeAnyBytesAreDelivered()
    {
        using var body = new MemoryStream([1, 2, 3]);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<ProviderAudioQuality>(), null, false))
            .ReturnsAsync(new ProtocolProviderStream(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) },
                Lease(), "deezer", "42"));
        var http = new DefaultHttpContext();
        var request = ProviderAudioDeliveryRequests.Jellyfin(new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["AudioCodec"] = "unsupported-fixture-codec"
        }));

        await Assert.ThrowsAsync<ProviderAudioProfileException>(() => new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .OpenAsync(http, _context, "deezer", "42", request.Quality, request));

        Assert.False(body.CanRead);
        Assert.False(http.Response.HasStarted);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }
}
