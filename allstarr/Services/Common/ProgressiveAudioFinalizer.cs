using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;

namespace allstarr.Services.Common;

public static class ProgressiveAudioFinalizer
{
    private const long MaximumSeconds = 86400;
    private const int MaximumMetadataBytes = 32 * 1024 * 1024;

    public static async Task FinalizeAsync(string source, string target, ProviderMediaFormat media, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (source.Equals(target, comparison) ||
            !string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), comparison) || File.Exists(target))
            throw new IOException("Audio finalization requires a new adjacent file.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        try
        {
            if (media.Container == "flac" && media.Codec == "flac")
                await FinalizeFlacAsync(source, target, timeout.Token);
            else if (media.Container is "mp4" or "m4a" && media.Codec is "aac" or "alac")
                await FinalizeMp4Async(source, target, media, timeout.Token);
            else throw new InvalidDataException("The audio format cannot be finalized.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Audio finalization exceeded its time limit."); }
    }

    private static async Task FinalizeFlacAsync(string source, string target, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var signature = new byte[4];
        await input.ReadExactlyAsync(signature, token);
        if (!signature.AsSpan().SequenceEqual("fLaC"u8)) throw new InvalidDataException("The FLAC header is invalid.");
        var blocks = new List<MetadataBlock>();
        var header = new byte[4];
        long metadataBytes = 0;
        while (true)
        {
            await input.ReadExactlyAsync(header, token);
            var kind = header[0] & 127;
            var length = header[1] << 16 | header[2] << 8 | header[3];
            metadataBytes += length + 4L;
            if (kind == 127 || metadataBytes > MaximumMetadataBytes || blocks.Count >= 1024 ||
                blocks.Count == 0 && (kind != 0 || length != 34) || blocks.Count > 0 && kind == 0)
                throw new InvalidDataException("The FLAC metadata is invalid or exceeds its limit.");
            var bytes = new byte[length];
            await input.ReadExactlyAsync(bytes, token);
            blocks.Add(new(kind, bytes));
            if ((header[0] & 128) != 0) break;
        }
        var audioOffset = input.Position;
        var info = blocks[0].Body;
        var packed = BinaryPrimitives.ReadUInt64BigEndian(info.AsSpan(10));
        var rate = (int)(packed >> 44);
        var channels = (int)((packed >> 41) & 7) + 1;
        var bits = (int)((packed >> 36) & 31) + 1;
        if (rate <= 0 || audioOffset >= input.Length) throw new InvalidDataException("The FLAC audio is invalid.");
        var timeline = await ReadTimelineAsync(source, audioOffset, input.Length, rate, token);
        var digest = await DecodeDigestAsync(source, timeline.Samples, channels, bits, token);
        BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10), (packed & ~((1UL << 36) - 1)) | (ulong)timeline.Samples);
        digest.CopyTo(info, 18);
        blocks.RemoveAll(block => block.Kind is 1 or 3);
        var seekTable = new byte[timeline.Points.Count * 18];
        for (var index = 0; index < timeline.Points.Count; index++)
        {
            var point = timeline.Points[index];
            BinaryPrimitives.WriteUInt64BigEndian(seekTable.AsSpan(index * 18), (ulong)point.Sample);
            BinaryPrimitives.WriteUInt64BigEndian(seekTable.AsSpan(index * 18 + 8), (ulong)point.Offset);
            BinaryPrimitives.WriteUInt16BigEndian(seekTable.AsSpan(index * 18 + 16), (ushort)point.Count);
        }
        blocks.Add(new(3, seekTable));
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(signature, token);
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            header[0] = (byte)(block.Kind | (index == blocks.Count - 1 ? 128 : 0));
            header[1] = (byte)(block.Body.Length >> 16);
            header[2] = (byte)(block.Body.Length >> 8);
            header[3] = (byte)block.Body.Length;
            await output.WriteAsync(header, token);
            await output.WriteAsync(block.Body, token);
        }
        await input.CopyToAsync(output, 81920, token);
        await output.FlushAsync(token);
    }

    private static Task<FlacTimeline> ReadTimelineAsync(string source, long audioOffset, long length, int rate, CancellationToken token) =>
        RunAsync("ffprobe", ["-v", "error", "-threads", "1", "-protocol_whitelist", "file,pipe", "-select_streams", "a:0",
            "-show_packets", "-show_entries", "packet=pts,duration,pos", "-of", "compact=p=0", source], async (output, cancellation) =>
        {
            using var reader = new StreamReader(output, Encoding.UTF8, leaveOpen: true);
            var points = new List<SeekPoint>();
            long samples = 0, firstPosition = -1, previousPosition = -1, previousSeek = -10L * rate;
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                if (line.Length == 0) continue;
                if (line.Length > 1024) throw new InvalidDataException("The FLAC packet metadata exceeds its limit.");
                long? sample = null, count = null, position = null;
                foreach (var field in line.Split('|'))
                {
                    var pair = field.Split('=', 2);
                    if (pair.Length != 2 || !long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
                    switch (pair[0]) { case "pts": sample = value; break; case "duration": count = value; break; case "pos": position = value; break; }
                }
                if (sample != samples || count is not (> 0 and <= ushort.MaxValue) || position is not { } current ||
                    current < audioOffset || current >= length || current <= previousPosition)
                    throw new InvalidDataException("The FLAC packet timeline is invalid.");
                if (firstPosition < 0) firstPosition = current;
                if (samples - previousSeek >= 10L * rate)
                {
                    points.Add(new(samples, current - firstPosition, (int)count.Value));
                    previousSeek = samples;
                }
                samples += count.Value;
                if (samples > MaximumSeconds * rate || samples >= 1L << 36)
                    throw new InvalidDataException("The FLAC timeline exceeds its limit.");
                previousPosition = current;
            }
            if (samples == 0 || firstPosition != audioOffset) throw new InvalidDataException("The FLAC audio timeline is empty or invalid.");
            return new FlacTimeline(samples, points);
        }, token);

    private static Task<byte[]> DecodeDigestAsync(string source, long samples, int channels, int bits, CancellationToken token) =>
        RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-err_detect", "explode", "-threads", "1", "-filter_threads", "1",
            "-protocol_whitelist", "file,pipe", "-i", source, "-map", "0:a:0", "-threads", "1", "-c:a", "pcm_s32le", "-f", "s32le", "pipe:1"],
            async (output, cancellation) =>
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[81920];
                var pcm = new byte[81920];
                var width = (bits + 7) / 8;
                long bytes = 0;
                var pending = 0;
                int read;
                while ((read = await output.ReadAsync(buffer.AsMemory(pending), cancellation)) > 0)
                {
                    var available = pending + read;
                    var complete = available / 4 * 4;
                    var written = 0;
                    for (var index = 0; index < complete; index += 4)
                    {
                        var value = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(index)) >> (32 - bits);
                        for (var octet = 0; octet < width; octet++) pcm[written++] = (byte)(value >> (octet * 8));
                    }
                    bytes += written;
                    if (bytes > samples * channels * width) throw new InvalidDataException("The decoded FLAC timeline is too long.");
                    md5.AppendData(pcm, 0, written);
                    pending = available - complete;
                    buffer.AsSpan(complete, pending).CopyTo(buffer);
                }
                if (pending != 0 || bytes != samples * channels * width)
                    throw new InvalidDataException("The decoded FLAC timeline does not match its packets.");
                return md5.GetHashAndReset();
            }, token);

    private static async Task FinalizeMp4Async(string source, string target, ProviderMediaFormat media, CancellationToken token)
    {
        List<string> timing = [];
        if (media.Codec == "alac")
        {
            // Older demuxers omit the last packet duration, but its decoded sample count is exact.
            var filter = await RunAsync("ffprobe", ["-v", "error", "-threads", "1", "-protocol_whitelist", "file,pipe",
                "-select_streams", "a:0", "-show_frames", "-show_entries", "frame=nb_samples:stream=sample_rate",
                "-of", "compact=p=0", source], async (output, cancellation) =>
            {
                using var reader = new StreamReader(output, Encoding.UTF8, leaveOpen: true);
                long frames = 0, samples = 0;
                var last = 0;
                var rate = 0;
                while (await reader.ReadLineAsync(cancellation) is { } line)
                {
                    foreach (var field in line.Split('|'))
                    {
                        var pair = field.Split('=', 2);
                        if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
                        if (pair[0] == "sample_rate") rate = value;
                        if (pair[0] != "nb_samples") continue;
                        if (value <= 0) throw new InvalidDataException("The ALAC frame timeline is invalid.");
                        last = value;
                        frames++;
                        samples += value;
                        if (samples > MaximumSeconds * 768000) throw new InvalidDataException("The ALAC timeline exceeds its limit.");
                    }
                }
                if (rate <= 0 || frames == 0 || samples > MaximumSeconds * rate)
                    throw new InvalidDataException("The ALAC frame timeline is invalid or exceeds its limit.");
                return FormattableString.Invariant($"setts=duration='if(eq(N,{frames - 1}),{last},DURATION)'");
            }, token);
            timing.AddRange(["-bsf:a", filter]);
        }
        await RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-threads", "1", "-filter_threads", "1",
            "-protocol_whitelist", "file,pipe", "-i", source, "-map", "0", "-map_metadata", "0", "-c", "copy", "-threads", "1",
            .. timing, "-movflags", "+faststart", "-f", "mp4", "-n", target], async (output, cancellation) =>
        {
            await output.CopyToAsync(Stream.Null, cancellation);
            return true;
        }, token);
        var duration = await RunAsync("ffprobe", ["-v", "error", "-threads", "1", "-protocol_whitelist", "file,pipe",
            "-show_entries", "format=duration", "-of", "json", target], async (output, cancellation) =>
        {
            using var reader = new StreamReader(output, Encoding.UTF8, leaveOpen: true);
            var text = new StringBuilder();
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer, cancellation)) > 0)
            {
                if (text.Length + count > 65536) throw new InvalidDataException("The audio metadata exceeds its limit.");
                text.Append(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(text.ToString());
            return document.RootElement.GetProperty("format").TryGetProperty("duration", out var value) &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : double.NaN;
        }, token);
        if (!double.IsFinite(duration) || duration is <= 0 or > MaximumSeconds)
            throw new InvalidDataException("The MP4 audio timeline is invalid or exceeds its limit.");
    }

    private static async Task<T> RunAsync<T>(string executable, IEnumerable<string> arguments,
        Func<Stream, CancellationToken, Task<T>> readOutput, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? executable : "nice")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (!OperatingSystem.IsWindows())
            foreach (var argument in new[] { "-n", "19", executable }) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The audio finalization process could not start.");
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            var result = await readOutput(process.StandardOutput.BaseStream, token);
            await process.WaitForExitAsync(token);
            await errors;
            if (process.ExitCode != 0) throw new IOException("The audio finalization process failed.");
            return result;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
        }
    }

    private sealed record MetadataBlock(int Kind, byte[] Body);
    private sealed record SeekPoint(long Sample, long Offset, int Count);
    private sealed record FlacTimeline(long Samples, List<SeekPoint> Points);
}
