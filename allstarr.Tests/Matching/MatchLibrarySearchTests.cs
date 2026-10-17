using System.Collections.Concurrent;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;

namespace allstarr.Tests;

public sealed class MatchLibrarySearchTests
{
    [Theory]
    [InlineData("Silk & Cologne Ei8ht", "Silk & Cologne")]
    [InlineData("Café and Cologne (Extended) - Live Ei8ht", "Cafe & Cologne")]
    public async Task Search_merges_title_variants_artist_songs_and_isrc(string query, string expectedQuery)
    {
        var client = new LibraryClient(expectedQuery);
        var search = new MatchLibrarySearch(client);
        var results = await search.GatherAsync(Context(), ["music"], query, Source(), default);

        Assert.Contains(query, client.Queries);
        Assert.Contains(expectedQuery, client.Queries, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Ei8ht", "Offset" }, client.Artists.Order().ToArray());
        Assert.Contains("US1234567890", client.Isrcs);
        Assert.Equal(new[] { "artist-song", "isrc-song", "title-song" }, results.Select(item => item.BackendItemId).Order().ToArray());
        Assert.InRange(client.Peak, 1, 3);
    }

    [Fact]
    public async Task Search_returns_completed_results_when_another_strategy_exceeds_the_budget()
    {
        var client = new LibraryClient("Silk & Cologne") { StallArtist = true };
        var search = new MatchLibrarySearch(client, TimeSpan.FromMilliseconds(100));
        var results = await search.GatherAsync(Context(), ["music"], "Silk & Cologne", Source(), default);

        Assert.Contains(results, item => item.BackendItemId == "title-song");
        Assert.True(client.Cancelled);
    }

    [Fact]
    public async Task Search_does_not_replace_a_custom_query_with_the_source_title()
    {
        var client = new LibraryClient("Other Song");
        var results = await new MatchLibrarySearch(client).GatherAsync(Context(), ["music"], "Other Song Ei8ht", Source(), default);
        Assert.Contains("Other Song", client.Queries);
        Assert.DoesNotContain("Silk & Cologne", client.Queries);
        Assert.Contains(results, item => item.BackendItemId == "title-song");
    }

    [Fact]
    public async Task Slow_title_queries_leave_time_for_artist_and_isrc_strategies()
    {
        var client = new LibraryClient("Cafe & Cologne") { StallQueries = true };
        var results = await new MatchLibrarySearch(client, TimeSpan.FromSeconds(2)).GatherAsync(
            Context(), ["music"], "Café and Cologne (Extended) - Live Ei8ht", Source(), default);
        Assert.Contains(results, item => item.BackendItemId == "artist-song");
        Assert.Contains(results, item => item.BackendItemId == "isrc-song");
    }

    private static ExternalTrackMatchSnapshot Source() => new("snapshot", "spotify", "source", "Silk & Cologne", "Ei8ht, Offset, Third", "Album", null, 180000, "US1234567890", null, null);

    private static ProtocolExecutionContext Context()
    {
        var user = Guid.CreateVersion7();
        return new(ProtocolKind.Jellyfin, "server", "listener", new AllstarrPrincipal(user, "jellyfin", "server", "listener", "Listener", false), "search", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }

    private sealed class LibraryClient(string titleQuery) : IMatchLibraryClient
    {
        public ConcurrentBag<string> Queries { get; } = [];
        public ConcurrentBag<string> Artists { get; } = [];
        public ConcurrentBag<string> Isrcs { get; } = [];
        public bool StallArtist { get; init; }
        public bool StallQueries { get; init; }
        public bool Cancelled { get; private set; }
        public int Peak;
        private int _active;

        public async Task<IReadOnlyList<LibraryTrackIndexInput>> SearchAsync(ProtocolExecutionContext context, string libraryId, string query, CancellationToken token)
        {
            Queries.Add(query);
            if (StallQueries) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            await EnterAsync(token);
            return query.Equals(titleQuery, StringComparison.OrdinalIgnoreCase) ? [Track("title-song", titleQuery)] : [];
        }

        public async Task<IReadOnlyList<LibraryTrackIndexInput>> ArtistSongsAsync(ProtocolExecutionContext context, string libraryId, string artist, CancellationToken token)
        {
            Artists.Add(artist);
            if (StallArtist)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            await EnterAsync(token);
            return [Track("title-song", titleQuery), Track("artist-song", titleQuery), Track("unrelated", "Unrelated Song")];
        }

        public async Task<IReadOnlyList<LibraryTrackIndexInput>> IsrcAsync(ProtocolExecutionContext context, string libraryId, string isrc, CancellationToken token)
        {
            Isrcs.Add(isrc);
            await EnterAsync(token);
            return [Track("isrc-song", titleQuery)];
        }

        private async Task EnterAsync(CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = Peak; }
            while (active > observed && Interlocked.CompareExchange(ref Peak, active, observed) != observed);
            try { await Task.Delay(5, token); }
            finally { Interlocked.Decrement(ref _active); }
        }

        private static LibraryTrackIndexInput Track(string id, string title) => new("music", id, $"/music/{id}.flac", title, "Ei8ht", "Album", null, 180000, "jellyfin", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow);
    }
}
