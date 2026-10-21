using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Feishin", "Subsonic", "/rest/download.view", "json", "GET", false, true)]
    [InlineData("Feishin", "Subsonic", "/rest/download.view", "json", "GET", true, true)]
    [InlineData("Feishin", "Subsonic", "/rest/download.view", "json", "HEAD", true, true)]
    [InlineData("Tempo", "Subsonic", "/rest/download", "xml", "GET", false, true)]
    [InlineData("Tempo", "Subsonic", "/rest/download", "xml", "GET", true, true)]
    [InlineData("Tempo", "Subsonic", "/rest/download", "xml", "HEAD", false, true)]
    [InlineData("Tempo", "Subsonic", "/rest/download", "xml", "GET", false, false)]
    [InlineData("Feishin", "Subsonic", "/rest/download.view", "json", "POST", false, true)]
    [InlineData("Feishin", "Jellyfin", "/Items/ext-deezer-song-42/Download", "json", "GET", false, true)]
    [InlineData("Jellyfin Web", "Jellyfin", "/Items/ext-deezer-song-42/Download", "json", "GET", true, true)]
    [InlineData("Jellyfin Web", "Jellyfin", "/Items/ext-deezer-song-42/Download", "json", "HEAD", false, true)]
    [InlineData("Finamp", "Jellyfin", "/Items/ext-deezer-song-42/File", "json", "GET", false, true)]
    [InlineData("Finamp", "Jellyfin", "/Items/ext-deezer-song-42/File", "json", "GET", true, true)]
    [InlineData("Finamp", "Jellyfin", "/Items/ext-deezer-song-42/File", "json", "HEAD", true, true)]
    [InlineData("Finamp", "Jellyfin", "/Items/ext-deezer-song-42/File", "json", "GET", false, false)]
    public async Task OfflineDownload_ClientOriginalRequestsPreserveBytesAndTransport(
        string clientName, string backend, string path, string format, string method, bool range, bool knownLength)
    {
        byte[] bytes = [73, 68, 51, 4, 0, 0, 0, 0, 0, 0, 102, 76, 97, 67, 0, 0, 0, 34, 99];
        var delivered = range ? bytes[4..8] : bytes;
        var upstream = new HttpResponseMessage(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new StreamContent(new DownloadBytes(delivered))
        };
        upstream.Content.Headers.ContentType = new("audio/flac");
        if (knownLength) upstream.Content.Headers.ContentLength = delivered.Length;
        if (range) upstream.Content.Headers.ContentRange = new(4, 7, bytes.Length);
        upstream.Headers.AcceptRanges.Add("bytes");
        upstream.Headers.ETag = new("\"original-v1\"");
        upstream.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(
                It.Is<ProtocolExecutionContext>(context => context.Protocol.ToString() == backend &&
                    context.RequestedItemId == "ext-deezer-song-42" && context.IsOriginalDownload && context.AudioDelivery == null),
                "deezer", "42", ProviderAudioQuality.Any, range ? "bytes=4-7" : null, method == "HEAD"))
            .ReturnsAsync(new ProtocolProviderStream(upstream, DownloadLease(), "deezer", "42"));
        using var factory = DownloadFactory(backend, gateway.Object);
        using var client = factory.CreateClient();
        var credentials = $"u=listener&t=fixture-hash&s=fixture-salt&v=1.13.0&c={Uri.EscapeDataString(clientName)}{(format == "json" ? "&f=json" : "")}&id=ext-deezer-song-42";
        // Original downloads ignore playback/transcode preferences even if a client appends them.
        credentials += "&maxBitRate=64&format=mp3&timeOffset=30";
        using var request = new HttpRequestMessage(new HttpMethod(method), backend == "Subsonic"
            ? path + (method == "POST" ? "" : "?" + credentials)
            : path + (clientName == "Finamp" ? "" : clientName == "Feishin"
                ? "?apiKey=fixture-key&playSessionId=" : "?ApiKey=fixture-key"));
        if (method == "POST") request.Content = new StringContent(credentials, Encoding.UTF8, "application/x-www-form-urlencoded");
        if (backend == "Jellyfin") request.RequestUri = new Uri(request.RequestUri +
            (request.RequestUri!.OriginalString.Contains('?') ? "&" : "?") +
            "AudioCodec=mp3&Container=mp3&MaxStreamingBitrate=64000&StartTimeTicks=300000000", UriKind.Relative);
        if (clientName == "Finamp") request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"fixture-key\"");
        if (range) request.Headers.Range = new RangeHeaderValue(4, 7);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(upstream.StatusCode, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(knownLength ? delivered.Length : (long?)null, response.Content.Headers.ContentLength);
        Assert.Equal(range ? $"bytes 4-7/{bytes.Length}" : null, response.Content.Headers.ContentRange?.ToString());
        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
        Assert.Equal("\"original-v1\"", response.Headers.ETag?.Tag);
        Assert.Equal(upstream.Content.Headers.LastModified, response.Content.Headers.LastModified);
        Assert.Equal(method == "HEAD" ? [] : delivered, await response.Content.ReadAsByteArrayAsync());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/rest/download", "json")]
    [InlineData("/rest/download.view", "xml")]
    public async Task OfflineDownload_MissingIdUsesRequestedSubsonicEnvelope(string path, string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = DownloadFactory("Subsonic", gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path + $"?u=listener&p=fixture&v=1.13.0&c=Feishin&f={format}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var envelope = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("failed", envelope.GetProperty("status").GetString());
            Assert.Equal(10, envelope.GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            var envelope = XDocument.Parse(body).Root!;
            Assert.Equal("failed", envelope.Attribute("status")?.Value);
            Assert.Equal("10", envelope.Element(envelope.Name.Namespace + "error")?.Attribute("code")?.Value);
        }
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task OfflineDownload_ProviderFailureUsesRequestedSubsonicEnvelope(string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Any, null, false))
            .ThrowsAsync(new HttpRequestException("private provider response", null, HttpStatusCode.NotFound));
        using var factory = DownloadFactory("Subsonic", gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/download.view?id=ext-deezer-song-42&u=listener&p=fixture&v=1.13.0&c=Feishin&f={format}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private provider response", body);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(70, document.RootElement.GetProperty("subsonic-response").GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            var envelope = XDocument.Parse(body).Root!;
            Assert.Equal("70", envelope.Element(envelope.Name.Namespace + "error")?.Attribute("code")?.Value);
        }
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/rest/download", "application/json", "{ \"subsonic-response\": {\"status\":\"failed\",\"error\":{\"code\":70,\"message\":\"missing\"}} }")]
    [InlineData("/rest/download.view", "application/xml", "<subsonic-response status=\"failed\"><error code=\"70\" message=\"missing\" /></subsonic-response>")]
    [InlineData("/rest/download.view", "audio/mpeg", "original native audio")]
    public async Task OfflineDownload_NativeSubsonicResponseRemainsByteForByte(string path, string type, string body)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view") return DownloadAuthentication("Subsonic");
            Assert.Equal(path, request.RequestUri.AbsolutePath);
            Assert.Contains("id=native-42", request.RequestUri.Query);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
            response.Content.Headers.ContentDisposition = new("attachment") { FileName = "native.mp3" };
            return response;
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path + "?id=native-42&u=listener&p=fixture&c=Tempo&v=1.13.0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Encoding.UTF8.GetBytes(body), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("native.mp3", response.Content.Headers.ContentDisposition?.FileName);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Subsonic", "/rest/download.view?id=ext-deezer-song-42&u=listener&p=fixture&v=1.13.0&c=Feishin")]
    [InlineData("Jellyfin", "/Items/ext-deezer-song-42/File?api_key=fixture-key")]
    public async Task OfflineDownload_CancellationReachesProvider(string backend, string path)
    {
        var opened = new TaskCompletionSource<ProtocolExecutionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Any, null, false))
            .Returns(async (ProtocolExecutionContext context, string _, string _, ProviderAudioQuality _, string? _, bool _) =>
            {
                opened.TrySetResult(context);
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                finally { stopped.TrySetResult(); }
                return (ProtocolProviderStream?)null;
            });
        using var factory = DownloadFactory(backend, gateway.Object);
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var sending = client.GetAsync(path, cancellation.Token);
        var context = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(context.CancellationToken.IsCancellationRequested);
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("/Items/native-42/Download", "GET", false)]
    [InlineData("/Items/native-42/File", "GET", true)]
    [InlineData("/Items/native-42/File", "HEAD", false)]
    public async Task OfflineDownload_NativeJellyfinBytesAndTransportAreUnchanged(string path, string method, bool range)
    {
        byte[] bytes = [73, 68, 51, 4, 0, 255, 19];
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return DownloadAuthentication("Jellyfin");
            if (IsItemLookup(request, "native-42")) return ItemLookup("""{"Id":"native-42","Type":"Audio"}""");
            if (request.RequestUri.AbsolutePath == "/Items/native-42") return Json(200, """{"Id":"native-42","Type":"Audio"}""");
            Assert.Equal(path, request.RequestUri.AbsolutePath);
            Assert.Equal(method, request.Method.Method);
            Assert.Equal(range ? "bytes=2-4" : null, request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(range ? bytes[2..5] : bytes)
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            if (range) response.Content.Headers.ContentRange = new(2, 4, bytes.Length);
            return response;
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path + "?ApiKey=fixture-key");
        if (range) request.Headers.Range = new(2, 4);
        using var response = await client.SendAsync(request);
        Assert.Equal(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(method == "HEAD" ? [] : range ? bytes[2..5] : bytes, await response.Content.ReadAsByteArrayAsync());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    public async Task OfflineDownload_UnavailableSourceUsesSubsonicEnvelope(string format, bool upstreamMissing)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Any, null, false))
            .ReturnsAsync(upstreamMissing
                ? new ProtocolProviderStream(new HttpResponseMessage(HttpStatusCode.NotFound), DownloadLease(), "deezer", "42")
                : null);
        using var factory = DownloadFactory("Subsonic", gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/download?id=ext-deezer-song-42&u=listener&p=fixture&v=1.13.0&c=Tempo&f={format}");
        Assert.Equal(upstreamMissing ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal("failed", document.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
        }
        else Assert.Equal("failed", XDocument.Parse(body).Root!.Attribute("status")?.Value);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Subsonic", "/rest/download?id=ext-deezer-song-42&u=fixture&p=fixture&v=1.13.0&c=Tempo")]
    [InlineData("Jellyfin", "/Items/ext-deezer-song-42/File?ApiKey=fixture-key")]
    public async Task OfflineDownload_SequentialOriginalDoesNotPublishCacheOrFetchMetadata(string backend, string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        byte[] bytes = [73, 68, 51, 4, 0, 0, 0, 0, 0, 0, 102, 76, 97, 67, 0, 0, 0, 34, 99];
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DownloadBytes(bytes)) };
        upstream.Content.Headers.ContentType = new("audio/flac");
        upstream.Content.Headers.ContentLength = bytes.Length;
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Any, null, false))
            .ReturnsAsync(new ProtocolProviderStream(upstream, DownloadLease(), "deezer", "42"));
        try
        {
            using var factory = new ProtocolFactory(backend, request =>
                request.RequestUri!.AbsolutePath is "/Users/Me" or "/rest/ping.view"
                    ? DownloadAuthentication(backend)
                    : throw new InvalidOperationException("Unexpected native request"), services =>
                {
                    services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
                    var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
                    policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                            ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                        { UserId = user });
                    services.RemoveAll<IEffectiveProviderPolicyResolver>();
                    services.AddSingleton(policies.Object);
                    services.RemoveAll<IProtocolProviderGateway>();
                    services.AddSingleton(gateway.Object);
                }, new Dictionary<string, string?> { ["Subsonic:StorageMode"] = "Cache", ["Library:DownloadPath"] = root });
            using var client = factory.CreateClient();
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            gateway.VerifyAll();
            gateway.VerifyNoOtherCalls();
            Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ProtocolFactory DownloadFactory(string backend, IProtocolProviderGateway gateway) => new(backend,
        request => request.RequestUri!.AbsolutePath is "/Users/Me" or "/rest/ping.view"
            ? DownloadAuthentication(backend)
            : throw new InvalidOperationException($"Unexpected native request: {request.RequestUri.AbsolutePath}"),
        services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway);
        });

    private static HttpResponseMessage DownloadAuthentication(string backend) => Json(200, backend == "Subsonic"
        ? """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""
        : """{"Id":"user-1"}""");

    private static ProviderStreamLease DownloadLease() => new("original", new Uri("https://media.example.test/original"),
        DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry);

    private sealed class DownloadBytes(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
