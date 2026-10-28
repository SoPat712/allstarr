using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Theory]
    [InlineData(false, "response-length", false)]
    [InlineData(true, "response-length", false)]
    [InlineData(false, "response-length", true)]
    [InlineData(true, "response-length", true)]
    [InlineData(false, "descriptor-length", false)]
    [InlineData(true, "descriptor-length", false)]
    [InlineData(false, "decoded-duration", false)]
    [InlineData(true, "decoded-duration", false)]
    [InlineData(false, "canceled", false)]
    [InlineData(true, "canceled", false)]
    public async Task Download_IncompleteAudioPublishesNoArtifactOrStream(bool streaming, string failure, bool segmented)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr incomplete download", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        try
        {
            var transfer = failure is "response-length" or "canceled"
                ? segmented
                    ? "var transfer=file.downloadSegments(['https://media.example.test/audio'],path,{maxAttempts:1}); if(!transfer.success)return transfer;"
                    : "var transfer=file.download('https://media.example.test/audio',path); if(!transfer.success)return transfer;"
                : "file.writeBytes(path,'AAAAEGZ0eXBpZ3Bz');";
            var script = $$$"""
                registerExtension({download:function(id,quality,path){
                  path += '.m4a'; {{{transfer}}}
                  return {success:true,file_path:path,actual_extension:'.m4a',audio_codec:'flac',
                    requires_container_conversion:true,duration_ms:1000{{{(failure == "descriptor-length" ? ",size_bytes:13" : "")}}}};
                }});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
                """{"name":"incomplete-download","version":"1.0.0","type":["download_provider"]}""", script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var permissions = new ExtensionRuntimePermissionSet(new HashSet<string>(["https://media.example.test/"]),
                new HashSet<string>(), new HashSet<string>());
            var handler = new IncompleteDownloadHandler(failure == "canceled" ? cancellation : null);
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(handler), NullLogger.Instance,
                permissions, Path.Combine(root, "runtime"));
            var calls = new List<string>();
            var encoded = false;
            Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
            {
                calls.Add(executable);
                if (output != null) encoded = true;
                return executable == "ffprobe"
                    ? Task.FromResult(ExtensionDownloadConversionTests.Probe("flac", "mov,mp4,m4a"))
                    : Task.FromResult("MD5=0123456789abcdef0123456789abcdef\nout_time_us=250000\nprogress=end\n");
            }
            sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(5), Run);
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 100_000 };
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), options, audioVerifier: FixtureAudioVerifier.Instance);
            var context = Context(manifest.Id, cancellationToken: cancellation.Token);
            if (streaming)
            {
                await using var adapter = new ExtensionDownloadStreamingCapabilityAdapter(sandbox, manifest, null, resolver, options);
                var result = await adapter.GetStreamLeaseAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track")));
                Assert.False(result.IsSuccess);
            }
            else
            {
                var job = Guid.CreateVersion7();
                var workspace = await resolver.CreateWorkspaceAsync(new(context.Actor.EffectiveUserId, job, manifest.Id, null, context.IdempotencyKey!));
                var result = await new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options)
                    .DownloadAsync(context, new(new(manifest.Id, ProviderResourceKind.Track, "track"), job, workspace.Reference, ProviderAudioQuality.Any));
                Assert.False(result.IsSuccess);
            }
            Assert.False(encoded);
            if (failure == "decoded-duration") Assert.Equal(["ffprobe", "ffmpeg"], calls);
            else Assert.Empty(calls);
            Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class IncompleteDownloadHandler(CancellationTokenSource? cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([0, 0, 0, 16, 102, 116, 121, 112, 109, 52, 97, 32])
            };
            response.Content.Headers.ContentLength = 13;
            cancellation?.Cancel();
            return Task.FromResult(response);
        }
    }
}
