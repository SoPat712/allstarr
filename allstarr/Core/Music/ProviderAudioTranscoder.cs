using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

public sealed record ProviderAudioConversion(ProviderMediaFormat Source, ProviderMediaFormat Media, string Encoder, string Muxer, bool Hls = false);

public static class ProviderAudioTranscoder
{
    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private static readonly MediaConversionGate DefaultGate = new();

    public static ProviderAudioConversion? Plan(ProviderAudioDeliveryRequest request, ProviderMediaFormat source)
    {
        if (request.RequestError != null || !request.EnableTranscoding || request.Evaluate(source).Supported) return null;
        var preserveLossless = IsLossless(source.Codec) &&
            (!request.MaxBitrate.HasValue || (source.SampleRate ?? 44100) * (long)(source.Channels ?? 2) * (source.BitDepth ?? 24) * 1.1 <= request.MaxBitrate);
        var codecs = new[] { !request.MaxBitrate.HasValue || preserveLossless ? source.Codec : request.PreferredCodec, request.PreferredCodec, "aac", "mp3", "opus", "flac", "alac", "pcm_s16le", "pcm_s24le", "pcm_u8", "vorbis" }.Distinct(StringComparer.Ordinal);
        foreach (var hls in request.RequireHls ? new[] { true } : request.AllowHls ? new[] { false, true } : new[] { false })
            foreach (var codec in codecs)
            {
                var lossless = IsLossless(codec);
                var inheritRate = source.SampleRate == null && request.SampleRate == null && request.MaxSampleRate == null && !Constrains(request, codec, "AudioSampleRate");
                var inheritChannels = source.Channels == null && request.Channels == null && request.MaxChannels == null && !Constrains(request, codec, "AudioChannels") &&
                    !(request.Profiles ?? []).Concat(request.TranscodingProfiles ?? []).Any(profile => profile.MaxChannels.HasValue &&
                        (profile.Codecs.Length == 0 || profile.Codecs.Contains(codec, StringComparer.OrdinalIgnoreCase)));
                var inheritDepth = source.BitDepth == null && request.MaxBitDepth == null && !Constrains(request, codec, "AudioBitDepth");
                var rates = codec == "opus" ? new[] { 48000 } : new[] { source.SampleRate ?? 44100, 44100, 48000, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 96000, 88200, 64000 }.Distinct().ToArray();
                var channels = new[] { request.Channels ?? source.Channels ?? 2, 2, 1 }.Distinct();
                var codecMaximum = codec switch { "aac" => 512000, "mp3" => 320000, "opus" => 510000, _ => int.MaxValue };
                var maximum = (int)Math.Min(request.MaxBitrate ?? 320000, codecMaximum);
                var preferred = request.MaxBitrate.HasValue || source.Codec != codec || IsLossless(source.Codec)
                    ? maximum : Math.Min(maximum, source.Bitrate ?? 320000);
                var bitrates = lossless ? new int?[] { null } : new[] { preferred, maximum, 128000, 96000, 64000, 48000, 32000, 160000, 192000, 256000, 320000, 24000, 16000, 8000 }
                    .Where(value => value > 0 && value <= preferred).Distinct().OrderDescending().Select(value => (int?)value).ToArray();
                IEnumerable<int?> depths = lossless ? new[] { Math.Min(request.MaxBitDepth ?? source.BitDepth ?? 16, 24), 16, 24, 8 }.Distinct().Select(value => (int?)value) : [null];
                foreach (var container in Containers(codec))
                {
                    if (!request.AcceptsConversionFormat(new(Mime(container), container, codec), hls)) continue;
                    foreach (var rate in rates)
                        foreach (var channel in channels)
                            foreach (var bitrate in bitrates)
                                foreach (var depth in depths)
                                {
                                    if (!CanEncode(codec, rate, channel, bitrate, depth)) continue;
                                    if (lossless && request.MaxBitrate is { } cap && rate * (long)channel * (depth ?? 16) * 1.1 > cap) continue;
                                    var media = new ProviderMediaFormat(Mime(container), container, codec, bitrate,
                                        inheritRate && codec != "opus" ? null : rate,
                                        inheritDepth && codec is "flac" or "alac" ? null : depth,
                                        inheritChannels ? null : channel);
                                    if (!request.AcceptsConversion(media, hls)) continue;
                                    return new(source, media, codec switch
                                    {
                                        "mp3" => "libmp3lame",
                                        "opus" => "libopus",
                                        "vorbis" => "libvorbis",
                                        _ => codec
                                    }, container == "aac" ? "adts" : container, hls);
                                }
                }
            }
        return null;
    }

