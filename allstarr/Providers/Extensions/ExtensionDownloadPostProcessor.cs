using allstarr.Services.Common;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace allstarr.Core.Extensions;

internal sealed record ExtensionDecryptedMedia(string Container, string Codec);

internal sealed record ExtensionMediaProcessingDiagnostic(string Operation, string Step, string Failure, int? ExitCode,
    string Stderr, long? InputBytes, long? OutputBytes, double? DeclaredDuration, double? InputDuration, double? OutputDuration, long ElapsedMs);

internal delegate Task<string> ExtensionMediaProcessRunner(string executable, IEnumerable<string> arguments,
    string? output, long maximumBytes, CancellationToken token);

internal sealed class ExtensionDownloadPostProcessor
{
    private readonly TimeSpan timeout;
    private readonly ExtensionMediaProcessRunner run;
    internal ExtensionDownloadPostProcessor(TimeSpan timeout, ExtensionMediaProcessRunner? runner = null, MediaConversionGate? conversions = null)
    {
        this.timeout = timeout;
        var gate = conversions ?? new MediaConversionGate();
        var spawn = runner ?? RunAsync;
        run = (executable, arguments, output, maximumBytes, token) => executable == "ffmpeg"
            ? gate.RunAsync(() => spawn(executable, arguments, output, maximumBytes, token), token)
            : spawn(executable, arguments, output, maximumBytes, token);
    }
    internal const string FailureMessage = "Download decryption failed.";
    internal const string ConversionFailureMessage = "Download container conversion failed.";
    internal const string ToolsUnavailableMessage = "Download media tools are unavailable.";
    internal const string DiagnosticKey = "MediaProcessing";

    private sealed class ProcessingState
    {
        public readonly long Started = Stopwatch.GetTimestamp();
        public string Operation = "decryption";
        public string Step = "descriptor";
        public long? InputBytes;
        public long? OutputBytes;
        public double? DeclaredDuration;
        public double? InputDuration;
        public double? OutputDuration;
    }

