using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.Music;

public sealed partial class MusicMixService : IDisposable
{
    private readonly MemoryCache radioCache = new(new MemoryCacheOptions { SizeLimit = 256 });

    public async Task<IReadOnlyList<MusicSearchEntry<TPayload>>> RadioAsync<TPayload>(
        ProtocolExecutionContext context, string providerId, string resourceType, string externalId,
        string publicId, int limit, INativeMusicSearch<TPayload> native, CancellationToken cancellationToken = default)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        caller.Token.ThrowIfCancellationRequested();
        var clock = timeProvider ?? TimeProvider.System;
        var deadline = clock.GetUtcNow().AddSeconds(5);
        if (context.Deadline < deadline) deadline = context.Deadline;
        if (deadline <= clock.GetUtcNow()) return [];
        using var timeout = new CancellationTokenSource(deadline - clock.GetUtcNow(), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, timeout.Token);
        var token = bounded.Token;
        context = new(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId,
            context.Principal, context.CorrelationId, deadline, token, context.Client)
        { Policy = context.Policy };
        limit = Math.Clamp(limit, 1, 50);
        var result = new List<MusicSearchEntry<TPayload>>();
        var accepted = new List<Song>();
        var candidates = new List<Song>();
        var projection = musicSearch ?? new MusicSearchService(providers);
        string? cacheKey = null;
        var cached = false;
        string? seedArtist = null;
        try
        {
            if (effectivePolicies != null)
                context = context with
                {
                    Policy = await (context.Actor?.EffectiveUserId is { } user
                    ? effectivePolicies.ResolveForUserAsync(user, token) : effectivePolicies.ResolveAsync(token)).WaitAsync(token)
                };
            cacheKey = JsonSerializer.Serialize(new
            {
                context.Protocol,
                context.BackendInstanceId,
                context.VerifiedBackendPrincipalId,
                context.Actor,
                context.Policy,
                providerId,
                resourceType,
                externalId,
                publicId,
                limit
            });
            if (radioCache.TryGetValue<CachedRadio>(cacheKey, out var previous) && previous!.ExpiresAt > clock.GetUtcNow())
            {
                cached = true;
                seedArtist = previous.SeedArtist;
                foreach (var batch in previous.Songs.Chunk(50)) await AddAsync(batch);
            }
            else if (resourceType == "song")
            {
                var seed = await providers.GetSongAsync(context, providerId, externalId).WaitAsync(token);
                if (seed == null) return [];
                seedArtist = seed.Artist;
                await AddAsync([seed]);
                if (result.Count < limit)
                    await AddAsync(await ReadRadioSourceAsync(providers.GetSimilarSongsAsync(context, providerId, externalId, limit), token) ?? []);
                if (result.Count < limit && lastFm != null)
                {
                    var suggestions = await lastFm.FindAsync(context, seed.Artist, seed.Title,
                        Math.Min(limit, SuggestionLookups), token).WaitAsync(token);
                    foreach (var suggestion in suggestions)
                    {
                        if (result.Count >= limit) break;
                        var found = await ReadRadioSourceAsync(providers.SearchAsync(context,
                            $"{suggestion.Artist} {suggestion.Title}", 3, 0, 0), token);
                        await AddAsync(found?.Songs.Where(song =>
                            MusicCatalogMerge.LeadArtistMatches(song.Artist, FuzzyMatcher.NormalizeForMatching(suggestion.Artist)) &&
                            FuzzyMatcher.NormalizeForMatching(song.Title) == FuzzyMatcher.NormalizeForMatching(suggestion.Title)).ToArray() ?? []);
                    }
                }
            }
            else if (resourceType == "artist")
            {
                var seed = await providers.GetArtistAsync(context, providerId, externalId).WaitAsync(token);
                if (seed == null) return [];
                await AddAsync((await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, providerId, externalId), token) ?? []).Take(10).ToArray());
                if (result.Count < limit)
                {
                    var related = await ReadRadioSourceAsync(providers.GetSimilarArtistsAsync(context, providerId, externalId, 5), token) ?? [];
                    foreach (var artist in related.Take(5))
                    {
                        if (result.Count >= limit) break;
                        if (string.IsNullOrWhiteSpace(artist.ExternalId) || string.IsNullOrWhiteSpace(artist.ExternalProvider)) continue;
                        await AddAsync((await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context,
                            artist.ExternalProvider, artist.ExternalId), token) ?? []).Take(10).ToArray());
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { }
        catch (Exception) when (!token.IsCancellationRequested) { }
        caller.Token.ThrowIfCancellationRequested();
        if (!cached && cacheKey != null && candidates.Count > 0)
            radioCache.Set(cacheKey, new CachedRadio(clock.GetUtcNow().AddMinutes(1), candidates.ToArray(), seedArtist),
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
        if (taste != null && context.Actor?.EffectiveUserId is { } listener)
        {
            var ranked = await taste.RankAsync(listener, accepted, seedArtist, caller.Token);
            return ranked.Select(song =>
            {
                var row = result[accepted.FindIndex(item => item.Id == song.Id)];
                return row with { TasteReason = song.TasteReason, Song = row.Song?.WithTasteReason(song.TasteReason) };
            }).ToArray();
        }
        return result;

        async Task AddAsync(IReadOnlyList<Song> songs)
        {
            candidates.AddRange(songs.Take(50));
            if (taste != null && context.Actor?.EffectiveUserId is { } user)
                songs = await taste.RankAsync(user, songs, seedArtist, token);
            foreach (var song in songs.Take(50))
            {
                token.ThrowIfCancellationRequested();
                if (result.Count >= limit) break;
                if (context.Policy?.Includes(song) == false || accepted.Any(other => SameRadioRecording(other, song))) continue;
                try
                {
                    var rows = await projection.ProjectSongsAsync(context, [song], native, token).WaitAsync(token);
                    var row = rows.FirstOrDefault();
                    if (row == null || row.Native == null && !await providers.CanPlaySongAsync(context, song).WaitAsync(token)) continue;
                    token.ThrowIfCancellationRequested();
                    if (row.Native != null && result.Any(existing => existing.Native?.Id == row.Native.Id)) continue;
                    accepted.Add(song);
                    result.Add(row);
                    if (context.Actor?.EffectiveUserId != null)
                        await providers.RememberTrackAsync(context, song.Id).WaitAsync(token);
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }
        }
    }

    private static async Task<T?> ReadRadioSourceAsync<T>(Task<T> task, CancellationToken token)
    {
        try { return await task.WaitAsync(token); }
        catch (Exception) when (!token.IsCancellationRequested) { return default; }
    }

    private static bool SameRadioRecording(Song left, Song right) =>
        left.Id == right.Id || left.CanonicalRecordingId is { } recording && recording != Guid.Empty && recording == right.CanonicalRecordingId ||
        RadioIsrc(left.Isrc) is { } isrc && isrc == RadioIsrc(right.Isrc) ||
        MusicSearchService.MetadataRecordingsMatch(left, right);

    private static string? RadioIsrc(string? value)
    {
        var normalized = value?.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return normalized?.Length == 12 ? normalized : null;
    }

    public void Dispose() => radioCache.Dispose();

    private sealed record CachedRadio(DateTimeOffset ExpiresAt, IReadOnlyList<Song> Songs, string? SeedArtist);
}
