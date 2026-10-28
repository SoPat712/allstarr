using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [MediaToolsFact]
    public Task Download_DashSegmentsConvertThroughBridgeWithMilliseconds() => AssertDashConversionAsync(16, false, "duration_ms:3000", 3);

    [MediaToolsFact]
    public Task Download_DashSegmentsConvertThroughBridgeWithSeconds() => AssertDashConversionAsync(24, true, "duration:3", 3);

    [MediaToolsFact]
    public Task Download_DashSegmentsConvertThroughBridgeWithTrackDuration() => AssertDashConversionAsync(24, false, "", 3);

    [MediaToolsFact]
    public Task Download_DashSegmentsConvertThroughBridgeWithDashDuration() => AssertDashConversionAsync(16, false, "", 3, false);

    [MediaToolsFact]
    public Task Download_DashSegmentsConvertFullTrackThroughBridge() => AssertDashConversionAsync(24, false, "duration_ms:180000", 180);

    private static async Task AssertDashConversionAsync(int bits, bool streaming, string duration, int seconds, bool metadataDuration = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr dash conversion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rate = bits == 16 ? 44100 : 96000;
            await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-filter_threads", "1",
                "-f", "lavfi", "-i", $"aevalsrc=0.25*sin(2*PI*997*t)|0.125*sin(2*PI*659*t):s={rate}:d={seconds}",
                "-threads", "1", "-c:a", "flac", "-sample_fmt", bits == 16 ? "s16" : "s32", "-bits_per_raw_sample", bits.ToString(),
                "-strict", "experimental", "-f", "dash", "-seg_duration", seconds == 3 ? "0.25" : "2", Path.Combine(root, "manifest.mpd")]);
            var files = new[] { Path.Combine(root, "init-stream0.m4s") }
                .Concat(Directory.GetFiles(root, "chunk-stream0-*.m4s").Order(StringComparer.Ordinal)).ToArray();
            Assert.True(files.Length > 10);
            var segments = files.Select(path => WithoutSegmentIndex(File.ReadAllBytes(path))).ToArray();
            var init = segments[0];
            var mvhd = init.AsSpan().IndexOf("mvhd"u8);
            Assert.True(mvhd > 0);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(mvhd + 20, 4)));
            XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
            var timeline = XDocument.Load(Path.Combine(root, "manifest.mpd")).Descendants(ns + "S");
            var totalSamples = timeline.Sum(item => (long)item.Attribute("d")! * ((long?)item.Attribute("r") + 1 ?? 1));
            Assert.InRange(totalSamples, (long)seconds * rate, (long)seconds * rate + rate / 10);
            var original = Path.Combine(root, "original.m4a");
            await File.WriteAllBytesAsync(original, segments.SelectMany(item => item).ToArray());
            var expectedHash = await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-threads", "1",
                "-f", "mov", "-i", original, "-map", "0:a:0", "-threads", "1", "-c:a", bits == 16 ? "pcm_s16le" : "pcm_s24le", "-f", "md5", "-"]);
            var urls = JsonSerializer.Serialize(files.Select((_, index) => $"https://segments.example.test/{index}"));
            var script = $$$"""
                registerExtension({getTrack:function(){return {id:'track/one',duration_ms:{{{(metadataDuration ? seconds * 1000 : 0)}}}};},
                  download:function(id,quality,path){
                    path = 'nested audio/' + path.replace(/\.[^.]+$/, '.m4a');
                    var downloaded = file.downloadSegments({{{urls}}}, path, {maxParallel:4,persistentCheckpoint:true});
                    if (!downloaded.success) throw new Error(downloaded.error);
                    var info = gobackend && typeof gobackend.getAudioQuality === 'function' ? gobackend.getAudioQuality(path) : null;
                    return {success:true,file_path:path,actual_extension:'.m4a',audio_codec:info ? String(info.codec || '').toLowerCase() : '',
                      requires_container_conversion:true,bit_depth:{{{bits}}},sample_rate:{{{rate}}},{{{duration}}}};
                }});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
                """{"name":"dash-fixture","version":"1.0.0","type":["download_provider"]}""", script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var logger = new ProcessingLogger();
            var permissions = new ExtensionRuntimePermissionSet(new HashSet<string>(["https://segments.example.test/"]), new HashSet<string>(), new HashSet<string>());
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(new DashSegmentHandler(segments)), logger,
                permissions, Path.Combine(root, "runtime"));
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 128 * 1024 * 1024 };
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), options, audioVerifier: FixtureAudioVerifier.Instance);
            var context = Context(manifest.Id);
            byte[] bytes;
            if (streaming)
            {
                await using var adapter = new ExtensionDownloadStreamingCapabilityAdapter(sandbox, manifest, null, resolver, options);
                var outcome = await adapter.GetStreamLeaseAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track/one")));
                Assert.True(outcome.IsSuccess, outcome.Error?.Code + " " + string.Join("\n", logger.Messages));
                var lease = outcome.RequireValue();
                Assert.Equal("flac", lease.Media.Container);
                using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
                using var response = await lease.ProtectedResponseFactory!(request, default);
                bytes = await response.Content.ReadAsByteArrayAsync();
            }
            else
            {
                var job = Guid.CreateVersion7();
                var workspace = await resolver.CreateWorkspaceAsync(new(context.Actor.EffectiveUserId, job, manifest.Id, null, context.IdempotencyKey!));
                var outcome = await new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options)
                    .DownloadAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track/one"), job, workspace.Reference, ProviderAudioQuality.Any));
                Assert.True(outcome.IsSuccess, outcome.Error?.Code + " " + string.Join("\n", logger.Messages));
                var artifact = outcome.RequireValue();
                Assert.True(artifact.Verified);
                Assert.Equal("flac", artifact.Media.Container);
                var path = Assert.Single(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
                Assert.EndsWith(".flac", path);
                bytes = await File.ReadAllBytesAsync(path);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), artifact.Sha256);
                await resolver.ResolveAsync(workspace.Reference, artifact);
            }
            Assert.True(bytes.AsSpan(0, 4).SequenceEqual("fLaC"u8));
            var converted = Path.Combine(root, "converted.flac");
            await File.WriteAllBytesAsync(converted, bytes);
            using var probe = JsonDocument.Parse(await ExtensionDownloadDecryptionTests.RunAsync("ffprobe", ["-v", "error", "-f", "flac",
                "-i", converted, "-show_entries", "stream=sample_rate,channels,bits_per_raw_sample:format=duration", "-of", "json"]));
            var audio = Assert.Single(probe.RootElement.GetProperty("streams").EnumerateArray());
            Assert.Equal(rate.ToString(), audio.GetProperty("sample_rate").GetString());
            Assert.Equal(bits.ToString(), audio.GetProperty("bits_per_raw_sample").GetString());
            Assert.Equal(2, audio.GetProperty("channels").GetInt32());
            Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture), seconds - 0.01, seconds + 0.01);
            var actualHash = await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-threads", "1",
                "-f", "flac", "-i", converted, "-map", "0:a:0", "-threads", "1", "-c:a", bits == 16 ? "pcm_s16le" : "pcm_s24le", "-f", "md5", "-"]);
            Assert.Equal(expectedHash, actualHash);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_ConversionFailureLogsInternalStepWithoutExposingProviderText()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr processing log", Guid.NewGuid().ToString("N"));
        try
        {
            const string script = """
                registerExtension({download:function(id,quality,path){
                  path = path.replace(/\.[^.]+$/, '.m4a'); file.writeBytes(path,'AAAAEGZ0eXBpZ3Bz');
                  return {success:true,file_path:path,audio_codec:'flac',requires_container_conversion:true};
                }});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
                """{"name":"processing-log","version":"1.0.0","type":["download_provider"]}""", script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var logger = new ProcessingLogger();
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(), logger,
                runtimeStateDirectory: Path.Combine(root, "runtime"));
            sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(5), (_, _, _, _, _) =>
                throw new InvalidDataException("https://media.example.test/?token=private-value key=00112233445566778899aabbccddeeff"));
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces") };
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), options, audioVerifier: FixtureAudioVerifier.Instance);
            var context = Context(manifest.Id);
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(context.Actor.EffectiveUserId, job, manifest.Id, null, context.IdempotencyKey!));
            var outcome = await new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options)
                .DownloadAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track"), job, workspace.Reference, ProviderAudioQuality.Any));
            Assert.Equal("container-conversion-failed", outcome.Error?.Code);
            var logs = string.Join("\n", logger.Messages);
            Assert.Contains("input-probe", logs);
            Assert.DoesNotContain("private-value", logs);
            Assert.DoesNotContain("00112233445566778899aabbccddeeff", logs);
            Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("alac")]
    public async Task Download_ConversionDiscoversLosslessCodecWhenQualityHelperIsUnavailable(string codec)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr conversion metadata", Guid.NewGuid().ToString("N"));
        try
        {
            var script = $$$"""
                registerExtension({download:function(id,quality,path){
                  path = path.replace(/\.[^.]+$/, '.m4a'); file.writeBytes(path,'AAAAEGZ0eXBpZ3Bz');
                  var info = gobackend && typeof gobackend.getAudioQuality === 'function' ? gobackend.getAudioQuality(path) : null;
                  return {success:true,file_path:path,audio_codec:info ? String(info.codec || '').toLowerCase() : '',
                    requires_container_conversion:true,duration_ms:1000};
                }});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
                """{"name":"conversion-metadata","version":"1.0.0","type":["download_provider"]}""", script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                runtimeStateDirectory: Path.Combine(root, "runtime"));
            sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(5), (executable, arguments, output, limit, token) =>
                codec == "alac" && executable == "ffprobe" ? Task.FromResult(ExtensionDownloadConversionTests.Probe("alac", "mov,mp4,m4a"))
                    : ExtensionDownloadConversionTests.FixtureRunner(executable, arguments, output, limit, token));
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces") };
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), options, audioVerifier: FixtureAudioVerifier.Instance);
            var context = Context(manifest.Id);
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(context.Actor.EffectiveUserId, job, manifest.Id, null, context.IdempotencyKey!));
            var outcome = await new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options)
                .DownloadAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track"), job, workspace.Reference, ProviderAudioQuality.Any));
            Assert.True(outcome.IsSuccess, outcome.Error?.Code);
            Assert.Equal(codec == "flac" ? "flac" : "m4a", outcome.RequireValue().Media.Container);
            Assert.Equal(codec, outcome.RequireValue().Media.Codec);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] WithoutSegmentIndex(byte[] bytes)
    {
        using var output = new MemoryStream();
        for (var position = 0; position < bytes.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4)));
            Assert.InRange(length, 8, bytes.Length - position);
            var type = bytes.AsSpan(position + 4, 4);
            if (!type.SequenceEqual("sidx"u8) && !type.SequenceEqual("mfra"u8)) output.Write(bytes, position, length);
            position += length;
        }
        return output.ToArray();
    }

    private sealed class DashSegmentHandler(byte[][] segments) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = int.Parse(request.RequestUri!.AbsolutePath.Trim('/'));
            await Task.Delay((segments.Length - index) % 4, cancellationToken);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(segments[index]), RequestMessage = request };
        }
    }

    private sealed class ProcessingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
