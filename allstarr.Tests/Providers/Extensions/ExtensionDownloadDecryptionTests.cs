using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed class ExtensionDownloadDecryptionTests : IAsyncLifetime
{
    internal const string Key = "00112233445566778899aabbccddeeff";
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-decryption", Guid.NewGuid().ToString("N"));
    private string input = null!;
    private byte[] encrypted = null!;

    [Fact]
    public async Task Decryption_RemuxesAndVerifiesAudioBeforeReplacingInput()
    {
        var result = await ProcessAsync(Descriptor());
        Assert.Equal("m4a", result.Container);
        Assert.Equal("aac", result.Codec);
        Assert.NotEqual(encrypted, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(root));
        var probe = await MediaRunner("ffprobe", ["-v", "error", "-show_entries", "stream=codec_name", "-of", "json", input], null, 0, default);
        Assert.Contains("aac", probe);
    }

    [Theory]
    [InlineData("", "ffmpeg.mov_key", "")]
    [InlineData("abcd", "ffmpeg.mov_key", "")]
    [InlineData("00112233445566778899aabbccddeefg", "ffmpeg.mov_key", "")]
    [InlineData("00112233445566778899aabbccddeeff00", "ffmpeg.mov_key", "")]
    [InlineData(Key, "widevine", "")]
    [InlineData(Key, "playready", "")]
    [InlineData(Key, "ffmpeg.mov_key", "bad-id")]
    public async Task Decryption_RefusesUnusableDescriptors(string key, string strategy, string keyId)
    {
        await AssertFailureAsync(Descriptor(key, strategy, keyId));
    }

    [Fact]
    public async Task Decryption_RejectsWrongKeyWithoutPublishingOutput() =>
        await AssertFailureAsync(Descriptor("ffeeddccbbaa99887766554433221100"));

    [Theory]
    [InlineData("flac", 1)]
    [InlineData("aac", 50)]
    public async Task Decryption_RejectsCodecOrDurationMismatch(string codec, double duration) =>
        await AssertFailureAsync(Descriptor(codec: codec, duration: duration));

    [Fact]
    public async Task Decryption_RejectsOversizedInput() =>
        await AssertFailureAsync(Descriptor(), maximumBytes: encrypted.Length - 1);

    [Fact]
    public async Task Decryption_TimeoutCleansOutputAndPreservesInput()
    {
        await AssertFailureAsync(Descriptor(), timeout: TimeSpan.Zero);
    }

    [Fact]
    public async Task Decryption_CancellationCleansOutputAndPreservesInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessAsync(Descriptor(), token: cancellation.Token));
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task Decryption_RejectsNonMp4InputWithoutPublishingOutput()
    {
        input = Path.Combine(root, "audio.webm");
        await File.WriteAllBytesAsync(input, encrypted);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProcessAsync(Descriptor()));
    }

    [Fact]
    public async Task Decryption_AcceptsOptionalHexKeyId() =>
        await ProcessAsync(Descriptor(keyId: "102132435465768798a9bacbdcedfe0f"));

    [Theory]
    [InlineData("strategy", "123")]
    [InlineData("scheme", "\"cbcs\"")]
    [InlineData("input_format", "false")]
    [InlineData("key_id", "42")]
    public async Task Decryption_RejectsMalformedOrUnknownDescriptorFields(string field, string json)
    {
        var descriptor = System.Text.Json.Nodes.JsonNode.Parse(Descriptor())!;
        descriptor["decryption"]![field] = System.Text.Json.Nodes.JsonNode.Parse(json);
        await AssertFailureAsync(descriptor.ToJsonString());
    }

    [Theory]
    [InlineData("video")]
    [InlineData("subtitle")]
    public async Task Decryption_RejectsAdditionalNonAudioStreams(string streamType)
    {
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var probe = System.Text.Json.Nodes.JsonNode.Parse(await FixtureRunner("ffprobe", arguments, null, limit, token))!;
            probe["streams"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["codec_type"] = streamType, ["codec_name"] = "unknown" });
            return probe.ToJsonString();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, Descriptor(), 100_000, default));
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task Decryption_RejectsOversizedOutputAndCleansTemporaryFile()
    {
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            if (output == null) return await FixtureRunner(executable, arguments, output, limit, token);
            await File.WriteAllBytesAsync(output, new byte[100_001], token);
            return "";
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, Descriptor(), 100_000, default));
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task Decryption_RejectsOutputThatFailsVerification()
    {
        var probes = 0;
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            var response = await FixtureRunner(executable, arguments, output, limit, token);
            return executable == "ffprobe" && ++probes == 2 ? response.Replace("aac", "mp3", StringComparison.Ordinal) : response;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, Descriptor(), 100_000, default));
        await AssertOriginalAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decryption_StopsWorkInProgressAndCleansPartialOutput(bool cancelCaller)
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
        else await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task Decryption_ProcessRunnerCancelsAndReapsSubprocess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExtensionDownloadPostProcessor.RunAsync(
            "/bin/sleep", ["30"], null, 0, cancellation.Token));
    }

    [Fact]
    public async Task Decryption_ProcessRunnerBoundsCapturedOutput()
    {
        var text = Path.Combine(root, "output.txt");
        await File.WriteAllTextAsync(text, new string('x', 200_000));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ExtensionDownloadPostProcessor.RunAsync(
            "/bin/cat", [text], null, 0, cancellation.Token));
        Assert.Equal("Download decryption failed.", error.Message);
    }

    [MediaToolsFact]
    public async Task Decryption_RealProcessesRejectWrongKeyAndAcceptCorrectKey()
    {
        var processor = new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(input,
            Descriptor("ffeeddccbbaa99887766554433221100"), 100_000, default));
        await AssertOriginalAsync();
        Assert.Equal("aac", (await processor.ProcessAsync(input, Descriptor(), 100_000, default)).Codec);
    }

    [MediaToolsFact]
    public async Task Decryption_RealProcessCancellationWaitsForExit()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExtensionDownloadPostProcessor.RunAsync("ffmpeg",
            ["-v", "error", "-nostdin", "-re", "-f", "lavfi", "-i", "anullsrc", "-threads", "1", "-t", "60", "-f", "null", "-"],
            null, 100_000, cancellation.Token));
    }

    private Task<ExtensionDecryptedMedia> ProcessAsync(string descriptor, long maximumBytes = 100_000,
        TimeSpan? timeout = null, CancellationToken token = default) =>
        new ExtensionDownloadPostProcessor(timeout ?? TimeSpan.FromSeconds(30), MediaRunner).ProcessAsync(
            input, descriptor, maximumBytes, token);

    private async Task AssertFailureAsync(string descriptor, long maximumBytes = 100_000, TimeSpan? timeout = null)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProcessAsync(descriptor, maximumBytes, timeout));
        Assert.Equal("Download decryption failed.", error.Message);
        Assert.DoesNotContain(Key, error.ToString());
        Assert.DoesNotContain(root, error.ToString());
        await AssertOriginalAsync();
    }

    private async Task AssertOriginalAsync()
    {
        Assert.Equal(encrypted, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(root));
    }

    internal static string Descriptor(string key = Key, string strategy = "ffmpeg.mov_key", string keyId = "",
        string codec = "aac", double duration = 1) => JsonSerializer.Serialize(new
        {
            decryption = new { strategy, key, key_id = keyId, input_format = "mov" },
            audio_codec = codec,
            duration_ms = duration * 1000
        });

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        input = Path.Combine(root, "audio.m4a");
        await CreateFixtureAsync(input);
        encrypted = await File.ReadAllBytesAsync(input);
        Assert.InRange(encrypted.Length, 1, 99_999);
    }

    internal static async Task CreateFixtureAsync(string input, int seconds = 1, bool video = false)
    {
        // Synthetic audio encrypted with a public test key; no service is contacted.
        if (!MediaToolsAvailable)
        {
            File.Copy(Fixture("encrypted.m4a"), input, overwrite: true);
            return;
        }
        List<string> arguments = ["-v", "error", "-nostdin", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}"];
        if (video) arguments.AddRange(["-f", "lavfi", "-i", "color=size=16x16:duration=1", "-c:v", "mpeg4"]);
        arguments.AddRange(["-threads", "1", "-c:a", "aac", "-encryption_scheme", "cenc-aes-ctr", "-encryption_key", Key,
            "-encryption_kid", "102132435465768798a9bacbdcedfe0f", "-y", input]);
        await RunAsync("ffmpeg", arguments);
    }

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Extensions", "Audio", name);
    internal static readonly bool MediaToolsAvailable = Available("ffmpeg") && Available("ffprobe");
    internal static ExtensionMediaProcessRunner MediaRunner => MediaToolsAvailable ? ExtensionDownloadPostProcessor.RunAsync : FixtureRunner;

    private static bool Available(string executable)
    {
        try { RunAsync(executable, ["-version"]).GetAwaiter().GetResult(); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    internal static async Task<string> FixtureRunner(string executable, IEnumerable<string> arguments, string? output,
        long maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var args = arguments.ToArray();
        if (executable == "ffprobe") return """
            {"streams":[{"codec_type":"audio","codec_name":"aac","duration":"1.000000"}],
             "format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"1.000000"}}
            """;
        Assert.Equal("ffmpeg", executable);
        Assert.Contains("-nostdin", args);
        Assert.Contains("-map", args);
        Assert.Contains("0:a:0", args);
        if (output != null)
        {
            Assert.Contains("copy", args);
            var index = Array.IndexOf(args, "-decryption_key");
            Assert.True(index >= 0);
            if (args[index + 1] != Key) throw new InvalidDataException();
            await File.WriteAllBytesAsync(output, await File.ReadAllBytesAsync(Fixture("clear.m4a"), token), token);
        }
        else Assert.Contains("-xerror", args);
        return "";
    }

    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.True(process.ExitCode == 0, executable + " failed: " + await error);
        return await output;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}

public sealed class MediaToolsFactAttribute : FactAttribute
{
    public MediaToolsFactAttribute()
    {
        if (!ExtensionDownloadDecryptionTests.MediaToolsAvailable) Skip = "ffmpeg and ffprobe are unavailable on the test PATH.";
    }
}
