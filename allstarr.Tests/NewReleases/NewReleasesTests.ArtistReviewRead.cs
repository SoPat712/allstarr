using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed partial class NewReleasesTests
{
    private readonly ITestOutputHelper _reviewOutput;

    public NewReleasesTests(ITestOutputHelper output) => _reviewOutput = output;

    [Fact]
    public async Task LibraryArtist_review_saved_statuses_match_fresh_parts()
    {
        var (review, index, _) = await ArtistReviewSetupAsync();
        await ArtistSongAsync("Shared", "linked", "Songs");
        await index.RecordCandidatesAsync(_first, ["Artist"], [new() { Name = "Artist", ExternalProvider = "deezer", ExternalId = "candidate" }]);
        await index.SetFixAsync(_first, "Decided", "deezer", "same", true);
        await index.SetFixAsync(_first, "Apart", "deezer", "apart", false);
        string[] names = ["Artist", "Songs", "Decided", "Apart", "Library"];
        var fresh = await index.ReadReviewAsync(_first, names, detail: true);
        var saved = await index.ReadReviewAsync(_first, names);
        foreach (var name in names)
        {
            Assert.Equal(fresh[name].Songs, saved[name].Songs);
            Assert.Equal(fresh[name].Fixes, saved[name].Fixes);
            Assert.Equal(JsonSerializer.Serialize(fresh[name].Candidates), JsonSerializer.Serialize(saved[name].Candidates));
            Assert.Empty(saved[name].Evidence);
        }
        var page = await review.ListAsync(ArtistReviewContext(_first), "all", null, null, 50, default);
        Assert.Equal(new ArtistReviewCounts(1, 1, 1, 1, 5), page.Counts);
        Assert.Equal(["needs-look", "linked", "decided", "apart", "library-only"], page.Artists.Select(row => row.Status));
    }

    [Fact]
    public async Task LibraryArtist_review_list_in_progress_does_not_block_parts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ReviewQueries();
        probe.Before = async command =>
        {
            if (!command.CommandText.Contains("library-artist-candidate", StringComparison.Ordinal) &&
                !command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value?.ToString() == "library-artist-candidate")) return;
            entered.TrySetResult();
            await release.Task;
        };
        var (review, index, _) = await ArtistReviewSetupAsync(interceptor: probe);
        var list = review.ListAsync(ArtistReviewContext(_first), "all", null, null, 50, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await index.ReadPartsAsync(_first, "Unrelated").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(list.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await list;
        }
    }

    [Fact]
    public async Task LibraryArtist_review_check_releases_the_lock_between_artists()
    {
        var first = await ArtistSongAsync("First song", null, "First");
        var second = await ArtistSongAsync("Second song", null, "Second");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherEntered = false;
        var secondSawOther = false;
        var probe = new ReviewQueries();
        probe.Before = async command =>
        {
            if (!command.CommandText.Contains("library_tracks", StringComparison.Ordinal)) return;
            var values = command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value?.ToString()).ToArray();
            if (values.Contains("First")) { entered.TrySetResult(); await release.Task; }
            if (values.Contains("Unrelated")) otherEntered = true;
            if (values.Contains("Second")) secondSawOther = otherEntered;
        };
        var index = ArtistIndex(factory: new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options).AddInterceptors(probe).Options));
        await ArtistIdentityAsync(first, "first");
        await ArtistIdentityAsync(second, "second");
        var check = index.RecordCandidatesAsync(_first, ["First", "Second"],
        [
            new() { Name = "First", ExternalProvider = "deezer", ExternalId = "namesake" },
            new() { Name = "Second", ExternalProvider = "deezer", ExternalId = "second-namesake" }
        ]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = index.ReadPartsAsync(_first, "Unrelated");
            release.TrySetResult();
            await Task.WhenAll(check, other).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); await check; }
        Assert.True(secondSawOther, "The waiting reader must run between the two artists.");
        Assert.Equal("first", Assert.Single((await index.ReadReviewAsync(_first, ["First"]))["First"].Songs).ArtistId);
        Assert.Equal("second", Assert.Single((await index.ReadReviewAsync(_first, ["Second"]))["Second"].Songs).ArtistId);
        // Later changes reach the saved links through the per-artist link refresh, not through candidate recording.
        await ArtistIdentityAsync(first, "conflict");
        await index.ReadPartsAsync(_first, "First");
        Assert.Empty((await index.ReadReviewAsync(_first, ["First"]))["First"].Songs);
        await using (var db = _factory.CreateDbContext())
        {
            db.LibraryTracks.Remove(await db.LibraryTracks.SingleAsync(track => track.Id == second.Id));
            await db.SaveChangesAsync();
        }
        await index.ReadPartsAsync(_first, "Second");
        Assert.Empty((await index.ReadReviewAsync(_first, ["Second"]))["Second"].Songs);
    }

    [Fact]
    public async Task LibraryArtist_review_detail_refreshes_saved_links_and_evidence()
    {
        var (review, index, _) = await ArtistReviewSetupAsync();
        var song = await ArtistSongAsync("Fresh title", null, "Songs");
        await index.ReadPartsAsync(_first, "Songs");
        await ArtistIdentityAsync(song, "fresh");
        Assert.Empty((await index.ReadReviewAsync(_first, ["Songs"]))["Songs"].Songs);
        var detail = await review.DetailAsync(ArtistReviewContext(_first), "songs", default);
        var service = Assert.Single(detail!.Services);
        Assert.Equal("fresh", service.ArtistId);
        Assert.Equal(["Fresh title"], service.SharedSongTitles);
        Assert.Equal(1, service.SharedSongs);
        Assert.NotNull(service.CheckedAt);
        Assert.Equal("fresh", Assert.Single((await index.ReadReviewAsync(_first, ["Songs"]))["Songs"].Songs).ArtistId);
    }

    [Fact]
    public async Task LibraryArtist_review_realistic_first_page_and_search_have_flat_queries()
    {
        var probe = new ReviewQueries();
        var (review, index, _) = await ArtistReviewSetupAsync(new ArtistReviewHttp(3000), probe);
        for (var number = 0; number < 300; number++)
            await ArtistSongAsync("Matched " + number, "linked-" + number, "Artist " + number);
        await using (var db = _factory.CreateDbContext())
        {
            for (var number = 300; number < 25000; number++)
                db.LibraryTracks.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = _first,
                    Protocol = "subsonic",
                    BackendInstanceId = "main",
                    BackendLibraryId = "music",
                    BackendItemId = "track-" + number,
                    FilePath = "/music/track-" + number,
                    Title = "Track " + number,
                    Artist = "Artist " + number % 3000,
                    DurationMilliseconds = 180000,
                    IndexedAt = _clock.UtcNow,
                    SourceModifiedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                });
            for (var number = 0; number < 600; number++)
            {
                var key = CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
                {
                    Protocol = allstarr.Core.Protocols.ProtocolKind.Subsonic,
                    BackendInstanceId = "main",
                    Household = true,
                    Name = allstarr.Services.Common.FuzzyMatcher.NormalizeForMatching("Artist " + number)
                }));
                var json = JsonSerializer.Serialize(new LibraryArtistEvidence[] { new("deezer", "candidate-" + number, "Artist " + number, 0, [], true) });
                db.ExternalMetadataSnapshots.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = _first,
                    ProviderId = "allstarr",
                    ResourceKind = "library-artist-candidate",
                    ExternalIdHash = key,
                    SnapshotVersion = 1,
                    ProviderRevision = "1",
                    PayloadJson = json,
                    PayloadSha256 = CanonicalCatalogKeys.Hash(json),
                    Protocol = "subsonic",
                    BackendInstanceId = "main",
                    BackendPrincipalId = _first.ToString(),
                    RetrievedAt = _clock.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        for (var number = 0; number < 300; number++) await index.ReadPartsAsync(_first, "Artist " + number);
        var context = ArtistReviewContext(_first);
        // Populate only the media-server cache; each measured review read starts from the database.
        Assert.Equal(3000, (await review.ReadLibraryAsync(context, default)).Count);
        probe.Reset();
        var watch = Stopwatch.StartNew();
        var page = await review.ListAsync(context, "needs-look", null, null, 50, default);
        var firstTime = watch.Elapsed;
        var firstQueries = probe.Count;
        var firstScans = probe.TrackScans;
        var firstWrites = probe.Writes;
        probe.Reset();
        watch.Restart();
        var search = await review.ListAsync(context, "needs-look", "Artist 1", null, 50, default);
        var searchTime = watch.Elapsed;
        var searchQueries = probe.Count;
        var searchScans = probe.TrackScans;
        var searchWrites = probe.Writes;
        probe.Reset();
        await index.ReadReviewAsync(_first, ["Artist 1"]);
        var smallQueries = probe.Count;
        var measurements = $"3000 artists, 25000 tracks, 300 identities and track snapshots, 300 saved indexes, 600 candidates: " +
            $"first page {firstTime.TotalMilliseconds:F1} ms / {firstQueries} queries / {firstScans} track scans / {firstWrites} writes; " +
            $"search q=Artist 1 {searchTime.TotalMilliseconds:F1} ms / {searchQueries} queries / {searchScans} track scans / {searchWrites} writes; one artist {smallQueries} queries";
        _reviewOutput.WriteLine(measurements);
        Assert.Equal(50, page.Artists.Count);
        Assert.Equal(new ArtistReviewCounts(600, 0, 0, 0, 3000), page.Counts);
        Assert.Equal(new ArtistReviewCounts(111, 0, 0, 0, 1111), search.Counts);
        Assert.True(firstScans == 0 && searchScans == 0 && firstWrites == 0 && searchWrites == 0 &&
            firstQueries == smallQueries && searchQueries == smallQueries && firstTime.TotalMilliseconds < 500 && searchTime.TotalMilliseconds < 500, measurements);
    }

    private sealed class ReviewQueries : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public int TrackScans { get; private set; }
        public int Writes { get; private set; }
        public Func<DbCommand, Task>? Before { get; set; }
        public void Reset() { Count = 0; TrackScans = 0; Writes = 0; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            if (command.CommandText.Contains("library_tracks", StringComparison.Ordinal)) TrackScans++;
            if (!command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)) Writes++;
            if (Before != null) await Before(command);
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            Writes++;
            return ValueTask.FromResult(result);
        }
    }
}
