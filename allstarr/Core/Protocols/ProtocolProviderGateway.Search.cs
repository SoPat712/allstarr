using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Models.Search;
using allstarr.Models.Settings;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> searchWork = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SearchFailureState> searchFailures = new(StringComparer.Ordinal);

    private CacheSettings SearchSettings => searchSettings?.Value ??
        configuration?.GetSection("Cache").Get<CacheSettings>() ?? new();

    private async Task<IReadOnlyList<SearchSnapshot>> SearchPlayableProvidersWithinBudgetAsync(
        IReadOnlyList<ProviderRouteCandidate<IProviderMetadataCapability>> candidates,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var budget = TimeSpan.FromSeconds(9);
        var settings = SearchSettings;
        var started = clock.GetTimestamp();
        var tasks = candidates.Select(async candidate =>
        {
            var snapshot = await SearchProviderWithinWindowAsync(
                candidate, query, limit, true, false, false, settings);
            return (Snapshot: snapshot, CompletedAfter: clock.GetElapsedTime(started));
        }).ToArray();
        try
        {
            var remaining = budget - clock.GetElapsedTime(started);
            await Task.WhenAll(tasks).WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                clock, cancellationToken);
        }
        catch (TimeoutException)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        var droppedProviders = candidates.Where((_, index) =>
                !tasks[index].IsCompletedSuccessfully || tasks[index].Result.CompletedAfter > budget)
            .Select(candidate => candidate.Provider.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (droppedProviders.Length > 0)
            logger?.LogInformation("Playable search budget expired for providers {ProviderIds}",
                string.Join(", ", droppedProviders));
        return tasks.Where(task => task.IsCompletedSuccessfully && task.Result.CompletedAfter <= budget)
            .Select(task => task.Result.Snapshot)
            .OfType<SearchSnapshot>()
            .ToArray();
    }

    private async Task<SearchSnapshot?> SearchProviderWithinWindowAsync(
        ProviderRouteCandidate<IProviderMetadataCapability> candidate, string query, int limit,
        bool songs, bool albums, bool artists, CacheSettings settings)
    {
        var context = candidate.Context;
        var scope = JsonSerializer.Serialize(new { context.Actor, context.Policy });
        var key = CacheKeyBuilder.BuildProviderSearchKey(candidate.Provider.Id, context.Account?.AccountId,
            context.Account?.Revision, scope, query, limit, songs, albums, artists);
        var work = searchWork.GetOrAdd(key, _ => new(() => SearchAsync()));
        var serialized = await work.Value;
        if (serialized == null) return null;
        var snapshot = JsonSerializer.Deserialize<SearchSnapshot>(serialized)!;
        for (var index = 0; index < snapshot.Result.Songs.Count; index++)
            snapshot.Result.Songs[index].PreciseDuration = snapshot.Durations[index];
        await CacheSongsAsync(context, snapshot.Result.Songs);
        await CacheResourcesAsync(context, snapshot.Result.Albums, snapshot.Result.Artists);
        return snapshot;

        async Task<string?> SearchAsync()
        {
            try
            {
                return await FetchAsync();
            }
            catch (Exception)
            {
                logger?.LogDebug("Provider search cache work failed for {ProviderId}", candidate.Provider.Id);
                return null;
            }
            finally
            {
                searchWork.TryRemove(key, out _);
            }
        }

        async Task<string?> FetchAsync()
        {
            var clock = timeProvider ?? TimeProvider.System;
            var deadline = clock.GetUtcNow() + settings.SearchBackgroundWindow;
            using var timeout = new CancellationTokenSource(settings.SearchBackgroundWindow, clock);
            if (applicationCache?.IsEnabled == true)
            {
                try
                {
                    var cached = await applicationCache.GetStringAsync(key).WaitAsync(timeout.Token);
                    if (cached != null && JsonSerializer.Deserialize<SearchSnapshot>(cached) is { } snapshot &&
                        snapshot.Durations?.Length == snapshot.Result.Songs.Count &&
                        snapshot.TrackIds?.Length == snapshot.Result.Songs.Count) return cached;
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception)
                {
                    logger?.LogDebug("Provider search cache read failed for {ProviderId}", candidate.Provider.Id);
                }
            }

            var failureKey = $"{candidate.Provider.Id}:{context.Account?.AccountId}:{context.Account?.Revision}";
            var failures = searchFailures.GetOrAdd(failureKey, _ => new());
            lock (failures)
            {
                if (failures.RetryAfter > clock.GetUtcNow())
                {
                    logger?.LogDebug("Skipping search for {ProviderId} during cooldown until {RetryAfter}",
                        candidate.Provider.Id, failures.RetryAfter);
                    return null;
                }
            }

            // Search may warm the cache after the listener response has finished.
            var execution = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account,
                context.Policy, context.OperationId, context.CorrelationId,
                deadline, timeout.Token);
            var request = new ProviderMetadataSearchRequest(query, new ProviderPageRequest(limit));
            var songsTask = TrySearchAsync(() => candidate.Implementation.SearchTracksAsync(execution, request),
                timeout.Token, songs);
            var albumsTask = TrySearchAsync(() => candidate.Implementation.SearchAlbumsAsync(execution, request),
                timeout.Token, albums);
            var artistsTask = TrySearchAsync(() => candidate.Implementation.SearchArtistsAsync(execution, request),
                timeout.Token, artists);
            try
            {
                await Task.WhenAll(songsTask, albumsTask, artistsTask).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                RecordFailure("background timeout");
                return null;
            }

            var songPage = await songsTask;
            var albumPage = await albumsTask;
            var artistPage = await artistsTask;
            if (SearchFailed(songs, songPage) || SearchFailed(albums, albumPage) || SearchFailed(artists, artistPage))
                RecordFailure("provider failure");
            else
            {
                lock (failures)
                {
                    failures.Count = 0;
                    failures.RetryAfter = default;
                }
            }

            var albumItems = albumPage?.IsSuccess == true ? albumPage.RequireValue().Items : [];
            var artistItems = artistPage?.IsSuccess == true ? artistPage.RequireValue().Items : [];
            var result = new SearchResult
            {
                Songs = songPage?.IsSuccess == true
                    ? songPage.RequireValue().Items.Select(item => EnrichRelationships(Map(item), albumItems, artistItems)).ToList()
                    : [],
                Albums = albumItems.Select(Map).ToList(),
                Artists = artistItems.Select(Map).ToList()
            };
            var serialized = JsonSerializer.Serialize(new SearchSnapshot(result,
                result.Songs.Select(song => song.PreciseDuration).ToArray(),
                songPage?.IsSuccess == true ? songPage.RequireValue().Items.Select(item => item.Id).ToArray() : []));
            if (applicationCache?.IsEnabled == true &&
                (songPage?.IsSuccess == true || albumPage?.IsSuccess == true || artistPage?.IsSuccess == true))
            {
                try
                {
                    await applicationCache.SetStringAsync(key, serialized, settings.SearchResultsTTL).WaitAsync(timeout.Token);
                }
                catch (Exception)
                {
                    logger?.LogDebug("Provider search cache write failed for {ProviderId}", candidate.Provider.Id);
                }
            }
            return serialized;

            void RecordFailure(string reason)
            {
                lock (failures)
                {
                    failures.Count++;
                    if (failures.Count < 3) return;
                    failures.RetryAfter = clock.GetUtcNow() + settings.SearchFailureCooldown;
                    logger?.LogWarning("Search for {ProviderId} cooling down until {RetryAfter} after repeated {Reason}",
                        candidate.Provider.Id, failures.RetryAfter, reason);
                }
            }
        }
    }

    private static bool SearchFailed<T>(bool enabled, ProviderOutcome<ProviderPage<T>>? outcome) =>
        enabled && (outcome == null || !outcome.IsSuccess &&
            outcome.Error?.Kind is not (ProviderErrorKind.NotSupported or ProviderErrorKind.NotFound));

    private sealed record SearchSnapshot(SearchResult Result, TimeSpan?[] Durations, ProviderExternalResourceId[] TrackIds);

    private sealed class SearchFailureState
    {
        public int Count { get; set; }
        public DateTimeOffset RetryAfter { get; set; }
    }
}
