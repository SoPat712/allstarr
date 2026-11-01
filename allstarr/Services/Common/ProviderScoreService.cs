using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed record ProviderCapabilityScore(string Capability, int? Score, double? UptimePercent, int Checks, int Failures,
    long? TypicalLatencyMilliseconds, string? LastFailureCode);

public sealed record ProviderScoreDay(DateOnly Day, int Passed, int Failed);

public sealed record ProviderQuality(string Typical, string? Best, bool Lossless, int Samples);

public sealed record ProviderScore(string ProviderId, int? Score, double? UptimePercent, int Checks,
    IReadOnlyList<ProviderCapabilityScore> Capabilities, IReadOnlyList<ProviderScoreDay> Days, ProviderQuality? Quality = null);

/// <summary>Turns the last 30 days of provider checks and playback tests into one comparable score per provider.</summary>
public sealed class ProviderScoreService(IDbContextFactory<AllstarrDbContext> factory, TimeProvider time)
{
    internal const int MinimumChecks = 5;
    private const int WindowDays = 30;
    private const string PlaybackTestCategory = "provider-cts";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<ProviderScore>? cached;
    private DateTimeOffset cachedAt;

    internal readonly record struct Check(string Provider, string Capability, DateTimeOffset At, bool Passed, long? Latency, string? Reason);

    internal readonly record struct AudioSample(string Provider, string Codec, int? BitDepth, int? SampleRate, int? Bitrate);

    private sealed record PlaybackTest(bool Succeeded, string? ProviderId, string? Stage, string? Error,
        double? ClickToStreamMilliseconds, PlaybackMedia? Media, DateTimeOffset MeasuredAt);

    private sealed record PlaybackMedia(string? Codec, int? Bitrate, int? SampleRate, int? BitDepth);

    public async Task<IReadOnlyList<ProviderScore>> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var now = time.GetUtcNow();
            if (cached != null && now - cachedAt < CacheFor) return cached;
            var cutoff = now.AddDays(-WindowDays);
            await using var db = await factory.CreateDbContextAsync(token);
            var rows = await db.AuditEvents.AsNoTracking()
                .Where(row => (row.Category == ProviderCheckHistory.Category || row.Category == PlaybackTestCategory) && row.CreatedAt >= cutoff)
                .Select(row => new { row.Category, row.DetailsJson }).ToListAsync(token);
            var checks = new List<Check>(rows.Count);
            var samples = new List<AudioSample>();
            foreach (var row in rows)
            {
                if (row.Category == PlaybackTestCategory) ParsePlaybackTest(row.DetailsJson, checks, samples);
                else if (Parse(row.DetailsJson) is { } check) checks.Add(check);
            }
            cachedAt = now;
            return cached = Summarize(checks, samples, now);
        }
        finally { gate.Release(); }
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

    /// <summary>How much of the quality share a source earns, and the highest overall score it can reach.</summary>
    private static (double Earned, int Ceiling) QualityValue(ProviderQuality quality, IReadOnlyCollection<AudioSample> samples)
    {
        if (quality.Lossless) return (1, 100);
        var bitrates = samples.Where(sample => !IsLossless(sample)).Select(RealBitrate).OfType<int>().Order().ToArray();
        if (bitrates.Length == 0) return (0.5, 75);
        return bitrates[bitrates.Length / 2] >= 256_000 ? (0.6, 80) : (0.25, 60);
    }

    private static IReadOnlyList<ProviderScore> Summarize(IEnumerable<Check> checks, IEnumerable<AudioSample> samples, DateTimeOffset now)
    {
        var audio = samples.GroupBy(sample => sample.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<AudioSample>)group.ToArray(), StringComparer.OrdinalIgnoreCase);
        return Summarize(checks, audio, now);
    }

    private static IReadOnlyList<ProviderScore> Summarize(IEnumerable<Check> checks,
        Dictionary<string, IReadOnlyCollection<AudioSample>> audio, DateTimeOffset now) =>
        checks.Where(check => !allstarr.Core.Settings.RetiredProviderSettings.IsRetired(check.Provider))
            .GroupBy(check => check.Provider, StringComparer.OrdinalIgnoreCase).Select(provider =>
            {
                var rows = provider.ToArray();
                var samples = audio.GetValueOrDefault(provider.Key) ?? [];
                var quality = Quality(samples);
                return new ProviderScore(provider.Key, Score(rows, now, quality == null ? null : QualityValue(quality, samples)), Uptime(rows), rows.Length,
                    rows.GroupBy(check => check.Capability, StringComparer.OrdinalIgnoreCase).Select(group =>
                    {
                        var capability = group.ToArray();
                        return new ProviderCapabilityScore(group.Key, Score(capability, now), Uptime(capability), capability.Length,
                            capability.Count(check => !check.Passed), Typical(capability),
                            capability.Where(check => !check.Passed).OrderByDescending(check => check.At).FirstOrDefault().Reason);
                    }).OrderBy(item => item.Capability, StringComparer.Ordinal).ToArray(),
                    rows.GroupBy(check => DateOnly.FromDateTime(check.At.UtcDateTime))
                        .Select(day => new ProviderScoreDay(day.Key, day.Count(check => check.Passed), day.Count(check => !check.Passed)))
                        .OrderBy(day => day.Day).ToArray(), quality);
            }).OrderBy(item => item.ProviderId, StringComparer.Ordinal).ToArray();

    private static double? Uptime(IReadOnlyCollection<Check> rows) =>
        rows.Count == 0 ? null : Math.Round(100d * rows.Count(check => check.Passed) / rows.Count, 1);

    private static long? Typical(IEnumerable<Check> rows)
    {
        var latencies = rows.Where(check => check.Passed && check.Latency != null).Select(check => check.Latency!.Value).Order().ToArray();
        return latencies.Length == 0 ? null : latencies[latencies.Length / 2];
    }

    // Reliability, with the last week counting double, and speed. Sources that play audio also earn a share for
    // the quality they deliver, and lossy sources have a ceiling however dependable they are.
    private static int? Score(IReadOnlyCollection<Check> rows, DateTimeOffset now, (double Earned, int Ceiling)? quality = null)
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
        var speed = Typical(rows) is { } typical ? Math.Clamp(1 - (typical - 500) / 7500d, 0, 1) : 1;
        if (quality is not { } value) return (int)Math.Round(100 * (0.8 * reliability + 0.2 * speed));
        return Math.Min(value.Ceiling, (int)Math.Round(100 * (0.5 * reliability + 0.2 * speed + 0.3 * value.Earned)));
    }

    internal static IReadOnlyList<ProviderScore> ForTest(IEnumerable<(string Provider, string Capability, DateTimeOffset At, bool Passed, long? Latency, string? Reason)> rows,
        DateTimeOffset now, IEnumerable<AudioSample>? samples = null) =>
        Summarize(rows.Select(row => new Check(row.Provider, row.Capability, row.At, row.Passed, row.Latency, row.Reason)), samples ?? [], now);
}
