using allstarr.Core.Enrichment;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public async Task AnAddWaitsForMusicBrainzThatWasAwayAndTagsFromItOnceItAnswers()
    {
        var add = await MusicBrainzAddAsync(away: true);
        Assert.Equal("Album - Single", (await add.Batch.GetAsync(Context, add.RunId)).Tracks[0].Song.Album);
        await ExecuteBatchAsync(add.Batch);
        var waiting = await add.Batch.GetAsync(Context, add.RunId);
        Assert.Equal("Running", waiting.Outcome);
        Assert.Equal("Tags", waiting.Step);
        Assert.Contains(waiting.Tracks[0].Steps, step => step.Name == "Tags" && step.Outcome == "Running");

        add.Away = false;
        add.Clock.Advance(LibraryPromotionService.MusicBrainzAskEvery);
        await ExecuteDueAsync(add.Batch);
        var placed = await add.Batch.GetAsync(Context, add.RunId);
        Assert.Equal("Placed", placed.Outcome);
        Assert.Equal("Album", placed.Tracks[0].Song.Album);
        Assert.Equal(add.ReleaseId, placed.Tracks[0].Song.ProviderIds["musicbrainz_releaseid"]);
        Assert.Contains(placed.Tracks[0].Steps, step => step.Name == "Tags" && step.Outcome == "Complete" &&
            step.Reason == "Release metadata and profile artwork written.");
    }

    [Fact]
    public async Task AnAddWhoseMusicBrainzStaysAwayFallsBackToTheServicesNamesAfterTheWait()
    {
        var add = await MusicBrainzAddAsync(away: true);
        var started = add.Clock.GetUtcNow();
        await ExecuteBatchAsync(add.Batch);
        var runs = 1;
        while ((await add.Batch.GetAsync(Context, add.RunId)).Outcome == "Running")
        {
            Assert.True(runs < 10, "The add kept waiting for MusicBrainz.");
            add.Clock.Advance(LibraryPromotionService.MusicBrainzAskEvery);
            await ExecuteDueAsync(add.Batch);
            runs++;
        }
        var placed = await add.Batch.GetAsync(Context, add.RunId);
        Assert.Equal("Placed", placed.Outcome);
        Assert.Equal("Album - Single", placed.Tracks[0].Song.Album);
        Assert.DoesNotContain("musicbrainz_releaseid", placed.Tracks[0].Song.ProviderIds.Keys);
        Assert.Contains(placed.Tracks[0].Steps, step => step.Name == "Tags" && step.Outcome == "Complete" &&
            step.Reason.StartsWith("The service's own tags", StringComparison.Ordinal));
        Assert.InRange(add.Clock.GetUtcNow() - started, LibraryPromotionService.MusicBrainzWait,
            LibraryPromotionService.MusicBrainzWait + LibraryPromotionService.MusicBrainzAskEvery);
    }

    [Fact]
    public async Task AnAddIsNotHeldWhenMusicBrainzSaysItHasNoSuchRecording()
    {
        var add = await MusicBrainzAddAsync(away: false, known: false);
        await ExecuteBatchAsync(add.Batch);
        var placed = await add.Batch.GetAsync(Context, add.RunId);
        Assert.Equal("Placed", placed.Outcome);
        Assert.Equal("Album - Single", placed.Tracks[0].Song.Album);
        Assert.Equal(1, add.IsrcLookups);
    }

    private sealed class MusicBrainzAdd(ManualTimeProvider clock, string releaseId)
    {
        internal ManualTimeProvider Clock => clock;
        internal string ReleaseId => releaseId;
        internal LibraryPromotionService Batch { get; set; } = null!;
        internal Guid RunId { get; set; }
        internal bool Away { get; set; }
        internal int IsrcLookups;
    }

    private async Task<MusicBrainzAdd> MusicBrainzAddAsync(bool away, bool known = true)
    {
        await SaveBatchProfileAsync();
        PromotionCatalog.BusyWait = TimeSpan.FromMilliseconds(1);
        var song = new Song
        {
            CanonicalRecordingId = recordingId,
            Title = "Song",
            Artist = "Artist",
            Album = "Album - Single",
            ExternalProvider = "deezer",
            ExternalId = "track",
            AlbumId = "album",
            Isrc = "USAT21900001",
            Track = 1,
            Year = 2020
        };
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "album"))
            .ReturnsAsync(() => new Album { SongCount = 1, Songs = [song] });
        var mbid = Guid.NewGuid().ToString("D");
        var add = new MusicBrainzAdd(new ManualTimeProvider(DateTimeOffset.UtcNow), Guid.NewGuid().ToString("D")) { Away = away };
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>();
        musicBrainz.Setup(item => item.LookupByIsrcAsync("USAT21900001", It.IsAny<CancellationToken>())).Returns(() =>
        {
            Interlocked.Increment(ref add.IsrcLookups);
            return add.Away ? Task.FromException<MusicBrainzRecording?>(new MusicBrainzLookupException("unavailable", "MusicBrainz is unavailable.", true))
                : Task.FromResult(known ? new MusicBrainzRecording { Id = mbid, Title = "Song" } : null);
        });
        musicBrainz.Setup(item => item.LookupByMbidAsync(mbid, It.IsAny<CancellationToken>())).ReturnsAsync(new MusicBrainzRecording
        {
            Id = mbid,
            Title = "Song",
            Releases = [new() { Id = add.ReleaseId, Title = "Album", Status = "Official", Date = "2020-01-10" }]
        });
        musicBrainz.Setup(item => item.LookupReleaseByMbidAsync(add.ReleaseId, It.IsAny<CancellationToken>())).ReturnsAsync(new MusicBrainzRelease
        {
            Id = add.ReleaseId,
            Title = "Album",
            Date = "2020-01-10",
            Status = "Official",
            ArtistCredit = [new() { Name = "Artist" }],
            Media = [new() { Position = 1, TrackCount = 1, Tracks = [new() { Id = Guid.NewGuid().ToString("D"), Position = 1, Title = "Song", Recording = new() { Id = mbid } }] }]
        });
        musicBrainz.Setup(item => item.SearchRecordingsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MusicBrainzRecording>());
        var catalog = new PromotionCatalog(factory, CatalogIdentities(song).Object, providers.Object, musicBrainz.Object);
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        add.Batch = BatchService(catalog, acquisition.Object, clock: add.Clock);
        add.RunId = (await add.Batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty, Unit = "song" })).Id;
        return add;
    }

    // A deferred job is due again once its wait is over; the queue keeps real time, so the wait is ended by hand.
    private async Task ExecuteDueAsync(LibraryPromotionService batch)
    {
        await using (var db = factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(item => item.Type == LibraryPromotionService.JobType && item.State != DurableJobState.Succeeded);
            job.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        await ExecuteBatchAsync(batch);
    }
}
