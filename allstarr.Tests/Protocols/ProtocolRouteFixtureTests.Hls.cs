using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private static readonly JsonElement HlsFixture = ReadHlsFixture();
    private static string FinampHlsQuery => HlsFixture.GetProperty("query").GetString()!;
    private static byte[] HlsBytes => Convert.FromBase64String(HlsFixture.GetProperty("bytes").GetString()!);

    [Fact]
    public async Task HlsManifestOpeningUsesListenerCapacityAndReleasesAfterCancellation()
    {
        var gateway = HlsGateway();
        var blocking = new HlsBlockingContent(false);
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<ProviderAudioQuality>(), null, false))
            .ReturnsAsync(new ProtocolProviderStream(new HttpResponseMessage(HttpStatusCode.OK) { Content = blocking },
                new("fixture", new Uri("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                    true, true, DeliveryMedia("mp4", "aac"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42"));
        using var factory = HlsFactory(gateway, services =>
        {
            services.RemoveAll<MediaCapacityLimits>();
            services.AddSingleton(new MediaCapacityLimits(new ConfigurationManager { [MediaCapacityLimits.StreamsKey] = "1" }));
        });
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery, cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var busy = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter!.Delta);
        Assert.Equal("busy", busy.Headers.GetValues("X-Allstarr-Capacity").Single());
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await blocking.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<ProviderAudioQuality>(), null, false))
            .ReturnsAsync(() => HlsGateway().Object.OpenStreamAsync(null!, "deezer", "42", ProviderAudioQuality.Any, null, false).GetAwaiter().GetResult());
        using var recovered = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        gateway.Verify(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<ProviderAudioQuality>(), null, false), Times.Exactly(2));
    }

    [Fact]
    public async Task HlsSegmentResponsesUseTheSharedResponseLeaseUntilAbortOrDisposal()
    {
        using var factory = HlsFactory(HlsGateway(), services =>
        {
            services.RemoveAll<MediaCapacityLimits>();
            services.AddSingleton(new MediaCapacityLimits(new ConfigurationManager { [MediaCapacityLimits.StreamsKey] = "1" }));
        });
        using var client = factory.CreateClient();
        var manifest = await client.GetStringAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        var (_, segment) = HlsLinks(manifest);
        var sessionId = segment.Split('/')[1];
        var sessions = factory.Services.GetRequiredService<ProviderHlsSessions>();
        var session = sessions.Find(sessionId)!;
        var principal = new allstarr.Core.Identity.AllstarrPrincipal(Guid.NewGuid(), "jellyfin", session.BackendInstanceId, session.PrincipalId, "Listener", false);
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, session.BackendInstanceId, session.PrincipalId,
            principal, "segment-capacity", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var admission = factory.Services.GetRequiredService<ProviderStreamAdmission>();
        var responses = factory.Services.GetRequiredService<ProtocolStreamingResponseAdapter>();
        using var scope = factory.Services.CreateScope();
        var controller = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<allstarr.Controllers.JellyfinController>(scope.ServiceProvider);
        using var cancellation = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = cancellation.Token };
        http.Request.Method = "GET";
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = execution;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        var result = Assert.IsType<FileStreamResult>(await controller.HlsAudioSegment(session.ItemId, session.Id, "0.mp4", sessions, admission, responses));
        Assert.Null(admission.TryEnter(execution));
        cancellation.Cancel();
        using var replacement = admission.TryEnter(execution);
        Assert.NotNull(replacement);
        result.FileStream.Dispose();
        Assert.Null(admission.TryEnter(execution));
    }

    [Theory]
    [InlineData("fixture-key")]
    [InlineData("fixture+key/with?escaped=&\"value")]
    public async Task FinampHls_DefaultRequestServesOriginalInitializationAndMediaWithUrlAuthentication(string credential)
    {
        var gateway = HlsGateway();
        using var factory = HlsFactory(gateway);
        using var client = factory.CreateClient();
        const string path = "/Audio/ext-deezer-song-42/main.m3u8";
        using var response = await client.GetAsync(path + "?" + FinampHlsQuery.Replace("fixture-key", Uri.EscapeDataString(credential), StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", response.Content.Headers.ContentType?.MediaType);
        var manifest = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("#EXTM3U\n", manifest);
        Assert.Contains("#EXT-X-VERSION:7\n", manifest);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD\n", manifest);
        Assert.EndsWith("#EXT-X-ENDLIST\n", manifest);
        var (init, segment) = HlsLinks(manifest);
        foreach (var link in new[] { init, segment })
        {
            Assert.False(Uri.IsWellFormedUriString(link, UriKind.Absolute));
            Assert.StartsWith("hls/", link);
            Assert.Equal(credential, QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, "/" + link).Query)["ApiKey"]);
        }
        using var initialization = await client.GetAsync("/Audio/ext-deezer-song-42/" + init);
        using var media = await client.GetAsync("/Audio/ext-deezer-song-42/" + segment);
        Assert.Equal(HttpStatusCode.OK, initialization.StatusCode);
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);
        Assert.Equal("audio/mp4", media.Content.Headers.ContentType?.MediaType);
        var initBytes = await initialization.Content.ReadAsByteArrayAsync();
        var mediaBytes = await media.Content.ReadAsByteArrayAsync();
        Assert.Equal(HlsFixture.GetProperty("initLength").GetInt32(), initBytes.Length);
        Assert.Equal(HlsBytes, initBytes.Concat(mediaBytes).ToArray());
        gateway.Verify(service => service.OpenStreamAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "42",
            ProviderAudioQuality.DataSaver, null, false), Times.Once);
        gateway.Verify(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<ProviderAudioQuality>(), It.IsAny<string?>(), true), Times.Never);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("audioCodec=opus")]
    [InlineData("audioBitRate=32000")]
    [InlineData("audioSampleRate=48000")]
    [InlineData("maxAudioChannels=1")]
    [InlineData("segmentContainer=ts")]
    [InlineData("EnableDirectPlay=false&EnableDirectStream=false")]
    [InlineData("startTimeTicks=-1")]
    [InlineData("startTimeTicks=100000000")]
    public async Task FinampHls_UnavailableTransformsFailClearly(string constraint)
    {
        using var factory = HlsFactory(HlsGateway());
        using var client = factory.CreateClient();
        var query = QueryHelpers.ParseQuery("?" + FinampHlsQuery)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in QueryHelpers.ParseQuery("?" + constraint)) query[pair.Key] = pair.Value.ToString();
        using var response = await client.GetAsync(QueryHelpers.AddQueryString("/Audio/ext-deezer-song-42/main.m3u8", query!));
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(error.RootElement.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData("m4a", "aac")]
    [InlineData("flac", "flac")]
    public async Task FinampHls_UnfragmentedSourcesAreNotAdvertisedAsSegments(string container, string codec)
    {
        using var factory = HlsFactory(HlsGateway(DeliveryBytes(codec), DeliveryMedia(container, codec)));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        Assert.DoesNotContain("#EXTM3U", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("moof")]
    [InlineData("mvex")]
    [InlineData("tfdt")]
    [InlineData("mp4a")]
    [InlineData("esds")]
    [InlineData("iso6")]
    public async Task FinampHls_MalformedOrUnsupportedFragmentsFailBeforePublishing(string missingBox)
    {
        var bytes = HlsBytes;
        var index = bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(missingBox));
        Assert.True(index >= 0);
        System.Text.Encoding.ASCII.GetBytes("free").CopyTo(bytes, index);
        using var factory = HlsFactory(HlsGateway(bytes));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task FinampHls_InvalidSampleDataOffsetsAreRejected()
    {
        var bytes = HlsBytes;
        var trun = bytes.AsSpan().IndexOf("trun"u8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(trun + 12, 4), 0);
        using var factory = HlsFactory(HlsGateway(bytes));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task FinampHls_UnknownNumericFactsRemainCompatibleAndDurationComesFromFragments()
    {
        using var factory = HlsFactory(HlsGateway(media: new("audio/mp4", "mp4", "aac")));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var manifest = await response.Content.ReadAsStringAsync();
        var duration = double.Parse(Regex.Match(manifest, "#EXTINF:([0-9.]+),").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(HlsFixture.GetProperty("duration").GetDouble(), duration, 5);
        Assert.DoesNotContain("BANDWIDTH", manifest);
    }

    [Theory]
    [InlineData("audioSampleRate", "48000")]
    [InlineData("maxAudioChannels", "1")]
    public async Task FinampHls_ActualAudioConfigurationEnforcesLimitsWhenProviderFactsAreUnknown(string parameter, string value)
    {
        using var factory = HlsFactory(HlsGateway(media: new("audio/mp4", "mp4", "aac")));
        using var client = factory.CreateClient();
        var query = FinampHlsQuery.Replace(parameter == "audioSampleRate" ? "audioSampleRate=44100" : "maxAudioChannels=2",
            $"{parameter}={value}", StringComparison.Ordinal);
        using var response = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + query);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task FinampHls_RetryRangeHeadAndResumeUseTheSameMediaTimeline()
    {
        var gateway = HlsGateway();
        using var factory = HlsFactory(gateway);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery + "&StartTimeTicks=300000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var manifest = await response.Content.ReadAsStringAsync();
        Assert.Contains("#EXT-X-START:TIME-OFFSET=0.03,PRECISE=YES\n", manifest);
        var (_, segment) = HlsLinks(manifest);
        var url = "/Audio/ext-deezer-song-42/" + segment;
        using var first = await client.GetAsync(url);
        using var second = await client.GetAsync(url);
        var expected = HlsBytes[HlsFixture.GetProperty("initLength").GetInt32()..];
        Assert.Equal(expected, await first.Content.ReadAsByteArrayAsync());
        Assert.Equal(expected, await second.Content.ReadAsByteArrayAsync());
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(3, 19);
        using var range = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(expected[3..20], await range.Content.ReadAsByteArrayAsync());
        gateway.Verify(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42",
            It.IsAny<ProviderAudioQuality>(), null, false), Times.Once);
    }

    [Fact]
    public async Task FinampHls_SegmentsRequireTheOriginalViewerItemAndCredentials()
    {
        using var factory = HlsFactory(HlsGateway());
        using var client = factory.CreateClient();
        var manifest = await client.GetStringAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        var (_, segment) = HlsLinks(manifest);
        var url = "/Audio/ext-deezer-song-42/" + segment;
        using var noCredentials = await client.GetAsync(url.Split('?')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, noCredentials.StatusCode);
        using var otherViewer = await client.GetAsync(url.Replace("fixture-key", "other-viewer", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, otherViewer.StatusCode);
        using var otherItem = await client.GetAsync(url.Replace("song-42", "song-43", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, otherItem.StatusCode);
        using var missing = await client.GetAsync("/Audio/ext-deezer-song-42/hls/00000000000000000000000000000000/0.mp4?ApiKey=fixture-key");
        Assert.Equal(HttpStatusCode.Gone, missing.StatusCode);
    }

    [Fact]
    public async Task FinampHls_ExpiredSegmentsRequireANewManifest()
    {
        var clock = new SubsonicPreparationClock();
        using var factory = HlsFactory(HlsGateway(), services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var client = factory.CreateClient();
        var manifest = await client.GetStringAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        var (_, segment) = HlsLinks(manifest);
        clock.Now = clock.Now.AddHours(1);
        using var expired = await client.GetAsync("/Audio/ext-deezer-song-42/" + segment);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        using var refreshed = await client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinampHls_AbortingManifestCancelsTheProviderReadAndDisposesItsResponse(bool buffered)
    {
        var gateway = HlsGateway();
        var content = new HlsBlockingContent(buffered);
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<ProviderAudioQuality>(), null, false))
            .ReturnsAsync(new ProtocolProviderStream(new HttpResponseMessage(HttpStatusCode.OK) { Content = content },
                new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                    true, true, DeliveryMedia("mp4", "aac"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42"));
        var factory = HlsFactory(gateway);
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var request = client.GetAsync("/Audio/ext-deezer-song-42/main.m3u8?" + FinampHlsQuery, cancellation.Token);
            await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await cancellation.CancelAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(15)));
            await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await content.Finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(content.ProducerCancelled);
        }
        finally
        {
            content.Dispose();
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private sealed class HlsBlockingContent(bool buffered) : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _lifetime = new();
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ProducerCancelled => _lifetime.IsCancellationRequested;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => buffered
            ? base.CreateContentReadStreamAsync()
            : Task.FromResult<Stream>(new HlsBlockingStream(this));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            BlockAsync(cancellationToken);
        public async Task BlockAsync(CancellationToken cancellationToken)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            Started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(45), cancellation.Token); }
            finally { Finished.TrySetResult(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _lifetime.Cancel();
            base.Dispose(disposing);
            Disposed.TrySetResult();
        }
    }

    private sealed class HlsBlockingStream(HlsBlockingContent content) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await content.BlockAsync(cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task JellyfinNativeHls_PreservesManifestAndSelectedQueryWithoutProviderCalls()
    {
        const string native = "#EXTM3U\n#EXT-X-VERSION:7\n#EXTINF:180,\nnative-segment.mp4?api_key=fixture-key\n#EXT-X-ENDLIST\n";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"verified-user"}""");
            if (IsItemLookup(request, "native")) return ItemLookup("""{"Id":"native","Type":"Audio"}""");
            Assert.Equal("/Audio/native/main.m3u8", request.RequestUri.AbsolutePath);
            Assert.Contains("audioCodec=aac", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent(native, System.Text.Encoding.UTF8, "application/vnd.apple.mpegurl") };
        }, services => { services.RemoveAll<IProtocolProviderGateway>(); services.AddSingleton(gateway.Object); });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Audio/native/main.m3u8?" + FinampHlsQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    }

    private static (string Init, string Segment) HlsLinks(string manifest) =>
        (Regex.Match(manifest, "#EXT-X-MAP:URI=\"([^\"]+)\"").Groups[1].Value,
            manifest.Split('\n').Single(line => line.Length > 0 && !line.StartsWith('#')));

    private static JsonElement ReadHlsFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocols", "hls-audio.json")));
        return document.RootElement.Clone();
    }

    private static Mock<IProtocolProviderGateway> HlsGateway(byte[]? bytes = null, ProviderMediaFormat? media = null)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<ProviderAudioQuality>(), null, false))
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? HlsBytes) };
                response.Content.Headers.ContentType = new("audio/mp4");
                return new ProtocolProviderStream(response, new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"),
                    DateTimeOffset.UtcNow.AddMinutes(1), true, true, media ?? new("audio/mp4", "mp4", "aac", HlsFixture.GetProperty("bitrate").GetInt32(), 44100, channels: 2), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42");
            });
        return gateway;
    }

    private static ProtocolFactory HlsFactory(Mock<IProtocolProviderGateway> gateway, Action<IServiceCollection>? configure = null) => new("Jellyfin", request =>
        request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, JsonSerializer.Serialize(new { Id = QueryHelpers.ParseQuery(request.RequestUri.Query)["ApiKey"].ToString() }))
            : throw new InvalidOperationException($"Unexpected native request: {request.RequestUri}"), services =>
        { services.RemoveAll<IProtocolProviderGateway>(); services.AddSingleton(gateway.Object); configure?.Invoke(services); });
}
