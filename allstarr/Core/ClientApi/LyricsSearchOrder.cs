using System.Collections.Concurrent;
using allstarr.Core.Caching;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

/// <summary>
/// Orders lyric hits that match a line equally well. The listener's own copy, or a song they have played, comes first;
/// then the copy their own services list highest when the song's title is searched, because services rank an original
/// above its covers and karaoke versions. Every setup has at least one service, so this needs no keys or accounts.
/// </summary>
public sealed class LyricsSearchOrder(IProtocolProviderGateway providers, IApplicationCache cache,
    IDbContextFactory<AllstarrDbContext> factory, ILogger<LyricsSearchOrder> logger)
{
    private static readonly TimeSpan Kept = TimeSpan.FromHours(6);
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(20);
    // A title search the wait cut off keeps going, so the next search for the line is ordered.
    private static readonly ConcurrentDictionary<string, Task<TitleRanks?>> Searching = new(StringComparer.Ordinal);

    internal sealed record TitleRanks(Dictionary<string, int> ByArtist);

    internal sealed record Hit(string Title, string Artist, bool Owned);

    /// <summary>Hits whose <paramref name="tied"/> is true keep their place as a block and are reordered within it.</summary>
    internal async Task<IReadOnlyList<T>> OrderAsync<T>(ProtocolExecutionContext execution, IReadOnlyList<T> hits,
        Func<T, bool> tied, Func<T, Hit> song, TimeSpan wait)
    {
        var block = hits.Select((hit, index) => (Hit: hit, Index: index)).Where(item => tied(item.Hit)).ToArray();
        if (block.Length < 2 || (execution.Actor?.EffectiveUserId ?? execution.Principal?.UserId) is not { } owner) return hits;
        try
        {
            var songs = block.Select(item => song(item.Hit)).ToArray();
            var plays = await PlaysAsync(owner, songs, execution.CancellationToken);
            // The most common title in the block is the song itself; one title search covers its covers too, and is only
            // worth asking when at least two equal hits share it.
            var shared = songs.GroupBy(item => TitleKey(item.Title), StringComparer.Ordinal).OrderByDescending(group => group.Count()).First();
            var title = shared.First().Title;
            var ranks = shared.Count() > 1 ? await RanksAsync(execution, owner, title, wait) : null;
            var ordered = block.Select((item, order) => (item.Hit, Order: order, Song: songs[order]))
                .OrderByDescending(item => item.Song.Owned)
                .ThenByDescending(item => plays.GetValueOrDefault(Key(item.Song)))
                .ThenBy(item => TitleKey(item.Song.Title) == TitleKey(title) && ranks?.ByArtist.TryGetValue(ArtistKey(item.Song.Artist), out var rank) == true
                    ? rank : int.MaxValue)
                .ThenBy(item => item.Order).Select(item => item.Hit).ToArray();
            var result = hits.ToArray();
            for (var index = 0; index < block.Length; index++) result[block[index].Index] = ordered[index];
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException || !execution.CancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Lyric hit order unavailable ({ErrorType}); keeping the index order", error.GetType().Name);
            return hits;
        }
    }

    private async Task<Dictionary<string, int>> PlaysAsync(Guid owner, IReadOnlyList<Hit> songs, CancellationToken token)
    {
        var titles = songs.Select(item => item.Title).Distinct(StringComparer.Ordinal).ToArray();
        await using var db = await factory.CreateDbContextAsync(token);
        var played = await db.ListeningEvents.AsNoTracking()
            .Where(item => item.OwnerUserId == owner && item.Title != null && titles.Contains(item.Title))
            .Select(item => new { item.Title, item.Artist }).ToArrayAsync(token);
        return played.GroupBy(item => TitleKey(item.Title!) + "\u001f" + ArtistKey(item.Artist ?? ""), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    private async Task<TitleRanks?> RanksAsync(ProtocolExecutionContext execution, Guid owner, string title, TimeSpan wait)
    {
        var key = $"lyrics:order:v1:{owner}:{CanonicalCatalogKeys.Hash(TitleKey(title))}";
        if (await cache.GetAsync<TitleRanks>(key) is { } known) return known;
        var search = Searching.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try
            {
                using var stop = new CancellationTokenSource(SearchBudget);
                var detached = new ProtocolExecutionContext(execution.Protocol, execution.BackendInstanceId,
                    execution.VerifiedBackendPrincipalId, execution.Principal, execution.CorrelationId,
                    DateTimeOffset.UtcNow.Add(SearchBudget), stop.Token, execution.Client)
                { Policy = execution.Policy };
                var found = await providers.SearchAsync(detached, title, 40, 0, 0);
                // Each service's own order counts: an artist's rank is the best place any service lists the title under them.
                var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var service in found.Songs.GroupBy(item => item.ExternalProvider ?? "", StringComparer.Ordinal))
                    foreach (var (item, place) in service.Select((item, place) => (item, place)))
                        if (TitleKey(item.Title) == TitleKey(title) && ArtistKey(item.Artist) is { Length: > 0 } artist)
                            ranks[artist] = Math.Min(ranks.GetValueOrDefault(artist, int.MaxValue), place);
                var answer = new TitleRanks(ranks);
                await cache.SetAsync(key, answer, Kept);
                return answer;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                logger.LogWarning("Title search for lyric hit order failed ({ErrorType})", error.GetType().Name);
                return null;
            }
        }));
        _ = search.ContinueWith(_ => Searching.TryRemove(new KeyValuePair<string, Task<TitleRanks?>>(key, search)), TaskScheduler.Default);
        var finished = await Task.WhenAny(search, Task.Delay(wait, execution.CancellationToken));
        return finished == search ? await search : null;
    }

    private static string Key(Hit song) => TitleKey(song.Title) + "\u001f" + ArtistKey(song.Artist);

    private static string TitleKey(string title) => FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(title));

    private static string ArtistKey(string? artist) =>
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(artist).FirstOrDefault() ?? "");
}
