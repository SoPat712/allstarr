using System.Text.Json.Nodes;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed class ExtensionDownloadConversionDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr conversion diagnostics", Guid.NewGuid().ToString("N"));
    private readonly string input;

    public ExtensionDownloadConversionDiagnosticsTests()
    {
        Directory.CreateDirectory(root);
        input = Path.Combine(root, "input.m4a");
        File.WriteAllBytes(input, [0, 0, 0, 16]);
    }

    [Theory]
    [InlineData("missing", "flac")]
    [InlineData("null", "flac")]
    [InlineData("", "flac")]
    [InlineData("missing", "alac")]
    [InlineData("null", "alac")]
    [InlineData("", "alac")]
    public async Task Conversion_InfersOnlyMissingDeclarationsFromVerifiedInput(string declaration, string codec)
    {
        var descriptor = JsonNode.Parse(ExtensionDownloadConversionTests.Descriptor())!;
        if (declaration == "missing") descriptor.AsObject().Remove("audio_codec");
        else descriptor["audio_codec"] = declaration == "null" ? null : declaration;
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token) =>
            codec == "alac" ? Task.FromResult(ExtensionDownloadConversionTests.Probe(codec, "mov,mp4,m4a"))
                : ExtensionDownloadConversionTests.FixtureRunner(executable, arguments, output, limit, token);
        var media = await new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, descriptor.ToJsonString(), 100_000, default);
        Assert.Equal(codec, media.Codec);
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("opus")]
    [InlineData("unknown")]
    public async Task Conversion_RefusesUnsupportedProbedCodecsWithoutEncoding(string codec)
    {
        var descriptor = JsonNode.Parse(ExtensionDownloadConversionTests.Descriptor())!;
        descriptor["audio_codec"] = "";
        var calls = 0;
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            calls++;
            Assert.Equal("ffprobe", executable);
            return Task.FromResult(ExtensionDownloadConversionTests.Probe(codec, "mov,mp4,m4a"));
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, descriptor.ToJsonString(), 100_000, default));
        Assert.Equal("Download container conversion failed.", error.Message);
        Assert.Equal(1, calls);
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("input-probe")]
    [InlineData("encode")]
    [InlineData("output-probe")]
    [InlineData("output-decode")]
    [InlineData("input-hash")]
    [InlineData("output-hash")]
    public async Task Conversion_FailureRetainsExactStepAndSafeProcessDetails(string failureStep)
    {
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var args = arguments.ToArray();
            var step = executable == "ffprobe" ? args.Contains("flac") ? "output-probe" : "input-probe"
                : output != null ? "encode" : !args.Contains("md5") ? "output-decode"
                : args.Contains("flac") ? "output-hash" : "input-hash";
            if (step == failureStep)
            {
                var failure = new InvalidDataException("untrusted provider text");
                failure.Data["MediaStderr"] = "invalid media trailer";
                failure.Data["MediaExitCode"] = 7;
                throw failure;
            }
            return await ExtensionDownloadConversionTests.FixtureRunner(executable, args, output, limit, token);
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, ExtensionDownloadConversionTests.Descriptor(), 100_000, default));
        Assert.Equal("Download container conversion failed.", error.Message);
        Assert.DoesNotContain("untrusted provider text", error.ToString());
        var diagnostic = Assert.IsType<ExtensionMediaProcessingDiagnostic>(error.Data[ExtensionDownloadPostProcessor.DiagnosticKey]);
        Assert.Equal(failureStep, diagnostic.Step);
        Assert.Equal(7, diagnostic.ExitCode);
        Assert.Equal("invalid media trailer", diagnostic.Stderr);
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("{\"duration_ms\":1000}", true)]
    [InlineData("{\"duration\":1}", true)]
    [InlineData("{}", true)]
    [InlineData("{\"duration_ms\":50000}", false)]
    [InlineData("{\"duration\":50}", false)]
    public async Task Conversion_ValidatesDeclaredDurationInItsDeclaredUnits(string durationJson, bool succeeds)
    {
        var descriptor = JsonNode.Parse(durationJson)!;
        descriptor["requires_container_conversion"] = true;
        descriptor["audio_codec"] = "";
        var pending = new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), ExtensionDownloadConversionTests.FixtureRunner)
            .ProcessAsync(input, descriptor.ToJsonString(), 100_000, default);
        if (succeeds) Assert.Equal("flac", (await pending).Codec);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => pending);
            var diagnostic = Assert.IsType<ExtensionMediaProcessingDiagnostic>(error.Data[ExtensionDownloadPostProcessor.DiagnosticKey]);
            Assert.Equal("input-validation", diagnostic.Step);
            Assert.Equal(50d, diagnostic.DeclaredDuration);
            Assert.Equal(1d, diagnostic.InputDuration);
        }
    }

    public void Dispose() => Directory.Delete(root, true);
}
