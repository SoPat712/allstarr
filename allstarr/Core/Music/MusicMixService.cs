using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

/// <summary>
/// Similar songs and instant mixes seeded from provider items, shared by every protocol.
/// Provider recommendations come first when the provider offers them; otherwise the seed
/// artist's catalog stands in.
/// Native seeds never reach this service; each protocol relays them to its backend.
/// </summary>
public sealed class MusicMixService(IProtocolProviderGateway providers, LastFmSimilarTracks? lastFm = null,
    ListenBrainzSimilarTracks? listenBrainz = null)
{
    private const int SuggestionLookups = 10;

    private const int AlbumsPerArtist = 3;

    /// <summary>Songs by the seed song's artist, excluding the seed itself.</summary>
    public async Task<IReadOnlyList<Song>> SimilarSongsAsync(
        ProtocolExecutionContext context,
        string providerId,
        string externalId,
        string publicId,
        int limit)
    {
        var song = await providers.GetSongAsync(context, providerId, externalId);
        if (song == null) return [];
        var recommended = await RecommendedAsync(context, providerId, song, limit);
        if (recommended.Count > 0)
            return recommended.Where(candidate => !IsSeed(candidate, externalId, publicId)).Take(limit).ToList();
        if (string.IsNullOrWhiteSpace(song.Artist)) return [];
        var candidates = await SearchSongsAsync(context, providerId, song.Artist, limit);
        return candidates
            .Where(candidate => !IsSeed(candidate, externalId, publicId))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// A mix around a provider song, album or artist: the artist's albums first, topped up by
    /// the artist's songs, in an order that stays stable for the same seed.
    /// </summary>
    public async Task<IReadOnlyList<Song>> InstantMixAsync(
        ProtocolExecutionContext context,
        string providerId,
        string resourceType,
        string externalId,
        string publicId,
        int limit)
    {
        var mix = new List<Song>();
        string? artistName;
        switch (resourceType.ToLowerInvariant())
        {
            case "album":
                {
                    var album = await providers.GetAlbumAsync(context, providerId, externalId);
                    if (album == null) return [];
                    mix.AddRange(album.Songs);
                    artistName = album.Artist;
                    break;
                }
            case "artist":
                {
                    var artist = await providers.GetArtistAsync(context, providerId, externalId);
                    if (artist == null) return [];
                    artistName = artist.Name;
                    await AddArtistAlbumsAsync(context, providerId, externalId, mix, limit);
                    break;
                }
            default:
                {
                    var song = await providers.GetSongAsync(context, providerId, externalId);
                    if (song == null) return [];
                    artistName = song.Artist;
                    // Provider recommendations lead a song mix; the artist's albums only fill the rest.
                    mix.AddRange(await RecommendedAsync(context, providerId, song, limit));
                    if (!string.IsNullOrEmpty(song.ExternalProvider) && !string.IsNullOrEmpty(song.ArtistId))
                    {
                        await AddArtistAlbumsAsync(context, song.ExternalProvider,
                            song.ArtistId.Replace($"ext-{song.ExternalProvider}-artist-", ""), mix, limit);
                    }
                    break;
                }
        }

        if (mix.Count < limit && !string.IsNullOrWhiteSpace(artistName))
        {
            var songs = await SearchSongsAsync(context, providerId, artistName, limit);
            mix.AddRange(songs.Where(song => mix.All(existing => existing.Id != song.Id)));
        }

        var seedIsSong = resourceType is not ("album" or "artist");
        return mix
            .Where(song => !seedIsSong || !IsSeed(song, externalId, publicId))
            .GroupBy(song => song.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(song => StableOrder(publicId, song.Id), StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Songs recommended after a seed: the provider's recommendations, then optional Last.fm
    /// or ListenBrainz suggestions resolved on the seed's provider. Unresolved suggestions are dropped.
    /// </summary>
    private async Task<IReadOnlyList<Song>> RecommendedAsync(
        ProtocolExecutionContext context,
        string providerId,
        Song seed,
        int limit)
    {
        var recommended = await providers.GetSimilarSongsAsync(context, providerId, seed.ExternalId ?? "", limit) ?? [];
        if (recommended.Count > 0) return recommended;
        IReadOnlyList<SimilarTrackSuggestion> suggestions = lastFm == null ? [] :
            await lastFm.FindAsync(context, seed.Artist, seed.Title, Math.Min(limit, SuggestionLookups), context.CancellationToken);
        if (suggestions.Count == 0 && listenBrainz != null)
            suggestions = await listenBrainz.FindAsync(context, seed.Id, Math.Min(limit, SuggestionLookups), context.CancellationToken);
        var found = await Task.WhenAll(suggestions.Select(suggestion => FindOnProviderAsync(context, providerId, suggestion)));
        return found.OfType<Song>()
            .GroupBy(song => song.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<Song?> FindOnProviderAsync(
        ProtocolExecutionContext context,
        string providerId,
        SimilarTrackSuggestion suggestion)
    {
        try
        {
            var artistKey = FuzzyMatcher.NormalizeForMatching(suggestion.Artist);
            var titleKey = FuzzyMatcher.NormalizeForMatching(suggestion.Title);
            var candidates = await SearchSongsAsync(context, providerId, $"{suggestion.Artist} {suggestion.Title}", 3);
            return candidates.FirstOrDefault(song =>
                MusicCatalogMerge.LeadArtistMatches(song.Artist, artistKey) &&
                (FuzzyMatcher.NormalizeForMatching(song.Title) == titleKey ||
                 FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(song.Title)) == titleKey));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsSeed(Song song, string externalId, string publicId) =>
        string.Equals(song.ExternalId, externalId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(song.Id, publicId, StringComparison.OrdinalIgnoreCase);

    private async Task AddArtistAlbumsAsync(
        ProtocolExecutionContext context,
        string providerId,
        string artistId,
        List<Song> mix,
        int limit)
    {
        var albums = await providers.GetArtistAlbumsAsync(context, providerId, artistId);
        foreach (var album in albums.Take(AlbumsPerArtist))
        {
            if (string.IsNullOrWhiteSpace(album.ExternalId)) continue;
            var full = await providers.GetAlbumAsync(context, providerId, album.ExternalId);
            if (full != null) mix.AddRange(full.Songs);
            if (mix.Count >= limit) break;
        }
    }

    private async Task<IReadOnlyList<Song>> SearchSongsAsync(
        ProtocolExecutionContext context,
        string providerId,
        string query,
        int limit) =>
        (await providers.SearchAsync(context, query, limit, 0, 0, providerId)).Songs;

    // Keep the same seed stable across retries and process restarts.
    internal static string StableOrder(string seedItemId, string candidateItemId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{seedItemId}\n{candidateItemId}")));
}
