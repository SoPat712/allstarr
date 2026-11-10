using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    IEffectiveProviderPolicyResolver? effectivePolicies = null, TimeProvider? timeProvider = null, TasteService? taste = null, IApplicationCache? applicationCache = null)
{
    private const int SuggestionLookups = 10;
    private readonly ConcurrentDictionary<string, CachedItemMix> itemMixCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<Song>>>> itemMixLoads = new();

    /// <summary>Songs by the seed song's artist, excluding the seed itself.</summary>
    public Task<IReadOnlyList<Song>> SimilarSongsAsync(
        ProtocolExecutionContext context,
        string providerId,
        string externalId,
        string publicId,
        int limit) => ReadItemMixAsync(context, providerId, "song", externalId, publicId, limit,
            bounded => SimilarSongsCoreAsync(bounded, providerId, externalId, publicId, limit));

    private async Task<IReadOnlyList<Song>> SimilarSongsCoreAsync(
        ProtocolExecutionContext context, string providerId, string externalId, string publicId, int limit)
    {
        IReadOnlyList<Song> candidates = [];
        try
        {
            var song = await providers.GetSongAsync(context, providerId, externalId).WaitAsync(context.CancellationToken);
            if (song == null) return [];
            var recommended = await RecommendedAsync(context, providerId, song, limit);
            candidates = recommended.Count > 0 ? recommended : string.IsNullOrWhiteSpace(song.Artist) ? [] :
                await SearchSongsAsync(context, providerId, song.Artist, limit);
            candidates = candidates.Where(candidate => !IsSeed(candidate, externalId, publicId)).ToArray();
            if (taste != null && context.Actor?.EffectiveUserId is { } user)
                candidates = await taste.RankAsync(user, candidates, song.Artist, context.CancellationToken)
                    .WaitAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
        return candidates.Where(candidate => !IsSeed(candidate, externalId, publicId)).Take(limit).ToArray();
    }

    /// <summary>
    /// An album mix in an order that stays stable for the same seed.
    /// </summary>
    public Task<IReadOnlyList<Song>> InstantMixAsync(
        ProtocolExecutionContext context,
        string providerId,
        string resourceType,
        string externalId,
        string publicId,
        int limit) => resourceType != "album" ? Task.FromResult<IReadOnlyList<Song>>([]) :
            ReadItemMixAsync(context, providerId, resourceType, externalId, publicId, limit,
                bounded => InstantMixCoreAsync(bounded, providerId, externalId, publicId, limit));

    private async Task<IReadOnlyList<Song>> InstantMixCoreAsync(
        ProtocolExecutionContext context, string providerId, string externalId, string publicId, int limit)
    {
        var mix = new List<Song>();
        try
        {
            var album = await providers.GetAlbumAsync(context, providerId, externalId).WaitAsync(context.CancellationToken);
            if (album == null) return [];
            mix.AddRange(album.Songs);
            if (mix.Count < limit && !string.IsNullOrWhiteSpace(album.Artist))
            {
                var songs = await SearchSongsAsync(context, providerId, album.Artist, limit);
                mix.AddRange(songs.Where(song => mix.All(existing => existing.Id != song.Id)));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }

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
        context.CancellationToken.ThrowIfCancellationRequested();
        var recommended = await providers.GetSimilarSongsAsync(context, providerId, seed.ExternalId ?? "", limit)
            .WaitAsync(context.CancellationToken) ?? [];
        if (recommended.Count > 0) return recommended;
        IReadOnlyList<SimilarTrackSuggestion> suggestions = lastFm == null ? [] :
            await lastFm.FindAsync(context, seed.Artist, seed.Title, Math.Min(limit, SuggestionLookups), context.CancellationToken)
                .WaitAsync(context.CancellationToken);
        if (suggestions.Count == 0 && listenBrainz != null)
            suggestions = await listenBrainz.FindAsync(context, seed.Id, Math.Min(limit, SuggestionLookups), context.CancellationToken)
                .WaitAsync(context.CancellationToken);
        using var matching = new SemaphoreSlim(4);
        var found = await Task.WhenAll(suggestions.Take(SuggestionLookups).Select(FindAsync));
        return found.OfType<Song>()
            .GroupBy(song => song.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        async Task<Song?> FindAsync(SimilarTrackSuggestion suggestion)
        {
            try { await matching.WaitAsync(context.CancellationToken); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { return null; }
            try { return await FindOnProviderAsync(context, providerId, suggestion); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { return null; }
            finally { matching.Release(); }
        }
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
        int limit)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        return (await providers.SearchAsync(context, query, limit, 0, 0, providerId)
            .WaitAsync(context.CancellationToken)).Songs;
    }

    private async Task<IReadOnlyList<Song>> ReadItemMixAsync(
        ProtocolExecutionContext context, string providerId, string resourceType, string externalId,
        string publicId, int limit, Func<ProtocolExecutionContext, Task<IReadOnlyList<Song>>> read)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var clock = timeProvider ?? TimeProvider.System;
        var key = JsonSerializer.Serialize(new
        {
            context.Protocol,
            context.BackendInstanceId,
            context.VerifiedBackendPrincipalId,
            context.Actor,
            context.Policy,
            context.Client,
            context.UseStoredBackendCredentials,
            context.BackendCredentialReferenceId,
            providerId,
            resourceType,
            externalId,
            publicId,
            limit
        });
        if (itemMixCache.TryGetValue(key, out var cached) && cached.ExpiresAt > clock.GetUtcNow())
            return Copy(cached.Songs);
        var persistedKey = CacheKeyBuilder.BuildProtocolMetadataKey("allstarr", key, "item-mix", publicId);
        if (applicationCache != null && await applicationCache.GetAsync<StoredItemMix>(persistedKey) is { } retained &&
            retained.Songs.Length == retained.Durations.Length && retained.Songs.Length == retained.Routes.Length)
        {
            for (var index = 0; index < retained.Songs.Length; index++)
            {
                retained.Songs[index].PreciseDuration = retained.Durations[index];
                retained.Songs[index].SearchRoutes = retained.Routes[index].ToList();
            }
            return Copy(retained.Songs);
        }
        var pending = itemMixLoads.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<Song>>>(LoadAsync));
        return Copy(await pending.Value.WaitAsync(context.CancellationToken));

        async Task<IReadOnlyList<Song>> LoadAsync()
        {
            try
            {
                if (itemMixCache.TryGetValue(key, out var ready) && ready.ExpiresAt > clock.GetUtcNow())
                    return ready.Songs;
                // A cancelled caller stops waiting while other callers retain the shared read.
                var duration = context.Deadline - clock.GetUtcNow();
                if (duration <= TimeSpan.Zero) return [];
                if (duration > TimeSpan.FromSeconds(3)) duration = TimeSpan.FromSeconds(3);
                using var timeout = new CancellationTokenSource(duration, clock);
                var bounded = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId,
                    context.VerifiedBackendPrincipalId, context.Principal, context.CorrelationId,
                    clock.GetUtcNow().Add(duration), timeout.Token, context.Client)
                {
                    Policy = context.Policy,
                    RequestedItemId = context.RequestedItemId,
                    IsReadOnly = context.IsReadOnly,
                    IsPreparation = context.IsPreparation,
                    PreparationQuality = context.PreparationQuality,
                    IsOriginalDownload = context.IsOriginalDownload,
                    RetainOriginalMedia = context.RetainOriginalMedia,
                    UseStoredBackendCredentials = context.UseStoredBackendCredentials,
                    BackendCredentialReferenceId = context.BackendCredentialReferenceId,
                    AudioDelivery = context.AudioDelivery,
                    RefreshLyrics = context.RefreshLyrics
                };
                var songs = Copy(await read(bounded));
                if (songs.Count > 0 || !timeout.IsCancellationRequested)
                {
                    itemMixCache[key] = new(clock.GetUtcNow().AddHours(6), songs);
                    if (applicationCache != null)
                        await applicationCache.SetAsync(persistedKey, new StoredItemMix(songs.ToArray(),
                            songs.Select(song => song.PreciseDuration).ToArray(), songs.Select(song => song.SearchRoutes.ToArray()).ToArray()), TimeSpan.FromHours(6));
                    if (itemMixCache.Count > 256)
                        foreach (var oldest in itemMixCache.OrderBy(item => item.Value.ExpiresAt).Take(itemMixCache.Count - 256))
                            itemMixCache.TryRemove(oldest.Key, out _);
                }
                return songs;
            }
            finally { itemMixLoads.TryRemove(key, out _); }
        }

        static IReadOnlyList<Song> Copy(IReadOnlyList<Song> songs) =>
            songs.Select(song => song.WithTasteReason(song.TasteReason)).ToArray();
    }

    private sealed record StoredItemMix(Song[] Songs, TimeSpan?[] Durations, allstarr.Core.Capabilities.ProviderExternalResourceId[][] Routes);
    private sealed record CachedItemMix(DateTimeOffset ExpiresAt, IReadOnlyList<Song> Songs);

    // Keep the same seed stable across retries and process restarts.
    internal static string StableOrder(string seedItemId, string candidateItemId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{seedItemId}\n{candidateItemId}")));
}
