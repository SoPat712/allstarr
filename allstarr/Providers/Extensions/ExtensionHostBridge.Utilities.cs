using allstarr.Core.Playback;
using allstarr.Services.Common;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using allstarr.Core.Extensions;

namespace allstarr.Services.Common;

public partial class ExtensionHostBridge
{
    public long ResolutionRemainingMs() => ExtensionInvocationBudget.Current?.ResolutionRemainingMilliseconds ?? 12_000;

    public bool IsRequestCancelled() => ExtensionInvocationBudget.Current?.Token.IsCancellationRequested == true ||
        ExtensionArtifactInvocationScope.Current?.CancellationToken.IsCancellationRequested == true;

    // Adapted from SpotiFLAC-Mobile (MIT), rust_backend/crates/core/src/matching.rs.
    private static string Lowercase(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            result.Append(rune.Value switch
            {
                0x130 => "i",
                0x1c89 or 0xa7cb or 0xa7cc or 0xa7ce or 0xa7d2 or 0xa7d4 or 0xa7da or 0xa7dc or
                    >= 0x10d50 and <= 0x10d65 or >= 0x16ea0 and <= 0x16eb8 => rune.ToString(),
                _ => Rune.ToLowerInvariant(rune).ToString()
            });
        return result.ToString();
    }

    public string Normalize(string value)
    {
        value = Lowercase(value);
        foreach (var suffix in new[] { " (remastered)", " (remaster)", " - remastered", " - remaster", " (deluxe)",
                     " (deluxe edition)", " - deluxe", " - deluxe edition", " (explicit)", " (clean)", " [explicit]",
                     " [clean]", " (album version)", " (single version)", " (radio edit)", " (feat.", " (ft.", " feat.", " ft." })
        {
            var index = value.IndexOf(suffix, StringComparison.Ordinal);
            if (index >= 0) value = value[..index];
        }
        var filtered = new string(value.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or ' ').ToArray());
        return string.Join(' ', filtered.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public double CompareStrings(string first, string second)
    {
        var a = Encoding.UTF8.GetBytes(Lowercase(first.Trim()));
        var b = Encoding.UTF8.GetBytes(Lowercase(second.Trim()));
        if (a.AsSpan().SequenceEqual(b)) return 1;
        var maximum = Math.Max(a.Length, b.Length);
        if (a.Length == 0 || b.Length == 0) return 0;
        var prefix = 0;
        while (prefix < Math.Min(a.Length, b.Length) && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(a.Length, b.Length) - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        a = a[prefix..(a.Length - suffix)];
        b = b[prefix..(b.Length - suffix)];
        if (a.Length < b.Length) (a, b) = (b, a);
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 0; i < a.Length; i++)
        {
            ExtensionInvocationBudget.Current?.Token.ThrowIfCancellationRequested();
            var diagonal = row[0];
            row[0] = i + 1;
            for (var j = 0; j < b.Length; j++)
            {
                if (j % 4096 == 0) ExtensionInvocationBudget.Current?.Token.ThrowIfCancellationRequested();
                var previous = row[j + 1];
                row[j + 1] = Math.Min(Math.Min(previous + 1, row[j] + 1), diagonal + (a[i] == b[j] ? 0 : 1));
                diagonal = previous;
            }
        }
        return 1.0 - (double)row[b.Length] / maximum;
    }

    public bool CompareDuration(double first, double second, double tolerance)
    {
        var difference = unchecked((long)first - (long)second);
        if (difference < 0) difference = unchecked(-difference);
        return difference <= (long)tolerance;
    }

    public object FfmpegConvert(string input, string output, string optionsJson)
    {
        using var timing = PlaybackStartTimings.Current?.Measure(PlaybackStartPhase.Process, _extensionId);
        string? staged = null;
        try
        {
            var scope = RequireFileScope();
            var source = scope.ResolveTemporaryPath(input);
            var target = scope.ResolveTemporaryPath(output);
            using var options = JsonDocument.Parse(optionsJson);
            if (!options.RootElement.TryGetProperty("codec", out var codec) || codec.GetString() != "copy")
                return new { success = false, error = "Only codec: copy is supported." };
            if (new FileInfo(source).Length > scope.MaximumBytes) return new { success = false, error = "Input exceeds the managed artifact size limit." };
            staged = scope.ResolveTemporaryPath(output + "." + Guid.NewGuid().ToString("N") + Path.GetExtension(output));
            using var capacity = _conversions.EnterAsync(scope.CancellationToken).AsTask().GetAwaiter().GetResult();
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                         "-protocol_whitelist", "file,pipe", "-format_whitelist", "aac,flac,mp3,mpegts,mov,ogg,wav,matroska,webm",
                         "-i", source, "-map", "0:a", "-c", "copy" }) start.ArgumentList.Add(argument);
            if (Path.GetExtension(target).ToLowerInvariant() is ".m4a" or ".mp4")
            {
                start.ArgumentList.Add("-movflags");
                start.ArgumentList.Add("+faststart");
            }
            start.ArgumentList.Add(staged);
            using var process = new Process { StartInfo = start };
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                while (!process.WaitForExit(100))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (File.Exists(staged) && new FileInfo(staged).Length > scope.MaximumBytes)
                        throw new InvalidDataException("Output exceeds the managed artifact size limit.");
                }
                timeout.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Task.WhenAll(stderr, stdout).GetAwaiter().GetResult();
            }
            if (process.ExitCode != 0 || !File.Exists(staged) || new FileInfo(staged).Length > scope.MaximumBytes)
                return new { success = false, error = "Media conversion failed." };
            File.Move(staged, target, overwrite: true);
            return new { success = true, output_path = output };
        }
        catch (LocalCapacityException) { return new { success = false, error = LocalCapacityException.Code, retryAfterSeconds = 5 }; }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        { return new { success = false, error = "ffmpeg is not available" }; }
        catch (Exception) { return new { success = false, error = "Media conversion failed." }; }
        finally
        {
            if (staged != null && File.Exists(staged)) File.Delete(staged);
        }
    }
}
