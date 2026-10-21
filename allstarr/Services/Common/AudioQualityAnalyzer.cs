using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace allstarr.Services.Common;

public sealed record AudioQualityCheck(
    [property: JsonPropertyName("claimedSampleRate")] int ClaimedSampleRate,
    [property: JsonPropertyName("claimedBitDepth")] int ClaimedBitDepth,
    [property: JsonPropertyName("effectiveBandwidthHz")] double? EffectiveBandwidthHz,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("likelySourceRate")] int? LikelySourceRate = null);

public sealed class AudioQualityAnalyzer(FileMediaApplicationCache cache)
{
    private const int TransformSize = 8192;
    private const int MaximumChannels = 8;
    private const int MaximumRate = 384000;
    private const double WindowSeconds = 4;

    public async Task<AudioQualityCheck> AnalyzeAsync(
        string path, CancellationToken cancellationToken = default, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unknown = new AudioQualityCheck(0, 0, null, "unknown", 0);
        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists || file.Length == 0) return unknown;
            var identity = $"v1\n{file.FullName}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}";
            var key = "audio_quality:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            if (!force && await cache.GetAsync<AudioQualityCheck>(key).WaitAsync(cancellationToken) is { } retained)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return retained;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var token = deadline.Token;
            AudioQualityCheck result;
            try
            {
                result = await Task.Run(async () =>
                {
                    var wave = ReadWaveHeader(file.FullName, token);
                    var format = wave ?? await ProbeAsync(file.FullName, token);
                    if (format is null || format.Rate is < 8000 or > MaximumRate ||
                        format.Channels is < 1 or > MaximumChannels || format.Duration <= 0 ||
                        !double.IsFinite(format.Duration)) return unknown;
                    var analysis = new SpectrumAnalysis(format);
                    // Five disjoint windows span the recording while decoding at most twenty seconds.
                    var duration = Math.Min(WindowSeconds, format.Duration / 5);
                    for (var index = 0; index < 5; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        var start = index * (format.Duration - duration) / 4;
                        if (wave is not null) ReadWaveWindow(file.FullName, format, start, duration, analysis, token);
                        else await DecodeWindowAsync(file.FullName, format, start, duration, analysis, token);
                    }
                    return analysis.Result();
                }, token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return unknown;
            }

            cancellationToken.ThrowIfCancellationRequested();
            file.Refresh();
            var currentIdentity = $"v1\n{file.FullName}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}";
            if (identity == currentIdentity && result.Verdict != "unknown")
                await cache.SetAsync(key, result).WaitAsync(cancellationToken);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return unknown;
        }
    }

    private sealed record AudioFormat(int Rate, int Bits, int Channels, double Duration,
        long DataOffset = 0, long DataBytes = 0, int StorageBits = 0, bool FloatingPoint = false, bool Lossless = true);

    private static AudioFormat? ReadWaveHeader(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (stream.Length < 12 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") return null;
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") return null;
        int rate = 0, channels = 0, bits = 0, storageBits = 0, block = 0, encoding = 0;
        long dataOffset = 0, dataBytes = 0;
        // Headers can contain large artwork chunks; seek over them without allocating them.
        var chunkCount = 0;
        while (stream.Position + 8 <= stream.Length && chunkCount++ < 256)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32();
            var next = checked(stream.Position + length + (length & 1));
            if (next > stream.Length + 1) throw new InvalidDataException("Truncated audio chunk.");
            if (chunk == "fmt " && length >= 16)
            {
                encoding = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); block = reader.ReadUInt16(); storageBits = bits = reader.ReadUInt16();
                if (encoding == 0xfffe && length >= 40)
                {
                    reader.ReadUInt16();
                    var valid = reader.ReadUInt16();
                    if (valid > 0) bits = valid;
                    reader.ReadUInt32(); encoding = reader.ReadUInt16();
                }
            }
            if (chunk == "data") { dataOffset = stream.Position; dataBytes = length; }
            stream.Position = next;
            if (dataOffset > 0 && rate > 0) break;
        }
        if (encoding is not (1 or 3) || storageBits is not (16 or 24 or 32 or 64) || bits is < 1 or > 64 ||
            channels < 1 || block != channels * storageBits / 8 || rate <= 0 || dataOffset == 0) return null;
        if (encoding == 3 && storageBits is not (32 or 64)) return null;
        if (encoding == 1 && storageBits > 32) return null;
        return new(rate, bits, channels, (double)dataBytes / block / rate, dataOffset, dataBytes,
            storageBits, encoding == 3);
    }

    private static void ReadWaveWindow(string path, AudioFormat format, double start, double seconds,
        SpectrumAnalysis analysis, CancellationToken cancellationToken)
    {
        var bytesPerSample = format.StorageBits / 8;
        var block = bytesPerSample * format.Channels;
        var first = (long)(start * format.Rate);
        var frames = (int)Math.Min(seconds * format.Rate, format.DataBytes / block - first);
        if (frames <= 0) return;
        using var stream = File.OpenRead(path);
        stream.Position = format.DataOffset + first * block;
        var buffer = new byte[checked(frames * block)];
        stream.ReadExactly(buffer);
        var samples = Enumerable.Range(0, format.Channels).Select(_ => new double[frames]).ToArray();
        for (var frame = 0; frame < frames; frame++)
        {
            if ((frame & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var span = buffer.AsSpan((frame * format.Channels + channel) * bytesPerSample, bytesPerSample);
                if (format.FloatingPoint)
                {
                    var value = bytesPerSample == 4 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span))
                        : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span));
                    samples[channel][frame] = double.IsFinite(value) ? value : 0;
                }
                else
                {
                    var value = bytesPerSample switch
                    {
                        2 => BinaryPrimitives.ReadInt16LittleEndian(span),
                        3 => (span[0] | span[1] << 8 | span[2] << 16) << 8 >> 8,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(span)
                    };
                    value >>= format.StorageBits - format.Bits;
                    analysis.ObserveInteger(value);
                    samples[channel][frame] = value / Math.Pow(2, format.Bits - 1);
                }
            }
        }
        analysis.Add(samples, cancellationToken);
    }

    private static async Task<AudioFormat?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await RunAsync("ffprobe", ["-v", "error", "-protocol_whitelist", "file,pipe", "-threads", "1", "-select_streams", "a:0", "-show_entries",
            "stream=sample_rate,channels,bits_per_raw_sample,bits_per_sample,codec_name,duration:format=duration",
            "-of", "json", path], 256 * 1024, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
        if (stream.ValueKind != JsonValueKind.Object) return null;
        static string? Value(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : null;
        static int Integer(JsonElement item, string name) => int.TryParse(Value(item, name), out var value) ? value : 0;
        var bits = Integer(stream, "bits_per_raw_sample");
        if (bits == 0) bits = Integer(stream, "bits_per_sample");
        var durationValue = Value(stream, "duration");
        if (!double.TryParse(durationValue, CultureInfo.InvariantCulture, out var duration) &&
            document.RootElement.TryGetProperty("format", out var container))
            double.TryParse(Value(container, "duration"), CultureInfo.InvariantCulture, out duration);
        var codec = Value(stream, "codec_name") ?? "";
        var lossless = codec is "flac" or "alac" or "wavpack" or "tta" or "ape" || codec.StartsWith("pcm_", StringComparison.Ordinal);
        return new(Integer(stream, "sample_rate"), bits, Integer(stream, "channels"), duration, Lossless: lossless);
    }

    private static async Task DecodeWindowAsync(string path, AudioFormat format, double start, double seconds,
        SpectrumAnalysis analysis, CancellationToken cancellationToken)
    {
        var maximumFrames = (int)Math.Ceiling(seconds * format.Rate);
        var bytes = await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-protocol_whitelist", "file,pipe", "-threads", "1", "-ss", start.ToString("R", CultureInfo.InvariantCulture),
            "-i", path, "-map", "0:a:0", "-t", seconds.ToString("R", CultureInfo.InvariantCulture),
            "-vn", "-sn", "-dn", "-c:a", "pcm_s32le", "-threads", "1", "-f", "s32le", "pipe:1"],
            checked((maximumFrames + 4096) * format.Channels * 4), cancellationToken);
        var frames = Math.Min(maximumFrames, bytes.Length / (format.Channels * 4));
        var samples = Enumerable.Range(0, format.Channels).Select(_ => new double[frames]).ToArray();
        for (var frame = 0; frame < frames; frame++)
        {
            if ((frame & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((frame * format.Channels + channel) * 4, 4));
                if (format.Bits is > 0 and <= 32) analysis.ObserveInteger(value >> (32 - format.Bits));
                samples[channel][frame] = value / 2147483648.0;
            }
        }
        analysis.Add(samples, cancellationToken);
    }

    private static async Task<byte[]> RunAsync(string executable, string[] arguments, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidDataException("Audio decoder could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var registration = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        Task errors = Task.CompletedTask;
        try
        {
            errors = DrainAsync(process.StandardError.BaseStream, timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            while (await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token) is var count && count > 0)
            {
                if (output.Length + count > maximumBytes) throw new InvalidDataException("Audio decoder output exceeded its limit.");
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException("Audio decoder could not read the file.");
            return output.ToArray();
        }
        finally
        {
            timeout.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await errors; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, cancellationToken) > 0) { }
    }

    private sealed class SpectrumAnalysis(AudioFormat format)
    {
        private readonly double[] _powers = new double[TransformSize / 2 + 1];
        private uint _integerBits;
        private readonly HashSet<uint> _lowValues = [];
        private long _integerSamples;
        private double _energy;
        private int _windows;

        public void ObserveInteger(int value)
        {
            _integerBits |= unchecked((uint)value);
            _integerSamples++;
            if (format.Bits is > 16 and <= 32 && _lowValues.Count <= 2)
                _lowValues.Add(unchecked((uint)value) & ((1u << (format.Bits - 16)) - 1));
        }

        public void Add(double[][] channels, CancellationToken cancellationToken)
        {
            foreach (var samples in channels)
            {
                for (var start = 0; start + TransformSize <= samples.Length; start += TransformSize / 2)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = new Complex[TransformSize];
                    for (var index = 0; index < TransformSize; index++)
                    {
                        var value = samples[start + index];
                        _energy += value * value;
                        values[index] = new Complex(value * (0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (TransformSize - 1))), 0);
                    }
                    Transform(values);
                    for (var index = 1; index < _powers.Length; index++)
                        _powers[index] += values[index].Real * values[index].Real + values[index].Imaginary * values[index].Imaginary;
                    _windows++;
                }
            }
        }

        public AudioQualityCheck Result()
        {
            AudioQualityCheck ResultOf(string verdict, double confidence, double? bandwidth, int? source = null) =>
                new(format.Rate, format.Bits, bandwidth, verdict, confidence, source);
            if (_windows == 0 || _energy / (_windows * TransformSize) < 1e-12) return ResultOf("unknown", 0, null);
            var peak = _powers.Max();
            var threshold = peak * Math.Pow(10, -65.0 / 10);
            var last = Array.FindLastIndex(_powers, value => value > threshold);
            var bandwidth = (double)last * format.Rate / TransformSize;
            if (!format.Lossless) return ResultOf("unknown", 0, bandwidth);
            if (!format.FloatingPoint && format.Bits is > 16 and <= 32 && _integerSamples >= 4096 &&
                _integerBits != 0 && BitOperations.TrailingZeroCount(_integerBits) >= format.Bits - 16)
                return ResultOf("padded-bit-depth", 0.99, bandwidth);
            if (!format.FloatingPoint && format.Lossless && format.Bits is > 16 and <= 32 &&
                _integerSamples >= 4096 && _lowValues.Count is > 0 and <= 2)
                return ResultOf("padded-bit-depth", 0.85, bandwidth);

            var low = Math.Max(1, (int)(500.0 * TransformSize / format.Rate));
            var high = Math.Min(last, (int)(Math.Min(bandwidth, 20000) * TransformSize / format.Rate));
            var occupied = 0;
            for (var index = low; index <= high; index++) if (_powers[index] > peak * 0.001) occupied++;
            var broad = high > low && (double)occupied / (high - low + 1) >= 0.3;
            if (!broad) return ResultOf(format.Rate > 48000 || format.Bits > 16 ? "unknown" : "standard", 0.3, bandwidth);

            double Band(double from, double to)
            {
                var begin = Math.Max(1, (int)(from * TransformSize / format.Rate));
                var end = Math.Min(_powers.Length - 1, (int)(to * TransformSize / format.Rate));
                if (end < begin) return 0;
                double sum = 0;
                for (var index = begin; index <= end; index++) sum += _powers[index];
                return sum / (end - begin + 1);
            }
            var below = Band(bandwidth - 2500, bandwidth - 500);
            var above = Band(bandwidth + 1000, bandwidth + 3000);
            var cliff = below > 0 && above < below * 0.001;
            if (bandwidth is >= 14500 and <= 19500 && cliff)
                return ResultOf("lossy-transcode-suspected", 0.65, bandwidth);
            if (format.Rate > 48000 && bandwidth is > 19500 and < 27000 && cliff)
                return ResultOf("upsampled", 0.78, bandwidth, bandwidth <= 22500 ? 44100 : 48000);
            if (format.Rate > 48000)
            {
                var upper = Band(28000, Math.Min(format.Rate / 2.0 - 500, 45000));
                var audible = Band(2000, 16000);
                return upper > audible * 0.001
                    ? ResultOf("true-hi-res", 0.85, bandwidth)
                    : ResultOf("unknown", 0.25, bandwidth);
            }
            return ResultOf(format.Bits > 16 ? "true-hi-res" : "standard", format.Bits > 16 ? 0.6 : 0.9, bandwidth);
        }
    }

    private static void Transform(Complex[] values)
    {
        for (int index = 1, reverse = 0; index < values.Length; index++)
        {
            var bit = values.Length >> 1;
            for (; (reverse & bit) != 0; bit >>= 1) reverse ^= bit;
            reverse ^= bit;
            if (index < reverse) (values[index], values[reverse]) = (values[reverse], values[index]);
        }
        for (var length = 2; length <= values.Length; length <<= 1)
        {
            var root = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var start = 0; start < values.Length; start += length)
            {
                var factor = Complex.One;
                for (var index = 0; index < length / 2; index++)
                {
                    var even = values[start + index];
                    var odd = values[start + index + length / 2] * factor;
                    values[start + index] = even + odd;
                    values[start + index + length / 2] = even - odd;
                    factor *= root;
                }
            }
        }
    }
}
