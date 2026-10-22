using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("'unknown'")]
    [InlineData("-1")]
    [InlineData("1e100")]
    public async Task HealthProbe_UsesElapsedTimeWhenOptionalLatencyIsUnusable(string latency)
    {
        var manifest = Manifest(ProviderCapabilityKind.Health, "probeStreaming");
        var sandbox = Sandbox(manifest, "registerExtension({probeStreaming:function(){return {status:'Healthy'," +
            "observedAt:'2030-01-01T00:00:00Z',latencyMs:" + latency + "};}});");
        var result = await new ExtensionHealthCapabilityAdapter(sandbox, manifest)
            .ProbeAsync(Context(includeAccount: true), new(ProviderCapabilityKind.Streaming));
        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(ProviderProbeStatus.Healthy, result.RequireValue().Status);
        Assert.True(result.RequireValue().Latency >= TimeSpan.Zero);
    }

    [Theory]
    [InlineData(ProviderAudioQuality.HighResolution, false)]
    [InlineData(ProviderAudioQuality.Any, false)]
    [InlineData(ProviderAudioQuality.HighResolution, true)]
    public async Task StreamingQuality_RetriesDeclaredHighestWhenPlainLosslessFails(ProviderAudioQuality requested, bool diagnostic)
    {
        var (outcome, observed) = await RunDeclaredQualityDownload(MixedQualityOptions, requested,
            "if (quality === 'LOSSLESS') throw new Error('HTTP 503');", streaming: !diagnostic, diagnostic: diagnostic);

        Assert.True(outcome.IsSuccess, outcome.Error?.Code);
        Assert.Equal(["LOSSLESS", "HI_RES_LOSSLESS"], observed.GetProperty("calls").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(96000, outcome.RequireValue().Media.SampleRate);
        Assert.Equal("Using HI_RES_LOSSLESS after LOSSLESS download failed.", outcome.RequireValue().QualityDowngradeReason);
    }

    [Theory]
    [InlineData("throw new Error('HTTP 503');", 2)]
    [InlineData("throw new Error('HTTP 404');", 1)]
    [InlineData("throw new Error('Operation canceled');", 1)]
    [InlineData("return {success:false,needsVerification:true};", 1)]
    public async Task StreamingQuality_BoundsFallbackAndPreservesFailures(string failure, int attempts)
    {
        var (outcome, observed) = await RunDeclaredQualityDownload(MixedQualityOptions,
            ProviderAudioQuality.HighResolution, failure, streaming: true);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(attempts, observed.GetProperty("calls").GetArrayLength());
        if (attempts == 2) Assert.Equal("service_unavailable", outcome.Error?.Code);
    }

    [Fact]
    public async Task StreamingQuality_RespectsExplicitLosslessRequest()
    {
        var (outcome, observed) = await RunDeclaredQualityDownload(MixedQualityOptions,
            ProviderAudioQuality.Lossless, "throw new Error('HTTP 503');", streaming: true);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(["LOSSLESS"], observed.GetProperty("calls").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData(false, "HI_RES_LOSSLESS", "LOSSLESS")]
    [InlineData(true, "LOSSLESS", "HI_RES_LOSSLESS")]
    public async Task StreamingQuality_PreparesFallbackWithItsActualQuality(bool streaming, string first, string fallback)
    {
        var (outcome, observed) = await RunDeclaredQualityDownload(MixedQualityOptions, ProviderAudioQuality.HighResolution,
            "if (options.preparedContext.quality !== quality) throw new Error('wrong prepared quality'); " +
            "if (calls.length === 1) throw new Error('HTTP 503');", streaming: streaming);

        Assert.True(outcome.IsSuccess, outcome.Error?.Code);
        Assert.Equal([first, fallback], observed.GetProperty("calls").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(fallback, observed.GetProperty("quality").GetString());
        Assert.Equal("HighResolution", observed.GetProperty("requestedQuality").GetString());
    }

    [Theory]
    [InlineData(ProviderCapabilityKind.Metadata, 200, ProviderProbeStatus.Healthy)]
    [InlineData(ProviderCapabilityKind.Streaming, 200, ProviderProbeStatus.Healthy)]
    [InlineData(ProviderCapabilityKind.Download, 502, ProviderProbeStatus.Unavailable)]
    public async Task DeclaredServiceHealth_ProbesSoundCloudHomepageWithoutParsingHtml(
        ProviderCapabilityKind capability, int status, ProviderProbeStatus expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-service-probe", Guid.NewGuid().ToString("N"));
        try
        {
            var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Extensions", "Registry", "soundcloud.json"));
            const string script = """
                registerExtension({getTrack:function(id){return {id:id,name:'Song'};},
                  download:function(){throw new Error('health must not download');}});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var health = Assert.Single(manifest.Capabilities, item => item.Kind == ProviderCapabilityKind.Health);
            Assert.Contains("probe" + capability, health.Hooks);
            var requests = new List<string>();
            var handler = new AppleProviderTestFactory.Handler(request =>
            {
                requests.Add(request.Method + " " + request.RequestUri);
                return new HttpResponseMessage((HttpStatusCode)status)
                { RequestMessage = request, Content = new StringContent("<html>SoundCloud</html>") };
            });
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(handler), NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(["https://soundcloud.com/"]),
                    new HashSet<string>(), new HashSet<string>()), Path.Combine(root, "runtime"));
            var adapter = new ExtensionHealthCapabilityAdapter(sandbox, manifest);

            var result = await adapter.ProbeAsync(Context(manifest.Id), new(capability));

            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(expected, result.RequireValue().Status);
            Assert.Equal(["GET https://soundcloud.com/"], requests);
            Assert.True(result.RequireValue().Latency >= TimeSpan.Zero);
            Assert.Equal(status == 200 ? null : "service_unavailable", result.RequireValue().SafeCode);
            var unsupported = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Intelligence));
            Assert.Equal(ProviderErrorKind.NotSupported, unsupported.Error?.Kind);
            Assert.Single(requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true, 503, ProviderProbeStatus.Unavailable)]
    [InlineData(false, 503, ProviderProbeStatus.Degraded)]
    [InlineData(true, 204, ProviderProbeStatus.Healthy)]
    public async Task DeclaredServiceHealth_RespectsHeadAndRequiredEndpoints(bool required, int status, ProviderProbeStatus expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-service-head", Guid.NewGuid().ToString("N"));
        try
        {
            var source = $$$"""
                {"name":"health-fixture","type":["metadata_provider"],"permissions":{"network":["health.example.test"]},
                 "serviceHealth":[{"url":"https://health.example.test/","method":"HEAD","required":{{{required.ToString().ToLowerInvariant()}}}}]}
                """;
            const string script = "registerExtension({getTrack:function(id){return {id:id,name:'Song'};}});";
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            Assert.Contains(manifest.Capabilities, item => item.Kind == ProviderCapabilityKind.Health);
            var requests = new List<HttpMethod>();
            var handler = new AppleProviderTestFactory.Handler(request =>
            {
                requests.Add(request.Method);
                return new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request };
            });
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(handler), NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(["https://health.example.test/"]),
                    new HashSet<string>(), new HashSet<string>()), Path.Combine(root, "runtime"));

            var result = await new ExtensionHealthCapabilityAdapter(sandbox, manifest)
                .ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Metadata));

            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(expected, result.RequireValue().Status);
            Assert.Equal([HttpMethod.Head], requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeclaredServiceHealth_DoesNotReportHealthyWhenChecksExceedBound()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-service-bound", Guid.NewGuid().ToString("N"));
        try
        {
            var source = JsonSerializer.Serialize(new
            {
                name = "health-fixture",
                type = new[] { "metadata_provider" },
                permissions = new { network = new[] { "health.example.test" } },
                serviceHealth = Enumerable.Range(0, 17).Select(index => new
                { url = "https://health.example.test/" + index, required = true }).ToArray()
            });
            const string script = "registerExtension({getTrack:function(id){return {id:id,name:'Song'};}});";
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var requests = 0;
            var handler = new AppleProviderTestFactory.Handler(request =>
            {
                requests++;
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            });
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(handler), NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(["https://health.example.test/"]),
                    new HashSet<string>(), new HashSet<string>()), Path.Combine(root, "runtime"));

            var result = await new ExtensionHealthCapabilityAdapter(sandbox, manifest)
                .ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Metadata));

            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(ProviderProbeStatus.Unavailable, result.RequireValue().Status);
            Assert.InRange(requests, 0, 16);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
