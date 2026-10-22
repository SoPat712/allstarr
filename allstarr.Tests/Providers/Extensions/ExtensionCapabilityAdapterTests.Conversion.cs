using System.Security.Cryptography;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Theory]
    [InlineData(false, false, "container-conversion-failed")]
    [InlineData(true, false, "container-conversion-failed")]
    [InlineData(false, true, "media-tools-unavailable")]
    [InlineData(true, true, "media-tools-unavailable")]
    public async Task Download_ProcessingFailurePreservesReasonAndPublishesNoArtifact(bool streaming, bool missingTool, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-processing-failure", Guid.NewGuid().ToString("N"));
        try
        {
            const string script = """
                registerExtension({download:function(id,quality,path){
                  path += '.m4a'; file.writeBytes(path,'AAAAEGZ0eXBpZ3Bz');
                  return {success:true,file_path:path,actual_extension:'.m4a',audio_codec:'flac',requires_container_conversion:true};
                }});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
                """{"name":"processing-fixture","version":"1.0.0","type":["download_provider"]}""", script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(), NullLogger.Instance,
                runtimeStateDirectory: Path.Combine(root, "runtime"));
            async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
            {
                if (missingTool) throw new System.ComponentModel.Win32Exception(2);
                var response = await ExtensionDownloadConversionTests.FixtureRunner(executable, arguments, output, limit, token);
                if (output != null) await File.WriteAllBytesAsync(output, [1, 2, 3, 4], token);
                return response;
            }
            sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(5), Run);
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 100_000 };
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), options);
            var context = Context(manifest.Id);
            ProviderError? error;
            if (streaming)
            {
                await using var adapter = new ExtensionDownloadStreamingCapabilityAdapter(sandbox, manifest, null, resolver, options);
                var result = await adapter.GetStreamLeaseAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track")));
                Assert.False(result.IsSuccess);
                error = result.Error;
            }
            else
            {
                var job = Guid.CreateVersion7();
                var workspace = await resolver.CreateWorkspaceAsync(new(context.Actor.EffectiveUserId, job, manifest.Id, null, context.IdempotencyKey!));
                var result = await new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options)
                    .DownloadAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track"), job, workspace.Reference, ProviderAudioQuality.Any));
                Assert.False(result.IsSuccess);
                error = result.Error;
            }
            Assert.Equal(expected, error?.Code);
            Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, "flac", true)]
    [InlineData(true, "flac", true)]
    [InlineData(false, "alac", true)]
    [InlineData(true, "alac", true)]
    [InlineData(false, "aac", false)]
    [InlineData(true, "aac", false)]
    public async Task Download_ReportsProcessedContainerAndServesVerifiedBytes(bool streaming, string codec, bool conversion)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-container", Guid.NewGuid().ToString("N"));
        try
        {
            var script = $$$"""
                registerExtension({download:function(id,quality,path){
                  path += '.m4a'; file.writeBytes(path,'AAAAEGZ0eXBpZ3Bz');
                  return {success:true,file_path:path,actual_extension:'.m4a',audio_codec:'{{{codec}}}',
                    requires_container_conversion:{{{conversion.ToString().ToLowerInvariant()}}},duration_ms:1000};
                }});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
                """{"name":"container-fixture","version":"1.0.0","type":["download_provider"]}""", script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(), NullLogger.Instance,
                runtimeStateDirectory: Path.Combine(root, "runtime"));
            Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
            {
                Assert.True(conversion, "Lossy audio must not enter post-processing.");
                return codec == "alac"
                    ? Task.FromResult(ExtensionDownloadConversionTests.Probe("alac", "mov,mp4,m4a"))
                    : ExtensionDownloadConversionTests.FixtureRunner(executable, arguments, output, limit, token);
            }
            sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(5), Run);
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 100_000 };
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), options);
            var context = Context(manifest.Id);
            var container = codec == "flac" ? "flac" : "m4a";
            ProviderMediaFormat media;
            if (streaming)
            {
                await using var adapter = new ExtensionDownloadStreamingCapabilityAdapter(sandbox, manifest, null, resolver, options);
                var result = await adapter.GetStreamLeaseAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track")));
                Assert.True(result.IsSuccess, result.Error?.Code);
                var lease = result.RequireValue();
                media = lease.Media;
                using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
                using var response = await lease.ProtectedResponseFactory!(request, default);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.NotEmpty(bytes);
                if (codec == "flac") Assert.Equal(ExtensionDownloadConversionTests.Converted, bytes);
            }
            else
            {
                var job = Guid.CreateVersion7();
                var workspace = await resolver.CreateWorkspaceAsync(new(context.Actor.EffectiveUserId, job, manifest.Id, null, context.IdempotencyKey!));
                var result = await new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options)
                    .DownloadAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track"), job, workspace.Reference, ProviderAudioQuality.Any));
                Assert.True(result.IsSuccess, result.Error?.Code);
                var artifact = result.RequireValue();
                media = artifact.Media;
                Assert.True(artifact.Verified);
                var path = Assert.Single(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
                Assert.EndsWith("." + container, path);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(), artifact.Sha256);
                await resolver.ResolveAsync(workspace.Reference, artifact);
            }
            Assert.Equal(codec, media.Codec);
            Assert.Equal(container, media.Container);
            Assert.Equal(codec == "flac" ? "audio/flac" : "audio/mp4", media.MimeType);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
