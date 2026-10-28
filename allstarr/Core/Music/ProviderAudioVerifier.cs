using System.Diagnostics;
using System.Globalization;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

public interface IProviderAudioVerifier
{
    Task<ProviderOutcome<TrackMediaFacts>> VerifyPrefixAsync(HttpResponseMessage response, ProviderMediaFormat expected,
        double? recordingSeconds, CancellationToken token,
        Func<long, long?, CancellationToken, Task<HttpResponseMessage>>? openRange = null);
    Task<TrackMediaFacts> VerifyFileAsync(string path, ProviderMediaFormat expected, double? recordingSeconds, CancellationToken token, bool derivedDuration = false);
}

public sealed class ProviderAudioVerifier(MediaConversionGate? conversions = null) : IProviderAudioVerifier
{
    private const int PrefixBytes = 256 * 1024;
    private const int MaximumMoviePrefixBytes = 4 * 1024 * 1024;
    private static readonly MediaConversionGate DefaultGate = new();

    public async Task<ProviderOutcome<TrackMediaFacts>> VerifyPrefixAsync(HttpResponseMessage response,
        ProviderMediaFormat expected, double? recordingSeconds, CancellationToken token,
        Func<long, long?, CancellationToken, Task<HttpResponseMessage>>? openRange = null)
    {
        var range = response.Content.Headers.ContentRange;
        if (range?.From is > 0 || range?.To is < 4095)
        {
            if (openRange == null) return Invalid();
            using var prefix = await openRange(0, null, token);
            if (!prefix.IsSuccessStatusCode || prefix.Content.Headers.ContentRange?.From is > 0 ||
                response.Headers.ETag is { IsWeak: false } tag && !tag.Equals(prefix.Headers.ETag)) return Invalid();
            return await VerifyPrefixAsync(prefix, expected, recordingSeconds, token, openRange);
        }
        var content = response.Content;
        var input = await content.ReadAsStreamAsync(token);
        var total = range?.Length ?? content.Headers.ContentLength;
        var maximum = (int)Math.Min(PrefixBytes, content.Headers.ContentLength ?? PrefixBytes);
        if (maximum <= 0) return Invalid();
        var bytes = new byte[maximum];
        var count = 0;
        var ended = false;
        Task<int>? pending = null;
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        var deadline = Task.Delay(Timeout.Infinite, budget.Token);
        while (count < bytes.Length)
        {
            pending = input.ReadAsync(bytes.AsMemory(count), token).AsTask();
            if (await Task.WhenAny(pending, deadline) != pending) break;
            var read = await pending;
            pending = null;
            if (read == 0) { ended = true; break; }
            count += read;
            if (count == bytes.Length)
            {
                var required = MoviePrefixBytes(bytes.AsSpan(0, count), total);
                var available = (int)Math.Min(required, content.Headers.ContentLength ?? required);
                if (available > bytes.Length) Array.Resize(ref bytes, available);
            }
        }
        var buffered = new BufferedPrefix(input, content, bytes, count, pending);
        PrefetchedStream.ReplaceContent(response, buffered, null);
        if (count == 0) return Invalid();
        if (MoviePrefixBytes(bytes.AsSpan(0, count), total) > count &&
            range is { To: { } end, Length: { } length } && end < length - 1 && openRange != null)
        {
            using var expanded = await openRange(0, null, token);
            if (!expanded.IsSuccessStatusCode || expanded.Content.Headers.ContentRange?.From is > 0 ||
                expanded.Content.Headers.ContentRange is { To: { } last, Length: { } size } && last < size - 1 ||
                (expanded.Content.Headers.ContentRange?.Length ?? expanded.Content.Headers.ContentLength) is { } expandedLength && expandedLength != total ||
                response.Headers.ETag is { IsWeak: false } tag && !tag.Equals(expanded.Headers.ETag)) return Invalid();
            var result = await VerifyPrefixAsync(expanded, expected, recordingSeconds, token, openRange);
            if (result.IsSuccess) PrefetchedStream.ReplaceContent(response, buffered, result.RequireValue());
            return result;
        }
        var directory = Directory.CreateTempSubdirectory("allstarr-audio-check-").FullName;
        try
        {
            var path = Path.Combine(directory, "prefix.audio");
            await File.WriteAllBytesAsync(path, bytes.AsMemory(0, count).ToArray(), token);
            var header = AudioMediaFacts.ReadPrefix(bytes.AsSpan(0, count), total);
            if (header?.Media.Container == "mp4" && total > count && !HasMovieHeader(bytes.AsSpan(0, count)) &&
                !await AddMovieTailAsync(path, response, total.Value, openRange, token))
                return await VerifyBufferedFileAsync(response, expected, recordingSeconds, openRange, token);
            using var capacity = await (conversions ?? DefaultGate).EnterAsync(token);
            using var probing = CancellationTokenSource.CreateLinkedTokenSource(token);
            probing.CancelAfter(TimeSpan.FromSeconds(2));
            var measured = header is { Media.Codec: not "unknown" } ? header : await AudioMediaFacts.ReadFileAsync(path, probing.Token);
            if (measured == null || !Compatible(expected, measured.Media)) return Invalid();
            var complete = ended || total is { } size && count == size;
            var duration = header?.DurationSeconds ?? (complete || header?.Media.Container == "mp4" ? measured.DurationSeconds : null);
            if (!Close(duration, recordingSeconds)) return Invalid();
            var decoded = await DecodeAsync(path, complete, measured.Media, token);
            if (decoded <= 0 || complete && !Close(decoded, recordingSeconds ?? duration)) return Invalid();
            var media = measured.Media;
            if (duration is > 0 && total is > 0 && media.Codec is "flac" or "alac")
                media = new(media.MimeType, media.Container, media.Codec,
                    (int)Math.Min(int.MaxValue, total.Value * 8d / duration.Value), media.SampleRate, media.BitDepth, media.Channels);
            var facts = new TrackMediaFacts(media, total, duration, measured.ReplayGain, measured.Tags ?? AudioMediaFacts.ReadTags(path, media));
            PrefetchedStream.ReplaceContent(response, buffered, facts);
            response.Content.Headers.ContentType = new(media.MimeType);
            return ProviderOutcome<TrackMediaFacts>.Success(facts);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        { return Invalid(); }
        finally { Directory.Delete(directory, true); }
    }

    private static int MoviePrefixBytes(ReadOnlySpan<byte> bytes, long? total)
    {
        if (bytes.Length < 12 || !bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return bytes.Length;
        var offset = 0;
        while (offset <= bytes.Length - 8)
        {
            ulong length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            var header = 8;
            if (length == 1)
            {
                if (offset > bytes.Length - 16) return bytes.Length;
                length = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[(offset + 8)..]);
                header = 16;
            }
            if (length < (ulong)header || length > long.MaxValue || total is { } size && (size < offset || length > (ulong)(size - offset))) return bytes.Length;
            if (bytes.Slice(offset + 4, 4).SequenceEqual("moov"u8))
                return (int)Math.Min(total ?? MaximumMoviePrefixBytes,
                    Math.Min(MaximumMoviePrefixBytes, Math.Max(PrefixBytes,
                        (long)Math.Min(length, (ulong)MaximumMoviePrefixBytes) + offset + PrefixBytes / 2)));
            if (length > (ulong)(bytes.Length - offset)) return bytes.Length;
            offset += (int)length;
        }
        return bytes.Length;
    }

    private static bool HasMovieHeader(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        while (offset <= bytes.Length - 8)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (bytes.Slice(offset + 4, 4).SequenceEqual("moov"u8)) return true;
            if (length < 8 || length > bytes.Length - offset) return false;
            offset += (int)length;
        }
        return false;
    }

