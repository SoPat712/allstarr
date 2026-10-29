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
    public async Task SavedNotReadyMix_UpdatesReadinessWithoutRegeneration()
    {
        await service.GenerateAsync(owner);
        await Seed(count: 10, plays: 5);
        var mixes = await NewService().ListAsync(owner);
        Assert.True(mixes.Single(item => item.Kind == "on-repeat").Readiness.Ready);
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
