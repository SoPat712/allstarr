using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;

namespace allstarr.Services.Common;

internal static class AudioMediaFacts
{
    public static TrackMediaFacts? ReadPrefix(ReadOnlySpan<byte> bytes, long? size)
    {
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            int rate = 0, channels = 0, bits = 0, storageBits = 0, block = 0, encoding = 0;
            long? data = null;
            var offset = 12;
            while (offset <= bytes.Length - 8)
            {
                var chunk = bytes.Slice(offset, 4);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
                var body = bytes[(offset + 8)..];
                if (chunk.SequenceEqual("fmt "u8) && length >= 16 && body.Length >= 16)
                {
                    encoding = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                    rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                    block = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
                    bits = storageBits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                    if (encoding == 0xfffe && length >= 40 && body.Length >= 40)
                    {
                        var valid = BinaryPrimitives.ReadUInt16LittleEndian(body[18..]);
                        if (valid > 0) bits = valid;
                        encoding = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
                    }
                }
                if (chunk.SequenceEqual("data"u8)) { data = length; break; }
                var next = offset + 8L + length + (length & 1);
                if (next > bytes.Length) break;
                offset = (int)next;
            }
            if (encoding is not (1 or 3) || rate is <= 0 or > 768000 || channels is <= 0 or > 32 ||
                storageBits is not (8 or 16 or 24 or 32 or 64) || bits <= 0 || bits > storageBits ||
                block != channels * storageBits / 8) return null;
            var codec = encoding == 3 ? $"pcm_f{storageBits}le" : storageBits == 8 ? "pcm_u8" : $"pcm_s{storageBits}le";
            var bitrate = (long)rate * block * 8;
            return new(new("audio/wav", "wav", codec, bitrate <= int.MaxValue ? (int)bitrate : null,
                rate, bits, channels), size, data is > 0 ? (double)data.Value / block / rate : null);
        }
        if (bytes.Length >= 42 && bytes[..4].SequenceEqual("fLaC"u8) &&
            (bytes[4] & 0x7f) == 0 && bytes[7] == 34)
        {
            var packed = BinaryPrimitives.ReadUInt64BigEndian(bytes[18..]);
            var rate = (int)(packed >> 44);
            if (rate <= 0) return null;
            var channels = (int)((packed >> 41) & 7) + 1;
            var bits = (int)((packed >> 36) & 31) + 1;
            var samples = packed & ((1UL << 36) - 1);
            var seconds = samples > 0 ? (double?)samples / rate : null;
            return new(new("audio/flac", "flac", "flac", AverageBitrate(size, seconds), rate, bits, channels), size, seconds);
        }
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
            return new(new("audio/mp4", "mp4", "unknown"), size);
        if (bytes.Length >= 27 && bytes[..4].SequenceEqual("OggS"u8))
        {
            var opus = bytes.IndexOf("OpusHead"u8);
            if (opus >= 0 && opus <= bytes.Length - 19 && bytes[opus + 9] > 0)
                return new(new("audio/ogg", "ogg", "opus", sampleRate: 48000, channels: bytes[opus + 9]), size);
            var vorbis = bytes.IndexOf("\x01vorbis"u8);
            if (vorbis >= 0 && vorbis <= bytes.Length - 16)
            {
                var channels = bytes[vorbis + 11];
                var rate = BinaryPrimitives.ReadInt32LittleEndian(bytes[(vorbis + 12)..]);
                if (channels > 0 && rate is > 0 and <= 768000)
                    return new(new("audio/ogg", "ogg", "vorbis", sampleRate: rate, channels: channels), size);
            }
            return new(new("audio/ogg", "ogg", "unknown"), size);
        }
        var frame = 0;
        if (MediaDurationHeaders.TryGetId3TagLength(bytes, out var tag)) frame = tag;
        if (frame <= bytes.Length - 4 && bytes[frame] == 0xff && (bytes[frame + 1] & 0xe0) == 0xe0)
        {
            var second = bytes[frame + 1];
            var third = bytes[frame + 2];
            // ADTS has a zero layer field; MPEG Layer III has layer bits 01.
            if ((second & 0xf6) == 0xf0)
            {
                int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
                var index = (third >> 2) & 15;
                var channels = (third & 1) << 2 | bytes[frame + 3] >> 6;
                if (index < rates.Length)
                    return new(new("audio/aac", "aac", "aac", sampleRate: rates[index], channels: channels > 0 ? channels : null), size);
            }
            var version = (second >> 3) & 3;
            var layer = (second >> 1) & 3;
            var rateIndex = (third >> 2) & 3;
            var bitrateIndex = third >> 4;
            if (version != 1 && layer == 1 && rateIndex != 3 && bitrateIndex is > 0 and < 15)
            {
                int[] rates = [44100, 48000, 32000];
                var rate = rates[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
                return new(new("audio/mpeg", "mp3", "mp3", sampleRate: rate,
                    channels: bytes[frame + 3] >> 6 == 3 ? 1 : 2), size);
            }
        }
        return null;
    }

