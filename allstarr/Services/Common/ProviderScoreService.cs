using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

/// <summary>How a score was earned. The parts add up to <see cref="Earned"/>; a lossy source may then be held at a ceiling.</summary>
public sealed record ProviderScoreParts(int Earned, int Reliability, int ReliabilityOutOf, int Speed, int SpeedOutOf, int Quality, int QualityOutOf,
    int? CappedAt, long? TypicalMilliseconds, int Coverage = 0, int CoverageOutOf = 0, int? SongsFound = null, int? SongsTried = null,
    int RealPlays = 0, int RealPlayFailures = 0, bool TimedOnRealPlays = false);

public sealed record ProviderCapabilityScore(string Capability, int? Score, double? UptimePercent, int Checks, int Failures,
    long? TypicalLatencyMilliseconds, string? LastFailureCode, ProviderScoreParts? Parts = null);

public sealed record ProviderScoreDay(DateOnly Day, int Passed, int Failed);

public sealed record ProviderQuality(string Typical, string? Best, bool Lossless, int Samples);

public sealed record ProviderScore(string ProviderId, int? Score, double? UptimePercent, int Checks,
    IReadOnlyList<ProviderCapabilityScore> Capabilities, IReadOnlyList<ProviderScoreDay> Days, ProviderQuality? Quality = null, ProviderScoreParts? Parts = null);

/// <summary>Turns the last 30 days of provider checks and playback tests into one comparable score per provider.</summary>
public sealed class ProviderScoreService(IDbContextFactory<AllstarrDbContext> factory, TimeProvider time)
{
    internal const int MinimumChecks = 20;
    internal const int MinimumSongs = 20;
    internal const int MinimumRealPlays = 30;
    internal const int MinimumRealStarts = 10;
    private const int WindowDays = 30;
    private const string PlaybackTestCategory = "provider-cts";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Measurements? cached;
    private readonly Dictionary<string, IReadOnlyList<ProviderScore>> summaries = new(StringComparer.Ordinal);
    private DateTimeOffset cachedAt;

    private sealed record Measurements(List<Check> Checks, List<AudioSample> Samples, List<SongCheck> Songs, List<RealPlay> Plays);

    internal readonly record struct Check(string Provider, string Capability, DateTimeOffset At, bool Passed, long? Latency, string? Reason);

    internal readonly record struct AudioSample(string Provider, string Codec, int? BitDepth, int? SampleRate, int? Bitrate);

    internal readonly record struct SongCheck(string Provider, string Capability, string Song, bool Found, DateTimeOffset At);

    internal readonly record struct RealPlay(string Provider, bool Succeeded, bool Cached, long StartMilliseconds);

    private sealed record RealPlayStart(string? Provider, string? Outcome, bool Cached, double ElapsedMs);

    private sealed record RealPlayStats(int Count, int Failures, long? TypicalStart);

    private enum DeliveredQuality { Low, UnknownLossy, HighLossy, Lossless, HighResolution }

    private sealed record PlaybackTest(bool Succeeded, string? ProviderId, string? Stage, string? Error,
        double? ClickToStreamMilliseconds, PlaybackMedia? Media, DateTimeOffset MeasuredAt);

    private sealed record PlaybackMedia(string? Codec, int? Bitrate, int? SampleRate, int? BitDepth);

