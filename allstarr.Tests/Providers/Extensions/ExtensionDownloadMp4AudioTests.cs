using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed class ExtensionDownloadMp4AudioTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr mp4 audio", Guid.NewGuid().ToString("N"));
    private readonly string input;

    public ExtensionDownloadMp4AudioTests()
    {
        Directory.CreateDirectory(root);
        input = Path.Combine(root, "input.mp4");
        File.WriteAllBytes(input, [0, 0, 0, 16]);
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("")]
    public async Task Conversion_AcceptsDeclaredOrInferredAacWithoutInventingBitDepth(string declaration)
    {
        var calls = new List<string>();
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            calls.Add(executable);
            if (executable == "ffprobe")
            {
                var probe = JsonNode.Parse(ExtensionDownloadConversionTests.Probe("aac", "mov,mp4,m4a", bits: 0))!;
                probe["streams"]![0]!.AsObject().Remove("bits_per_raw_sample");
                return Task.FromResult(probe.ToJsonString());
            }
            Assert.Null(output);
            Assert.Contains("-xerror", arguments);
            return Task.FromResult(ExtensionDownloadConversionTests.HashResult(arguments));
        }
        var original = await File.ReadAllBytesAsync(input);
        var media = await new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, ExtensionDownloadConversionTests.Descriptor(declaration), 100_000, default);
        Assert.Equal(new ExtensionDecryptedMedia("m4a", "aac"), media);
        Assert.Equal(["ffprobe", "ffmpeg"], calls);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
    }

    [Theory]
    [InlineData("flac", "missing", true)]
    [InlineData("flac", "N/A", false)]
    [InlineData("alac", "0", true)]
    [InlineData("alac", "null", false)]
    [InlineData("aac", "NaN", true)]
    [InlineData("aac", "-1", false)]
    public async Task Conversion_MeasuresDecodedDurationWhenContainerDurationIsUnavailable(string codec, string duration, bool declared)
    {
        var decodes = 0;
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            if (executable == "ffprobe" && !arguments.Contains("flac"))
            {
                var probe = JsonNode.Parse(ExtensionDownloadConversionTests.Probe(codec, "mov,mp4,m4a"))!;
                if (duration == "missing") probe["format"]!.AsObject().Remove("duration");
                else probe["format"]!["duration"] = duration == "null" ? null : duration;
                probe["streams"]![0]!.AsObject().Remove("duration");
                return probe.ToJsonString();
            }
            if (executable == "ffmpeg" && output == null && arguments.Contains("mov"))
            {
                Assert.Contains("-progress", arguments);
                Assert.Contains("asetpts=N/SR/TB", arguments);
                decodes++;
                return ExtensionDownloadConversionTests.HashResult(arguments);
            }
            return await ExtensionDownloadConversionTests.FixtureRunner(executable, arguments, output, limit, token);
        }
        var descriptor = JsonNode.Parse(ExtensionDownloadConversionTests.Descriptor(codec))!;
        if (!declared) descriptor.AsObject().Remove("duration_ms");
        var media = await new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, descriptor.ToJsonString(), 100_000, default);
        Assert.Equal(codec, media.Codec);
        Assert.Equal(1, decodes);
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("duration-mismatch", "out_time_us=250000\nprogress=end\n", true)]
    [InlineData("decoded-duration", "out_time_us=0\nprogress=end\n", false)]
    [InlineData("decoded-duration", "out_time_us=1000000\n", false)]
    public async Task Conversion_MissingDurationStillRejectsShortOrUnmeasurableAudio(string check, string progress, bool declared)
    {
        var original = await File.ReadAllBytesAsync(input);
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            Assert.Null(output);
            if (executable != "ffprobe") return Task.FromResult("MD5=0123456789abcdef0123456789abcdef\n" + progress);
            var probe = JsonNode.Parse(ExtensionDownloadConversionTests.Probe("flac", "mov,mp4,m4a"))!;
            probe["format"]!.AsObject().Remove("duration");
            return Task.FromResult(probe.ToJsonString());
        }
        var descriptor = JsonNode.Parse(ExtensionDownloadConversionTests.Descriptor())!;
        if (!declared) descriptor.AsObject().Remove("duration_ms");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, descriptor.ToJsonString(), 100_000, default));
        var diagnostic = Assert.IsType<ExtensionMediaProcessingDiagnostic>(error.Data[ExtensionDownloadPostProcessor.DiagnosticKey]);
        Assert.Contains(check, diagnostic.Failure);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("streams", "[]", "audio-streams")]
    [InlineData("streams", "[{\"codec_type\":\"video\",\"codec_name\":\"flac\"}]", "audio-streams")]
    [InlineData("codec_name", "\"alac\"", "codec-mismatch")]
    [InlineData("format_name", "\"matroska\"", "container-mismatch")]
    [InlineData("duration", "\"50\"", "duration-mismatch")]
    public async Task Conversion_ReportsTheFailedValidationCheck(string field, string json, string check)
    {
        Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            Assert.Equal("ffprobe", executable);
            var probe = JsonNode.Parse(ExtensionDownloadConversionTests.Probe("flac", "mov,mp4,m4a"))!;
            if (field == "streams") probe[field] = JsonNode.Parse(json);
            else if (field == "codec_name") probe["streams"]![0]![field] = JsonNode.Parse(json);
            else probe["format"]![field] = JsonNode.Parse(json);
            return Task.FromResult(probe.ToJsonString());
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(5), Run)
            .ProcessAsync(input, ExtensionDownloadConversionTests.Descriptor(), 100_000, default));
        var diagnostic = Assert.IsType<ExtensionMediaProcessingDiagnostic>(error.Data[ExtensionDownloadPostProcessor.DiagnosticKey]);
        Assert.Contains(check, diagnostic.Failure);
        Assert.DoesNotContain(root, diagnostic.Failure);
    }

    [MediaToolsTheory]
    [InlineData("aac", "tail", "M4A ", false)]
    [InlineData("aac", "front", "iso8", false)]
    [InlineData("aac", "fragmented", "dash", false)]
    [InlineData("flac", "tail", "iso8", false)]
    [InlineData("flac", "front", "dash", false)]
    [InlineData("flac", "fragmented", "M4A ", false)]
    [InlineData("alac", "tail", "dash", false)]
    [InlineData("alac", "front", "M4A ", false)]
    [InlineData("alac", "fragmented", "iso8", false)]
    [InlineData("aac", "fragmented", "iso8", true)]
    [InlineData("flac", "fragmented", "dash", true)]
    [InlineData("alac", "fragmented", "M4A ", true)]
    public async Task Conversion_RealProcessesAcceptAudioAcrossMp4Layouts(string codec, string layout, string brand, bool missingDuration)
    {
        List<string> arguments = ["-v", "error", "-nostdin", "-y", "-filter_threads", "1", "-f", "lavfi", "-i",
            "sine=frequency=997:duration=1", "-threads", "1", "-c:a", codec, "-strict", "experimental"];
        if (codec == "flac") arguments.AddRange(["-sample_fmt", "s16"]);
        if (layout == "front") arguments.AddRange(["-movflags", "+faststart"]);
        if (layout == "fragmented") arguments.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "250000"]);
        arguments.AddRange(["-brand", brand, "-f", "mp4", input]);
        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", arguments);
        var original = await File.ReadAllBytesAsync(input);
        Assert.Equal(brand, Encoding.ASCII.GetString(original, 8, 4));
        var boxes = Boxes(original).ToArray();
        if (layout == "fragmented") Assert.Contains("moof", boxes);
        else Assert.Equal(layout == "front", Array.IndexOf(boxes, "moov") < Array.IndexOf(boxes, "mdat"));
        var expected = await PcmHashAsync(input, "mov");
        var descriptor = JsonSerializer.Serialize(new { requires_container_conversion = true, audio_codec = "", duration_ms = missingDuration ? 0 : 1000 });
        async Task<string> Run(string executable, IEnumerable<string> args, string? output, long limit, CancellationToken token)
        {
            var response = await ExtensionDownloadPostProcessor.RunAsync(executable, args, output, limit, token);
            if (executable != "ffprobe" || !missingDuration || args.Contains("flac")) return response;
            var probe = JsonNode.Parse(response)!;
            probe["format"]!.AsObject().Remove("duration");
            probe["streams"]![0]!.AsObject().Remove("duration");
            return probe.ToJsonString();
        }
        ExtensionDecryptedMedia media;
        try { media = await new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(10), Run).ProcessAsync(input, descriptor, 1_000_000, default); }
        catch (InvalidDataException error)
        {
            throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(error.Data[ExtensionDownloadPostProcessor.DiagnosticKey]));
        }
        Assert.Equal(codec == "flac" ? new("flac", "flac") : new ExtensionDecryptedMedia("m4a", codec), media);
        Assert.Equal(expected, await PcmHashAsync(input, codec == "flac" ? "flac" : "mov"));
        if (codec != "flac") Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(root));
    }

    [MediaToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conversion_RealProcessesRejectInvalidOrInitializationOnlyMp4(bool initializationOnly)
    {
        if (initializationOnly)
        {
            await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
                "sine=duration=1", "-threads", "1", "-c:a", "aac", "-movflags", "+frag_keyframe+empty_moov", input]);
            var bytes = await File.ReadAllBytesAsync(input);
            var moof = bytes.AsSpan().IndexOf("moof"u8);
            Assert.True(moof > 4);
            await File.WriteAllBytesAsync(input, bytes[..(moof - 4)]);
        }
        else await File.WriteAllBytesAsync(input, [0, 0, 0, 16, .. "ftypdash"u8.ToArray(), 0, 0, 0, 0]);
        var original = await File.ReadAllBytesAsync(input);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExtensionDownloadPostProcessor(TimeSpan.FromSeconds(10))
            .ProcessAsync(input, ExtensionDownloadConversionTests.Descriptor(""), 1_000_000, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(root));
    }

    private static IEnumerable<string> Boxes(byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            Assert.InRange(size, 8, bytes.Length - offset);
            yield return Encoding.ASCII.GetString(bytes, offset + 4, 4);
            offset += size;
        }
    }

    private static Task<string> PcmHashAsync(string path, string container) => ExtensionDownloadDecryptionTests.RunAsync("ffmpeg",
        ["-v", "error", "-nostdin", "-threads", "1", "-f", container, "-i", path, "-map", "0:a:0", "-threads", "1", "-c:a", "pcm_s24le", "-f", "md5", "-"]);

    public void Dispose() => Directory.Delete(root, true);
}