    private static int? AverageBitrate(long? size, double? seconds) =>
        size is > 0 && seconds is > 0 && size.Value * 8d / seconds.Value is > 0 and <= int.MaxValue
            ? (int)(size.Value * 8d / seconds.Value) : null;

    public static async Task<TrackMediaFacts?> ReadFileAsync(string path, CancellationToken token)
    {
        TrackMediaFacts? prefixFacts = null;
        try
        {
            await using (var file = File.OpenRead(path))
            {
                var prefix = new byte[4096];
                var count = await file.ReadAsync(prefix, token);
                prefixFacts = ReadPrefix(prefix.AsSpan(0, count), file.Length);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var start = new ProcessStartInfo("ffprobe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-v", "error", "-threads", "1", "-protocol_whitelist", "file,pipe", "-select_streams", "a:0",
                "-show_entries", "stream=codec_name,sample_rate,channels,bits_per_raw_sample,bits_per_sample,bit_rate,duration:format=format_name,duration,bit_rate:format_tags=replaygain_track_gain,replaygain_album_gain,replaygain_track_peak,replaygain_album_peak:stream_tags=replaygain_track_gain,replaygain_album_gain,replaygain_track_peak,replaygain_album_peak",
                "-of", "json", path }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process == null) return prefixFacts;
            try
            {
                var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
                var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
                await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
                if (process.ExitCode != 0) return prefixFacts;
                return ParseProbe(await output, new FileInfo(path).Length) ?? prefixFacts;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return prefixFacts; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            System.ComponentModel.Win32Exception or InvalidOperationException or InvalidDataException)
        { return prefixFacts; }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + read > 65536) throw new InvalidDataException("Audio metadata exceeds the limit.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    internal static TrackMediaFacts? ParseProbe(string json, long size)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return null;
        var audio = streams[0];
        if (!root.TryGetProperty("format", out var format)) return null;
        static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.ToString() : null;
        static double? Number(JsonElement value, string name) => double.TryParse(Text(value, name)?.Replace(" dB", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
        static int? Positive(JsonElement value, string name) => Number(value, name) is > 0 and <= int.MaxValue and { } number ? (int)number : null;
        var container = Text(format, "format_name")?.Split(',').FirstOrDefault(value => value is "flac" or "wav" or "mp3" or "mp4" or "m4a" or "ogg" or "aac" or "aiff");
        if (container is "m4a") container = "mp4";
        if (container == null) return null;
        var mime = Mime(container);
        var seconds = Number(format, "duration") ?? Number(audio, "duration");
        var codec = Text(audio, "codec_name") ?? "unknown";
        if (codec == "unknown" || Positive(audio, "sample_rate") == null || Positive(audio, "channels") == null) return null;
        var bits = Positive(audio, "bits_per_raw_sample") ?? Positive(audio, "bits_per_sample");
        // Decoder sample precision is not the bit depth of a lossy source.
        if (codec is "mp3" or "aac" or "opus" or "vorbis") bits = null;
        double? Tag(string name, bool peak = false)
        {
            var value = audio.TryGetProperty("tags", out var tags) ? Number(tags, name) : null;
            value ??= format.TryGetProperty("tags", out tags) ? Number(tags, name) : null;
            return peak && value < 0 ? null : value;
        }
        var gain = new ProviderReplayGain
        {
            TrackGain = Tag("replaygain_track_gain"),
            AlbumGain = Tag("replaygain_album_gain"),
            TrackPeak = Tag("replaygain_track_peak", true),
            AlbumPeak = Tag("replaygain_album_peak", true)
        };
        if (gain.TrackGain == null && gain.AlbumGain == null && gain.TrackPeak == null && gain.AlbumPeak == null) gain = null;
        return new(new(mime, container, codec, Positive(audio, "bit_rate") ?? Positive(format, "bit_rate") ?? AverageBitrate(size, seconds),
            Positive(audio, "sample_rate"), bits, Positive(audio, "channels")), size,
            seconds is > 0 ? seconds : null, gain);
    }

    internal static string Mime(string container) => container switch
    {
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        "mp4" or "m4a" => "audio/mp4",
        "mp3" => "audio/mpeg",
        "ogg" or "opus" => "audio/ogg",
        "aac" => "audio/aac",
        "aiff" => "audio/aiff",
        _ => "application/octet-stream"
    };
}
