using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public async Task PromotionList_RestrictsOwnersAndAllowsAdministrators()
    {
        await SeedReceiptAsync([ReceiptTrack("Queued")]);
        var foreign = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new() { Id = foreign, Enabled = true, BackendType = "jellyfin", BackendInstanceId = "backend", BackendPrincipalId = "other" });
            await db.SaveChangesAsync();
        }
        var foreignRun = await SeedReceiptAsync([ReceiptTrack("Queued")], runOwner: foreign);
        var mine = await service.ListAsync(Context, "all");
        Assert.Single(mine.Items);
        Assert.Equal(1, mine.Counts.All);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetReceiptAsync(Context, foreignRun));
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).IsAdmin = true;
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, (await service.ListAsync(Context, "all")).Counts.All);
        Assert.Equal(foreign, (await service.GetReceiptAsync(Context, foreignRun)).Requester.Id);
    }

    [Fact]
    public async Task PromotionList_CursorOrdersTiesAndKeepsPopulationCounts()
    {
        var time = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = await SeedReceiptAsync([ReceiptTrack("Queued")], createdAt: time);
        var second = await SeedReceiptAsync([ReceiptTrack("Queued")], createdAt: time);
        var newest = await SeedReceiptAsync([ReceiptTrack("Queued")], createdAt: time.AddMinutes(1));
        var page = await service.ListAsync(Context, "all", 1);
        Assert.Equal(newest, Assert.Single(page.Items).Id);
        var next = await service.ListAsync(Context, "all", 1, page.NextCursor);
        var last = await service.ListAsync(Context, "all", 1, next.NextCursor);
        Assert.Equal(3, next.Counts.All);
        Assert.Equal(new[] { first, second }.OrderDescending(), new[] { next.Items[0].Id, last.Items[0].Id });
        Assert.Null(last.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(Context, "all", cursor: new string('x', 129)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(Context, "all", cursor: "invalid"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(Context, "all", 101));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(Context, "unknown"));
    }

    [Fact]
    public async Task PromotionReceipt_RequiresConfirmedHandoverForAdded()
    {
        var refresh = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("library.refresh", "receipt-refresh", new { }, owner));
        var placed = ReceiptTrack("Placed");
        placed.ManagedFileId = Guid.NewGuid();
        var runId = await SeedReceiptAsync([placed], refreshId: refresh.JobId, outcome: "Placed");
        var pending = await service.GetReceiptAsync(Context, runId);
        Assert.Equal(0, pending.Counts.Added);
        Assert.True(pending.Active);
        Assert.Empty((await service.ListAsync(Context, "added")).Items);
        var link = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(LibraryRecordingLinkQueue.JobType,
            $"promotion-refresh:{refresh.JobId:N}", new { }, owner));
        var completed = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Jobs.SingleAsync(item => item.Id == refresh.JobId)).State = DurableJobState.Succeeded;
            var linked = await db.Jobs.SingleAsync(item => item.Id == link.JobId);
            linked.State = DurableJobState.Succeeded;
            linked.CompletedAt = completed;
            await db.SaveChangesAsync();
        }
        var added = await service.GetReceiptAsync(Context, runId);
        Assert.Equal("added", added.State);
        Assert.Equal(1, added.Added);
        Assert.False(added.Active);
        Assert.Equal(completed, added.FinishedAt);
        Assert.Equal(1, (await service.ListAsync(Context, "added")).Counts.AddedToday);
    }

    [Fact]
    public async Task PromotionReceipt_DedupeCountsAsAddedAndReviewCanOverlapActive()
    {
        var dedupe = ReceiptTrack("Skipped");
        dedupe.Steps.Add(new("Dedupe", "Skipped", "Already in library."));
        await SeedReceiptAsync([dedupe]);
        var review = ReceiptTrack("Needs review");
        var working = ReceiptTrack("Queued");
        working.RecordingId = Guid.NewGuid();
        await SeedReceiptAsync([review, working]);
        var list = await service.ListAsync(Context, "all");
        Assert.Equal(1, list.Counts.Added);
        Assert.Equal(1, list.Counts.Active);
        Assert.Equal(1, list.Counts.NeedsLook);
        var item = Assert.Single(list.Items, item => item.NeedsLook > 0);
        Assert.Equal("needs-look", item.State);
        Assert.True(item.Active);
        Assert.Equal(2, item.Counts.Total);
        Assert.Equal(1, item.Counts.NeedsLook);
    }

    [Fact]
    public async Task PromotionList_FullyHeldSongsRemainInActive()
    {
        await SeedReceiptAsync([ReceiptTrack("Needs review")]);
        var page = await service.ListAsync(Context, "active");
        Assert.Single(page.Items);
        Assert.Equal(1, page.Counts.Active);
        Assert.Equal(1, page.Counts.NeedsLook);
        Assert.False(page.Items[0].Active);
    }

    [Fact]
    public async Task PromotionList_CancelledRunRetainsAddedCountsWithoutNeedingReview()
    {
        var existing = ReceiptTrack("Skipped");
        existing.RecordingId = Guid.NewGuid();
        existing.Steps.Add(new("Dedupe", "Skipped", "Already in library."));
        var id = await SeedReceiptAsync([ReceiptTrack("Needs review"), existing], outcome: "Cancelled");
        var receipt = await service.GetReceiptAsync(Context, id);
        Assert.Equal("cancelled", receipt.State);
        Assert.Equal(1, receipt.Added);
        Assert.Equal(0, receipt.NeedsLook);
        Assert.False(receipt.Active);
        Assert.Empty((await service.ListAsync(Context, "active")).Items);
        Assert.Empty((await service.ListAsync(Context, "needs-look")).Items);
    }

    [Fact]
    public async Task PromotionReceipt_CompletedAlbumWithSkippedSongStillShowsConfirmedAdditions()
    {
        var existing = ReceiptTrack("Skipped");
        existing.Steps.Add(new("Dedupe", "Skipped", "Already in library."));
        var skipped = ReceiptTrack("Skipped");
        skipped.RecordingId = Guid.NewGuid();
        var id = await SeedReceiptAsync([existing, skipped], outcome: "Placed");
        var receipt = await service.GetReceiptAsync(Context, id);
        Assert.Equal("added", receipt.State);
        Assert.Equal(1, receipt.Added);
        Assert.Equal(2, receipt.Total);
        Assert.False(receipt.Active);
    }

    [Fact]
    public async Task PromotionKept_ProjectsRealRecordingPathAndLibraryMembership()
    {
        var fileId = Guid.NewGuid();
        var keptRoot = Path.Combine(directory, "kept");
        await new PromotionProfiles(factory).SaveMineAsync(Context, new() { LibraryRootId = rootId });
        await using (var db = factory.CreateDbContext())
        {
            var job = await db.Jobs.FirstAsync();
            db.ManagedFiles.Add(new()
            {
                Id = fileId,
                RootId = rootId,
                TargetRootPath = keptRoot,
                CanonicalPath = Path.Combine(keptRoot, "Artist", "Song.flac"),
                IsManaged = true,
                ScopeKey = allstarr.Core.ManagedFiles.ManagedFileScopeKey.Create(null, rootId),
                ContentSha256 = new string('a', 64)
            });
            db.Add(new allstarr.Core.ManagedFiles.RecordingKeptRouteEntity
            {
                CanonicalRecordingId = recordingId,
                ManagedFileId = fileId,
                PublishedByUserId = owner,
                PublicationJobId = job.Id,
                SourceArtifactId = artifactId
            });
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                BackendInstanceId = "backend",
                BackendLibraryId = "music",
                BackendItemId = "existing",
                CanonicalRecordingId = recordingId,
                Title = "Song"
            });
            await db.SaveChangesAsync();
        }
        var page = await service.KeptAsync(Context);
        var item = Assert.Single(page.Items);
        Assert.Equal(Path.Combine("Artist", "Song.flac"), item.Path);
        Assert.Equal(recordingId, item.RecordingId);
        Assert.Equal("music", item.BackendLibraryId);
        Assert.True(item.InLibrary);
        Assert.Equal(1, page.AlreadyInLibrary);
        Assert.Equal(rootId, page.RootId);
    }

    [Fact]
    public async Task PromotionAlbum_PlacesReadySiblingAndKeepsEachHandoverSeparate()
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        { NamingScript = "%title%.%_extension%", ReplayGain = false, CoverMode = "none", LyricsMode = "off" });
        var siblingId = Guid.NewGuid();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Held" },
                new Song { CanonicalRecordingId = siblingId, Title = "Ready" } });
        var heldSource = await WaveSourceAsync(96000, 24);
        var heldPath = Path.Combine(directory, "held.wav");
        File.Copy(heldSource.SourcePath, heldPath);
        heldSource = heldSource with { SourcePath = heldPath };
        var readySource = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, PromotionTrack, PromotionProfile, CancellationToken>((_, _, track, _, _) =>
                Task.FromResult(new PromotionAcquisitionResult(track.RecordingId == recordingId ? heldSource : readySource, false, "Verified source.")));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { Unit = "album", ArtifactId = Guid.Empty });
        await ExecuteBatchAsync(batch);
        var partial = await batch.GetReceiptAsync(Context, run.Id);
        Assert.Equal("Needs review", partial.Tracks.Single(item => item.RecordingId == recordingId).Outcome);
        var placedSibling = partial.Tracks.Single(item => item.RecordingId == siblingId);
        Assert.Equal("Placed", placedSibling.Outcome);
        Assert.True(File.Exists(placedSibling.DestinationPath));
        Assert.True(Directory.Exists(Path.Combine(directory, "workspaces", "promotions", run.Id.ToString("N"), $"{recordingId:N}-0")));
        var firstRefreshId = placedSibling.RefreshJobId!.Value;
        var firstLink = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(LibraryRecordingLinkQueue.JobType,
            $"promotion-refresh:{firstRefreshId:N}", new { }, owner));
        await using (var db = factory.CreateDbContext())
        {
            (await db.Jobs.SingleAsync(item => item.Id == firstRefreshId)).State = DurableJobState.Succeeded;
            (await db.Jobs.SingleAsync(item => item.Id == firstLink.JobId)).State = DurableJobState.Succeeded;
            await db.SaveChangesAsync();
        }
        var reviewed = await batch.ReviewAsync(Context, run.Id, recordingId, "add-anyway", "Accept the checked file.");
        Assert.True(reviewed.Tracks.Single(item => item.RecordingId == recordingId).Lossy);
        await ExecuteBatchAsync(batch);
        var later = await batch.GetReceiptAsync(Context, run.Id);
        Assert.Equal(1, later.Added);
        Assert.Equal("Placed", later.Tracks.Single(item => item.RecordingId == recordingId).Outcome);
        Assert.True(later.Tracks.Single(item => item.RecordingId == recordingId).Lossy);
        using (var audio = TagLib.File.Create(later.Tracks.Single(item => item.RecordingId == recordingId).DestinationPath!))
            Assert.Equal("Lossy source", audio.Tag.Comment);
        Assert.NotEqual(firstRefreshId, later.Tracks.Single(item => item.RecordingId == recordingId).RefreshJobId);
        Assert.DoesNotContain(later.Tracks.Single(item => item.RecordingId == recordingId).Steps,
            item => item.Name == "Handover" && item.Outcome == "Complete");
    }

    [Fact]
    public async Task PromotionPreview_UsesOpenedSongAndSavedProfileWithRealSourceFormats()
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        { NamingScript = "%title%.%_extension%", MusicBrainzTags = false, CoverMode = "none", LyricsMode = "off", ReplayGain = false, FakeLosslessCheck = false });
        var first = new Song { CanonicalRecordingId = Guid.NewGuid(), Title = "First", Album = "Release", Artist = "Artist" };
        var opened = new Song { CanonicalRecordingId = recordingId, Title = "Opened", Album = "Release", Artist = "Artist", Year = 2024, Container = "mp3", Codec = "mp3" };
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { first, opened });
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), recordingId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] {
                new Song { ExternalProvider = "qobuz", ExternalId = "best", Container = "flac", Codec = "flac", BitDepth = 24, SampleRate = 96000 },
                new Song { ExternalProvider = "alternate", ExternalId = "fallback", Codec = "flac", BitDepth = 16, SampleRate = 44100 },
                new Song { ExternalProvider = "below-rate", ExternalId = "low-rate", Codec = "flac", BitDepth = 24, SampleRate = 32000 },
                new Song { ExternalProvider = "deezer", ExternalId = "unknown", Codec = "flac" },
                new Song { ExternalProvider = "other", ExternalId = "low", Codec = "mp3", SampleRate = 44100 }
            });
        var batch = BatchService(catalog.Object, Mock.Of<IPromotionAcquisition>(), identityService: identities.Object);
        var preview = await batch.PreviewAsync(Context, Request() with { Unit = "album", ArtifactId = Guid.Empty });
        Assert.EndsWith("Opened.flac", preview.DestinationPath);
        Assert.Equal("Release", preview.Album!.Title);
        Assert.Equal(2, preview.Album.SongCount);
        Assert.Equal("qobuz", preview.BestCopies[0].Provider);
        Assert.Equal("meets-your-floor", preview.BestCopies[0].Verdict);
        Assert.Equal("Your floor is lossless 16/44 · keeps hi-res up to 24/96", preview.BestCopies[0].Reason);
        Assert.Equal("alternate", preview.BestCopies[1].Provider);
        Assert.Equal("skipped-below-floor", preview.BestCopies.Single(item => item.Provider == "below-rate").Verdict);
        Assert.Equal("fallback", preview.BestCopies.Single(item => item.Provider == "alternate").Verdict);
        Assert.Contains("Next choice if qobuz fails", preview.BestCopies.Single(item => item.Provider == "alternate").Reason);
        Assert.Equal("fallback", preview.BestCopies.Single(item => item.Provider == "deezer").Verdict);
        Assert.Equal("skipped-below-floor", preview.BestCopies.Single(item => item.Provider == "other").Verdict);
        Assert.Equal(5, preview.ProfileSteps.Count);
        Assert.All(preview.ProfileSteps, item => Assert.False(item.Enabled));
        Assert.Equal(rootId, preview.RootId);
        Assert.Equal("music", preview.BackendLibraryId);
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), recordingId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Song>());
        Assert.Empty((await batch.PreviewAsync(Context, Request() with { Unit = "album", ArtifactId = Guid.Empty })).DestinationPath);
    }

    [Fact]
    public async Task PromotionReceipt_OnlyProjectsARealRemainingSource()
    {
        var track = ReceiptTrack("Needs review");
        track.Provider = "deezer";
        var id = await SeedReceiptAsync([track]);
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), recordingId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { ExternalProvider = "qobuz", ExternalId = "remaining", Codec = "flac", BitDepth = 24, SampleRate = 96000 } });
        var batch = BatchService(Mock.Of<IPromotionCatalog>(), Mock.Of<IPromotionAcquisition>(), identityService: identities.Object);
        Assert.Equal("qobuz", (await batch.GetReceiptAsync(Context, id)).Tracks[0].NextSource!.Provider);
        Assert.Null((await service.GetReceiptAsync(Context, id)).Tracks[0].NextSource);
        var unknownSource = await SeedReceiptAsync([ReceiptTrack("Needs review")]);
        Assert.Null((await batch.GetReceiptAsync(Context, unknownSource)).Tracks[0].NextSource);
    }

    [Fact]
    public async Task PromotionQuality_ExposesMeasuredBandsAndKeepsOldChecksCompatible()
    {
        var source = await WaveSourceAsync(42000);
        var cache = new allstarr.Services.Common.FileMediaApplicationCache(new allstarr.Services.Common.FileMediaCacheOptions(Path.Combine(directory, "band-cache")),
            new allstarr.Core.Operations.SystemPlatformClock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Services.Common.FileMediaApplicationCache>.Instance);
        var check = await new allstarr.Services.Common.AudioQualityAnalyzer(cache).AnalyzeAsync(source.SourcePath);
        Assert.Equal(22000, check.FrequencyMaxHz);
        Assert.Equal(24, check.EnergyByFrequency!.Length);
        Assert.Contains(check.EnergyByFrequency, value => value == 1);
        Assert.Contains(check.EnergyByFrequency, value => value == null);
        Assert.All(check.EnergyByFrequency.Where(value => value.HasValue), value => Assert.InRange(value!.Value, 0, 1));
        var old = JsonSerializer.Deserialize<allstarr.Services.Common.AudioQualityCheck>("{\"claimedSampleRate\":44100,\"claimedBitDepth\":16,\"effectiveBandwidthHz\":21000,\"verdict\":\"standard\",\"confidence\":0.9}");
        Assert.Null(old!.EnergyByFrequency);
        Assert.Null(old.FrequencyMaxHz);
    }

    private PromotionTrack ReceiptTrack(string outcome) => new()
    {
        RecordingId = recordingId,
        Song = new Song { CanonicalRecordingId = recordingId, Title = "Song", Artist = "Artist", Album = "Album" },
        Outcome = outcome
    };

    private async Task<Guid> SeedReceiptAsync(List<PromotionTrack> tracks, Guid? runOwner = null,
        DateTimeOffset? createdAt = null, Guid? refreshId = null, string outcome = "Running")
    {
        var id = Guid.NewGuid();
        var currentOwner = runOwner ?? owner;
        var job = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(LibraryPromotionService.JobType, $"receipt:{id:N}", new { }, currentOwner));
        await using var db = factory.CreateDbContext();
        db.Add(new PromotionRunRecord
        {
            Id = id,
            OwnerUserId = currentOwner,
            JobId = job.JobId,
            RequestKey = id.ToString("N"),
            RequestJson = JsonSerializer.Serialize(Request(id.ToString("N")) with { Unit = "album" }),
            PlanJson = JsonSerializer.Serialize(new { Version = 2, Profile = new PromotionProfile(), Tracks = tracks, RefreshJobId = refreshId }),
            Outcome = outcome,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }
}
