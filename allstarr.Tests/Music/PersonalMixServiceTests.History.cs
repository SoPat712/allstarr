using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task ImportedHistory_FillsRecentFallbackAndToggleExcludesAllMixInputs()
    {
        await Seed(count: 40, age: 1000);
        var mixes = await service.GenerateAsync(owner);
        Assert.Equal(30, mixes.Single(item => item.Kind == "on-repeat").Songs.Count);
        Assert.Equal(30, mixes.Single(item => item.Kind == "rewind").Songs.Count);
        await taste.SetUseImportedHistoryAsync(owner, false);
        mixes = await service.GenerateAsync(owner);
        Assert.All(mixes, mix => Assert.Empty(mix.Songs));
    }

    [Fact]
    public async Task ImportedHistory_RecentFavouritesPreferSufficientLivePlays()
    {
        await Seed(count: 40, age: 1);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in await db.ListeningEvents.Where(item => item.Title == "Song 39").ToArrayAsync())
                play.SourceKind = "protocol";
            var seed = await db.ListeningEvents.FirstAsync(item => item.Title == "Song 39");
            for (var index = 0; index < 30; index++) db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                SourceKind = "protocol",
                State = ListeningEventState.Completed,
                OccurrenceKey = "live-" + index,
                TrackReference = seed.TrackReference,
                CanonicalRecordingId = seed.CanonicalRecordingId,
                Title = seed.Title,
                Artist = seed.Artist,
                ListenedAt = clock.UtcNow.AddDays(-1),
                UpdatedAt = clock.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        Assert.Single(repeat.Songs);
        Assert.Equal("Song 39", repeat.Songs[0].Song.Title);
    }

    [Fact]
    public async Task ImportedHistory_PowersDiscoveryDailyAndMoodScoresAndDeletionRemovesInputs()
    {
        await Seed();
        await NewPicks();
        foreach (var song in songs.Values) await tags.SaveAsync(allstarr.Core.Enrichment.SharedTagStore.RecordingKey(song.CanonicalRecordingId!.Value),
            [new("lastfm-track", [new("calm")], clock.UtcNow)], [], false, default);
        var mixes = await service.GenerateAsync(owner);
        Assert.NotEmpty(mixes.Single(item => item.Kind == "discover-weekly").Songs);
        Assert.NotEmpty(mixes.Single(item => item.Kind == "daily-1").Songs);
        Assert.NotEmpty(mixes.Single(item => item.Kind == "chill").Songs);
        Assert.Contains(mixes.Single(item => item.Kind == "daily-1").Songs, item => item.Score > 0);
        await using (var db = factory.CreateDbContext()) await db.ListeningEvents.ExecuteDeleteAsync();
        mixes = await service.GenerateAsync(owner);
        Assert.Empty(mixes.Single(item => item.Kind == "discover-weekly").Songs);
        Assert.Empty(mixes.Single(item => item.Kind == "daily-1").Songs);
        Assert.Empty(mixes.Single(item => item.Kind == "chill").Songs);
    }

    [Fact]
    public async Task ImportedHistoryToggle_QueuesRefreshForExistingMixes()
    {
        await Seed();
        await service.GenerateAsync(owner);
        var jobs = new DurableJobQueue(factory, new(), new(new()), clock);
        await new allstarr.Core.Music.TasteService(factory, tags, clock, jobs).SetUseImportedHistoryAsync(owner, false);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Jobs.Where(item => item.Type == PersonalMixService.JobType).ToArrayAsync());
    }

    [Fact]
    public async Task HistoryTagCompletion_DebouncesRefreshForExistingMixes()
    {
        await Seed();
        await service.GenerateAsync(owner);
        var jobs = new DurableJobQueue(factory, new(), new(new()), clock);
        var queue = new allstarr.Core.Enrichment.SharedTagQueue(factory, jobs, clock);
        var due = clock.UtcNow.AddMinutes(5);
        foreach (var song in songs.Values.Take(3))
        {
            await queue.RefreshListenersAsync(song.CanonicalRecordingId!.Value, default);
            clock.UtcNow += TimeSpan.FromSeconds(1);
        }
        await using var db = factory.CreateDbContext();
        var job = Assert.Single(await db.Jobs.Where(item => item.Type == PersonalMixService.JobType).ToArrayAsync());
        Assert.Equal(owner, job.OwnerUserId);
        Assert.Equal(due, job.AvailableAt);
        Assert.Equal(-100, job.Priority);
    }

    [Fact]
    public async Task SavedEmptyMix_DoesNotAppearReadyBeforeRegeneration()
    {
        await service.GenerateAsync(owner);
        await Seed(count: 10, plays: 5);
        var mixes = await NewService().ListAsync(owner);
        Assert.False(mixes.Single(item => item.Kind == "on-repeat").Readiness.Ready);
        Assert.Contains("Refresh", mixes.Single(item => item.Kind == "on-repeat").Readiness.Message);
        Assert.Contains("150 more", mixes.Single(item => item.Kind == "daily-1").Readiness.Message);
        Assert.All(mixes, item => Assert.Equal(1, item.Generation));
        Assert.All(mixes, item => Assert.Empty(item.Songs));
    }

    [Fact]
    public async Task ResolvedImportedLibraryPlays_CountBeyondSearchFallbackLimit()
    {
        await Seed(count: 40);
        access.Setup(item => item.ResolveAsync(Moq.It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new allstarr.Core.Protocols.BackendLibraryAccess(true, ["music"]));
        await using (var db = factory.CreateDbContext())
        {
            foreach (var song in songs.Values)
            {
                var track = new LibraryTrackRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    BackendLibraryId = "music",
                    BackendItemId = "local-" + song.ExternalId,
                    Title = song.Title,
                    Artist = song.Artist,
                    CanonicalRecordingId = song.CanonicalRecordingId
                };
                db.LibraryTracks.Add(track);
                foreach (var play in await db.ListeningEvents.Where(item => item.CanonicalRecordingId == song.CanonicalRecordingId).ToArrayAsync())
                {
                    play.LibraryTrackId = track.Id;
                    play.TrackReference = "spotify:history-" + song.ExternalId;
                }
            }
            await db.SaveChangesAsync();
        }
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        Assert.Equal(30, repeat.Songs.Count);
        Assert.All(repeat.Songs, item => { Assert.True(item.Song.IsLocal); Assert.Equal("Played 5 times in 30 days", item.Reason); });
        gateway.Verify(item => item.SearchAutomaticSongsAsync(Moq.It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), Moq.It.IsAny<string>(), 3), Moq.Times.Never);
    }

    [Fact]
    public async Task ResolvedProviderHistory_UsesEveryResolvedTrackBeyondMetadataFallbackLimit()
    {
        await Seed(count: 40);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in await db.ListeningEvents.ToArrayAsync())
                play.TrackReference = "ext-deezer-song-" + play.Title!["Song ".Length..];
            await db.SaveChangesAsync();
        }
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        Assert.Equal(30, repeat.Songs.Count);
        gateway.Verify(item => item.GetSongAsync(Moq.It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), "deezer", Moq.It.IsAny<string>()),
            Moq.Times.AtLeast(40));
        gateway.Verify(item => item.SearchAutomaticSongsAsync(Moq.It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), Moq.It.IsAny<string>(), 3),
            Moq.Times.Never);
    }

    [Fact]
    public async Task HistoryResolution_QueuesOneRefreshForSavedReadinessTransition()
    {
        await service.GenerateAsync(owner);
        await Seed();
        await service.ListAsync(owner, "UTC");
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var resolutionId = Guid.NewGuid();
        await service.RefreshAfterHistoryResolutionAsync(owner, resolutionId, queue, default);
        await service.RefreshAfterHistoryResolutionAsync(owner, resolutionId, queue, default);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Jobs.Where(item => item.Type == PersonalMixService.JobType).ToArrayAsync());
        var claim = await queue.ClaimNextAsync("mix-history-test", [PersonalMixService.JobType]);
        Assert.NotNull(claim);
        using var services = new ServiceCollection().BuildServiceProvider();
        var result = await new PersonalMixJobHandler(service).ExecuteAsync(new(claim, services), default);
        await queue.CompleteAsync(claim, result);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        var repeat = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat");
        Assert.Equal(2, repeat.Generation);
        Assert.NotEmpty(repeat.Songs);
        Assert.False(publish);
        Assert.Empty(await service.PublishedAsync(Context(owner), default));
    }
}
