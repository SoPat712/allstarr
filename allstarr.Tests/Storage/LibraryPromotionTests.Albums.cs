using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public void PromotionNaming_CollisionSuffixRetainsTheSegmentLimit()
    {
        var rendered = PromotionNaming.Render("%title%.%_extension%", new Dictionary<string, string?> { ["title"] = new string('é', 200), ["_extension"] = "flac" });
        var collision = PromotionNaming.AddCollisionSuffix(rendered.RelativePath!, 2);
        Assert.EndsWith(" (2).flac", collision);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(collision), 1, 180);
    }

    [Fact]
    public async Task Promotion_ExistingSingleSongReviewRemainsAvailable()
    {
        await using (var db = factory.CreateDbContext())
        {
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).QualityCheckJson = null;
            await db.SaveChangesAsync();
        }
        var run = await service.StartAsync(Context, Request());
        await ExecuteAsync();
        Assert.Equal("Needs review", (await service.GetAsync(Context, run.Id)).Outcome);
        var current = BatchService(Mock.Of<IPromotionCatalog>(), Mock.Of<IPromotionAcquisition>());
        var approved = await current.ApproveAsync(Context, run.Id, "Reviewed this file");
        Assert.Equal("Queued", approved.Outcome);
        Assert.Equal("Reviewed this file", approved.ApprovalReason);
    }

    [Fact]
    public async Task Album_DefaultUsesProfileAndKeepsPerTrackProgress()
    {
        var sources = new Mock<IPromotionAcquisition>();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song", Codec = "flac", BitDepth = 16, SampleRate = 44100 } });
        var batch = BatchService(catalog.Object, sources.Object);
        var run = await batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty });
        var read = await batch.GetAsync(Context, run.Id);
        Assert.Single(read.Tracks);
        Assert.Equal(recordingId, read.Tracks[0].RecordingId);
        Assert.Equal("Queued", read.Tracks[0].Outcome);
        Assert.Equal(run.Id, (await batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty })).Id);
    }

    [Fact]
    public async Task Song_ExplicitUnitOverridesAlbumDefault()
    {
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song" } });
        var batch = BatchService(catalog.Object, Mock.Of<IPromotionAcquisition>());
        var run = await batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty, Unit = "song" });
        Assert.Single((await batch.GetAsync(Context, run.Id)).Tracks);
        catalog.VerifyAll();
        await Assert.ThrowsAsync<ArgumentException>(() => batch.StartAsync(Context, Request("invalid") with { Unit = "playlist" }));
    }

    [Fact]
    public async Task Review_RejectsUnknownActionAndMissingReason()
    {
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song" } });
        var batch = BatchService(catalog.Object, Mock.Of<IPromotionAcquisition>());
        var run = await batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty, Unit = "song" });
        await Assert.ThrowsAsync<ArgumentException>(() => batch.ReviewAsync(Context, run.Id, recordingId, "add-anyway", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => batch.ReviewAsync(Context, run.Id, recordingId, "unknown", null));
    }

    [Fact]
    public async Task Album_ContinuesOtherSongsWhileReviewIsPending()
    {
        await SaveBatchProfileAsync();
        var second = Guid.NewGuid();
        var firstSong = new Song { CanonicalRecordingId = recordingId, Title = "Missing source", Artist = "Artist", Album = "Album", Track = 1 };
        var nextSong = new Song { CanonicalRecordingId = second, Title = "Ready song", Artist = "Artist", Album = "Album", Track = 2 };
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { firstSong, nextSong });
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.Is<PromotionTrack>(track => track.RecordingId == recordingId),
            It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromotionAcquisitionResult(null, false, "No source meets the floor."));
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.Is<PromotionTrack>(track => track.RecordingId == second),
            It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty });
        await ExecuteBatchAsync(batch);
        var review = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Needs review", review.Outcome);
        Assert.Equal("Needs review", review.Tracks[0].Outcome);
        Assert.Equal("Placed", review.Tracks[1].Outcome);
        Assert.Contains(review.Tracks[1].Steps, step => step.Name == "Lyrics" && step.Outcome == "None fits");
        await using (var db = factory.CreateDbContext()) Assert.Single(await db.ManagedFiles.ToArrayAsync());
        await Assert.ThrowsAsync<PromotionException>(() => batch.ReviewAsync(Context, run.Id, recordingId, "add-anyway", "Accept source"));
        await batch.ReviewAsync(Context, run.Id, recordingId, "skip", null);
        await ExecuteBatchAsync(batch);
        var placed = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Placed", placed.Outcome);
        Assert.Equal("Skipped", placed.Tracks[0].Outcome);
        Assert.Equal("Placed", placed.Tracks[1].Outcome);
        Assert.True(File.Exists(placed.Tracks[1].DestinationPath));
        Assert.True(File.Exists(source.SourcePath));
        await using var verify = factory.CreateDbContext();
        var refresh = await verify.Jobs.SingleAsync(item => item.Type == "library.refresh");
        var payload = System.Text.Json.JsonSerializer.Deserialize<BackendLibraryRefreshJobPayload>(refresh.PayloadJson)!;
        Assert.Equal(new[] { second }, payload.RecordingIds);
    }

    [Fact]
    public async Task Album_ChecksTheReadyBatchBeforePlacingAnyTrack()
    {
        await SaveBatchProfileAsync();
        var second = Guid.NewGuid();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Song { CanonicalRecordingId = recordingId, Title = "First", Artist = "Artist", Album = "Album", Track = 1 },
                new Song { CanonicalRecordingId = second, Title = "Second", Artist = "Artist", Album = "Album", Track = 2 }
            });
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(),
            It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty });
        var reads = 0;
        var capacity = source.Length * 3 / 2;
        var constrained = new PromotionRoots(settings.Object, factory, new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Promotion:AllowedBase"] = Path.Combine(directory, "library"),
                ["Promotion:MinimumFreeBytes"] = "0"
            }).Build(), _ => ++reads < 3 ? capacity : 0);

        await ExecuteBatchAsync(BatchService(catalog.Object, acquisition.Object, promotionRoots: constrained));

        var failed = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Failed", failed.Outcome);
        Assert.All(failed.Tracks, track => Assert.Null(track.ManagedFileId));
        Assert.Equal(1, reads);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.ManagedFiles.ToArrayAsync());
    }

    [Fact]
    public async Task Album_AlreadyPlacedQueuesRefreshWithoutCheckingSpaceAgain()
    {
        await SaveBatchProfileAsync();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song", Artist = "Artist", Album = "Album" } });
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(),
            It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty, Unit = "song" });
        await ExecuteBatchAsync(batch);
        await using (var db = factory.CreateDbContext())
        {
            var stored = await db.Set<PromotionRunRecord>().SingleAsync(item => item.Id == run.Id);
            var album = System.Text.Json.JsonSerializer.Deserialize<PromotionAlbumPlan>(stored.PlanJson)!;
            album.RefreshJobId = null;
            foreach (var track in album.Tracks) track.RefreshJobId = null;
            var queued = await queue.EnqueueAsync(new DurableJobEnqueueRequest<Guid>(
                LibraryPromotionService.JobType, $"promotion:{run.Id:N}:resume", run.Id, owner));
            stored.JobId = queued.JobId;
            stored.Outcome = "Running";
            stored.PlanJson = System.Text.Json.JsonSerializer.Serialize(album);
            stored.Revision++;
            await db.SaveChangesAsync();
        }
        var reads = 0;
        var constrained = new PromotionRoots(settings.Object, factory, new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Promotion:AllowedBase"] = Path.Combine(directory, "library"),
                ["Promotion:MinimumFreeBytes"] = "1"
            }).Build(), _ => { reads++; return 0; });

        await ExecuteBatchAsync(BatchService(catalog.Object, acquisition.Object, promotionRoots: constrained));

        Assert.Equal("Placed", (await batch.GetAsync(Context, run.Id)).Outcome);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Album_DedupeSkipsSameIsrcBeforeAcquisition()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                BackendInstanceId = "backend",
                BackendLibraryId = "music",
                BackendItemId = "existing",
                Protocol = "jellyfin",
                Title = "Existing",
                Isrc = "USRC17607839"
            });
            await db.SaveChangesAsync();
        }
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song", Isrc = "us-rc1-76-07839" } });
        var acquisition = new Mock<IPromotionAcquisition>(MockBehavior.Strict);
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { ArtifactId = Guid.Empty });
        Assert.Equal("Skipped", run.Tracks[0].Outcome);
        await ExecuteBatchAsync(batch);
        Assert.Equal("Placed", (await batch.GetAsync(Context, run.Id)).Outcome);
        await using var verify = factory.CreateDbContext();
        var refresh = await verify.Jobs.SingleAsync(item => item.Type == "library.refresh");
        Assert.Equal(new[] { recordingId }, System.Text.Json.JsonSerializer.Deserialize<BackendLibraryRefreshJobPayload>(refresh.PayloadJson)!.RecordingIds);
    }

    [Fact]
    public async Task Album_PlacementPreservesUnownedCollision()
    {
        await SaveBatchProfileAsync();
        var song = new Song { CanonicalRecordingId = recordingId, Title = "Song", Artist = "Artist", Album = "Album", Track = 1 };
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { song });
        var acquisition = new Mock<IPromotionAcquisition>();
        var source = await WaveSourceAsync();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var target = Path.Combine(directory, "library", "music", "Song.wav");
        await File.WriteAllTextAsync(target, "unowned");
        var run = await batch.StartAsync(Context, Request() with { Unit = "song", ArtifactId = Guid.Empty });
        await ExecuteBatchAsync(batch);
        var placed = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Placed", placed.Outcome);
        Assert.EndsWith("Song (2).wav", placed.Tracks[0].DestinationPath);
        Assert.Equal("unowned", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task PromotionRefresh_QueuesFocusedLibraryLinkAfterAcceptedRefresh()
    {
        var backend = new Mock<IBackendLibraryRefresher>();
        backend.SetupGet(item => item.Protocol).Returns(ProtocolKind.Jellyfin);
        backend.Setup(item => item.RefreshAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<BackendLibraryRefreshRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryRefreshResult(true));
        var queued = await queue.EnqueueAsync(new DurableJobEnqueueRequest<BackendLibraryRefreshJobPayload>("library.refresh", "refresh-test",
            new("music", "backend", "principal") { RecordingIds = [recordingId], RemoveKeptCopy = true }, owner));
        var claim = await queue.ClaimNextAsync("promotion-test", ["library.refresh"]);
        Assert.Equal(queued.JobId, claim!.JobId);
        var handler = new BackendLibraryRefreshJobHandler(factory, new([backend.Object]), new SystemPlatformClock(), new TestBackendLibraryAccess(factory, "music"), queue);
        var result = await handler.ExecuteAsync(new(claim, services), default);
        await queue.CompleteAsync(claim, result);
        await using var db = factory.CreateDbContext();
        var link = await db.Jobs.SingleAsync(item => item.Type == LibraryRecordingLinkQueue.JobType);
        var payload = System.Text.Json.JsonSerializer.Deserialize<LibraryRecordingLinkPayload>(link.PayloadJson)!;
        Assert.Equal(new[] { recordingId }, payload.RecordingIds);
        Assert.True(payload.RemoveKeptCopy);
        Assert.Equal("music", payload.BackendLibraryId);
    }

    [Fact]
    public async Task PromotionLibraryLink_WaitsForIndexConfirmationDespiteEarlierBackfill()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.AuditEvents.Add(new() { Id = Guid.NewGuid(), ActorUserId = owner, Action = "library-recording-link.complete", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<allstarr.Core.Capabilities.ProviderActorContext>(), recordingId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanonicalRecordingIdentity(recordingId, owner, null, null, 1));
        identities.Setup(item => item.LinkLibraryRecordingsAsync(It.IsAny<allstarr.Core.Capabilities.ProviderActorContext>(), It.IsAny<string>(), recordingId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibraryRecordingLinkResult(1, 0, 0));
        await queue.EnqueueAsync(new DurableJobEnqueueRequest<LibraryRecordingLinkPayload>(LibraryRecordingLinkQueue.JobType, "promotion-link-test",
            new(owner) { RecordingIds = [recordingId], BackendLibraryId = "music", RemoveKeptCopy = true }, owner));
        var claim = await queue.ClaimNextAsync("promotion-test", [LibraryRecordingLinkQueue.JobType]);
        var handler = new LibraryRecordingLinkJobHandler(identities.Object, factory, new SystemPlatformClock());
        var completion = await handler.ExecuteAsync(new(claim!, services), default);
        await queue.CompleteAsync(claim!, completion);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(DurableJobState.RetryScheduled, (await verify.Jobs.SingleAsync(item => item.Id == claim!.JobId)).State);
        identities.Verify(item => item.LinkLibraryRecordingsAsync(It.IsAny<allstarr.Core.Capabilities.ProviderActorContext>(), It.IsAny<string>(), recordingId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Album_RestartRecoversAnInterruptedMetadataSwap()
    {
        await SaveBatchProfileAsync();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song" } });
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var writer = new InterruptingWriter(new TagLibManagedMetadataWriter(mutator, new PhysicalManagedFileOperations()), mutator);
        var batch = BatchService(catalog.Object, acquisition.Object, writer);
        var run = await batch.StartAsync(Context, Request() with { Unit = "song", ArtifactId = Guid.Empty });
        mutator.Interrupt = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ExecuteBatchAsync(batch));
        await queue.RecoverInterruptedAsync();
        await ExecuteBatchAsync(batch);
        Assert.Equal("Placed", (await batch.GetAsync(Context, run.Id)).Outcome);
        Assert.Equal(2, mutator.Writes);
        acquisition.Verify(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PromotionCeiling_ConvertsHigherResolutionBeforePlacement()
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        {
            NamingScript = "%title%.%_extension%",
            FakeLosslessCheck = false,
            ReplayGain = false,
            CoverMode = "none",
            LyricsMode = "off",
            HiResCeiling = new(16, 48000)
        });
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song" } });
        var source = await WaveSourceAsync(192000, 24);
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { Unit = "song", ArtifactId = Guid.Empty });
        await ExecuteBatchAsync(batch);
        var placed = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Placed", placed.Outcome);
        var track = Assert.Single(placed.Tracks);
        Assert.Equal(16, track.Song.BitDepth);
        Assert.Equal(48000, track.Song.SampleRate);
        using var file = TagLib.File.Create(track.DestinationPath!);
        Assert.Equal(16, file.Properties.BitsPerSample);
        Assert.Equal(48000, file.Properties.AudioSampleRate);
    }

    [Fact]
    public async Task PromotionLyrics_SidecarModePlacesOnlyDurationCheckedLyrics()
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        {
            NamingScript = "%title%.%_extension%",
            FakeLosslessCheck = false,
            ReplayGain = false,
            CoverMode = "none",
            LyricsMode = ".lrc"
        });
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song", Duration = 200 } });
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        const string lrc = "[00:00.100]First\n[00:00.800]Last";
        var lyrics = new Mock<IProtocolLyricsResolver>();
        lyrics.Setup(item => item.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()))
            .ReturnsAsync(new[] { new ProtocolLyricsCandidate("source", new allstarr.Models.Lyrics.LyricsInfo { SyncedLyrics = lrc }) });
        var batch = BatchService(catalog.Object, acquisition.Object, lyricsResolver: lyrics.Object);
        var run = await batch.StartAsync(Context, Request() with { Unit = "song", ArtifactId = Guid.Empty });
        await ExecuteBatchAsync(batch);
        var placed = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Placed", placed.Outcome);
        var track = Assert.Single(placed.Tracks);
        Assert.Equal(lrc, await File.ReadAllTextAsync(Path.ChangeExtension(track.DestinationPath!, ".lrc")));
        using var file = TagLib.File.Create(track.DestinationPath!);
        Assert.True(string.IsNullOrEmpty(file.Tag.Lyrics));
    }

    [Fact]
    public async Task PromotionRun_ProjectsRefreshAndConfirmedHandoverSteps()
    {
        await SaveBatchProfileAsync();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song" } });
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { Unit = "song", ArtifactId = Guid.Empty });
        await ExecuteBatchAsync(batch);
        Assert.Contains((await batch.GetAsync(Context, run.Id)).Tracks[0].Steps, step => step.Name == "Refresh" && step.Outcome == "Pending");
        Guid refreshId;
        await using (var db = factory.CreateDbContext())
        {
            var refresh = await db.Jobs.SingleAsync(item => item.Type == "library.refresh");
            refreshId = refresh.Id;
            refresh.State = DurableJobState.Succeeded;
            await db.SaveChangesAsync();
        }
        var linked = await queue.EnqueueAsync(new DurableJobEnqueueRequest<LibraryRecordingLinkPayload>(LibraryRecordingLinkQueue.JobType,
            $"promotion-refresh:{refreshId:N}", new(owner) { RecordingIds = [recordingId], BackendLibraryId = "music" }, owner));
        await using (var db = factory.CreateDbContext())
        {
            (await db.Jobs.SingleAsync(item => item.Id == linked.JobId)).State = DurableJobState.Succeeded;
            await db.SaveChangesAsync();
        }
        Assert.Contains((await batch.GetAsync(Context, run.Id)).Tracks[0].Steps, step => step.Name == "Handover" && step.Outcome == "Complete");
    }

    [Fact]
    public async Task PromotionQuality_ReviewCanTryAnotherSourceWithoutCarryingApproval()
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        { NamingScript = "%title%.%_extension%", ReplayGain = false, LyricsMode = "off", CoverMode = "none" });
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song" } });
        var firstPath = Path.Combine(directory, "workspaces", "source", "song.flac");
        var firstBytes = await File.ReadAllBytesAsync(firstPath);
        var first = new VerifiedProviderDownloadArtifact(artifactId, Guid.NewGuid(), firstPath,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(firstBytes)), firstBytes.Length, owner, Guid.NewGuid(), "deezer", null,
            ProviderDownloadArtifactState.Verified, null)
        { Codec = "flac", BitDepth = 16, SampleRate = 44100 };
        var second = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, PromotionTrack, PromotionProfile, CancellationToken>((_, _, track, _, _) =>
                Task.FromResult(new PromotionAcquisitionResult(track.SourceAttempt == 0 ? first : second with { ProviderId = "qobuz" }, false, "Verified source.")));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { Unit = "song", ArtifactId = Guid.Empty });
        await ExecuteBatchAsync(batch);
        var review = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Needs review", review.Tracks[0].Outcome);
        Assert.Equal("Quality", review.Tracks[0].Step);
        await Assert.ThrowsAsync<ArgumentException>(() => batch.ReviewAsync(Context, run.Id, recordingId, "add-anyway", ""));
        await batch.ReviewAsync(Context, run.Id, recordingId, "try-next-source", null);
        await ExecuteBatchAsync(batch);
        var placed = await batch.GetAsync(Context, run.Id);
        Assert.Equal("Placed", placed.Outcome);
        Assert.Equal("qobuz", placed.Tracks[0].Provider);
        Assert.Null(placed.Tracks[0].ApprovedByUserId);
        Assert.Equal("deezer", Assert.Single(placed.Tracks[0].SourceAttempts).Provider);
    }

    [Fact]
    public async Task PromotionCancel_StopsPendingTrackDownloadsAndRemovesPrivateWork()
    {
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song" } });
        var child = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(PromotionAcquisition.JobType, "pending-download", new { }, owner));
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, PromotionTrack, PromotionProfile, CancellationToken>((_, _, track, _, _) =>
            {
                track.AcquisitionJobId = child.JobId;
                return Task.FromResult(new PromotionAcquisitionResult(null, true, "Waiting for download."));
            });
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { Unit = "song", ArtifactId = Guid.Empty });
        await ExecuteBatchAsync(batch);
        var cancelled = await batch.CancelAsync(Context, run.Id);
        Assert.Equal("Cancelled", cancelled.Outcome);
        Assert.Equal("Cancelled", Assert.Single(cancelled.Tracks).Outcome);
        Assert.False(Directory.Exists(Path.Combine(directory, "workspaces", "promotions", run.Id.ToString("N"))));
        await using var db = factory.CreateDbContext();
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(item => item.Id == child.JobId)).State);
    }

    private Task SaveBatchProfileAsync() => new PromotionProfiles(factory).SaveMineAsync(Context, new()
    {
        NamingScript = "%title%.%_extension%",
        FakeLosslessCheck = false,
        ReplayGain = false,
        CoverMode = "none",
        LyricsMode = "off"
    });

    private async Task<VerifiedProviderDownloadArtifact> WaveSourceAsync(int rate = 44100, int bits = 16)
    {
        var path = Path.Combine(directory, "source.wav");
        await using (var stream = File.Create(path))
        {
            using var wave = new BinaryWriter(stream);
            var length = rate * bits / 8;
            wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wave.Write(36 + length);
            wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16); wave.Write((short)1);
            wave.Write((short)1); wave.Write(rate); wave.Write(length); wave.Write((short)(bits / 8)); wave.Write((short)bits);
            wave.Write(System.Text.Encoding.ASCII.GetBytes("data")); wave.Write(length);
            for (var index = 0; index < rate; index++)
            {
                var sample = (int)((bits == 24 ? 2000000 : 10000) * Math.Sin(2 * Math.PI * 440 * index / rate));
                if (bits == 16) wave.Write((short)sample);
                else { wave.Write((byte)sample); wave.Write((byte)(sample >> 8)); wave.Write((byte)(sample >> 16)); }
            }
        }
        var bytes = await File.ReadAllBytesAsync(path);
        return new(Guid.NewGuid(), Guid.NewGuid(), path, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length,
            owner, Guid.NewGuid(), "deezer", null, ProviderDownloadArtifactState.Verified, null)
        { Codec = bits == 24 ? "pcm_s24le" : "pcm_s16le", BitDepth = bits, SampleRate = rate };
    }

    private async Task ExecuteBatchAsync(LibraryPromotionService batch)
    {
        var claim = await queue.ClaimNextAsync("promotion-test", [LibraryPromotionService.JobType]);
        Assert.NotNull(claim);
        var completion = await batch.ExecuteAsync(new(claim, services), default);
        await queue.CompleteAsync(claim, completion);
    }

    private LibraryPromotionService BatchService(IPromotionCatalog catalog, IPromotionAcquisition acquisition, IManagedMetadataWriter? writer = null,
        IProtocolLyricsResolver? lyricsResolver = null, ITrackIdentityService? identityService = null, PromotionRoots? promotionRoots = null)
    {
        var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(directory, "workspaces") };
        var resolver = new ProviderDownloadArtifactResolver(new EfProviderDownloadArtifactStore(factory), options);
        var cache = new allstarr.Services.Common.FileMediaApplicationCache(new allstarr.Services.Common.FileMediaCacheOptions(Path.Combine(directory, "batch-cache")),
            new SystemPlatformClock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Services.Common.FileMediaApplicationCache>.Instance);
        return new(factory, queue, promotionRoots ?? roots, resolver, options, services.GetRequiredService<IServiceScopeFactory>(),
            writer ?? new TagLibManagedMetadataWriter(new TagLibManagedTagFileMutator(), new PhysicalManagedFileOperations()), new allstarr.Services.Common.AudioQualityAnalyzer(cache), new TestBackendLibraryAccess(factory, "music"),
            identities: identityService, promotionCatalog: catalog, promotionAcquisition: acquisition, lyrics: lyricsResolver);
    }
}