    /// <summary>Scores judged against one listener's quality setting, because a source that suits one person can fall short for another.</summary>
    public async Task<IReadOnlyList<ProviderScore>> ReadAsync(string qualityTarget, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var now = time.GetUtcNow();
            if (cached == null || now - cachedAt >= CacheFor)
            {
                cached = await LoadAsync(now, token);
                cachedAt = now;
                summaries.Clear();
            }
            if (summaries.TryGetValue(qualityTarget, out var known)) return known;
            return summaries[qualityTarget] = Summarize(cached.Checks, cached.Samples, cached.Songs, cached.Plays, now, qualityTarget);
        }
        finally { gate.Release(); }
    }

    private async Task<Measurements> LoadAsync(DateTimeOffset now, CancellationToken token)
    {
        var cutoff = now.AddDays(-WindowDays);
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(row => (row.Category == ProviderCheckHistory.Category || row.Category == PlaybackTestCategory ||
                row.Category == ProviderCoverageRunner.Category || row.Category == PlaybackStartTimingStore.Category) && row.CreatedAt >= cutoff)
            .Select(row => new { row.Category, row.DetailsJson }).ToListAsync(token);
        var measured = new Measurements(new(rows.Count), [], [], []);
        foreach (var row in rows)
        {
            if (row.Category == PlaybackTestCategory) ParsePlaybackTest(row.DetailsJson, measured.Checks, measured.Samples);
            else if (row.Category == ProviderCoverageRunner.Category) ParseSongCheck(row.DetailsJson, measured.Songs);
            else if (row.Category == PlaybackStartTimingStore.Category) ParseRealPlay(row.DetailsJson, measured.Plays);
            else if (Parse(row.DetailsJson) is { } check) measured.Checks.Add(check);
        }
        return measured;
    }

    private static void ParseSongCheck(string? json, List<SongCheck> songs)
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            if (JsonSerializer.Deserialize<ProviderCoverageResult>(json) is { } result)
                songs.Add(new(result.Provider, result.Capability, result.Song, result.Found, result.At));
        }
        catch (JsonException) { }
    }

    // Only a finished stream or a real failure says anything: a listener skipping away isn't the source's fault.
    private static void ParseRealPlay(string? json, List<RealPlay> plays)
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            if (JsonSerializer.Deserialize<RealPlayStart>(json) is { Provider.Length: > 0, Outcome: "completed" or "failed" } play)
                plays.Add(new(play.Provider!, play.Outcome == "completed", play.Cached, (long)play.ElapsedMs));
        }
        catch (JsonException) { }
    }

    private static Check? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 16384) return null;
        try
        {
            var result = JsonSerializer.Deserialize<ProviderCheckResult>(json);
            if (result?.Status is not { HasCheckResult: true, TestedAt: { } at } status) return null;
            return new(result.Key.Provider, result.Key.Capability, at, status.Health == ProviderHealthState.Healthy,
                status.LatencyMilliseconds is >= 0 ? status.LatencyMilliseconds : null,
                ProviderCheckHistory.SafeReasonCode(status.ReasonCode));
        }
        catch (JsonException) { return null; }
    }

    private static void ParsePlaybackTest(string? json, List<Check> checks, List<AudioSample> samples)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 16384) return;
        PlaybackTest? test;
        try { test = JsonSerializer.Deserialize<PlaybackTest>(json); }
        catch (JsonException) { return; }
        if (test is not { ProviderId.Length: > 0 }) return;
        var reason = test.Succeeded ? null : test.Error is { } error && !error.Contains(' ') ? error : test.Stage;
        if (!test.Succeeded && ProviderRuntimeStatus.IsNeutralReason(reason)) return;
        checks.Add(new(test.ProviderId, "streaming", test.MeasuredAt, test.Succeeded,
            test.ClickToStreamMilliseconds is > 0 and var started ? (long)started : null, ProviderCheckHistory.SafeReasonCode(reason)));
        if (test is { Succeeded: true, Media.Codec: { Length: > 0 } codec } && codec != "unknown")
            samples.Add(new(test.ProviderId, codec, test.Media.BitDepth, test.Media.SampleRate, test.Media.Bitrate));
    }

    private static bool IsLossless(AudioSample sample) => sample.Codec is "flac" or "alac" or "wav" ||
        sample.Codec.StartsWith("pcm", StringComparison.Ordinal);

    // Some sources report placeholder bitrates, so anything below a real audio rate counts as unknown.
    private static int? RealBitrate(AudioSample sample) => sample.Bitrate is >= 32_000 ? sample.Bitrate : null;

    private static string Label(AudioSample sample)
    {
        var rate = sample.SampleRate is { } hertz ? $"{hertz / 1000d:0.#} kHz" : null;
        if (IsLossless(sample))
            return sample.BitDepth is { } depth && rate != null ? $"Lossless {depth}-bit/{rate}" : "Lossless";
        var codec = sample.Codec.ToUpperInvariant();
        return RealBitrate(sample) is { } bitrate ? $"{codec} {bitrate / 1000} kbps" : codec;
    }

    private static ProviderQuality? Quality(IReadOnlyCollection<AudioSample> samples)
    {
        if (samples.Count == 0) return null;
        var lossless = samples.Where(IsLossless).ToArray();
        var typicallyLossless = lossless.Length * 2 >= samples.Count;
        var relevant = typicallyLossless ? lossless : samples.Where(sample => !IsLossless(sample)).ToArray();
        // Prefer samples that report full detail: they name the format the others only hint at.
        var detailed = relevant.Where(sample => typicallyLossless ? sample.BitDepth != null : RealBitrate(sample) != null).ToArray();
        var typical = (detailed.Length > 0 ? detailed : relevant).GroupBy(Label).OrderByDescending(group => group.Count()).First().Key;
        var best = lossless.Where(sample => sample.BitDepth != null)
            .OrderByDescending(sample => sample.BitDepth).ThenByDescending(sample => sample.SampleRate).Select(Label).FirstOrDefault();
        return new(typical, best != null && best != typical ? best : null, typicallyLossless, samples.Count);
    }

    private static DeliveredQuality Delivered(ProviderQuality quality, IReadOnlyCollection<AudioSample> samples)
    {
        if (quality.Lossless)
        {
            var depths = samples.Where(IsLossless).Select(sample => sample.BitDepth).OfType<int>().ToArray();
            return depths.Length > 0 && depths.Count(depth => depth >= 24) * 2 >= depths.Length
                ? DeliveredQuality.HighResolution : DeliveredQuality.Lossless;
        }
        var bitrates = samples.Where(sample => !IsLossless(sample)).Select(RealBitrate).OfType<int>().Order().ToArray();
        if (bitrates.Length == 0) return DeliveredQuality.UnknownLossy;
        return bitrates[bitrates.Length / 2] >= 256_000 ? DeliveredQuality.HighLossy : DeliveredQuality.Low;
    }

    /// <summary>How much of the quality share a source earns for what this listener asked for, and the highest score it can reach.</summary>
    private static (double Earned, int Ceiling) QualityValue(ProviderQuality quality, IReadOnlyCollection<AudioSample> samples, string target)
    {
        var delivered = Delivered(quality, samples);
        return target switch
        {
            "DataSaver" => (1, 100),
            "High" => delivered switch
            {
                DeliveredQuality.Low => (0.4, 65),
                DeliveredQuality.UnknownLossy => (0.7, 85),
                _ => (1, 100)
            },
            "CdLossless" => delivered switch
            {
                DeliveredQuality.Low => (0.25, 60),
                DeliveredQuality.UnknownLossy => (0.5, 75),
                DeliveredQuality.HighLossy => (0.6, 80),
                _ => (1, 100)
            },
            _ => delivered switch
            {
                DeliveredQuality.Low => (0.1, 40),
                DeliveredQuality.UnknownLossy => (0.25, 50),
                DeliveredQuality.HighLossy => (0.3, 55),
                DeliveredQuality.Lossless => (0.6, 75),
                _ => (1, 100)
            }
        };
    }

    private static IReadOnlyList<ProviderScore> Summarize(IEnumerable<Check> checks, IEnumerable<AudioSample> samples,
        IEnumerable<SongCheck> songs, IEnumerable<RealPlay> plays, DateTimeOffset now, string target)
    {
        var audio = samples.GroupBy(sample => sample.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<AudioSample>)group.ToArray(), StringComparer.OrdinalIgnoreCase);
        // The latest answer for each song stands, so a source that adds a song stops being charged for it.
        var found = songs.GroupBy(song => (Provider: song.Provider.ToLowerInvariant(), song.Capability))
            .ToDictionary(group => group.Key, group =>
            {
                var latest = group.GroupBy(song => song.Song).Select(song => song.MaxBy(item => item.At)).ToArray();
                return (Found: latest.Count(song => song.Found), Tried: latest.Length);
            });
        var real = plays.GroupBy(play => play.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group =>
            {
                var starts = group.Where(play => play.Succeeded && !play.Cached).Select(play => play.StartMilliseconds).Order().ToArray();
                return new RealPlayStats(group.Count(), group.Count(play => !play.Succeeded),
                    starts.Length >= MinimumRealStarts ? starts[starts.Length / 2] : null);
            }, StringComparer.OrdinalIgnoreCase);
        return checks.Where(check => !allstarr.Core.Settings.RetiredProviderSettings.IsRetired(check.Provider))
            .GroupBy(check => check.Provider, StringComparer.OrdinalIgnoreCase).Select(provider =>
            {
                var rows = provider.ToArray();
                var sourceSamples = audio.GetValueOrDefault(provider.Key) ?? [];
                var quality = Quality(sourceSamples);
                (double Earned, int Ceiling)? qualityValue = quality == null ? null : QualityValue(quality, sourceSamples, target);
                (int Found, int Tried)? Songs(string capability) =>
                    found.TryGetValue((provider.Key.ToLowerInvariant(), capability), out var result) ? result : null;
                var plays = real.GetValueOrDefault(provider.Key);
                // A source that plays audio is rated on playing it. Its other features are rated separately below.
                var streaming = rows.Where(check => check.Capability == "streaming").ToArray();
                var parts = qualityValue != null && streaming.Length >= MinimumChecks
                    ? Parts(streaming, now, qualityValue, true, Songs("streaming"), plays)
                    : Parts(rows, now, qualityValue);
                return new ProviderScore(provider.Key, Score(parts), Uptime(rows), rows.Length,
                    rows.GroupBy(check => check.Capability, StringComparer.OrdinalIgnoreCase).Select(group =>
                    {
                        var capability = group.ToArray();
                        var capabilityParts = group.Key switch
                        {
                            "streaming" => Parts(capability, now, qualityValue, true, Songs("streaming"), plays),
                            "download" => Parts(capability, now, qualityValue, false, Songs("streaming")),
                            "lyrics" => Parts(capability, now, null, true, Songs("lyrics")),
                            _ => Parts(capability, now)
                        };
                        return new ProviderCapabilityScore(group.Key, Score(capabilityParts), Uptime(capability), capability.Length,
                            capability.Count(check => !check.Passed), Typical(capability),
                            capability.Where(check => !check.Passed).OrderByDescending(check => check.At).FirstOrDefault().Reason, capabilityParts);
                    }).OrderBy(item => item.Capability, StringComparer.Ordinal).ToArray(),
                    rows.GroupBy(check => DateOnly.FromDateTime(check.At.UtcDateTime))
                        .Select(day => new ProviderScoreDay(day.Key, day.Count(check => check.Passed), day.Count(check => !check.Passed)))
                        .OrderBy(day => day.Day).ToArray(), quality, parts);
            }).OrderBy(item => item.ProviderId, StringComparer.Ordinal).ToArray();
    }

    private static double? Uptime(IReadOnlyCollection<Check> rows) =>
        rows.Count == 0 ? null : Math.Round(100d * rows.Count(check => check.Passed) / rows.Count, 1);

    private static long? Typical(IEnumerable<Check> rows)
    {
        var latencies = rows.Where(check => check.Passed && check.Latency != null).Select(check => check.Latency!.Value).Order().ToArray();
        return latencies.Length == 0 ? null : latencies[latencies.Length / 2];
    }

    // Reliability, with the last week counting double, and speed, both judged on the checks passed in. Nobody waits on a
    // download, so its time doesn't count. Playing and downloading also earn a share for the quality delivered, with a
    // ceiling for lossy sources however dependable they are.
    private static ProviderScoreParts? Parts(IReadOnlyCollection<Check> rows, DateTimeOffset now, (double Earned, int Ceiling)? quality = null,
        bool speedCounts = true, (int Found, int Tried)? songs = null, RealPlayStats? plays = null)
    {
        if (rows.Count < MinimumChecks) return null;
        var recent = now.AddDays(-7);
        double earned = 0, possible = 0;
        foreach (var check in rows)
        {
            var weight = check.At >= recent ? 2 : 1;
            possible += weight;
            if (check.Passed) earned += weight;
        }
        var reliability = earned / possible;
        // Tests can't see everything a listener hits, so real plays count for half once there are enough to trust.
        var realPlays = plays is { Count: >= MinimumRealPlays } ? plays : null;
        if (realPlays != null) reliability = (reliability + 1 - (double)realPlays.Failures / realPlays.Count) / 2;
        // For a source that plays audio, speed means how fast a song starts, not how fast it answers a search.
        var timed = quality != null && rows.Any(check => check.Capability == "streaming" && check.Passed && check.Latency != null)
            ? rows.Where(check => check.Capability == "streaming") : rows;
        var typicalTime = plays?.TypicalStart ?? Typical(timed);
        var speed = typicalTime is { } typical ? Math.Clamp(1 - (typical - 500) / 7500d, 0, 1) : 1;
        var songsCount = songs is { Tried: >= MinimumSongs };
        var (reliabilityShare, speedShare, qualityShare, coverageShare) = (speedCounts, quality != null, songsCount) switch
        {
            (true, true, true) => (40, 15, 25, 20),
            (true, true, false) => (50, 20, 30, 0),
            (true, false, true) => (60, 15, 0, 25),
            (true, false, false) => (80, 20, 0, 0),
            (false, true, true) => (50, 0, 30, 20),
            (false, true, false) => (60, 0, 40, 0),
            (false, false, true) => (70, 0, 0, 30),
            (false, false, false) => (100, 0, 0, 0)
        };
        var qualityEarned = quality?.Earned ?? 0;
        var coverageEarned = songsCount ? (double)songs!.Value.Found / songs.Value.Tried : 0;
        var total = (int)Math.Round(reliabilityShare * reliability + speedShare * speed + qualityShare * qualityEarned + coverageShare * coverageEarned);
        var speedPoints = (int)Math.Round(speedShare * speed);
        var qualityPoints = (int)Math.Round(qualityShare * qualityEarned);
        var coveragePoints = (int)Math.Round(coverageShare * coverageEarned);
        return new(total, Math.Max(0, total - speedPoints - qualityPoints - coveragePoints), reliabilityShare, speedPoints, speedShare, qualityPoints, qualityShare,
            quality is { } value && total > value.Ceiling ? value.Ceiling : null, typicalTime, coveragePoints, coverageShare, songs?.Found, songs?.Tried,
            realPlays?.Count ?? 0, realPlays?.Failures ?? 0, speedCounts && plays?.TypicalStart != null);
    }

    private static int? Score(ProviderScoreParts? parts) => parts == null ? null : parts.CappedAt ?? parts.Earned;

    internal static IReadOnlyList<ProviderScore> ForTest(IEnumerable<(string Provider, string Capability, DateTimeOffset At, bool Passed, long? Latency, string? Reason)> rows,
        DateTimeOffset now, IEnumerable<AudioSample>? samples = null, string target = "CdLossless", IEnumerable<SongCheck>? songs = null, IEnumerable<RealPlay>? plays = null) =>
        Summarize(rows.Select(row => new Check(row.Provider, row.Capability, row.At, row.Passed, row.Latency, row.Reason)), samples ?? [], songs ?? [], plays ?? [], now, target);
}
