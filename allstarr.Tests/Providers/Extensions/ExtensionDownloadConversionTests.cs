using System.ComponentModel;
using System.Text.Json;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed class ExtensionDownloadConversionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-conversion", Guid.NewGuid().ToString("N"));
    private string input = null!;
    private static readonly byte[] Original = [0, 0, 0, 16, 102, 116, 121, 112, 109, 52, 97, 32];
    internal static readonly byte[] Converted = [102, 76, 97, 67, 0, 0, 0, 0];

    [Fact]
    public async Task Conversion_CopiesFlacAndVerifiesBeforeReplacingInput()
    {
        var calls = new List<string>();
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            calls.Add(executable);
            Assert.Equal(Original, await File.ReadAllBytesAsync(input, token));
            return await FixtureRunner(executable, arguments, output, limit, token);
        }
        var media = await ProcessAsync(Run);
        Assert.Equal("flac", media.Container);
        Assert.Equal("flac", media.Codec);
        Assert.Equal(Converted, await File.ReadAllBytesAsync(input));
        Assert.Equal(["ffprobe", "ffmpeg", "ffprobe", "ffmpeg"], calls);
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public async Task Conversion_LeavesAlacInMp4WithoutRemuxing()
    {
        var calls = 0;
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            calls++;
            Assert.Equal("ffprobe", executable);
            return Task.FromResult(Probe("alac", "mov,mp4,m4a"));
        }
        var media = await ProcessAsync(Run, "alac");
        Assert.Equal("m4a", media.Container);
        Assert.Equal("alac", media.Codec);
        Assert.Equal(1, calls);
        await AssertOriginalAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conversion_DecryptsFlacBeforeCopyingToNativeContainer(bool explicitConversion)
    {
        var descriptor = System.Text.Json.Nodes.JsonNode.Parse(ExtensionDownloadDecryptionTests.Descriptor(codec: "flac"))!;
        descriptor["requires_container_conversion"] = explicitConversion;
        var copies = new List<string>();
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            if (executable == "ffmpeg" && output != null && arguments.Contains("-decryption_key"))
            {
                copies.Add("decryption");
                Assert.Contains("copy", arguments);
                await File.WriteAllBytesAsync(output, Original, token);
                return "";
            }
            if (output != null) copies.Add("conversion");
            return await FixtureRunner(executable, arguments, output, limit, token);
        }
        var media = await new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, descriptor.ToJsonString(), 100_000, default);
        Assert.Equal(["decryption", "conversion"], copies);
        Assert.Equal("flac", media.Container);
        Assert.Equal(Converted, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("opus")]
    [InlineData("unknown")]
    public async Task Conversion_RejectsOtherCodecsWithoutTranscoding(string codec)
    {
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token) =>
            throw new InvalidOperationException("No process should start.");
        await AssertFailureAsync(Run, codec: codec);
    }

    [Theory]
    [InlineData("codec")]
    [InlineData("duration")]
    [InlineData("container")]
    [InlineData("extra-stream")]
    [InlineData("magic")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("decode")]
    public async Task Conversion_RejectsUnverifiedOutputAndCleansTemporaryFile(string failure)
    {
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var response = await FixtureRunner(executable, arguments, output, limit, token);
            if (output != null)
            {
                if (failure == "magic") await File.WriteAllBytesAsync(output, Original, token);
                if (failure == "empty") await File.WriteAllBytesAsync(output, [], token);
                if (failure == "oversized") await File.WriteAllBytesAsync(output, new byte[100_001], token);
            }
            if (executable == "ffprobe" && arguments.Contains("flac"))
            {
                if (failure == "codec") return Probe("aac", "flac");
                if (failure == "duration") return Probe("flac", "flac", "50");
                if (failure == "container") return Probe("flac", "mp4");
                if (failure == "extra-stream") return Probe("flac", "flac").Replace("\"streams\":[", "\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"unknown\"},");
            }
            if (failure == "decode" && executable == "ffmpeg" && output == null) throw new InvalidDataException();
            return response;
        }
        await AssertFailureAsync(Run);
    }

    [Theory]
    [InlineData("ffmpeg")]
    [InlineData("ffprobe")]
    public async Task Conversion_ReportsMissingMediaTools(string missing)
    {
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token) =>
            executable == missing ? throw new Win32Exception(2) : FixtureRunner(executable, arguments, output, limit, token);
        await AssertFailureAsync(Run, "Download media tools are unavailable.");
    }

    [Fact]
    public async Task Conversion_RejectsOversizedInput() =>
        await AssertFailureAsync(FixtureRunner, maximumBytes: Original.Length - 1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Conversion_CancellationOrTimeoutPreservesInput(bool cancelCaller)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var response = await FixtureRunner(executable, arguments, output, limit, token);
            if (output != null)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return response;
        }
        var pending = new ExtensionDownloadPostProcessor(cancelCaller ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(200), Run)
            .ProcessAsync(input, Descriptor(), 100_000, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else Assert.Equal("Download container conversion failed.", (await Assert.ThrowsAsync<InvalidDataException>(() => pending)).Message);
        await AssertOriginalAsync();
    }

    [MediaToolsFact]
    public async Task Conversion_RealProcessesCopyFlacPacketsWithoutChangingAudio()
    {
        var native = Path.Combine(root, "source.flac");
        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=1", "-threads", "1", "-c:a", "flac", native]);
        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-i", native,
            "-threads", "1", "-c:a", "copy", "-strict", "experimental", input]);
        var media = await new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(10)).ProcessAsync(input, Descriptor(), 100_000, default);
        Assert.Equal("flac", media.Container);
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(input))[..4]));
        var expected = await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-i", native, "-threads", "1", "-f", "hash", "-hash", "sha256", "-"]);
        var actual = await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-f", "flac", "-i", input, "-threads", "1", "-f", "hash", "-hash", "sha256", "-"]);
        Assert.Equal(expected, actual);
    }

    [MediaToolsFact]
    public async Task Conversion_RealProcessesPreserveAlacContainerAndBytes()
    {
        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=1", "-threads", "1", "-c:a", "alac", input]);
        var original = await File.ReadAllBytesAsync(input);
        var media = await new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(10))
            .ProcessAsync(input, Descriptor("alac"), 100_000, default);
        Assert.Equal("m4a", media.Container);
        Assert.Equal("alac", media.Codec);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(root));
    }

    private Task<ExtensionDecryptedMedia> ProcessAsync(ExtensionMediaProcessRunner runner, string codec = "flac") =>
        new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), runner).ProcessAsync(input, Descriptor(codec), 100_000, default);

    private async Task AssertFailureAsync(ExtensionMediaProcessRunner runner, string message = "Download container conversion failed.",
        string codec = "flac", long maximumBytes = 100_000)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), runner)
            .ProcessAsync(input, Descriptor(codec), maximumBytes, default));
        Assert.Equal(message, error.Message);
        Assert.DoesNotContain(root, error.ToString());
        await AssertOriginalAsync();
    }

    private async Task AssertOriginalAsync()
    {
        Assert.Equal(Original, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(root));
    }

    internal static string Descriptor(string codec = "flac") => JsonSerializer.Serialize(new
    { requires_container_conversion = true, audio_codec = codec, actual_extension = ".m4a", duration_ms = 1000 });

    internal static string Probe(string codec, string container, string duration = "1") => JsonSerializer.Serialize(new
    {
        streams = new[] { new { codec_type = "audio", codec_name = codec, duration } },
        format = new { format_name = container, duration }
    });

    internal static async Task<string> FixtureRunner(string executable, IEnumerable<string> arguments, string? output,
        long maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var args = arguments.ToArray();
        if (executable == "ffprobe") return Probe("flac", args.Contains("flac") ? "flac" : "mov,mp4,m4a");
        Assert.Equal("ffmpeg", executable);
        Assert.Contains("-nostdin", args);
        Assert.Contains("0:a:0", args);
        Assert.Contains("file", args);
        if (output != null)
        {
            Assert.Equal("copy", args[Array.IndexOf(args, "-c") + 1]);
            Assert.Equal("flac", args[Array.LastIndexOf(args, "-f") + 1]);
            Assert.Contains("-fs", args);
            await File.WriteAllBytesAsync(output, Converted, token);
        }
        else Assert.Contains("-xerror", args);
        return "";
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        input = Path.Combine(root, "audio.m4a");
        await File.WriteAllBytesAsync(input, Original);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}
