using System.Net;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class AppleDownloadEndpointDiscoveryTests
{
    private const string FullManifest = """
        {"sidecarApiVersion":"1.0.0","capabilities":[
          {"id":"metadata-search-song","state":"supported"},
          {"id":"metadata-song","state":"supported"},
          {"id":"stream-audio-song","state":"supported"},
          {"id":"download-audio-song","state":"supported"}
        ]}
        """;

    [Fact]
    public async Task MissingConfiguration_DoesNotIssueARequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not request"));
        var snapshot = await Create(string.Empty, handler).DiscoverAsync();

        Assert.Equal(AppleDownloadEndpointState.NeedsConfiguration, snapshot.State);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RawWrapperUrlWithoutGatewayManifest_IsIncompatible()
    {
        var snapshot = await Create("http://wrapper.lan:8080", new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/capabilities"
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK, "{\"status\":\"ok\"}"))).DiscoverAsync();

        Assert.Equal(AppleDownloadEndpointState.Incompatible, snapshot.State);
        Assert.Equal("gateway_manifest_missing", snapshot.ReasonCode);
        Assert.All(snapshot.Capabilities, item =>
            Assert.Equal(AppleDownloadCapabilityState.Unsupported, item.State));
    }

    [Fact]
    public async Task IncompatibleApiVersion_IsRejectedBeforeHealthOrAuth()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"sidecarApiVersion\":\"2.0.0\",\"capabilities\":[]}"));

        var snapshot = await Create("http://apple-provider.lan", handler).DiscoverAsync();

        Assert.Equal(AppleDownloadEndpointState.Incompatible, snapshot.State);
        Assert.Equal("unsupported_api_version", snapshot.ReasonCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UnauthenticatedEndpoint_DegradesAdvertisedFeatures()
    {
        var snapshot = await Create("http://10.10.0.8:8000", new StubHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/api/capabilities" => Json(HttpStatusCode.OK, FullManifest),
                "/api/health" => Json(HttpStatusCode.OK,
                    "{\"staged\":true,\"daemon_running\":true,\"wrapper_healthy\":true,\"logged_in\":false}"),
                "/api/me" => Json(HttpStatusCode.Unauthorized, "{}"),
                _ => Json(HttpStatusCode.NotFound, "{}")
            })).DiscoverAsync();

        Assert.Equal(AppleDownloadEndpointState.NeedsAuthentication, snapshot.State);
        Assert.False(snapshot.Authenticated);
        Assert.Equal(AppleDownloadCapabilityState.Degraded,
            snapshot.Capability(ProviderCapabilities.Download).State);
    }

    [Fact]
    public async Task PartialManifest_ReportsUnsupportedFeaturesWithoutInventingRoutes()
    {
        const string partial = """
            {"sidecarApiVersion":"1.0.0","capabilities":[
              {"id":"metadata-search-song","state":"supported"},
              {"id":"metadata-song","state":"supported"}
            ]}
            """;
        var snapshot = await Create("http://apple-provider.lan", HealthyHandler(partial)).DiscoverAsync();

        Assert.Equal(AppleDownloadEndpointState.Available, snapshot.State);
        Assert.Equal(AppleDownloadCapabilityState.Available,
            snapshot.Capability(ProviderCapabilities.Metadata).State);
        Assert.Equal(AppleDownloadCapabilityState.Unsupported,
            snapshot.Capability(ProviderCapabilities.Streaming).State);
        Assert.Equal(AppleDownloadCapabilityState.Unsupported,
            snapshot.Capability(ProviderCapabilities.Download).State);
        Assert.Equal(AppleDownloadCapabilityState.Unsupported,
            snapshot.Capability("download-album").State);
    }

    [Fact]
    public async Task HealthyGateway_ReportsEveryAdvertisedFeatureAvailable()
    {
        var handler = HealthyHandler(FullManifest);
        var snapshot = await Create("https://apple-provider.example/base", handler).DiscoverAsync();

        Assert.Equal(AppleDownloadEndpointState.Available, snapshot.State);
        Assert.True(snapshot.Authenticated);
        Assert.Equal(AppleDownloadCapabilityState.Available,
            snapshot.Capability(ProviderCapabilities.Metadata).State);
        Assert.Equal(AppleDownloadCapabilityState.Available,
            snapshot.Capability(ProviderCapabilities.Streaming).State);
        Assert.Equal(AppleDownloadCapabilityState.Available,
            snapshot.Capability(ProviderCapabilities.Download).State);
        Assert.Equal(AppleDownloadCapabilityState.Unsupported,
            snapshot.Capability("stream-music-video").State);
        Assert.Equal(["/base/api/capabilities", "/base/api/health", "/base/api/me"], handler.Paths);
        Assert.All(handler.Requests, request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.TryGetValues("Cookie", out _));
        });
    }

    [Theory]
    [InlineData("http://user:password@apple-provider.lan")]
    [InlineData("http://apple-provider.lan?token=secret")]
    [InlineData("file:///tmp/provider")]
    public async Task UnsafeBaseUrl_IsRejectedWithoutRequest(string url)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not request"));
        var snapshot = await Create(url, handler).DiscoverAsync();
        Assert.Equal(AppleDownloadEndpointState.NeedsConfiguration, snapshot.State);
        Assert.Equal(0, handler.RequestCount);
    }

    private static StubHandler HealthyHandler(string manifest) => new(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/api/capabilities", StringComparison.Ordinal) ?
            Json(HttpStatusCode.OK, manifest) :
        request.RequestUri.AbsolutePath.EndsWith("/api/health", StringComparison.Ordinal) ?
            Json(HttpStatusCode.OK,
                "{\"staged\":true,\"daemon_running\":true,\"wrapper_healthy\":true,\"logged_in\":true}") :
        request.RequestUri.AbsolutePath.EndsWith("/api/me", StringComparison.Ordinal) ?
            Json(HttpStatusCode.OK, "{\"auth\":{\"state\":\"authenticated\"}}") :
            Json(HttpStatusCode.NotFound, "{}"));

    private static AppleDownloadEndpointDiscovery Create(string url, StubHandler handler) => new(
        new StubFactory(new HttpClient(handler)),
        Options.Create(new AppleDownloadSettings { BaseUrl = url }));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("AppleDownloadDiscovery", name);
            return client;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount => Requests.Count;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Paths => Requests.Select(item => item.RequestUri!.AbsolutePath).ToList();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
