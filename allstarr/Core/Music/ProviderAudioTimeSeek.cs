using System.Diagnostics;
using System.Globalization;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

internal static class ProviderAudioTimeSeek
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private static readonly MediaConversionGate DefaultGate = new();

    internal static async Task ApplyAsync(HttpResponseMessage response, HttpResponse outgoing, long ticks,
        MediaConversionGate? conversions, CancellationToken cancellationToken, ProviderMediaFormat? sourceMedia = null,
        double? durationSeconds = null)
    {
        if (ticks == 0) return;
        if (ticks < 0) throw new ProviderAudioOffsetException("Invalid audio time offset.", HttpStatusCode.BadRequest);
        var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var input = ManagedTrackCacheService.CompletedFilePath(source);
        await using var materialized = input == null && source.CanSeek && ManagedTrackCacheService.SeekRung(response.Content) == null
            ? new FileStream(Path.GetTempFileName(), FileMode.Truncate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose) : null;
        if (materialized != null)
        {
            var buffer = new byte[81920];
            int count;
            long copied = 0;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                copied += count;
                if (copied > MaximumBytes) throw new IOException("The time-seek source exceeds the supported size.");
                await materialized.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            await materialized.FlushAsync(cancellationToken);
            input = materialized.Name;
        }
        if (input == null)
        {
            ManagedTrackCacheService.PrepareForConversion(response.Content);
            var known = PrefetchedStream.Facts(response.Content);
            if (sourceMedia == null || sourceMedia.SampleRate == null || sourceMedia.Channels == null)
            {
                if (!await PrefetchedStream.PrepareAsync(response, cancellationToken))
                    throw new IOException("The audio source could not be opened for time seeking.");
                known = PrefetchedStream.Facts(response.Content) ?? known;
            }
            sourceMedia = known?.Media ?? sourceMedia;
            durationSeconds ??= known?.DurationSeconds;
            if (sourceMedia == null) throw new IOException("The audio format could not be verified for time seeking.");
            var request = new ProviderAudioDeliveryRequest
            {
                Formats = [new([sourceMedia.Container], [sourceMedia.Codec], ["http"])],
                EnableDirectPlay = false,
                EnableDirectStream = false
            };
            var conversion = ProviderAudioTranscoder.Plan(request, sourceMedia)
                ?? throw new ProviderAudioProfileException("The source format does not support streaming time seeking.");
            await ProviderAudioTranscoder.ApplyAsync(response, outgoing, conversion, conversions, cancellationToken, ticks, durationSeconds);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(150));
        var token = deadline.Token;
        using var capacity = await (conversions ?? DefaultGate).EnterAsync(token);
        var directory = Directory.CreateTempSubdirectory("allstarr-time-seek-").FullName;
        var retained = false;
        try
        {
            if (response.Content.Headers.ContentLength is { } length && new FileInfo(input).Length != length)
                throw new IOException("The time-seek source does not match its declared length.");
            token.ThrowIfCancellationRequested();
            var facts = await AudioMediaFacts.ReadFileAsync(input, token);
            if (facts != null && durationSeconds is > 0) facts = facts with { DurationSeconds = durationSeconds };
            if (facts?.DurationSeconds is not > 0)
                throw new IOException("The audio timeline could not be verified for time seeking.");
            if (facts.Media.Container == "aac")
                facts = facts with { DurationSeconds = await PacketDurationAsync(input, token) };
            var start = ticks / (double)TimeSpan.TicksPerSecond;
            var duration = facts.DurationSeconds.Value;
            if (start >= duration)
                throw new ProviderAudioOffsetException("The requested time offset is outside the audio timeline.", HttpStatusCode.RequestedRangeNotSatisfiable);
            var container = ProviderAudioDeliveryRequest.Container(facts.Media.Container);
            var output = Path.Combine(directory, "offset." + (container == "mp4" ? "m4a" : container));
            var lossless = facts.Media.Codec is "flac" or "alac" || facts.Media.Codec.StartsWith("pcm_", StringComparison.Ordinal);
            var tolerance = lossless ? 2d / (facts.Media.SampleRate ?? 44100) : 0.25;
            double? observedDuration = null;
            TrackMediaFacts? seekFacts = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var codec = attempt == 0 ? "copy" : facts.Media.Codec switch
                {
                    "mp3" => "libmp3lame",
                    "opus" => "libopus",
                    "vorbis" => "libvorbis",
                    _ => facts.Media.Codec
                };
                // FLAC frame timestamps and STREAMINFO may retain the original timeline on a packet copy.
                // Re-encoding the same codec is only needed when the remux cannot describe the remaining audio.
                try
                {
                    await RunAsync(input, output, start, container, codec, token);
                    seekFacts = await AudioMediaFacts.ReadFileAsync(output, token);
                    if (seekFacts?.Media.Container == "aac")
                        seekFacts = seekFacts with { DurationSeconds = await PacketDurationAsync(output, token) };
                    observedDuration = seekFacts?.DurationSeconds;
                    if (seekFacts?.DurationSeconds is > 0 &&
                        Math.Abs(seekFacts.DurationSeconds.Value - (duration - start)) <= tolerance) break;
                    seekFacts = null;
                }
                catch (IOException) when (attempt == 0) { }
            }
            if (seekFacts == null) throw new IOException(FormattableString.Invariant($"The time-seek output could not be verified ({facts.Media.Codec}; expected {duration - start:0.######} seconds, observed {observedDuration:0.######})."));
            token.ThrowIfCancellationRequested();
            var stream = new SeekFile(output, directory);
            var replacement = new StreamContent(stream);
            replacement.Headers.ContentType = new(seekFacts.Media.MimeType);
            replacement.Headers.ContentLength = stream.Length;
            var original = response.Content;
            response.Content = replacement;
            original.Dispose();
            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Remove("ETag");
            response.Headers.Remove("Accept-Ranges");
            outgoing.Headers["X-Allstarr-Start-Seconds"] = start.ToString("0.#######", CultureInfo.InvariantCulture);
            outgoing.Headers["X-Content-Duration"] = duration.ToString("0.#######", CultureInfo.InvariantCulture);
            retained = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The audio time seek did not finish before its preparation timeout."); }
        finally
        {
            if (!retained) Directory.Delete(directory, true);
        }
    }

    private static async Task RunAsync(string input, string output, double seconds, string container, string codec, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        List<string> arguments = ["-v", "error", "-nostdin", "-y", "-threads", "1", "-protocol_whitelist", "file,pipe"];
        var offset = seconds.ToString("0.#######", CultureInfo.InvariantCulture);
        arguments.AddRange(container == "aac" ? ["-i", input, "-ss", offset] : ["-ss", offset, "-i", input]);
        if (container == "mp4") arguments.AddRange(["-movflags", "+faststart"]);
        arguments.AddRange(["-map", "0:a:0", "-map_metadata", "-1",
            "-map_chapters", "-1", "-threads", "1", "-c:a", codec, "-avoid_negative_ts", "make_zero", "-fs",
            MaximumBytes.ToString(CultureInfo.InvariantCulture), "-f", container == "aac" ? "adts" : container, output]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The audio seek process could not start.");
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), errors, stdout);
            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length is <= 0 or >= MaximumBytes)
                throw new IOException("The audio seek process could not produce complete audio.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(errors, stdout); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<double> PacketDurationAsync(string path, CancellationToken token)
    {
        // ADTS duration is a bitrate estimate. Count stored audio packet times before accepting a timeline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-threads", "1", "-protocol_whitelist", "file", "-select_streams", "a:0",
            "-show_entries", "packet=duration_time", "-of", "default=noprint_wrappers=1:nokey=1", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The audio packet timeline could not be opened.");
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            double seconds = 0;
            var count = 0;
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                if (++count > 1_000_000 || !double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
                    !double.IsFinite(duration) || duration is <= 0 or > 60)
                    throw new IOException("The audio packet timeline is invalid.");
                seconds += duration;
            }
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            if (process.ExitCode != 0 || seconds <= 0) throw new IOException("The audio packet timeline is incomplete.");
            return seconds;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await errors; } catch (OperationCanceledException) { }
        }
    }

    private sealed class SeekFile(string path, string directory) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous)
    {
        private int disposed;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) Directory.Delete(directory, true);
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Interlocked.Exchange(ref disposed, 1) == 0) Directory.Delete(directory, true);
            GC.SuppressFinalize(this);
        }
    }
}

public sealed class ProviderAudioOffsetException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
