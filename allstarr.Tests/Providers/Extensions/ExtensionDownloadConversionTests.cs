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
    public async Task Conversion_ReencodesFlacAndVerifiesBeforeReplacingInput()
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
        Assert.Equal(["ffprobe", "ffmpeg", "ffprobe", "ffmpeg", "ffmpeg", "ffmpeg"], calls);
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(16, "s16", "pcm_s16le", "44100", 1)]
    [InlineData(24, "s32", "pcm_s24le", "96000", 2)]
    public async Task Conversion_PreservesAudioPropertiesAndHashesFullPrecisionPcm(int bits, string sampleFormat,
        string pcmCodec, string sampleRate, int channels)
    {
        var hashes = new List<string>();
        var decoded = false;
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var args = arguments.ToArray();
            if (executable == "ffprobe")
            {
                var entries = args[Array.IndexOf(args, "-show_entries") + 1];
                Assert.Contains("sample_rate", entries);
                Assert.Contains("channels", entries);
                Assert.Contains("bits_per_raw_sample", entries);
                return Probe("flac", args.Contains("flac") ? "flac" : "mov,mp4,m4a", bits: bits, sampleRate: sampleRate, channels: channels);
            }
            if (output != null)
            {
                Assert.Equal(sampleFormat, args[Array.IndexOf(args, "-sample_fmt") + 1]);
                Assert.Equal(bits.ToString(), args[Array.IndexOf(args, "-bits_per_raw_sample") + 1]);
                Assert.DoesNotContain("-ar", args);
                Assert.DoesNotContain("-ac", args);
                return await FixtureRunner(executable, args, output, limit, token);
            }
            if (args.Contains("md5"))
            {
                Assert.True(decoded);
                Assert.Contains("-xerror", args);
                Assert.Equal(pcmCodec, args[Array.IndexOf(args, "-c:a") + 1]);
                hashes.Add(args[Array.IndexOf(args, "-f") + 1]);
                Assert.Equal(Original, await File.ReadAllBytesAsync(input, token));
                return "MD5=0123456789abcdef0123456789abcdef\n";
            }
            decoded = true;
            return await FixtureRunner(executable, args, output, limit, token);
        }
        await ProcessAsync(Run);
        Assert.Equal(["mov", "flac"], hashes);
        Assert.Equal(Converted, await File.ReadAllBytesAsync(input));
    }

    [Theory]
    [InlineData("sample_rate", "\"48000\"")]
    [InlineData("channels", "1")]
    [InlineData("bits_per_raw_sample", "\"24\"")]
    [InlineData("bits_per_raw_sample", "null")]
    [InlineData("duration", "null")]
    public async Task Conversion_RejectsChangedOrMissingOutputProperties(string property, string json)
    {
        var inspected = false;
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var response = await FixtureRunner(executable, arguments, output, limit, token);
            if (executable != "ffprobe" || !arguments.Contains("flac")) return response;
            inspected = true;
            var probe = System.Text.Json.Nodes.JsonNode.Parse(response)!;
            probe["streams"]![0]![property] = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (property == "duration") probe["format"]![property] = null;
            return probe.ToJsonString();
        }
        await AssertFailureAsync(Run);
        Assert.True(inspected);
    }

    [Theory]
    [InlineData("sample_rate", "null")]
    [InlineData("sample_rate", "\"0\"")]
    [InlineData("channels", "0")]
    [InlineData("bits_per_raw_sample", "null")]
    [InlineData("bits_per_raw_sample", "\"0\"")]
    [InlineData("bits_per_raw_sample", "\"32\"")]
    public async Task Conversion_RejectsUnusableInputAudioPropertiesBeforeEncoding(string property, string json)
    {
        var calls = new List<string>();
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            calls.Add(executable);
            Assert.Equal("ffprobe", executable);
            var probe = System.Text.Json.Nodes.JsonNode.Parse(Probe("flac", "mov,mp4,m4a"))!;
            probe["streams"]![0]![property] = System.Text.Json.Nodes.JsonNode.Parse(json);
            return Task.FromResult(probe.ToJsonString());
        }
        await AssertFailureAsync(Run);
        Assert.Equal(["ffprobe"], calls);
    }

    [Theory]
    [InlineData("MD5=ffffffffffffffffffffffffffffffff")]
    [InlineData("")]
    [InlineData("MD5=invalid")]
    [InlineData("MD5=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task Conversion_RejectsDifferentOrInvalidDecodedAudioHash(string hash)
    {
        var hashes = 0;
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var response = await FixtureRunner(executable, arguments, output, limit, token);
            if (!arguments.Contains("md5")) return response;
            hashes++;
            return arguments.Contains("flac") ? hash : response;
        }
        await AssertFailureAsync(Run);
        Assert.Equal(2, hashes);
    }

    [Fact]
    public async Task Conversion_RejectsEmptyHashesEvenWhenBothMatch()
    {
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token) =>
            arguments.Contains("md5") ? Task.FromResult("") : FixtureRunner(executable, arguments, output, limit, token);
        await AssertFailureAsync(Run);
    }

    [Theory]
    [InlineData("mov")]
    [InlineData("flac")]
    public async Task Conversion_RejectsFailedAudioHashProcess(string container)
    {
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token) =>
            arguments.Contains("md5") && arguments.Contains(container) ? throw new InvalidDataException()
                : FixtureRunner(executable, arguments, output, limit, token);
        await AssertFailureAsync(Run);
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
    [InlineData(false, 16)]
    [InlineData(true, 16)]
    [InlineData(false, 24)]
    [InlineData(true, 24)]
    public async Task Conversion_DecryptsFlacBeforeReencodingToNativeContainer(bool explicitConversion, int bits)
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
            if (executable == "ffprobe") return Probe("flac", arguments.Contains("flac") ? "flac" : "mov,mp4,m4a", bits: bits);
            if (arguments.Contains("md5"))
            {
                Assert.Contains(bits == 16 ? "pcm_s16le" : "pcm_s24le", arguments);
                return "MD5=0123456789abcdef0123456789abcdef\n";
            }
            if (output != null)
            {
                copies.Add("conversion");
                var args = arguments.ToArray();
                Assert.Equal(bits.ToString(), args[Array.IndexOf(args, "-bits_per_raw_sample") + 1]);
            }
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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Conversion_CancellationOrTimeoutPreservesInput(bool cancelCaller, bool duringHash)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var response = await FixtureRunner(executable, arguments, output, limit, token);
            if (duringHash ? arguments.Contains("md5") : output != null)
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
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            var diagnostic = Assert.IsType<ExtensionMediaProcessingDiagnostic>(error.Data[ExtensionDownloadPostProcessor.DiagnosticKey]);
            Assert.Equal(duringHash ? "input-hash" : "encode", diagnostic.Step);
            Assert.Equal("canceled", diagnostic.Failure);
        }
        else Assert.Equal("Download container conversion failed.", (await Assert.ThrowsAsync<InvalidDataException>(() => pending)).Message);
        await AssertOriginalAsync();
    }

    [MediaToolsFact]
    public Task Conversion_RealProcessesPreserveFragmentedFlac16BitAudio() =>
        AssertFragmentedConversionAsync(16, "44100", 1);

    [MediaToolsFact]
    public Task Conversion_RealProcessesPreserveFragmentedFlac24BitAudio() =>
        AssertFragmentedConversionAsync(24, "48000", 2);

    private async Task AssertFragmentedConversionAsync(int bits, string sampleRate, int channels)
    {
        var signal = channels == 1 ? "0.25*sin(2*PI*997*t)" : "0.25*sin(2*PI*997*t)|0.125*sin(2*PI*659*t)";
        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-filter_threads", "1",
            "-f", "lavfi", "-i", $"aevalsrc={signal}:s={sampleRate}:d=1", "-threads", "1", "-c:a", "flac",
            "-sample_fmt", bits == 16 ? "s16" : "s32", "-bits_per_raw_sample", bits.ToString(), "-strict", "experimental",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "250000", "-f", "mp4", input]);
        Assert.Contains("moof", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(input)));
        var pcmCodec = bits == 16 ? "pcm_s16le" : "pcm_s24le";
        var expected = await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "mov",
            "-i", input, "-map", "0:a:0", "-threads", "1", "-c:a", pcmCodec, "-f", "md5", "-"]);
        // No declared duration: the conversion must use the fragmented input's probed duration.
        var descriptor = JsonSerializer.Serialize(new { requires_container_conversion = true, audio_codec = "flac" });
        ExtensionDecryptedMedia media;
        try { media = await new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(10)).ProcessAsync(input, descriptor, 1_000_000, default); }
        catch (InvalidDataException exception)
        {
            throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(exception.Data[ExtensionDownloadPostProcessor.DiagnosticKey]));
        }
        Assert.Equal("flac", media.Container);
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(input))[..4]));
        using var probe = JsonDocument.Parse(await ExtensionDownloadDecryptionTests.RunAsync("ffprobe", ["-v", "error",
            "-f", "flac", "-i", input, "-show_entries", "stream=sample_fmt,sample_rate,channels,bits_per_raw_sample:format=duration", "-of", "json"]));
        var stream = Assert.Single(probe.RootElement.GetProperty("streams").EnumerateArray());
        Assert.Equal(bits.ToString(), stream.GetProperty("bits_per_raw_sample").GetString());
        Assert.Equal(bits == 16 ? "s16" : "s32", stream.GetProperty("sample_fmt").GetString());
        Assert.Equal(sampleRate, stream.GetProperty("sample_rate").GetString());
        Assert.Equal(channels, stream.GetProperty("channels").GetInt32());
        Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture), 0.99, 1.01);
        var actual = await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "flac",
            "-i", input, "-map", "0:a:0", "-threads", "1", "-c:a", pcmCodec, "-f", "md5", "-"]);
        Assert.Equal(expected, actual);
        Assert.Single(Directory.GetFiles(root));
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

    internal static string Probe(string codec, string container, string duration = "1", int bits = 16,
        string sampleRate = "44100", int channels = 2) => JsonSerializer.Serialize(new
        {
            streams = new[] { new { codec_type = "audio", codec_name = codec, duration, sample_fmt = bits == 16 ? "s16" : "s32",
                sample_rate = sampleRate, channels, bits_per_raw_sample = bits.ToString() } },
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
            Assert.Contains("-c:a", args);
            Assert.Equal("flac", args[Array.IndexOf(args, "-c:a") + 1]);
            Assert.Equal("5", args[Array.IndexOf(args, "-compression_level") + 1]);
            Assert.DoesNotContain("copy", args);
            Assert.Equal("flac", args[Array.LastIndexOf(args, "-f") + 1]);
            Assert.Contains("-fs", args);
            await File.WriteAllBytesAsync(output, Converted, token);
        }
        else
        {
            Assert.Contains("-xerror", args);
            if (args.Contains("md5"))
            {
                Assert.Contains("pcm_s16le", args);
                return "MD5=0123456789abcdef0123456789abcdef\n";
            }
            Assert.Contains("null", args);
        }
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
