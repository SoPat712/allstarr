using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Extensions;

internal sealed record ExtensionDecryptedMedia(string Container, string Codec);

internal delegate Task<string> ExtensionMediaProcessRunner(string executable, IEnumerable<string> arguments,
    string? output, long maximumBytes, CancellationToken token);

internal sealed class ExtensionDownloadPostProcessor(TimeSpan timeout, ExtensionMediaProcessRunner? runner = null)
{
    internal const string FailureMessage = "Download decryption failed.";
    private readonly ExtensionMediaProcessRunner run = runner ?? RunAsync;

    public async Task<ExtensionDecryptedMedia> ProcessAsync(string input, string resultJson, long maximumBytes,
        CancellationToken cancellationToken)
    {
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
            var size = new FileInfo(input).Length;
            if (size < 1 || size > maximumBytes) throw new InvalidDataException();
            var declared = Duration(value);
            var before = await ProbeAsync(input, key, token);
            Validate(before, codec, declared);
            var expectedDuration = declared ?? ProbeDuration(before);
            await run("ffmpeg", ["-v", "error", "-nostdin", "-y", "-threads", "1", "-protocol_whitelist", "file",
                "-decryption_key", key!, "-f", "mov", "-i", input, "-map", "0:a:0", "-map_metadata", "-1", "-map_chapters", "-1",
                "-c", "copy", "-strict", "experimental", "-fs", maximumBytes.ToString(CultureInfo.InvariantCulture),
                "-f", "mp4", output], output, maximumBytes, token);
            size = new FileInfo(output).Length;
            if (size < 1 || size > maximumBytes) throw new InvalidDataException();
            Validate(await ProbeAsync(output, null, token), codec, expectedDuration);
            // A remux can succeed with corrupt packets and a wrong key. Decode without retaining PCM.
            await run("ffmpeg", ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-threads", "1",
                "-protocol_whitelist", "file", "-f", "mov", "-i", output, "-map", "0:a:0", "-threads", "1", "-f", "null", "-"],
                null, maximumBytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(output, input, overwrite: true);
            return new("m4a", codec!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new InvalidDataException(FailureMessage); }
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

    private async Task<JsonElement> ProbeAsync(string path, string? key, CancellationToken token)
    {
        List<string> arguments = ["-v", "error", "-threads", "1", "-protocol_whitelist", "file"];
        if (key != null) arguments.AddRange(["-decryption_key", key]);
        arguments.AddRange(["-f", "mov", "-i", path, "-show_entries", "stream=codec_type,codec_name,duration:format=format_name,duration", "-of", "json"]);
        using var document = JsonDocument.Parse(await run("ffprobe", arguments, null, 0, token));
        return document.RootElement.Clone();
    }

    private static double ProbeDuration(JsonElement probe)
    {
        var format = probe.GetProperty("format");
        if (!double.TryParse(Text(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
            !double.IsFinite(duration) || duration <= 0) throw new InvalidDataException();
        return duration;
    }

    private static void Validate(JsonElement probe, string? codec, double? expectedDuration)
    {
        var streams = probe.GetProperty("streams").EnumerateArray().ToArray();
        if (streams.Length != 1 || Text(streams[0], "codec_type") != "audio" || Text(streams[0], "codec_name") != codec ||
            !(Text(probe.GetProperty("format"), "format_name") ?? "").Split(',').Contains("mp4"))
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
        try
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
            var stdout = ReadBoundedAsync(process.StandardOutput, token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
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
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidDataException(FailureMessage); }
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
