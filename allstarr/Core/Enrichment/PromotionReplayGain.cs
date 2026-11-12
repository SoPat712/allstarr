using allstarr.Core.Playback;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace allstarr.Core.Enrichment;

public sealed record PromotionReplayGainTrack(string Path, double GainDb, double Peak)
{
    public IReadOnlyDictionary<string, string> Tags => new Dictionary<string, string>
    {
        ["REPLAYGAIN_TRACK_GAIN"] = PromotionReplayGain.FormatGain(GainDb),
        ["REPLAYGAIN_TRACK_PEAK"] = PromotionReplayGain.FormatPeak(Peak)
    };
}

public sealed record PromotionReplayGainResult(
    IReadOnlyList<PromotionReplayGainTrack> Tracks, double AlbumGainDb, double AlbumPeak)
{
    public IReadOnlyDictionary<string, string> AlbumTags => new Dictionary<string, string>
    {
        ["REPLAYGAIN_ALBUM_GAIN"] = PromotionReplayGain.FormatGain(AlbumGainDb),
        ["REPLAYGAIN_ALBUM_PEAK"] = PromotionReplayGain.FormatPeak(AlbumPeak)
    };
}

public sealed class PromotionReplayGain(MediaConversionGate? conversions = null)
{
    public const double TargetLufs = -18;
    private const int MaximumTracks = 256;
    private const int MaximumOutputCharacters = 65536;
    private readonly MediaConversionGate conversionGate = conversions ?? new();

