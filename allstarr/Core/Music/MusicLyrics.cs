using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

public sealed record ProviderSongLyrics(Song Song, LyricsInfo? Lyrics);

public sealed record TimedLyricLine(long StartMilliseconds, string Text);

/// <summary>
/// Lyrics lookup shared by every protocol: provider song metadata, lookup metadata without
/// presentation labels, and the configured source order through the protocol resolver.
/// Protocols keep only request parsing, native lyrics and response shaping.
/// </summary>
public static partial class MusicLyrics
{
    private static readonly string[] PresentationLabels = [" [S]", " [D]", " [Q]", " [AM]", " [E]"];

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
            Title = StripPresentationLabels(song.Title),
            Artist = artist,
            Artists = artists,
            Album = StripPresentationLabels(song.Album),
            Duration = song.Duration,
            SpotifyId = song.SpotifyId
        };
    }

    public static string StripPresentationLabels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        foreach (var label in PresentationLabels)
            value = value.Replace(label, "", StringComparison.Ordinal);
        return value.Trim();
    }

    /// <summary>
    /// Reads LRC lines in time order. A line may carry several timestamps, and fractions are
    /// read as written: ".1" is 100 ms, ".12" 120 ms and ".125" 125 ms. Tag lines such as
    /// "[ar:Artist]" and lines without a timestamp are skipped.
    /// </summary>
    public static IReadOnlyList<TimedLyricLine> ParseSyncedLines(string? value)
    {
        var lines = new List<TimedLyricLine>();
        foreach (var rawLine in (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var matches = TimestampRegex().Matches(rawLine);
            if (matches.Count == 0) continue;
            var text = TimestampRegex().Replace(rawLine, string.Empty).Trim();
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                var milliseconds = match.Groups[3].Success
                    ? int.Parse(match.Groups[3].Value.PadRight(3, '0')[..3], System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                lines.Add(new TimedLyricLine((((long)minutes * 60) + seconds) * 1000 + milliseconds, text));
            }
        }
        return lines.OrderBy(line => line.StartMilliseconds).ToList();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d{1,3}):([0-5]?\d)(?:[.:](\d{1,3}))?\]")]
    private static partial System.Text.RegularExpressions.Regex TimestampRegex();
}