    private static bool IsLossless(string codec) => codec is "flac" or "alac" || codec.StartsWith("pcm_", StringComparison.Ordinal);
    private static bool Constrains(ProviderAudioDeliveryRequest request, string codec, string property) =>
        request.Conditions.Any(condition => condition.Property.Equals(property, StringComparison.OrdinalIgnoreCase) &&
            (condition.Codec.Length == 0 || condition.Codec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(codec, StringComparer.OrdinalIgnoreCase)));

    private static IEnumerable<string> Containers(string codec) => codec switch
    {
        "aac" => ["aac", "mp4"],
        "mp3" => ["mp3"],
        "opus" => ["ogg", "webm"],
        "flac" => ["flac"],
        "alac" => ["mp4"],
        "vorbis" => ["ogg"],
        "pcm_s16le" or "pcm_s24le" or "pcm_u8" => ["wav"],
        _ => []
    };
    private static string Mime(string container) => container switch
    {
        "aac" => "audio/aac",
        "mp4" => "audio/mp4",
        "mp3" => "audio/mpeg",
        "ogg" => "audio/ogg",
        "webm" => "audio/webm",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        _ => "application/octet-stream"
    };
    private static bool CanEncode(string codec, int rate, int channels, int? bitrate, int? depth) =>
        channels is > 0 and <= 8 && rate is >= 8000 and <= 192000 &&
        (codec != "mp3" || channels <= 2 && rate <= 48000 && bitrate is >= 8000 and <= 320000 &&
            (rate > 24000 || bitrate <= 160000) &&
            new[] { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000 }.Contains(rate) &&
            new[] { 8000, 16000, 24000, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 160000, 192000, 224000, 256000, 320000 }.Contains(bitrate.Value)) &&
        (codec != "aac" || rate <= 96000 && bitrate is >= 8000 and <= 512000 && bitrate >= channels * 8000 &&
            bitrate <= rate * (long)channels * 6 && channels != 7) &&
        (codec != "opus" || rate == 48000 && bitrate is >= 6000 and <= 510000) &&
        (codec != "pcm_s16le" || depth == 16) && (codec != "pcm_s24le" || depth == 24) &&
        (codec != "pcm_u8" || depth == 8) && (codec is not ("flac" or "alac") || depth is 16 or 24);

