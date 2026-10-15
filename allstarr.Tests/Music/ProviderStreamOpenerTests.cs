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
using Microsoft.Extensions.Configuration;

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
        Assert.Null(http.Response.ContentLength);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedAudio_ReturnsHeadersWithinOneSecondAndPatchesLazilyFromCatalog(bool cacheEnabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal",
            new allstarr.Core.Identity.AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "principal", "Listener", false),
            "delayed-stream", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        string? registeredPath = null;
        var library = new Mock<allstarr.Services.Local.ILocalLibraryService>(MockBehavior.Strict);
        library.Setup(item => item.GetLocalPathForExternalSongAsync(
                It.IsAny<allstarr.Core.Downloads.DownloadedSongMappingScope>(), "deezer", "42"))
            .ReturnsAsync(() => registeredPath);
        library.Setup(item => item.RegisterDownloadedSongAsync(
                It.IsAny<allstarr.Core.Downloads.DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
            .Callback<allstarr.Core.Downloads.DownloadedSongMappingScope, Song, string>((_, _, path) => registeredPath = path)
            .Returns(Task.CompletedTask);
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build();
        var cache = cacheEnabled ? new allstarr.Services.Common.ManagedTrackCacheService(configuration,
            Microsoft.Extensions.Options.Options.Create(new allstarr.Models.Settings.SubsonicSettings
            { StorageMode = allstarr.Models.Settings.StorageMode.Cache }), library.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Services.Common.ManagedTrackCacheService>.Instance) : null;
        using var canceled = new CancellationTokenSource();
        var source = new DelayedAudio(Flac());
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        upstream.Content.Headers.ContentType = new("audio/flac");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(protocol, "deezer", "42", ProviderAudioQuality.Any, null, false))
            .ReturnsAsync(new ProtocolProviderStream(upstream, SequentialLease(), "deezer", "42"));
        gateway.Setup(item => item.GetSongAsync(protocol, "deezer", "42"))
            .ReturnsAsync(new Song { PreciseDuration = TimeSpan.FromSeconds(2.5) });
        var http = new DefaultHttpContext();
        http.RequestAborted = canceled.Token;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new MemoryStream();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(new StartedBody(body, started));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions.AddControllers(services);
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        http.RequestServices = provider;
        try
        {
            var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter(), cache)
                .OpenAsync(http, protocol, "deezer", "42", ProviderAudioQuality.Any).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(source.ReadStarted);
            var execution = result.ExecuteResultAsync(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(source.ReadStarted);
            Assert.False(execution.IsCompleted);
            await execution;
            var delivered = body.ToArray();
            Assert.Equal(120000UL, BinaryPrimitives.ReadUInt64BigEndian(delivered.AsSpan(18, 8)) & ((1UL << 36) - 1));
            Assert.Equal(Flac()[26..], delivered[26..]);
            gateway.Verify(item => item.GetSongAsync(protocol, "deezer", "42"), Times.Once);
            if (cacheEnabled)
            {
                Assert.NotNull(registeredPath);
                Assert.Equal(delivered, await File.ReadAllBytesAsync(registeredPath));
            }
        }
        finally
        {
            canceled.Cancel();
            upstream.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class DelayedAudio(byte[] bytes) : MemoryStream(bytes)
    {
        public bool ReadStarted;
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!ReadStarted)
            {
                ReadStarted = true;
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class StartedBody(Stream stream, TaskCompletionSource started) : Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature
    {
        public Stream Stream => stream;
        public System.IO.Pipelines.PipeWriter Writer { get; } = System.IO.Pipelines.PipeWriter.Create(stream);
        public void DisableBuffering() { }
        public Task StartAsync(CancellationToken cancellationToken = default) { started.TrySetResult(); return Task.CompletedTask; }
        public Task CompleteAsync() => Task.CompletedTask;
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
