using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [MediaToolsTheory]
    [InlineData("m4a")]
    [InlineData("mp4")]
    public async Task RemuxedMp4DecodesFromTheFirstChunkWithoutATailRequest(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-faststart", Guid.NewGuid().ToString("N"));
        try
        {
            const string manifest = """
                {"id":"remux-demo","displayName":"Demo","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
                 "capabilities":[{"kind":"Download","hooks":["download"],"accountScopes":[],"accountRequired":false}],"permissions":[]}
                """;
            const string script = """
                registerExtension({download:function(request) { return ffmpeg.convert('input.m4a',request.output,{codec:'copy'}); }});
                """;
            var sandbox = new ExtensionSandbox(root, manifest, script, new HttpClientFactory(), NullLogger.Instance,
                ExtensionRuntimePermissionSet.None, Path.Combine(root, "runtime"));
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(),
                new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 1024 * 1024 },
                audioVerifier: FixtureAudioVerifier.Instance);
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(Guid.CreateVersion7(), job, "remux-demo", null, "download"));
            using var scope = ExtensionArtifactInvocationScope.Open(resolver, workspace.Reference, job, "remux-demo", 1024 * 1024, CancellationToken.None);
            var input = scope.ResolveTemporaryPath("input.m4a");
            await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "lavfi",
                "-i", "sine=frequency=440:duration=8", "-c:a", "aac", "-threads", "1", input]);
            using var result = JsonDocument.Parse(sandbox.InvokeJson("download", JsonSerializer.Serialize(new { output = "output." + extension }))!);
            Assert.True(result.RootElement.GetProperty("success").GetBoolean());
            var output = scope.ResolveTemporaryPath("output." + extension);
            var bytes = await File.ReadAllBytesAsync(output);
            var metadata = bytes.AsSpan().IndexOf("moov"u8);
            var audio = bytes.AsSpan().IndexOf("mdat"u8);
            Assert.True(metadata >= 0 && audio > metadata, "MP4 metadata must precede audio for first-chunk decoding.");
            var prefix = scope.ResolveTemporaryPath("prefix.m4a");
            await File.WriteAllBytesAsync(prefix, bytes[..Math.Min(bytes.Length, 32768)]);
            await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-i", prefix,
                "-map", "0:a:0", "-frames:a", "1", "-f", "null", "-"]);
            async Task<string> AudioHash(string path) => await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg",
                ["-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-f", "md5", "-"]);
            Assert.Equal(await AudioHash(input), await AudioHash(output));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
