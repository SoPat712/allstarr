using allstarr.Core.Playback;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace allstarr.Tests;

public sealed class ProgressiveAudioFinalizerTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("allstarr-finalize-").FullName;
    public static IEnumerable<object[]> Samples => AppleProgressiveFixtures.Samples;

    [MediaToolsTheory]
    [MemberData(nameof(Samples))]
    public async Task FinalizesEveryProgressiveFormatWithoutChangingAudioOrSource(string file)
    {
        var sample = AppleProgressiveFixtures.Sample(file);
        var bytes = AppleProgressiveFixtures.Bytes(file);
        var source = Path.Combine(directory, "source" + Path.GetExtension(file));
        var target = Path.Combine(directory, "complete" + Path.GetExtension(file));
        await File.WriteAllBytesAsync(source, bytes);

        await ProgressiveAudioFinalizer.FinalizeAsync(source, target, sample.Media, default);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        using var probe = await ProbeAsync(target, "stream=codec_name,duration:format=duration");
        var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(duration, sample.DurationSeconds - 0.1, sample.DurationSeconds + 0.1);
        if (sample.Codec == "alac") Assert.Equal(sample.DurationSeconds, duration, precision: 6);
        Assert.Equal(await PacketHashesAsync(source), await PacketHashesAsync(target));
        if (sample.Codec == "flac")
            await AssertFlacAsync(source, target, sample.BitDepth!.Value, sample.SampleRate, sample.Channels);
        else
        {
            Assert.Equal(await TagsAsync(source), await TagsAsync(target));
            var result = await File.ReadAllBytesAsync(target);
            Assert.Equal(Mp4Artwork(bytes), Mp4Artwork(result));
            var boxes = Mp4Boxes(result, 0, result.Length).ToArray();
            Assert.True(Array.FindIndex(boxes, box => box.Kind == "moov") < Array.FindIndex(boxes, box => box.Kind == "mdat"));
            var movie = boxes.Single(box => box.Kind == "moov");
            Assert.DoesNotContain(Mp4Boxes(result, movie.Body, movie.End), box => box.Kind == "mvex");
        }
    }

    [MediaToolsFact]
    public async Task BuildsSeekPointsAcrossTheCompleteTimeline()
    {
        var source = Path.Combine(directory, "long.flac");
        var target = Path.Combine(directory, "complete.flac");
        await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "sine=duration=22:sample_rate=48000",
            "-threads", "1", "-c:a", "flac", "-sample_fmt", "s32", "-y", source]);

        await ProgressiveAudioFinalizer.FinalizeAsync(source, target, new("audio/flac", "flac", "flac"), default);

        await AssertFlacAsync(source, target, 24, 48000, 1);
        var table = FlacBlocks(await File.ReadAllBytesAsync(target)).Single(block => block.Kind == 3).Body;
        Assert.Equal(3 * 18, table.Length);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("existing")]
    [InlineData("other-directory")]
    public async Task RefusesPathsThatCouldOverwriteOrMoveTheSource(string destination)
    {
        var sample = AppleProgressiveFixtures.Sample("alac-16-44100.flac");
        var bytes = AppleProgressiveFixtures.Bytes(sample.File);
        var source = Path.Combine(directory, "source.flac");
        await File.WriteAllBytesAsync(source, bytes);
        var target = destination == "same" ? source : destination == "existing"
            ? Path.Combine(directory, "existing.flac") : Path.Combine(directory, "nested", "output.flac");
        if (destination == "existing") await File.WriteAllBytesAsync(target, bytes);

        await Assert.ThrowsAnyAsync<IOException>(() => ProgressiveAudioFinalizer.FinalizeAsync(source, target, sample.Media, default));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        if (destination == "existing") Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task CancellationLeavesTheSourceUntouched()
    {
        var sample = AppleProgressiveFixtures.Sample("alac-16-44100.flac");
        var bytes = AppleProgressiveFixtures.Bytes(sample.File);
        var source = Path.Combine(directory, "source.flac");
        var target = Path.Combine(directory, "complete.flac");
        await File.WriteAllBytesAsync(source, bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProgressiveAudioFinalizer.FinalizeAsync(source, target, sample.Media, cancellation.Token));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(target));
    }

    [MediaToolsFact]
    public async Task DecodeFailureLeavesTheSourceUntouched()
    {
        var sample = AppleProgressiveFixtures.Sample("alac-16-44100.flac");
        var bytes = AppleProgressiveFixtures.Bytes(sample.File);
        bytes.AsSpan(AppleProgressiveFixtures.FlacAudioOffset(bytes)).Fill(0);
        var source = Path.Combine(directory, "source.flac");
        await File.WriteAllBytesAsync(source, bytes);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => ProgressiveAudioFinalizer.FinalizeAsync(source,
            Path.Combine(directory, "complete.flac"), sample.Media, default));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    private static async Task AssertFlacAsync(string source, string target, int bits, int rate, int channels)
    {
        var before = await File.ReadAllBytesAsync(source);
        var after = await File.ReadAllBytesAsync(target);
        var original = FlacBlocks(before);
        var blocks = FlacBlocks(after);
        Assert.Equal(original.Where(block => block.Kind is not (0 or 1 or 3)).Select(block => Convert.ToHexString(block.Body)),
            blocks.Where(block => block.Kind is not (0 or 1 or 3)).Select(block => Convert.ToHexString(block.Body)));
        Assert.DoesNotContain(blocks, block => block.Kind == 1);
        Assert.Equal(before.AsSpan(AppleProgressiveFixtures.FlacAudioOffset(before)).ToArray(),
            after.AsSpan(AppleProgressiveFixtures.FlacAudioOffset(after)).ToArray());
        using var packets = await ProbeAsync(target, "packet=pts,duration,pos", packets: true);
        var frames = packets.RootElement.GetProperty("packets").EnumerateArray().Select(packet => (
            Sample: packet.GetProperty("pts").GetInt64(),
            Count: packet.GetProperty("duration").GetInt64(),
            Position: long.Parse(packet.GetProperty("pos").GetString()!, CultureInfo.InvariantCulture))).ToArray();
        var total = frames.Sum(frame => frame.Count);
        var info = blocks.Single(block => block.Kind == 0).Body;
        Assert.Equal((ulong)total, BinaryPrimitives.ReadUInt64BigEndian(info.AsSpan(10)) & ((1UL << 36) - 1));
        var md5 = await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-threads", "1", "-i", target, "-map", "0:a:0",
            "-threads", "1", "-c:a", $"pcm_s{bits}le", "-f", "md5", "pipe:1"]);
        Assert.Equal(md5.Trim().Split('=')[1], Convert.ToHexString(info.AsSpan(18, 16)), ignoreCase: true);
        Assert.Equal(channels, ((info[12] >> 1) & 7) + 1);
        var table = blocks.Single(block => block.Kind == 3).Body;
        Assert.True(table.Length > 0 && table.Length % 18 == 0);
        long previous = -10L * rate;
        for (var offset = 0; offset < table.Length; offset += 18)
        {
            var sample = (long)BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(offset));
            var position = (long)BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(offset + 8));
            var count = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(offset + 16));
            var frame = Assert.Single(frames, frame => frame.Sample == sample);
            Assert.Equal(frame.Position - frames[0].Position, position);
            Assert.Equal(frame.Count, count);
            Assert.True(sample - previous >= 10L * rate);
            previous = sample;
        }
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(table));
    }

    private static List<(int Kind, byte[] Body)> FlacBlocks(byte[] bytes)
    {
        var result = new List<(int, byte[])>();
        for (var offset = 4; ;)
        {
            var size = bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
            result.Add((bytes[offset] & 127, bytes.AsSpan(offset + 4, size).ToArray()));
            if ((bytes[offset] & 128) != 0) return result;
            offset += size + 4;
        }
    }

    private static async Task<string[]> PacketHashesAsync(string path)
    {
        using var probe = JsonDocument.Parse(await RunAsync("ffprobe", ["-v", "error", "-threads", "1", "-select_streams", "a:0",
            "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=data_hash", "-of", "json", path]));
        return probe.RootElement.GetProperty("packets").EnumerateArray().Select(packet => packet.GetProperty("data_hash").GetString()!).ToArray();
    }

    private static async Task<string[]> TagsAsync(string path)
    {
        using var probe = await ProbeAsync(path, "format_tags=title,artist,album,album_artist,genre,date,track,disc,comment,copyright,compilation,isrc");
        return probe.RootElement.GetProperty("format").TryGetProperty("tags", out var tags)
            ? tags.EnumerateObject().Select(tag => tag.Name + "=" + tag.Value.GetString()).Order().ToArray() : [];
    }

    private static byte[]? Mp4Artwork(byte[] bytes, int start = 0, int? end = null)
    {
        foreach (var box in Mp4Boxes(bytes, start, end ?? bytes.Length))
        {
            if (box.Kind == "covr") return bytes.AsSpan(box.Body, box.End - box.Body).ToArray();
            if (box.Kind is "moov" or "udta" or "meta" or "ilst")
            {
                var result = Mp4Artwork(bytes, box.Body + (box.Kind == "meta" ? 4 : 0), box.End);
                if (result != null) return result;
            }
        }
        return null;
    }

    private static IEnumerable<(string Kind, int Body, int End)> Mp4Boxes(byte[] bytes, int start, int end)
    {
        for (var offset = start; offset + 8 <= end;)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
            var header = 8;
            if (size == 1) { size = checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8))); header = 16; }
            if (size == 0) size = end - offset;
            Assert.InRange(size, header, end - offset);
            yield return (System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4), offset + header, offset + size);
            offset += size;
        }
    }

    private static async Task<JsonDocument> ProbeAsync(string path, string entries, bool packets = false) =>
        JsonDocument.Parse(await RunAsync("ffprobe", packets
            ? ["-v", "error", "-threads", "1", "-select_streams", "a:0", "-show_packets", "-show_entries", entries, "-of", "json", path]
            : ["-v", "error", "-threads", "1", "-show_entries", entries, "-of", "json", path]));

    private static Task<string> RunAsync(string executable, IEnumerable<string> arguments) =>
        ExtensionDownloadDecryptionTests.RunAsync(OperatingSystem.IsWindows() ? executable : "nice",
            OperatingSystem.IsWindows() ? arguments : new[] { "-n", "19", executable }.Concat(arguments));

    public void Dispose() => Directory.Delete(directory, true);
}
