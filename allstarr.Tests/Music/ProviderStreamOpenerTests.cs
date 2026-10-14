using System.Net;
using System.Buffers.Binary;
using System.Net.Http.Headers;
using allstarr.Models.Domain;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialFlacFillsDurationAndRemovesLegacyTagBeforeDelivery(bool legacyTag)
    {
        var bytes = Flac();
        if (legacyTag) bytes = ((byte[])[73, 68, 51, 4, 0, 0, 0, 0, 0, 0]).Concat(bytes).ToArray();
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(bytes)) };
        upstream.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        upstream.Content.Headers.ContentLength = bytes.Length;
        upstream.Headers.Add("X-Allstarr-Duration-Seconds", "2.5");
        var gateway = Gateway(upstream, false);
        var http = new DefaultHttpContext();
        http.Request.Headers.Range = "bytes=100-";
        gateway.Setup(item => item.OpenStreamAsync(_context, "deezer", "42", ProviderAudioQuality.Any, "bytes=100-", false))
            .ReturnsAsync(new ProtocolProviderStream(upstream, SequentialLease(), "deezer", "42"));

        var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .OpenAsync(http, _context, "deezer", "42", ProviderAudioQuality.Any);
        var file = Assert.IsType<FileStreamResult>(result);
        using var output = new MemoryStream();
        await file.FileStream.CopyToAsync(output);
        var delivered = output.ToArray();

        Assert.Equal(120000UL, BinaryPrimitives.ReadUInt64BigEndian(delivered.AsSpan(18, 8)) & ((1UL << 36) - 1));
        Assert.Equal(Flac()[26..], delivered[26..]);
        Assert.Equal(Flac().Length, http.Response.ContentLength);
        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal(0, http.Response.Headers.AcceptRanges.Count);
        Assert.Equal(0, http.Response.Headers.ContentRange.Count);
        Assert.False(file.EnableRangeProcessing);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MissingProbeUsesSongMetadataOnceAndReplaysEveryByte()
    {
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Flac()) };
        var gateway = Gateway(upstream, false);
        gateway.Setup(item => item.GetSongAsync(_context, "deezer", "42"))
            .ReturnsAsync(new Song { Duration = 3 });
        var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .OpenAsync(new DefaultHttpContext(), _context, "deezer", "42", ProviderAudioQuality.Any);
        using var output = new MemoryStream();
        await Assert.IsType<FileStreamResult>(result).FileStream.CopyToAsync(output);
        var delivered = output.ToArray();
        Assert.Equal(144000UL, BinaryPrimitives.ReadUInt64BigEndian(delivered.AsSpan(18, 8)) & ((1UL << 36) - 1));
        Assert.Equal(Flac()[26..], delivered[26..]);
        gateway.Verify(item => item.GetSongAsync(_context, "deezer", "42"), Times.Once);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    public async Task InvalidDurationAndFailedMetadataKeepSequentialBytes(string duration)
    {
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Flac()) };
        upstream.Headers.Add("X-Allstarr-Duration-Seconds", duration);
        var gateway = Gateway(upstream, false);
        gateway.Setup(item => item.GetSongAsync(_context, "deezer", "42")).ThrowsAsync(new HttpRequestException());
        var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .OpenAsync(new DefaultHttpContext(), _context, "deezer", "42", ProviderAudioQuality.Any);
        using var output = new MemoryStream();
        await Assert.IsType<FileStreamResult>(result).FileStream.CopyToAsync(output);
        Assert.Equal(Flac(), output.ToArray());
    }

    [Fact]
    public async Task TrueRangeTransportKeepsEncodedBytesAndDoesNotFetchMetadata()
    {
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Flac()) };
        upstream.Headers.Add("X-Allstarr-Duration-Seconds", "2.5");
        var gateway = Gateway(upstream, true);
        var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
            .OpenAsync(new DefaultHttpContext(), _context, "deezer", "42", ProviderAudioQuality.Any);
        using var output = new MemoryStream();
        await Assert.IsType<FileStreamResult>(result).FileStream.CopyToAsync(output);
        Assert.Equal(Flac(), output.ToArray());
        gateway.VerifyAll();
    }

    [Fact]
    public void MappingRetainsSubsecondDurationWithoutChangingSongWireShape()
    {
        var duration = TimeSpan.FromSeconds(2.56789);
        var song = ProtocolProviderGateway.Map(new ProviderTrackMetadata(new("deezer", ProviderResourceKind.Track, "42"),
            "Fixture", [new ProviderArtistCredit("Artist")], duration: duration));
        Assert.Equal(duration, song.PreciseDuration);
        Assert.Equal(2, song.Duration);
        Assert.DoesNotContain("PreciseDuration", System.Text.Json.JsonSerializer.Serialize(song), StringComparison.OrdinalIgnoreCase);
    }

    private Mock<IProtocolProviderGateway> Gateway(HttpResponseMessage upstream, bool ranges)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(_context, "deezer", "42", ProviderAudioQuality.Any, null, false))
            .ReturnsAsync(new ProtocolProviderStream(upstream, ranges ? Lease() : SequentialLease(), "deezer", "42"));
        return gateway;
    }

    private static byte[] Flac()
    {
        var bytes = new byte[50];
        "fLaC"u8.CopyTo(bytes);
        bytes[7] = 34;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), (48000UL << 44) | (1UL << 41) | (15UL << 36));
        bytes[49] = 99;
        return bytes;
    }

    private static ProviderStreamLease SequentialLease() => new("sequential", new Uri("https://media.example.test/track"),
        DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry);

    private static ProviderStreamLease Lease() => new(
        "lease",
        new Uri("https://media.example.test/track"),
        DateTimeOffset.UtcNow.AddMinutes(1),
        supportsByteRanges: true,
        supportsSeeking: true,
        new ProviderMediaFormat("audio/flac", "flac", "flac"),
        ProviderStreamRetryBehavior.DoNotRetry);
}
