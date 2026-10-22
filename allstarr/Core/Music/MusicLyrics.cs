using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

public sealed record ProviderSongLyrics(Song Song, LyricsInfo? Lyrics);

public sealed record TimedLyricLine(long StartMilliseconds, string Text, IReadOnlyList<TimedLyricCue>? Cues = null);

public sealed record TimedLyricCue(int Position, int EndPosition, long StartMilliseconds, long? EndMilliseconds);

/// <summary>
/// Lyrics lookup shared by every protocol: provider song metadata, lookup metadata without
/// presentation labels, and the configured source order through the protocol resolver.
/// Protocols keep only request parsing, native lyrics and response shaping.
/// </summary>
public static partial class MusicLyrics
{
    // Current external labels first, then older source labels still found in saved titles.
    private static readonly string[] PresentationLabels = [" [A]/[E]", " [A]", " [E]", " [S]", " [D]", " [Q]", " [AM]"];

    /// <summary>Returns null when the provider has no such song; otherwise the song and any lyrics found.</summary>
    public static async Task<ProviderSongLyrics?> FindForProviderSongAsync(
        IProtocolProviderGateway providers,
        IProtocolLyricsResolver lyrics,
        ProtocolExecutionContext protocol,
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        var song = await providers.GetSongAsync(protocol, provider, externalId);
        cancellationToken.ThrowIfCancellationRequested();
        if (song == null) return null;

        var found = await lyrics.FindAsync(
            protocol, ForLookup(song), $"{provider}:{externalId}", provider, externalId, song.SpotifyId);
        return new ProviderSongLyrics(song, found);
    }

    public static Task<LyricsInfo?> FindForLibrarySongAsync(
        IProtocolLyricsResolver lyrics,
        ProtocolExecutionContext protocol,
        Song song,
        string resourceKey,
        string? spotifyTrackId) =>
        lyrics.FindAsync(protocol, ForLookup(song), resourceKey, spotifyTrackId: spotifyTrackId);

    // Provider labels are presentation-only and degrade lyric matching.
    public static Song ForLookup(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        var artist = StripPresentationLabels(song.Artist);
        var artists = song.Artists.Select(StripPresentationLabels).ToList();
        if (artists.Count == 0 && !string.IsNullOrEmpty(artist)) artists.Add(artist);

        return new Song
        {
            Id = song.Id,
            CanonicalRecordingId = song.CanonicalRecordingId,
            IsLocal = song.IsLocal,
            ExternalProvider = song.ExternalProvider,
            ExternalId = song.ExternalId,
            Isrc = song.Isrc,
            Title = StripPresentationLabels(song.Title),
            Artist = artist,
            Artists = artists,
            Album = StripPresentationLabels(song.Album),
            Duration = song.Duration,
            PreciseDuration = song.PreciseDuration,
            SpotifyId = song.SpotifyId
        };
    }

    public static string StripPresentationLabels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        foreach (var label in PresentationLabels)
            value = value.Replace(label, "", StringComparison.Ordinal);
        return ExternalTrackPresentation.StripBestVersionPrefix(value).Trim();
    }

    /// <summary>
    /// Reads LRC lines in time order. A line may carry several timestamps, and fractions are
    /// read as written: ".1" is 100 ms, ".12" 120 ms and ".125" 125 ms. Tag lines such as
    /// "[ar:Artist]" and lines without a timestamp are skipped. A global offset advances
    /// positive values and delays negative values; projected timestamps are never negative.
    /// </summary>
    public static IReadOnlyList<TimedLyricLine> ParseSyncedLines(string? value)
    {
        var lines = new List<TimedLyricLine>();
        var rawLines = (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        long offset = 0;
        foreach (var rawLine in rawLines)
        {
            var match = OffsetRegex().Match(rawLine.Trim().TrimStart('\uFEFF'));
            if (match.Success && long.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) && Math.Abs(parsed) <= 86_400_000)
                offset = parsed;
        }
        foreach (var rawLine in rawLines)
        {
            var matches = TimestampRegex().Matches(rawLine);
            if (matches.Count == 0) continue;
            var text = TimestampRegex().Replace(rawLine, string.Empty).Trim();
            var wordTags = WordTimestampRegex().Matches(text);
            var plainText = WordTimestampRegex().Replace(text, string.Empty).Replace('\u2800', ' ');
            var leading = plainText.Length - plainText.TrimStart().Length;
            plainText = plainText.Trim();
            var firstTimestamp = ReadTimestamp(matches[0]);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var timestamp = ReadTimestamp(match);
                if (timestamp <= 86_400_000)
                {
                    var cues = new List<TimedLyricCue>();
                    var position = wordTags.Count > 0 ? wordTags[0].Index : 0;
                    var shift = timestamp - firstTimestamp - offset;
                    if (position > leading && wordTags.Count > 0)
                        cues.Add(new(0, Math.Min(position - leading, plainText.Length),
                            Math.Max(0, timestamp - offset), Math.Max(0, ReadTimestamp(wordTags[0]) + shift)));
                    for (var index = 0; index < wordTags.Count; index++)
                    {
                        var tag = wordTags[index];
                        var end = index + 1 < wordTags.Count ? wordTags[index + 1].Index : text.Length;
                        var length = end - tag.Index - tag.Length;
                        var startPosition = Math.Max(0, position - leading);
                        var endPosition = Math.Min(position + length - leading, plainText.Length);
                        if (endPosition > startPosition && !string.IsNullOrWhiteSpace(plainText[startPosition..endPosition]))
                            cues.Add(new(startPosition, endPosition, Math.Max(0, ReadTimestamp(tag) + shift),
                                index + 1 < wordTags.Count ? Math.Max(0, ReadTimestamp(wordTags[index + 1]) + shift) : null));
                        position += length;
                    }
                    lines.Add(new TimedLyricLine(Math.Max(0, timestamp - offset),
                        wordTags.Count > 0 ? plainText : text, cues.Count > 0 ? cues : null));
                }
            }
        }
        return lines.OrderBy(line => line.StartMilliseconds).ToList();
    }

    private static long ReadTimestamp(System.Text.RegularExpressions.Match match)
    {
        var minutes = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var milliseconds = match.Groups[3].Success
            ? int.Parse(match.Groups[3].Value.PadRight(3, '0')[..3], System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        return (((long)minutes * 60) + seconds) * 1000 + milliseconds;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<(\d{1,4}):([0-5]?\d)(?:[.:](\d{1,3}))?>")]
    private static partial System.Text.RegularExpressions.Regex WordTimestampRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d{1,4}):([0-5]?\d)(?:[.:](\d{1,3}))?\]")]
    private static partial System.Text.RegularExpressions.Regex TimestampRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\[offset:([+-]?\d{1,9})\]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OffsetRegex();
}