    public static async Task ApplyAsync(HttpResponseMessage source, HttpResponse outgoing,
        ProviderAudioConversion plan, MediaConversionGate? gate, CancellationToken token,
        long startTimeTicks = 0, double? durationSeconds = null)
    {
        if (source.StatusCode != HttpStatusCode.OK || source.Content.Headers.ContentRange != null)
            throw new IOException("Audio conversion requires a complete source response.");
        ValidateOffset(startTimeTicks, durationSeconds);
        var original = source.Content;
        var conversion = await ConversionStream.OpenAsync(original, plan, gate ?? DefaultGate, token, startTimeTicks, durationSeconds);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(2));
            try { await conversion.PrepareAsync(startup.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("The audio conversion did not produce its first audio before the startup timeout."); }
            source.Content = new StreamContent(conversion);
            Describe(source, outgoing, plan, startTimeTicks, durationSeconds);
        }
        catch { await conversion.DisposeAsync(); throw; }
    }

    internal static void ApplyHead(HttpResponseMessage source, HttpResponse outgoing, ProviderAudioConversion plan,
        long startTimeTicks = 0, double? durationSeconds = null)
    {
        ValidateOffset(startTimeTicks, durationSeconds);
        source.Content.Dispose();
        source.Content = new HeadContent();
        Describe(source, outgoing, plan, startTimeTicks, durationSeconds);
    }

    private static void ValidateOffset(long ticks, double? duration)
    {
        if (ticks < 0) throw new ProviderAudioOffsetException("Invalid audio time offset.", HttpStatusCode.BadRequest);
        if (ticks > 0 && duration is > 0 && ticks / (double)TimeSpan.TicksPerSecond >= duration)
            throw new ProviderAudioOffsetException("The requested time offset is outside the audio timeline.", HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    private static void Describe(HttpResponseMessage source, HttpResponse outgoing, ProviderAudioConversion plan,
        long ticks, double? duration)
    {
        source.StatusCode = HttpStatusCode.OK;
        source.Content.Headers.ContentType = new(plan.Media.MimeType);
        foreach (var header in new[] { "Content-Length", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Content-Encoding" })
            outgoing.Headers.Remove(header);
        source.Content.Headers.ContentLength = null;
        source.Content.Headers.ContentRange = null;
        source.Content.Headers.LastModified = null;
        source.Content.Headers.ContentEncoding.Clear();
        source.Headers.ETag = null;
        source.Headers.AcceptRanges.Clear();
        void Fact(string key, string value) { source.Headers.Remove(key); source.Headers.TryAddWithoutValidation(key, value); outgoing.Headers[key] = value; }
        Fact("X-Allstarr-Audio-Codec", plan.Media.Codec);
        Fact("X-Allstarr-Audio-Container", plan.Media.Container);
        source.Headers.Remove("X-Allstarr-Audio-Bitrate");
        outgoing.Headers.Remove("X-Allstarr-Audio-Bitrate");
        if (plan.Media.Bitrate is { } bitrate) Fact("X-Allstarr-Audio-Bitrate", bitrate.ToString(CultureInfo.InvariantCulture));
        if (ticks > 0) Fact("X-Allstarr-Start-Seconds", (ticks / (double)TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture));
        if (duration is > 0) Fact("X-Content-Duration", duration.Value.ToString("0.#######", CultureInfo.InvariantCulture));
    }

    private sealed class HeadContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
    }

    private sealed class ConversionStream : Stream
    {
        private readonly Process process;
        private readonly HttpContent owner;
        private readonly CancellationTokenSource lifetime;
        private readonly IDisposable capacity;
        private readonly string? directory;
        private readonly Task pump;
        private readonly Task stderr;
        private readonly double? expectedRemainingSeconds;
        private long outputMicroseconds = -1;
        private bool progressEnded;
        private readonly CancellationTokenRegistration aborted;
        private readonly object lifecycle = new();
        private byte[] prefix = [];
        private int prefixPosition;
        private Task? cleanup;
        private bool closed;

        private ConversionStream(Process process, HttpContent owner, CancellationTokenSource lifetime,
            IDisposable capacity, string? directory, Stream input, byte[] inputPrefix, bool file, double? expectedRemainingSeconds)
        {
            this.process = process; this.owner = owner; this.lifetime = lifetime; this.capacity = capacity; this.directory = directory;
            this.expectedRemainingSeconds = expectedRemainingSeconds;
            stderr = ReadProgressAsync();
            pump = file ? Task.CompletedTask : PumpAsync(input, inputPrefix);
            aborted = lifetime.Token.Register(Stop);
        }

        internal static async Task<ConversionStream> OpenAsync(HttpContent original, ProviderAudioConversion plan,
            MediaConversionGate gate, CancellationToken token, long ticks, double? durationSeconds)
        {
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            string? directory = null;
            IDisposable? capacity = null;
            Process? process = null;
            try
            {
                var movieInput = ProviderAudioDeliveryRequest.Container(plan.Source.Container) == "mp4";
                await ManagedTrackCacheService.PrepareForConversionAsync(original, token, useDownloadCompletion: !movieInput);
                var input = await original.ReadAsStreamAsync(token);
                var path = ManagedTrackCacheService.CompletedFilePath(input);
                byte[] prefix = [];
                if (path == null && movieInput)
                {
                    // Sniffing activates the producer, so reserve the encoder slot before its first read.
                    capacity = await gate.EnterAsync(token);
                    ManagedTrackCacheService.PrepareForConversion(original);
                    var movie = await ReadMoviePrefixAsync(input, token);
                    prefix = movie.Prefix;
                    if (!movie.Streamable)
                    {
                        capacity.Dispose();
                        capacity = null;
                        // Full-file preparation must await cache verification without holding its gate.
                        ManagedTrackCacheService.PrepareForConversion(original, useDownloadCompletion: false);
                        directory = Directory.CreateTempSubdirectory("allstarr-audio-input-").FullName;
                        path = Path.Combine(directory, "source");
                        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        preparation.CancelAfter(TimeSpan.FromSeconds(150));
                        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                        await file.WriteAsync(prefix, preparation.Token);
                        var total = (long)prefix.Length;
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await input.ReadAsync(buffer, preparation.Token)) != 0)
                        {
                            total = checked(total + read);
                            if (total > MaximumInputBytes) throw new IOException("The audio conversion source exceeds the supported size.");
                            await file.WriteAsync(buffer.AsMemory(0, read), preparation.Token);
                        }
                        var expected = StreamDurationGuidance.DeclaredLength(original) ?? original.Headers.ContentLength;
                        if (total == 0 || expected is { } length && total != length)
                            throw new IOException("The audio conversion source ended before its declared length.");
                    }
                }
                if (path != null && original.Headers.ContentLength is { } declared && new FileInfo(path).Length != declared)
                    throw new IOException("The complete audio conversion source does not match its declared length.");
                if (directory != null && path != null)
                {
                    var verified = await new ProviderAudioVerifier(gate).VerifyFileAsync(path, plan.Source, durationSeconds, token);
                    ValidateOffset(ticks, verified.DurationSeconds);
                }
                capacity ??= await gate.EnterAsync(token);
                var start = new ProcessStartInfo("ffmpeg")
                { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = path == null, CreateNoWindow = true };
                List<string> arguments = ["-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1", "-filter_complex_threads", "1", "-protocol_whitelist", "file,pipe", "-probesize", "32768", "-analyzeduration", "0", "-xerror", "-err_detect", "explode", "-i", path ?? "pipe:0"];
                if (ticks > 0) arguments.AddRange(["-ss", (ticks / (double)TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture)]);
                arguments.AddRange(["-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-nostats", "-stats_period", "1", "-progress", "pipe:2", "-threads", "1", "-c:a", plan.Encoder]);
                if (plan.Encoder == "aac") arguments.AddRange(["-profile:a", "aac_low"]);
                if (plan.Media.Bitrate is { } bitrate) arguments.AddRange(["-b:a", bitrate.ToString(CultureInfo.InvariantCulture)]);
                if (plan.Media.SampleRate is { } sampleRate) arguments.AddRange(["-ar", sampleRate.ToString(CultureInfo.InvariantCulture)]);
                if (plan.Media.Channels is { } channels) arguments.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);
                if (plan.Media.BitDepth is { } bitDepth && plan.Media.Codec is "flac" or "alac")
                    arguments.AddRange(["-sample_fmt", bitDepth <= 16 ? "s16" : "s32"]);
                if (plan.Media.Container == "mp4") arguments.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "250000"]);
                arguments.AddRange(["-flush_packets", "1", "-f", plan.Muxer, "pipe:1"]);
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                process = Process.Start(start) ?? throw new IOException("The audio conversion process could not start.");
                var remaining = durationSeconds is > 0 && double.IsFinite(durationSeconds.Value)
                    ? durationSeconds.Value - ticks / (double)TimeSpan.TicksPerSecond : (double?)null;
                return new(process, original, lifetime, capacity, directory, input, prefix, path != null, remaining);
            }
            catch
            {
                if (process != null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); process.Dispose(); }
                capacity?.Dispose(); lifetime.Dispose();
                if (directory != null) Directory.Delete(directory, true);
                throw;
            }
        }

        private async Task PumpAsync(Stream input, byte[] initial)
        {
            try
            {
                if (initial.Length > 0) await process.StandardInput.BaseStream.WriteAsync(initial, lifetime.Token);
                long copied = initial.Length;
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, lifetime.Token)) != 0)
                {
                    copied = checked(copied + count);
                    await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
                }
                var expected = StreamDurationGuidance.DeclaredLength(owner) ?? owner.Headers.ContentLength;
                if (expected is { } length && copied != length)
                    throw new IOException("The audio conversion source ended before its declared length.");
            }
            finally { process.StandardInput.Close(); }
        }

        private async Task ReadProgressAsync()
        {
            var buffer = new byte[4096];
            var line = new byte[256];
            var used = 0;
            var overflow = false;
            int count;
            while ((count = await process.StandardError.BaseStream.ReadAsync(buffer, lifetime.Token)) != 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        if (!overflow) ObserveProgress(line.AsSpan(0, used));
                        used = 0;
                        overflow = false;
                    }
                    else if (used < line.Length) line[used++] = value;
                    else overflow = true;
                }
            }
            if (!overflow && used > 0) ObserveProgress(line.AsSpan(0, used));
        }

        private void ObserveProgress(ReadOnlySpan<byte> line)
        {
            if (line.EndsWith("\r"u8)) line = line[..^1];
            if (line.SequenceEqual("progress=end"u8)) progressEnded = true;
            if (line.StartsWith("out_time_us="u8) && Utf8Parser.TryParse(line[12..], out long microseconds, out var consumed) &&
                consumed == line.Length - 12 && microseconds >= 0)
                outputMicroseconds = Math.Max(outputMicroseconds, microseconds);
        }

        internal async Task PrepareAsync(CancellationToken token)
        {
            var buffer = new byte[4096];
            var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token);
            if (read == 0) { await CompleteAsync(); throw new IOException("The audio conversion produced no audio."); }
            prefix = buffer[..read];
        }

        private void Stop()
        {
            lock (lifecycle)
            {
                if (closed) return;
                try { owner.Dispose(); } catch { }
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); } catch (InvalidOperationException) { }
                capacity.Dispose();
            }
        }

        private async Task CompleteAsync()
        {
            try
            {
                await process.WaitForExitAsync(lifetime.Token);
                await Task.WhenAll(pump, stderr);
                if (process.ExitCode != 0) throw new IOException("The audio conversion could not finish decoding its source.");
                if (expectedRemainingSeconds is { } expected && (!progressEnded || outputMicroseconds < 0 ||
                    Math.Abs(outputMicroseconds / 1_000_000d - expected) > Math.Max(0.25, expected * 0.02)))
                    throw new IOException("The audio conversion did not contain the expected audio timeline.");
            }
            finally { await CleanupAsync(); }
        }

        private Task CleanupAsync()
        {
            lock (lifecycle) return cleanup ??= CleanupCoreAsync();
        }
        private async Task CleanupCoreAsync()
        {
            await aborted.DisposeAsync();
            lifetime.Cancel();
            Stop();
            try { await Task.WhenAll(pump, stderr); } catch { }
            lock (lifecycle) { closed = true; process.Dispose(); }
            lifetime.Dispose();
            if (directory != null) Directory.Delete(directory, true);
        }

        public override bool CanRead => !closed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            if (closed) return 0;
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (prefixPosition < prefix.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var prefixCount = Math.Min(buffer.Length, prefix.Length - prefixPosition);
                    prefix.AsMemory(prefixPosition, prefixCount).CopyTo(buffer); prefixPosition += prefixCount; return prefixCount;
                }
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, reading.Token);
                if (count == 0) await CompleteAsync();
                return count;
            }
            catch { await CleanupAsync(); throw; }
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) CleanupAsync().GetAwaiter().GetResult(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await CleanupAsync(); GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(bool Streamable, byte[] Prefix)> ReadMoviePrefixAsync(Stream source, CancellationToken token)
    {
        using var prefix = new MemoryStream();
        while (prefix.Length < 65536)
        {
            var offset = (int)prefix.Length;
            var header = new byte[8];
            var read = 0;
            while (read < header.Length)
            {
                var count = await source.ReadAsync(header.AsMemory(read), token);
                if (count == 0) { prefix.Write(header, 0, read); return (false, prefix.ToArray()); }
                read += count;
            }
            prefix.Write(header);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (type is 0x6d6f6f76 or 0x6d6f6f66) return (true, prefix.ToArray());
            if (type == 0x6d646174 || size < 8 || size > 65536 - offset) return (false, prefix.ToArray());
            var remaining = new byte[(int)size - 8];
            read = 0;
            while (read < remaining.Length)
            {
                var count = await source.ReadAsync(remaining.AsMemory(read), token);
                if (count == 0) { prefix.Write(remaining, 0, read); return (false, prefix.ToArray()); }
                read += count;
            }
            prefix.Write(remaining);
        }
        return (false, prefix.ToArray());
    }
}
