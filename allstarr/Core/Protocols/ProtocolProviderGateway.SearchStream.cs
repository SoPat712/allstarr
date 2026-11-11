using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Routing;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private readonly Dictionary<string, SharedSearchWork> sharedSearchWork = new(StringComparer.Ordinal);
    private sealed class SharedSearchWork
    {
        public CancellationTokenSource Stop { get; } = new();
        public Lazy<Task<string?>> Work { get; }
        public SharedSearchWork(Func<CancellationToken, Task<string?>> fetch) =>
            Work = new(() => Task.Run(() => fetch(Stop.Token)));
        public int Readers;
    }

    private async Task<string?> RunSharedSearchAsync(string key, Func<CancellationToken, Task<string?>> fetch,
        CancellationToken cancellationToken)
    {
        SharedSearchWork entry;
        lock (sharedSearchWork)
        {
            if (!sharedSearchWork.TryGetValue(key, out entry!)) sharedSearchWork[key] = entry = new(fetch);
            entry.Readers++;
        }
        try { return await entry.Work.Value.WaitAsync(cancellationToken); }
        finally
        {
            bool finished;
            lock (sharedSearchWork)
            {
                finished = --entry.Readers == 0;
                if (finished) sharedSearchWork.Remove(key);
            }
            if (finished)
            {
                await entry.Stop.CancelAsync();
                _ = entry.Work.Value.ContinueWith(_ => entry.Stop.Dispose(), TaskScheduler.Default);
            }
        }
    }

    public async Task<IReadOnlyList<string>> StreamSearchAsync(ProtocolExecutionContext protocol, string query, int limit,
        bool songs, bool albums, bool artists, bool playlists,
        Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> writeBatch,
        CancellationToken cancellationToken = default)
    {
        var clock = timeProvider ?? TimeProvider.System;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(protocol.CancellationToken, cancellationToken, deadline.Token);
        var token = stop.Token;
        protocol = new(protocol.Protocol, protocol.BackendInstanceId, protocol.VerifiedBackendPrincipalId,
            protocol.Principal, protocol.CorrelationId, clock.GetUtcNow().AddSeconds(10), token, protocol.Client)
        { Policy = protocol.Policy };
        var pending = new List<(string Provider, Task<(SearchResult Items, IReadOnlyList<ExternalPlaylist> Playlists)> Work)>();
        try
        {
            var actor = protocol.RequireActor();
            var policy = await ResolveEffectivePolicyAsync(protocol).WaitAsync(token);
            var playable = songs ? await ResolvePlayableProviderOrderAsync(protocol, actor,
                ResolveProviderOrder(ProviderCapabilityKind.Streaming, policy)).WaitAsync(token) : [];
            var settings = SearchSettings;
            limit = Math.Clamp(limit, 1, 200);
            if (songs || albums || artists)
            {
                var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(protocol, actor,
                    ProviderCapabilityKind.Metadata, "protocol-stream-search", ResolveProviderOrder(ProviderCapabilityKind.Metadata, policy),
                    null, purpose: ProviderRoutePurpose.ManualSelection)).WaitAsync(token);
                foreach (var candidate in plan.Candidates)
                    pending.Add((candidate.Provider.Id, MetadataAsync(candidate)));
            }
            if (playlists)
            {
                var plan = await router.PlanAsync<IProviderPlaylistCapability>(Request(protocol, actor,
                    ProviderCapabilityKind.Playlist, "protocol-stream-playlist-search", ResolveProviderOrder(ProviderCapabilityKind.Playlist, policy),
                    null)).WaitAsync(token);
                foreach (var candidate in plan.Candidates)
                    pending.Add((candidate.Provider.Id, PlaylistsAsync(candidate)));
            }
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(pending.Select(item => item.Work)).WaitAsync(token);
                var index = pending.FindIndex(item => item.Work == completed);
                var batch = await completed;
                token.ThrowIfCancellationRequested();
                await ArtistArtworkCache.PrepareSearchAsync(applicationCache, protocol, [], batch.Items.Artists,
                    batch.Items.Albums, batch.Items.Songs);
                await writeBatch(pending[index].Provider, batch.Items, batch.Playlists, token).WaitAsync(token);
                pending.RemoveAt(index);
            }

            async Task<(SearchResult, IReadOnlyList<ExternalPlaylist>)> MetadataAsync(ProviderRouteCandidate<IProviderMetadataCapability> candidate)
            {
                var snapshot = await SearchProviderWithinWindowAsync(candidate, query, limit, songs, albums, artists, settings, token);
                if (snapshot == null) return (new(), []);
                var result = snapshot.Result;
                var tracks = playable.Contains(NormalizeProvider(candidate.Provider.Id), StringComparer.Ordinal)
                    ? result.Songs.Where(song => policy?.Includes(song) != false).ToArray() : [];
                var lookups = result.Songs.Select((song, index) => (Song: song,
                    Lookup: new TrackIdentityLookup(candidate.Context, snapshot.TrackIds[index])))
                    .Where(item => tracks.Contains(item.Song)).ToArray();
                var collapsed = await CollapseVerifiedSearchTracksAsync(tracks, lookups, playable, policy, protocol).WaitAsync(token);
                var knownDurationTitles = collapsed.Where(song => (song.PreciseDuration?.TotalSeconds ?? song.Duration) is > 0)
                    .Select(song => SearchRelevanceRanker.RecordingTitle(song.Title, song.Artist, song.AlbumArtist)).ToHashSet(StringComparer.Ordinal);
                // The shared scorer gives exact/prefix matches 95–100 and accepts token fits from 70.
                const double minimumArtistScore = 70;
                var rankedArtists = result.Artists.Select(artist => (Artist: artist, Score: SearchRelevanceRanker.Score(query, artist.Name)))
                    .OrderByDescending(item => item.Score);
                return (new SearchResult
                {
                    Songs = collapsed.OrderByDescending(song => SearchRelevanceRanker.ScoreSong(query, song,
                        knownDurationTitles.Contains(SearchRelevanceRanker.RecordingTitle(song.Title, song.Artist, song.AlbumArtist)))).Take(limit).ToList(),
                    Albums = CleanAlbums(result.Albums, policy).OrderByDescending(album => SearchRelevanceRanker.ScoreAlbum(query, album)).Take(limit).ToList(),
                    Artists = rankedArtists.Where((item, index) => index == 0 || item.Score >= minimumArtistScore)
                        .Take(limit).Select(item => item.Artist).ToList()
                }, []);
            }

            async Task<(SearchResult, IReadOnlyList<ExternalPlaylist>)> PlaylistsAsync(ProviderRouteCandidate<IProviderPlaylistCapability> candidate)
            {
                var context = candidate.Context;
                var scope = JsonSerializer.Serialize(new { context.Actor, context.Policy, Kind = "playlists" });
                var key = CacheKeyBuilder.BuildProviderSearchKey(candidate.Provider.Id, context.Account?.AccountId,
                    context.Account?.Revision, scope, query, limit, false, false, false);
                var value = await RunSharedSearchAsync(key, async cancellation =>
                {
                    using var gate = new SemaphoreSlim(ProviderSearchConcurrency);
                    return JsonSerializer.Serialize(await SearchPlaylistsCachedAsync(candidate, query, limit, settings, gate,
                        false, cancellation));
                }, token);
                List<ExternalPlaylist> found = value == null ? [] : JsonSerializer.Deserialize<List<ExternalPlaylist>>(value) ?? [];
                return (new(), found.OrderByDescending(playlist => SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName))
                    .Take(limit).ToArray());
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await stop.CancelAsync(); }
        return pending.Select(item => item.Provider).Distinct(StringComparer.Ordinal).ToArray();
    }
}
