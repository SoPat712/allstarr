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
    [InlineData(1000, true)]
    [InlineData(50000, false)]
    public async Task Download_EncryptedAudioUsesTrackDurationAndCommitsOnlyVerifiedArtifact(int duration, bool succeeds)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-kept-decryption", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "fixture.m4a");
            await ExtensionDownloadDecryptionTests.CreateFixtureAsync(input);
            var encoded = Convert.ToBase64String(await File.ReadAllBytesAsync(input));
            var descriptor = ExtensionDownloadDecryptionTests.Descriptor();
            var result = System.Text.Json.Nodes.JsonNode.Parse(descriptor)!.AsObject();
            result.Remove("duration_ms");
            var script = $$$"""
                registerExtension({getTrack:function(){return {id:'track-1',duration_ms:{{{duration}}}};},
                  download:function(id,quality,path){
                    path += '.m4a';
                    file.writeBytes(path,'{{{encoded}}}');
                    return Object.assign({success:true,file_path:path},{{{result.ToJsonString()}}});
                  }});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
                """{"name":"kept-audio","version":"1.0.0","type":["download_provider"]}""", script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(), NullLogger.Instance,
                ExtensionRuntimePermissionSet.None, Path.Combine(root, "runtime"));
            sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(30), ExtensionDownloadDecryptionTests.MediaRunner);
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 100_000 };
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), options);
            var adapter = new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options);
            var context = Context(manifest.Id);
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(context.Actor.EffectiveUserId, job, manifest.Id, null, context.IdempotencyKey!));
            var outcome = await adapter.DownloadAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track-1"),
                job, workspace.Reference, ProviderAudioQuality.Any));
            Assert.Equal(succeeds, outcome.IsSuccess);
            var files = Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories);
            if (succeeds)
            {
                var path = Assert.Single(files);
                Assert.EndsWith(".m4a", path);
                var artifact = outcome.RequireValue();
                Assert.Equal("aac", artifact.Media.Codec);
                Assert.True(artifact.Verified);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(), artifact.Sha256);
                await resolver.ResolveAsync(workspace.Reference, artifact);
            }
            else
            {
                Assert.Equal(ProviderErrorKind.IncompatibleMedia, outcome.Error!.Kind);
                Assert.Empty(files);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