    private static async Task<bool> AddMovieTailAsync(string path, HttpResponseMessage source, long length,
        Func<long, long?, CancellationToken, Task<HttpResponseMessage>>? openRange, CancellationToken token)
    {
        if (openRange == null || length > 512L * 1024 * 1024) return false;
        var offset = Math.Max(0, length - 1024 * 1024);
        using var tail = await openRange(offset, length - 1, token);
        if (tail.StatusCode != HttpStatusCode.PartialContent || tail.Content.Headers.ContentRange is not { } range ||
            range.From != offset || range.To != length - 1 || range.Length != length ||
            source.Headers.ETag is { IsWeak: false } tag && !tag.Equals(tail.Headers.ETag)) return false;
        if (!StreamByteRanges.HasValidContentRange(range, tail.Content.Headers.ContentLength)) return false;
        await using var input = await tail.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        output.Position = offset;
        var remaining = length - offset;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (read == 0) return false;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            remaining -= read;
        }
        return true;
    }

    private async Task<ProviderOutcome<TrackMediaFacts>> VerifyBufferedFileAsync(HttpResponseMessage response,
        ProviderMediaFormat expected, double? recordingSeconds,
        Func<long, long?, CancellationToken, Task<HttpResponseMessage>>? openRange, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        using var full = response.Content.Headers.ContentRange is { To: { } end, Length: { } length } && end < length - 1
            ? openRange == null ? null : await openRange(0, null, timeout.Token) : null;
        if (full != null && !(full.StatusCode == HttpStatusCode.OK && full.Content.Headers.ContentRange == null ||
            full.StatusCode == HttpStatusCode.PartialContent && full.Content.Headers.ContentRange is { From: 0, To: { } last, Length: { } fullLength } &&
            last == fullLength - 1 && StreamByteRanges.HasValidContentRange(full.Content.Headers.ContentRange, full.Content.Headers.ContentLength))) return Invalid();
        var content = (full ?? response).Content;
        var path = Path.GetTempFileName();
        var retained = false;
        try
        {
            await using (var output = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await using var input = await content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    written += read;
                    if (written > 512L * 1024 * 1024) throw new IOException("The audio verification input exceeds its limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                if (written == 0 || content.Headers.ContentLength is { } size && size != written) return Invalid();
            }
            var facts = await VerifyFileAsync(path, expected, recordingSeconds, timeout.Token);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var original = response.Content;
            PrefetchedStream.ReplaceContent(response, file, facts);
            original.Dispose();
            response.Content.Headers.ContentType = new(facts.Media.MimeType);
            response.Content.Headers.ContentLength = facts.Size;
            response.Content.Headers.ContentRange = null;
            response.StatusCode = HttpStatusCode.OK;
            retained = true;
            return ProviderOutcome<TrackMediaFacts>.Success(facts);
        }
        catch { File.Delete(path); throw; }
        finally { if (!retained) File.Delete(path); }
    }

    public async Task<TrackMediaFacts> VerifyFileAsync(string path, ProviderMediaFormat expected,
        double? recordingSeconds, CancellationToken token, bool derivedDuration = false)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        waiting.CancelAfter(TimeSpan.FromMinutes(5));
        using var capacity = await WaitForCapacityAsync(conversions ?? DefaultGate, waiting.Token, token);
        var facts = await AudioMediaFacts.ReadFileAsync(path, token);
        if (facts == null || !Compatible(expected, facts.Media)) throw new IOException("The completed file is not the selected audio format.");
        await using var input = File.OpenRead(path);
        var prefix = new byte[4096];
        var read = await input.ReadAsync(prefix, token);
        var headerDuration = AudioMediaFacts.ReadPrefix(prefix.AsSpan(0, read), input.Length)?.DurationSeconds;
        var decoded = await DecodeAsync(path, true, facts.Media, token);
        var completeHeader = derivedDuration || headerDuration is not > 0 || Math.Abs(decoded - headerDuration.Value) <= 0.5 / facts.Media.SampleRate!.Value;
        if (decoded <= 0 || !Close(decoded, recordingSeconds ?? facts.DurationSeconds) || !completeHeader ||
            !Close(facts.DurationSeconds, recordingSeconds))
            throw new IOException("The completed file does not contain the expected audio timeline.");
        return AudioMediaFacts.WithDecodedDuration(facts, new FileInfo(path).Length, decoded);
    }

    private static async Task<IDisposable> WaitForCapacityAsync(MediaConversionGate gate, CancellationToken token, CancellationToken caller)
    {
        while (true)
        {
            try { return await gate.EnterAsync(token); }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new LocalCapacityException(); }
            catch (LocalCapacityException)
            {
                try { await Task.Delay(50, token); }
                catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new LocalCapacityException(); }
            }
        }
    }

    private static bool Compatible(ProviderMediaFormat expected, ProviderMediaFormat actual) =>
        actual.Codec != "unknown" && actual.SampleRate is > 0 && actual.Channels is > 0 &&
        (expected.Container == "unknown" || ProviderAudioDeliveryRequest.Container(expected.Container) == ProviderAudioDeliveryRequest.Container(actual.Container)) &&
        (expected.Codec.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
         expected.Codec.Equals(actual.Codec, StringComparison.OrdinalIgnoreCase) ||
         expected.Codec.Equals("wav", StringComparison.OrdinalIgnoreCase) && actual.Codec.StartsWith("pcm_", StringComparison.Ordinal));

    private static bool Close(double? actual, double? expected) => actual is not > 0 || expected is not > 0 ||
        Math.Abs(actual.Value - expected.Value) <= Math.Max(0.25, expected.Value * 0.02);

    private static ProviderOutcome<TrackMediaFacts> Invalid() =>
        ProviderOutcome<TrackMediaFacts>.Failure(new(ProviderErrorKind.IncompatibleMedia));

    private static async Task<double> DecodeAsync(string path, bool complete, ProviderMediaFormat media, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(complete ? TimeSpan.FromSeconds(150) : TimeSpan.FromSeconds(2));
        var start = new ProcessStartInfo("ffmpeg")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        List<string> arguments = ["-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1", "-protocol_whitelist", "file,pipe"];
        if (complete) arguments.AddRange(["-xerror", "-err_detect", "explode"]);
        arguments.AddRange(["-i", path, "-map", "0:a:0"]);
        if (!complete) arguments.AddRange(["-t", "0.05"]);
        var bytesPerSecond = media.SampleRate!.Value * (long)media.Channels!.Value * 2;
        arguments.AddRange(["-threads", "1", "-ar", media.SampleRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", media.Channels.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The audio verification process could not start.");
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        long decodedBytes = 0;
        try
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                decodedBytes += count;
                if (decodedBytes > bytesPerSecond * 86400) throw new IOException("The decoded audio timeline exceeds its limit.");
            }
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), errors);
            if (process.ExitCode != 0) throw new IOException("The audio could not be decoded completely.");
            return decodedBytes / (double)bytesPerSecond;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await errors; } catch (OperationCanceledException) { }
        }
    }

    private sealed class BufferedPrefix(Stream source, HttpContent owner, byte[] bytes, int available, Task<int>? pending) : Stream
    {
        private int position;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty) return 0;
            if (position == available && pending != null)
            {
                available += await pending.WaitAsync(token);
                pending = null;
            }
            if (position < available)
            {
                var count = Math.Min(buffer.Length, available - position);
                bytes.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }
            return await source.ReadAsync(buffer, token);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Dispose();
                if (pending != null) _ = pending.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
