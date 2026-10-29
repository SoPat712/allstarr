using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

/// <summary>
/// Similar songs and instant mixes seeded from provider items, shared by every protocol.
/// Native seeds never reach this service; each protocol relays them to its backend.
/// </summary>
public sealed partial class MusicMixService(IProtocolProviderGateway providers, LastFmSimilarTracks? lastFm = null,
    ListenBrainzSimilarTracks? listenBrainz = null, MusicSearchService? musicSearch = null,
    IEffectiveProviderPolicyResolver? effectivePolicies = null, TimeProvider? timeProvider = null, TasteService? taste = null)
{
    private const int SuggestionLookups = 10;

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
        var candidates = recommended.Count > 0 ? recommended : string.IsNullOrWhiteSpace(song.Artist) ? [] :
            await SearchSongsAsync(context, providerId, song.Artist, limit);
        candidates = candidates.Where(candidate => !IsSeed(candidate, externalId, publicId)).ToArray();
        if (taste != null && context.Actor?.EffectiveUserId is { } user)
            candidates = await taste.RankAsync(user, candidates, song.Artist, context.CancellationToken);
        return candidates.Take(limit).ToArray();
    }

    /// <summary>
    /// An album mix in an order that stays stable for the same seed.
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
        if (resourceType != "album") return [];
        var album = await providers.GetAlbumAsync(context, providerId, externalId);
        if (album == null) return [];
        mix.AddRange(album.Songs);
        var artistName = album.Artist;

        if (mix.Count < limit && !string.IsNullOrWhiteSpace(artistName))
        {
            var songs = await SearchSongsAsync(context, providerId, artistName, limit);
            mix.AddRange(songs.Where(song => mix.All(existing => existing.Id != song.Id)));
        }

        return mix
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