    public async Task<ExtensionDecryptedMedia> ProcessAsync(string input, string resultJson, long maximumBytes,
        CancellationToken cancellationToken)
    {
        var conversion = false;
        var state = new ProcessingState();
        try
        {
            if (resultJson.Length > 64 * 1024) throw new InvalidDataException();
            using var result = JsonDocument.Parse(resultJson, new JsonDocumentOptions { MaxDepth = 16 });
            var value = result.RootElement;
            conversion = value.TryGetProperty("requires_container_conversion", out var flag) && flag.ValueKind == JsonValueKind.True;
            state.Operation = conversion ? "conversion" : "decryption";
            var encrypted = new[] { "decryption", "decryption_key", "encrypted", "needs_decryption", "encryption_scheme" }
                .Any(name => value.TryGetProperty(name, out var marker) && marker.ValueKind is not
                    (JsonValueKind.Null or JsonValueKind.False) && marker.ToString().Length > 0);
            if (encrypted || !conversion)
            {
                state.Operation = "decryption";
                var decrypted = await DecryptAsync(input, resultJson, maximumBytes, cancellationToken, state);
                if (!conversion && decrypted.Codec != "flac") return decrypted;
                conversion = true;
            }
            state.Operation = "conversion";
            return await ConvertContainerAsync(input, value, maximumBytes, cancellationToken, state);
        }
        catch (LocalCapacityException) { throw; }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            exception.Data[DiagnosticKey] = Diagnostic(state, exception, "canceled");
            throw;
        }
        catch (Exception exception)
        {
            var missingTool = exception is Win32Exception { NativeErrorCode: 2 };
            var error = new InvalidDataException(missingTool ? ToolsUnavailableMessage :
                state.Operation == "conversion" ? ConversionFailureMessage : FailureMessage);
            error.Data[DiagnosticKey] = Diagnostic(state, exception,
                exception is OperationCanceledException ? "timeout" : exception.GetType().Name);
            throw error;
        }
    }

    private static ExtensionMediaProcessingDiagnostic Diagnostic(ProcessingState state, Exception exception, string failure) =>
        new(state.Operation, state.Step, failure, exception.Data["MediaExitCode"] as int?, exception.Data["MediaStderr"] as string ?? "",
            state.InputBytes, state.OutputBytes, state.DeclaredDuration, state.InputDuration, state.OutputDuration,
            (long)Stopwatch.GetElapsedTime(state.Started).TotalMilliseconds);

    private async Task<ExtensionDecryptedMedia> ConvertContainerAsync(string input, JsonElement value, long maximumBytes,
        CancellationToken cancellationToken, ProcessingState state)
    {
        state.Step = "descriptor";
        var output = Path.Combine(Path.GetDirectoryName(input)!, $"convert-{Guid.NewGuid():N}.flac");
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            state.Step = "descriptor";
            var codec = Text(value, "audio_codec");
            if (codec is not (null or "" or "flac" or "alac") ||
                Path.GetExtension(input).ToLowerInvariant() is not (".mp4" or ".m4a")) throw new InvalidDataException();
            state.Step = "input-size";
            state.InputBytes = new FileInfo(input).Length;
            ValidateSize(input, maximumBytes);
            state.Step = "declared-duration";
            state.DeclaredDuration = Duration(value);
            state.Step = "input-probe";
            var before = await ProbeAsync(input, null, token);
            state.Step = "input-validation";
            if (string.IsNullOrEmpty(codec)) codec = Text(before.GetProperty("streams")[0], "codec_name");
            if (codec is not ("flac" or "alac")) throw new InvalidDataException();
            state.InputDuration = ProbeDuration(before);
            Validate(before, codec, state.DeclaredDuration);
            if (codec == "alac") return new("m4a", "alac");
            var expectedDuration = state.DeclaredDuration ?? state.InputDuration;
            state.Step = "input-audio-properties";
            var audio = ProbeAudioProperties(before);
            state.Step = "encode";
            await run("ffmpeg", ["-v", "error", "-nostdin", "-y", "-threads", "1", "-protocol_whitelist", "file",
                "-f", "mov", "-i", input, "-map", "0:a:0", "-map_metadata", "-1", "-map_chapters", "-1",
                "-threads", "1", "-c:a", "flac", "-compression_level", "5", "-sample_fmt", audio.Bits == 16 ? "s16" : "s32",
                "-bits_per_raw_sample", audio.Bits.ToString(CultureInfo.InvariantCulture),
                "-fs", maximumBytes.ToString(CultureInfo.InvariantCulture), "-f", "flac", output],
                output, maximumBytes, token);
            state.Step = "output-size";
            state.OutputBytes = new FileInfo(output).Length;
            ValidateSize(output, maximumBytes);
            state.Step = "output-magic";
            using (var stream = File.OpenRead(output))
            {
                var magic = new byte[4];
                if (stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) != magic.Length ||
                    !magic.AsSpan().SequenceEqual("fLaC"u8)) throw new InvalidDataException();
            }
            state.Step = "output-probe";
            var after = await ProbeAsync(output, null, token, "flac");
            state.Step = "output-validation";
            state.OutputDuration = ProbeDuration(after);
            Validate(after, codec, expectedDuration, "flac");
            state.Step = "output-audio-properties";
            if (ProbeAudioProperties(after) != audio) throw new InvalidDataException();
            state.Step = "output-decode";
            await run("ffmpeg", ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-threads", "1",
                "-protocol_whitelist", "file", "-f", "flac", "-i", output, "-map", "0:a:0", "-threads", "1", "-f", "null", "-"],
                null, maximumBytes, token);
            state.Step = "input-hash";
            var beforeHash = await AudioHashAsync(input, "mov", audio.Bits, token);
            state.Step = "output-hash";
            var afterHash = await AudioHashAsync(output, "flac", audio.Bits, token);
            state.Step = "hash-comparison";
            if (!string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            token.ThrowIfCancellationRequested();
            state.Step = "replace-input";
            File.Move(output, input, overwrite: true);
            return new("flac", "flac");
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch { }
        }
    }

    private static void ValidateSize(string path, long maximumBytes)
    {
        var size = new FileInfo(path).Length;
        if (size < 1 || size > maximumBytes) throw new InvalidDataException();
    }

    private async Task<ExtensionDecryptedMedia> DecryptAsync(string input, string resultJson, long maximumBytes,
        CancellationToken cancellationToken, ProcessingState state)
    {
        state.Step = "descriptor";
        var output = Path.Combine(Path.GetDirectoryName(input)!, $"decrypt-{Guid.NewGuid():N}.m4a");
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (resultJson.Length > 64 * 1024) throw new InvalidDataException();
            using var result = JsonDocument.Parse(resultJson, new JsonDocumentOptions { MaxDepth = 16 });
            var value = result.RootElement;
            if (value.TryGetProperty("decryption", out var encryption) && encryption.ValueKind is not
                (JsonValueKind.Object or JsonValueKind.Null)) throw new InvalidDataException();
            var descriptor = value.TryGetProperty("decryption", out var item) && item.ValueKind == JsonValueKind.Object ? item : value;
            var key = Text(descriptor, "key") ?? Text(value, "decryption_key");
            var duplicateKey = Text(value, "decryption_key");
            var strategy = Text(descriptor, "strategy");
            var scheme = Text(descriptor, "scheme") ?? Text(value, "encryption_scheme");
            var keyId = Text(descriptor, "key_id") ?? Text(value, "key_id");
            var codec = Text(value, "audio_codec");
            if (!Hex128(key) || (duplicateKey is { Length: > 0 } && !string.Equals(key, duplicateKey, StringComparison.OrdinalIgnoreCase)) ||
                (keyId is { Length: > 0 } && !Hex128(keyId)) ||
                (strategy != null && strategy != "ffmpeg.mov_key") ||
                (scheme != null && scheme is not ("cenc" or "cenc-aes-ctr")) ||
                (Text(descriptor, "input_format") is { } format && format is not ("mov" or "mp4" or "m4a")) ||
                value.TryGetProperty("license_url", out _) || descriptor.TryGetProperty("license_url", out _) ||
                codec is not ("aac" or "alac" or "flac" or "opus" or "eac3" or "ac4") ||
                Path.GetExtension(input).ToLowerInvariant() is not (".mp4" or ".m4a"))
                throw new InvalidDataException();
            state.Step = "input-size";
            state.InputBytes = new FileInfo(input).Length;
            ValidateSize(input, maximumBytes);
            state.Step = "declared-duration";
            state.DeclaredDuration = Duration(value);
            state.Step = "input-probe";
            var before = await ProbeAsync(input, key, token);
            state.Step = "input-validation";
            state.InputDuration = ProbeDuration(before);
            Validate(before, codec, state.DeclaredDuration);
            var expectedDuration = state.DeclaredDuration ?? state.InputDuration;
            state.Step = "decrypt";
            await run("ffmpeg", ["-v", "error", "-nostdin", "-y", "-threads", "1", "-protocol_whitelist", "file",
                "-decryption_key", key!, "-f", "mov", "-i", input, "-map", "0:a:0", "-map_metadata", "-1", "-map_chapters", "-1",
                "-c", "copy", "-strict", "experimental", "-fs", maximumBytes.ToString(CultureInfo.InvariantCulture),
                "-f", "mp4", output], output, maximumBytes, token);
            state.Step = "output-size";
            state.OutputBytes = new FileInfo(output).Length;
            ValidateSize(output, maximumBytes);
            state.Step = "output-probe";
            var after = await ProbeAsync(output, null, token);
            state.Step = "output-validation";
            state.OutputDuration = ProbeDuration(after);
            Validate(after, codec, expectedDuration);
            // A remux can succeed with corrupt packets and a wrong key. Decode without retaining PCM.
            state.Step = "output-decode";
            await run("ffmpeg", ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-threads", "1",
                "-protocol_whitelist", "file", "-f", "mov", "-i", output, "-map", "0:a:0", "-threads", "1", "-f", "null", "-"],
                null, maximumBytes, token);
            token.ThrowIfCancellationRequested();
            state.Step = "replace-input";
            File.Move(output, input, overwrite: true);
            return new("m4a", codec!);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch { }
        }
    }

    private static bool Hex128(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
    private static string? Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        return item.GetString();
    }

    private static double? Duration(JsonElement value)
    {
        if (!value.TryGetProperty("duration_ms", out var item) && !value.TryGetProperty("duration", out item)) return null;
        if (!item.TryGetDouble(out var duration) || !double.IsFinite(duration) || duration < 0) throw new InvalidDataException();
        if (value.TryGetProperty("duration_ms", out _)) duration /= 1000;
        return duration > 0 ? duration : null;
    }

    private async Task<JsonElement> ProbeAsync(string path, string? key, CancellationToken token, string container = "mov")
    {
        List<string> arguments = ["-v", "error", "-threads", "1", "-protocol_whitelist", "file"];
        if (key != null) arguments.AddRange(["-decryption_key", key]);
        arguments.AddRange(["-f", container, "-i", path, "-show_entries", "stream=codec_type,codec_name,duration,sample_rate,channels,bits_per_raw_sample:format=format_name,duration", "-of", "json"]);
        using var document = JsonDocument.Parse(await run("ffprobe", arguments, null, 0, token));
        return document.RootElement.Clone();
    }

    private static (int SampleRate, int Channels, int Bits) ProbeAudioProperties(JsonElement probe)
    {
        var stream = probe.GetProperty("streams")[0];
        if (!int.TryParse(Text(stream, "sample_rate"), NumberStyles.None, CultureInfo.InvariantCulture, out var sampleRate) ||
            sampleRate <= 0 || !stream.GetProperty("channels").TryGetInt32(out var channels) || channels <= 0 ||
            !int.TryParse(Text(stream, "bits_per_raw_sample"), NumberStyles.None, CultureInfo.InvariantCulture, out var bits) ||
            bits is not (16 or 24)) throw new InvalidDataException();
        return (sampleRate, channels, bits);
    }

    private async Task<string> AudioHashAsync(string path, string container, int bits, CancellationToken token)
    {
        // Hash full-precision PCM so 24-bit differences cannot disappear through a 16-bit decode.
        var hash = (await run("ffmpeg", ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-threads", "1",
            "-protocol_whitelist", "file", "-f", container, "-i", path, "-map", "0:a:0", "-threads", "1",
            "-c:a", bits == 16 ? "pcm_s16le" : "pcm_s24le", "-f", "md5", "-"], null, 0, token)).Trim();
        if (hash.Length != 36 || !hash.StartsWith("MD5=", StringComparison.Ordinal) || !hash[4..].All(char.IsAsciiHexDigit))
            throw new InvalidDataException();
        return hash;
    }

    private static double ProbeDuration(JsonElement probe)
    {
        var format = probe.GetProperty("format");
        if (!double.TryParse(Text(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
            !double.IsFinite(duration) || duration <= 0) throw new InvalidDataException();
        return duration;
    }

    private static void Validate(JsonElement probe, string? codec, double? expectedDuration, string container = "mp4")
    {
        var streams = probe.GetProperty("streams").EnumerateArray().ToArray();
        if (streams.Length != 1 || Text(streams[0], "codec_type") != "audio" || Text(streams[0], "codec_name") != codec ||
            !(Text(probe.GetProperty("format"), "format_name") ?? "").Split(',').Contains(container))
            throw new InvalidDataException();
        var duration = ProbeDuration(probe);
        if (expectedDuration.HasValue && Math.Abs(duration - expectedDuration.Value) > Math.Max(0.25, expectedDuration.Value * 0.02))
            throw new InvalidDataException();
    }

    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string? output,
        long maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidDataException();
        var stderr = ReadStderrTailAsync(process.StandardError, token);
        void AttachDetails(Exception exception)
        {
            if (stderr.IsCompletedSuccessfully) exception.Data["MediaStderr"] = stderr.GetAwaiter().GetResult();
            if (process.HasExited) exception.Data["MediaExitCode"] = process.ExitCode;
        }
        try
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
            var stdout = ReadBoundedAsync(process.StandardOutput, token);
            var completion = process.WaitForExitAsync(token);
            try
            {
                while (!completion.IsCompleted)
                {
                    token.ThrowIfCancellationRequested();
                    if (stdout.IsFaulted) await stdout;
                    if (stderr.IsFaulted) await stderr;
                    if (output != null && File.Exists(output) && new FileInfo(output).Length > maximumBytes)
                        throw new InvalidDataException();
                    await Task.WhenAny(completion, Task.Delay(25, token));
                }
                await completion;
                await Task.WhenAll(stdout, stderr);
                if (process.ExitCode != 0) throw new InvalidDataException();
                return await stdout;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(stdout, stderr); }
                catch { }
            }
        }
        catch (OperationCanceledException exception) { AttachDetails(exception); throw; }
        catch (Exception)
        {
            var error = new InvalidDataException(FailureMessage);
            AttachDetails(error);
            throw error;
        }
    }

    private static async Task<string> ReadStderrTailAsync(StreamReader reader, CancellationToken token)
    {
        const int limit = 4096;
        var lines = new Queue<string>();
        var line = new StringBuilder();
        var length = 0;
        var omitted = false;
        void AppendLine()
        {
            var safe = omitted ? "<stderr line omitted>" : SanitizeStderr(line.ToString());
            while (lines.Count > 0 && length + safe.Length + 1 > limit) length -= lines.Dequeue().Length + 1;
            lines.Enqueue(safe);
            length += safe.Length + 1;
            line.Clear();
            omitted = false;
        }
        var buffer = new char[1024];
        int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer, token)) > 0)
            {
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n') AppendLine();
                    else if (!omitted)
                    {
                        if (line.Length == limit) { line.Clear(); omitted = true; }
                        else line.Append(buffer[index]);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { line.Clear(); omitted = false; }
        if (omitted || line.Length > 0) AppendLine();
        return string.Join("\n", lines);
    }

    private static string SanitizeStderr(string text)
    {
        text = Regex.Replace(text, @"\b[a-z][a-z0-9+.-]*://[^\s""'<>]+", "<link>", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(authorization|password|secret|token|cookie|api[-_]?key|decryption[-_]?key|session[-_]?id|install[-_]?id|key)\b[""']?\s*[:=]\s*[""']?(?:(?:bearer|basic)\s+)?[^\s,;""'}&]+",
            "$1=<redacted>", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b[0-9a-f]{32,}\b", "<redacted>", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length < 4096 ? text : "<stderr line omitted>";
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (text.Length + count > 64 * 1024) throw new InvalidDataException();
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