    public async Task<PromotionReplayGainResult> AnalyzeAsync(
        IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > MaximumTracks)
            throw new ArgumentException($"ReplayGain requires between 1 and {MaximumTracks} files.", nameof(paths));
        var sources = paths.Select(Path.GetFullPath).ToArray();
        if (sources.Any(path => !System.IO.File.Exists(path)))
            throw new FileNotFoundException("A ReplayGain source file is missing.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromHours(1));
        try
        {
            var tracks = new List<PromotionReplayGainTrack>(sources.Length);
            var mappings = new List<string>(sources.Length);
            foreach (var source in sources)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (sources.Length > 1) mappings.Add(await ReadChannelMapAsync(source, deadline.Token));
                var measured = await MeasureAsync(
                    ["-threads", "1", "-protocol_whitelist", "file,pipe", "-i", source,
                        "-map", "0:a:0", "-af", "ebur128=peak=true:framelog=verbose"], deadline.Token);
                tracks.Add(new(source, measured.GainDb, measured.Peak));
            }

            if (tracks.Count == 1) return new(tracks, tracks[0].GainDb, tracks[0].Peak);
            var arguments = new List<string>();
            foreach (var source in sources)
                arguments.AddRange(["-threads", "1", "-protocol_whitelist", "file,pipe", "-i", source]);
            arguments.AddRange(["-filter_complex", BuildAlbumFilter(mappings), "-map", "[analysis]"]);
            var album = await MeasureAsync(arguments, deadline.Token);
            // True peak belongs to the original audio, before common PCM resampling.
            return new(tracks, album.GainDb, tracks.Max(track => track.Peak));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("ReplayGain analysis exceeded its time limit.");
        }
    }

    public static string FormatGain(double gainDb) =>
        gainDb.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture) + " dB";

    public static string FormatPeak(double peak) => peak.ToString("0.00000000", CultureInfo.InvariantCulture);

    internal static (double GainDb, double Peak) ParseSummary(string output)
    {
        var matches = Regex.Matches(output,
            @"Integrated loudness:\s*I:\s*(?<loudness>\S+)\s+LUFS[\s\S]*?True peak:\s*Peak:\s*(?<peak>\S+)\s+dBFS",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (matches.Count == 0) throw new InvalidDataException("ReplayGain returned no complete loudness and true peak summary.");
        var summary = matches[^1];
        var peakText = summary.Groups["peak"].Value;
        if (!double.TryParse(summary.Groups["loudness"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var loudness) ||
            !double.IsFinite(loudness) || loudness is < -120 or > 30)
            throw new InvalidDataException("ReplayGain returned invalid integrated loudness.");
        if (peakText.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return (0, 0);
        if (!double.TryParse(peakText, NumberStyles.Float, CultureInfo.InvariantCulture, out var peakDb) ||
            !double.IsFinite(peakDb) || peakDb is < -1000 or > 100)
            throw new InvalidDataException("ReplayGain returned invalid true peak.");
        return (loudness <= -70 ? 0 : TargetLufs - loudness, Math.Pow(10, peakDb / 20));
    }

    internal static string BuildAlbumFilter(int tracks) =>
        BuildAlbumFilter(Enumerable.Repeat("FL=c0|FR=c1", tracks).ToArray());

    internal static string BuildAlbumFilter(IReadOnlyList<string> mappings)
    {
        if (mappings.Count is < 2 or > MaximumTracks) throw new ArgumentOutOfRangeException(nameof(mappings));
        var filter = new StringBuilder();
        for (var index = 0; index < mappings.Count; index++)
            filter.Append(CultureInfo.InvariantCulture,
                $"[{index}:a:0]aresample=48000,pan=7.1|{mappings[index]},aformat=sample_fmts=dbl,asetpts=PTS-STARTPTS[a{index}];");
        for (var index = 0; index < mappings.Count; index++) filter.Append(CultureInfo.InvariantCulture, $"[a{index}]");
        filter.Append(CultureInfo.InvariantCulture,
            $"concat=n={mappings.Count}:v=0:a=1,ebur128=peak=true:framelog=verbose[analysis]");
        return filter.ToString();
    }

    internal static string ChannelMap(string? layout, int channels)
    {
        string[]? names = layout switch
        {
            "mono" => ["FC"],
            "stereo" => ["FL", "FR"],
            "2.1" => ["FL", "FR", "LFE"],
            "3.0" => ["FL", "FR", "FC"],
            "4.0" => ["FL", "FR", "FC", "BC"],
            "quad" => ["FL", "FR", "BL", "BR"],
            "quad(side)" => ["FL", "FR", "SL", "SR"],
            "5.0" => ["FL", "FR", "FC", "BL", "BR"],
            "5.0(side)" => ["FL", "FR", "FC", "SL", "SR"],
            "5.1" => ["FL", "FR", "FC", "LFE", "BL", "BR"],
            "5.1(side)" => ["FL", "FR", "FC", "LFE", "SL", "SR"],
            "7.1" => ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"],
            null or "" or "unknown" when channels == 1 => ["FC"],
            null or "" or "unknown" when channels == 2 => ["FL", "FR"],
            _ => null
        };
        // A back-center channel cannot be mapped at unity into a layout without it.
        if (names is null || names.Length != channels || names.Contains("BC"))
            throw new InvalidDataException("The album channel layout cannot be measured without changing its loudness.");
        return string.Join('|', names.Select((name, index) => $"{name}=c{index}"));
    }

    private async Task<string> ReadChannelMapAsync(string path, CancellationToken token)
    {
        var output = await RunAsync("ffprobe", ["-v", "error", "-threads", "1", "-protocol_whitelist", "file,pipe",
            "-select_streams", "a:0", "-show_entries", "stream=channels,channel_layout", "-of", "json", path],
            TimeSpan.FromSeconds(20), token);
        using var document = JsonDocument.Parse(output.Output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() != 1)
            throw new InvalidDataException("The ReplayGain source has no audio stream.");
        var stream = streams[0];
        return ChannelMap(stream.TryGetProperty("channel_layout", out var layout) ? layout.GetString() : null,
            stream.GetProperty("channels").GetInt32());
    }

    private async Task<(double GainDb, double Peak)> MeasureAsync(IEnumerable<string> input, CancellationToken token)
    {
        List<string> arguments = ["-hide_banner", "-nostdin", "-v", "info", "-nostats", "-xerror",
            "-filter_threads", "1", "-filter_complex_threads", "1"];
        arguments.AddRange(input);
        arguments.AddRange(["-vn", "-sn", "-dn", "-threads", "1", "-f", "null", "-"]);
        var output = await RunAsync("ffmpeg", arguments, TimeSpan.FromMinutes(15), token);
        return ParseSummary(output.Errors);
    }

    private async Task<(string Output, string Errors)> RunAsync(
        string executable, IEnumerable<string> arguments, TimeSpan duration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var capacity = executable == "ffmpeg" ? await conversionGate.EnterAsync(token) : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(duration);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? executable : "nice")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (!OperatingSystem.IsWindows())
            foreach (var argument in new[] { "-n", "19", executable }) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The ReplayGain process could not start.");
        using var registration = timeout.Token.Register(() => Stop(process));
        var output = ReadProcessOutputAsync(process.StandardOutput, false, process, timeout.Token);
        var errors = ReadProcessOutputAsync(process.StandardError, true, process, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("The ReplayGain process could not read the audio.");
            return (await output, await errors);
        }
        finally
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None);
            timeout.Cancel();
            try { await Task.WhenAll(output, errors); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (InvalidDataException) { }
        }
    }

    private static async Task<string> ReadProcessOutputAsync(StreamReader reader, bool keepTail, Process process, CancellationToken token)
    {
        try { return await ReadBoundedAsync(reader, keepTail, token); }
        catch
        {
            Stop(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, bool keepTail, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (!keepTail && result.Length + count > MaximumOutputCharacters)
                throw new InvalidDataException("The ReplayGain process output exceeded its limit.");
            result.Append(buffer, 0, count);
            if (result.Length > MaximumOutputCharacters) result.Remove(0, result.Length - MaximumOutputCharacters);
        }
        return result.ToString();
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
