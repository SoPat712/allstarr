using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

public sealed record ProviderSongLyrics(Song Song, LyricsInfo? Lyrics);

/// <summary>
/// Lyrics lookup shared by every protocol: provider song metadata, lookup metadata without
/// presentation labels, and the configured source order through the protocol resolver.
/// Protocols keep only request parsing, native lyrics and response shaping.
/// </summary>
public static class MusicLyrics
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
}
