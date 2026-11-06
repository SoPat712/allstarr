using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

internal sealed record LyricsScore(double Score, IReadOnlyList<string> Reasons);

internal static class LyricsQuality
{
    internal static LyricsScore? Evaluate(LyricsInfo? lyrics, Song song, long? recordingDurationMs = null)
    {
        var rich = RichLyricsMapper.Map(lyrics);
        if (rich == null || !rich.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text))) return null;
        var wordTimed = rich.Lines.Any(line => line.Words.Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => word.StartMs).Distinct().Skip(1).Any()) ||
            (lyrics!.TimedLines ?? []).SelectMany(line => line.VocalParts ?? [line]).Any(line =>
                line.Segments is { Count: 1 } && line.Segments.Any(segment =>
                    !string.IsNullOrWhiteSpace(segment.Text) && !segment.Text.Any(char.IsWhiteSpace) &&
                    segment.Duration is { } wordDuration && wordDuration > TimeSpan.Zero));
        var format = rich.Format == "plain" ? "plain" : wordTimed ? "word" : "line";
        var score = BaseScore(format);
        var reasons = new List<string> { format switch { "word" => "synced_words", "line" => "synced_lines", _ => "plain_text" } };
        if (format != "plain" && Duration(song, recordingDurationMs) is { } duration)
        {
            // Mapper end times can be inferred from song length; only body timestamps measure coverage.
            var last = LastTimestamp(lyrics!);
            var fits = last >= duration * 0.6 && last <= duration + Math.Max(2000, duration * 0.02);
            score += fits ? 0.1 : -0.25;
            reasons.Add(fits ? "duration_fits" : "duration_mismatch");
        }
        return new(Math.Clamp(score, 0, 1), reasons);
    }

    internal static long? Duration(Song song, long? recordingDurationMs = null) =>
        recordingDurationMs is > 0 ? recordingDurationMs : song.PreciseDuration is { TotalMilliseconds: > 0 } precise
            ? (long)precise.TotalMilliseconds : song.Duration is > 0 ? song.Duration.Value * 1000L : null;

    internal static double Rank(string? format) => BaseScore(format);

    private static double BaseScore(string? format) => format switch { "word" => 0.9, "line" => 0.6, "plain" => 0.3, _ => 0 };

    internal static double Maximum(string sourceId, Song song, long? recordingDurationMs = null)
    {
        var format = sourceId switch { "genius" => "plain", "spotify" => "line", _ => "word" };
        return BaseScore(format) + (format != "plain" && Duration(song, recordingDurationMs) != null ? 0.1 : 0);
    }

    internal static bool CanImprove(string sourceId, int rank, double score, int bestRank, Song song, long? recordingDurationMs = null) =>
        Maximum(sourceId, song, recordingDurationMs) > score || Maximum(sourceId, song, recordingDurationMs) == score && rank < bestRank;

    private static double LastTimestamp(LyricsInfo lyrics)
    {
        if (lyrics.TimedLines is { Count: > 0 })
            return lyrics.TimedLines.SelectMany(line => line.VocalParts ?? [line]).Select(line =>
                Math.Max((line.Start + (line.Duration ?? TimeSpan.Zero)).TotalMilliseconds,
                    (line.Segments ?? []).Select(segment => (segment.Start + (segment.Duration ?? TimeSpan.Zero)).TotalMilliseconds)
                        .DefaultIfEmpty(0).Max())).DefaultIfEmpty(0).Max();
        return MusicLyrics.ParseSyncedLines(lyrics.SyncedLyrics).Select(line =>
            Math.Max(line.StartMilliseconds, (line.Cues ?? []).Select(cue => cue.EndMilliseconds ?? cue.StartMilliseconds)
                .DefaultIfEmpty(0).Max())).DefaultIfEmpty(0).Max();
    }
}
