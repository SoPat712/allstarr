using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    private async Task RunRefreshJob(string key, Guid? mix = null)
    {
        var jobs = new DurableJobQueue(factory, new(), new(new()), clock);
        await jobs.EnqueueAsync(new DurableJobEnqueueRequest<Guid>(PersonalMixService.JobType, key, mix ?? Guid.Empty, owner));
        var claim = await jobs.ClaimNextAsync("mix-worker", [PersonalMixService.JobType]);
        Assert.NotNull(claim);
        using var services = new ServiceCollection().BuildServiceProvider();
        var completion = await new PersonalMixJobHandler(service).ExecuteAsync(new(claim, services), default);
        await jobs.CompleteAsync(claim, completion);
    }

    private sealed class ChartAccounts(Guid owner) : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken token)
        {
            Assert.Equal(owner, scope.OwnerUserId);
            Assert.Equal("subsonic", scope.Protocol);
            Assert.Equal("main", scope.BackendInstanceId);
            Assert.Equal("lastfm", providerId);
            return Task.FromResult(true);
        }
        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken token)
        {
            using var secret = JsonDocument.Parse("""{"apiKey":"fixture-key"}""");
            return await operation(secret.RootElement, token);
        }
    }

    [Fact]
    public async Task Chill_without_analysis_uses_the_connected_account_chart_key()
    {
        var chart = EnableChart(scopedKey: true);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.True(mix.Readiness.Ready);
        Assert.Equal(40, mix.Evidence!.FromServices);
        Assert.Equal(21, mix.Evidence.ProviderReads);
        Assert.Equal(1, chart.Reads);
        Assert.All(mix.Songs, x => Assert.Contains("Last.fm tags this chill", x.Reason));
    }

    [Fact]
    public async Task Missing_chart_key_is_reported_without_spending_reads()
    {
        var chart = EnableChart(missingKey: true);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Empty(mix.Songs);
        Assert.Equal(0, chart.Reads);
        Assert.Equal(0, mix.Evidence!.ProviderReads);
        Assert.Contains("No usable Last.fm key", JsonSerializer.Serialize(mix.Evidence));
    }

    [Fact]
    public async Task Blend_uses_the_played_artists_own_style_for_genreless_unplayed_tracks()
    {
        await Seed(count: 1, plays: 10);
        gateway.Setup(x => x.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist 0", 0, 0, 5, "deezer"))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult { Artists = [new Artist { Name = "Artist 0", ExternalId = "seed" }] });
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "Artist 0", 100))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([new Song { Id = "fresh", Title = "Unplayed",
                Artist = "Artist 0", ExternalProvider = "deezer", ExternalId = "fresh" },
                new Song { Id = "unrelated", Title = "Unplayed", Artist = "Unplayed artist", ExternalProvider = "deezer" }]));
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "daily-1").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal("New to you · rock", Assert.Single(mix.Songs, x => x.Song.Id == "fresh").Reason);
        Assert.DoesNotContain(mix.Songs, x => x.Song.Id == "unrelated");
        var chillId = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        Assert.Empty((await service.GenerateAsync(owner, chillId)).Single(x => x.Id == chillId).Songs);
    }

    [Fact]
    public async Task Ten_history_tag_refreshes_keep_service_songs_without_reads()
    {
        var chart = EnableChart();
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        var first = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(40, first.Songs.Count);
        var reads = chart.Reads;
        for (var i = 0; i < 10; i++)
        {
            clock.UtcNow += TimeSpan.FromMinutes(16);
            gateway.Invocations.Clear();
            service = NewService();
            await RunRefreshJob("history-tags:" + owner + ":" + i);
            var kept = await service.GetAsync(owner, id);
            Assert.Equal(first.Songs.Select(x => x.Song.Id), kept.Songs.Select(x => x.Song.Id));
            Assert.Equal(0, kept.Evidence!.ProviderReads);
            Assert.Equal(reads, chart.Reads);
            Assert.DoesNotContain(gateway.Invocations, x => x.Method.Name is "GetSongAsync" or "SearchAutomaticProviderSongsAsync" or "SearchAsync" or "GetSimilarArtistsAsync");
        }
        await using var db = factory.CreateDbContext();
        var row = await db.ExternalMetadataSnapshots.OrderByDescending(x => x.SnapshotVersion).FirstAsync();
        var json = JsonDocument.Parse(row.PayloadJson).RootElement;
        Assert.InRange(json.GetProperty("FillSongs").GetArrayLength(), 40, 500);
        Assert.All(json.GetProperty("FillSongs").EnumerateArray(), x => Assert.True(x.GetProperty("ReadAt").GetDateTimeOffset() <= clock.UtcNow));
    }

    [Fact]
    public async Task Failed_manual_job_retry_never_reads_providers()
    {
        var chart = EnableChart();
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        var jobs = new DurableJobQueue(factory, new(), new(new()), clock);
        await jobs.EnqueueAsync(new DurableJobEnqueueRequest<Guid>(PersonalMixService.JobType, "mix-refresh:failed", id, owner));
        var claim = (await jobs.ClaimNextAsync("mix-worker", [PersonalMixService.JobType]))!;
        using var services = new ServiceCollection().BuildServiceProvider();
        var validations = 0;
        access.Setup(x => x.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++validations > 1 ? throw new InvalidOperationException("Selection interrupted.") : Task.FromResult(new BackendLibraryAccess(true, [])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PersonalMixJobHandler(service).ExecuteAsync(new(claim, services), default));
        Assert.True(chart.Reads > 0);
        var reads = chart.Reads;
        gateway.Invocations.Clear();
        access.Setup(x => x.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BackendLibraryAccess(true, []));
        await jobs.CompleteAsync(claim, DurableJobCompletion.Retry("mix-interrupted", "Refresh interrupted.", TimeSpan.FromSeconds(1)));
        clock.UtcNow += TimeSpan.FromSeconds(2);
        var retry = (await jobs.ClaimNextAsync("mix-worker", [PersonalMixService.JobType]))!;
        Assert.Equal(2, retry.AttemptNumber);
        await new PersonalMixJobHandler(NewService()).ExecuteAsync(new(retry, services), default);
        Assert.Equal(reads, chart.Reads);
        Assert.DoesNotContain(gateway.Invocations, x => x.Method.Name is "GetSongAsync" or "SearchAutomaticProviderSongsAsync" or "SearchAsync");
        Assert.True((await service.GetAsync(owner, id)).Readiness.Ready);
    }

    [Fact]
    public async Task Explicit_blend_refresh_generates_only_that_recipe()
    {
        await Seed(count: 1);
        EnableChart();
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "daily-1").Id;
        var result = await service.GenerateAsync(owner, id);
        Assert.All(result.Where(x => x.Id != id), x => Assert.Equal(0, x.Generation));
        Assert.InRange(result.Single(x => x.Id == id).Evidence!.ProviderReads!.Value, 1, 30);
    }

    [Fact]
    public async Task Scheduled_generation_reads_each_recipe_once_in_twenty_four_hours()
    {
        var chart = EnableChart(availableArtists: 1);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        await RunRefreshJob("schedule:first", id);
        Assert.True(chart.Reads > 0);
        var reads = chart.Reads;
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromHours(23);
        await RunRefreshJob("schedule:second", id);
        Assert.Equal(reads, chart.Reads);
        Assert.Equal(0, (await service.GetAsync(owner, id)).Evidence!.ProviderReads);
        Assert.DoesNotContain(gateway.Invocations, x => x.Method.Name is "GetSongAsync" or "SearchAutomaticProviderSongsAsync");
    }

    [Fact]
    public async Task Canonical_enrichment_during_generation_reselects_fresh_history()
    {
        await Seed(count: 30);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "on-repeat").Id;
        var validations = 0;
        access.Setup(x => x.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (++validations == 2)
                {
                    await using var db = factory.CreateDbContext();
                    await db.ListeningEvents.Where(x => x.TrackReference == songs["1"].Id)
                        .ExecuteUpdateAsync(x => x.SetProperty(y => y.CanonicalRecordingId, songs["2"].CanonicalRecordingId));
                }
                return new BackendLibraryAccess(true, []);
            });
        var block = BlockSong("0");
        var generation = service.GenerateAsync(owner, id);
        try
        {
            await block.Entered.Task.WaitAsync(CompletionDeadline);
            await using var db = factory.CreateDbContext();
            var recording = songs["1"].CanonicalRecordingId;
            await db.ListeningEvents.Where(x => x.TrackReference == songs["0"].Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.CanonicalRecordingId, recording));
        }
        finally { block.Release.TrySetResult(); }
        var result = (await generation.WaitAsync(CompletionDeadline)).Single(x => x.Id == id);
        Assert.True(result.Readiness.Ready);
        Assert.Equal(150, result.Evidence!.ImportedPlays);
        Assert.Equal(3, validations);
        gateway.Verify(x => x.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0"), Times.Once);
    }

    [Fact]
    public async Task Sparse_imported_references_use_stored_canonical_routes_for_full_rotation()
    {
        await Seed(count: 60, plays: 1, age: 365);
        await using (var db = factory.CreateDbContext())
        {
            var originals = await db.ListeningEvents.ToArrayAsync();
            for (var i = 60; i < 27321; i++)
            {
                var original = originals[i % 60];
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    OccurrenceKey = "import-" + i,
                    TrackReference = original.TrackReference,
                    CanonicalRecordingId = original.CanonicalRecordingId,
                    Title = original.Title,
                    Artist = original.Artist,
                    SourceKind = "import",
                    State = ListeningEventState.Completed,
                    ListenedAt = clock.UtcNow.AddDays(-365),
                    UpdatedAt = clock.UtcNow.AddDays(-365)
                });
            }
            await db.SaveChangesAsync();
            await db.ListeningEvents.ExecuteUpdateAsync(x => x.SetProperty(y => y.TrackReference, y => "spotify:import:" + y.Title));
            for (var i = 0; i < 4; i++) db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                OccurrenceKey = "live-" + i,
                TrackReference = songs[(i % 2).ToString()].Id,
                Title = songs[(i % 2).ToString()].Title,
                Artist = songs[(i % 2).ToString()].Artist,
                CanonicalRecordingId = songs[(i % 2).ToString()].CanonicalRecordingId,
                SourceKind = "protocol",
                State = ListeningEventState.Completed,
                ListenedAt = clock.UtcNow.AddHours(-1),
                UpdatedAt = clock.UtcNow.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }
        gateway.Setup(x => x.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult(songs.Values.SingleOrDefault(x => x.CanonicalRecordingId.ToString() == id)));
        var mixId = (await service.ListAsync(owner)).Single(x => x.Kind == "on-repeat").Id;
        var mix = (await service.GenerateAsync(owner, mixId)).Single(x => x.Id == mixId);
        Assert.Equal(50, mix.Songs.Count);
        Assert.Equal(27321, mix.Evidence!.ImportedPlays);
        Assert.Equal(4, mix.Evidence.LivePlays);
        Assert.All(mix.Songs, x => Assert.Contains("2025", x.Reason));
        Assert.Equal(0, mix.Evidence.ProviderReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undated_artist_search_uses_dated_albums_for_frequent_and_followed_artists(bool followed)
    {
        if (!followed) await Seed(count: 1, plays: 10);
        else await NewReleaseService().FollowAsync(owner, new("deezer", "artist", "Artist 0"));
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "Artist 0", 100))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([new Song { Id = "undated", Title = "New", Artist = "Artist 0", ArtistIds = ["ext-deezer-artist-artist"] }]));
        gateway.Setup(x => x.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist 0", 0, 0, 5, "deezer"))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult { Artists = [new Artist { Name = "Artist 0", ExternalId = "artist" }] });
        gateway.Setup(x => x.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "artist"))
            .ReturnsAsync([new Album { ExternalId = "release", ReleaseDate = clock.UtcNow.AddDays(-2).ToString("yyyy-MM-dd") },
                new Album { ExternalId = "old", ReleaseDate = clock.UtcNow.AddDays(-20).ToString("yyyy-MM-dd") }, new Album { ExternalId = "unknown" }]);
        gateway.Setup(x => x.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "release"))
            .ReturnsAsync(new Album
            {
                Songs = Enumerable.Range(1, 3).Select(i => new Song
                {
                    Id = "release-" + i,
                    Title = "New " + i,
                    Artist = "Artist 0",
                    ExternalProvider = "deezer",
                    ExternalId = "release-" + i,
                    Track = i
                }).ToList()
            });
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "release-radar").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal("release-1", Assert.Single(mix.Songs).Song.Id);
        Assert.Contains(clock.UtcNow.AddDays(-2).ToString("dd MMM yyyy"), mix.Songs[0].Reason);
        Assert.InRange(mix.Evidence!.ProviderReads!.Value, 2, 30);
        gateway.Verify(x => x.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "old"), Times.Never);
    }
}
