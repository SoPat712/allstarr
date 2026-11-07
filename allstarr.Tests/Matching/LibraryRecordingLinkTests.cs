using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedLibraryRecordingLinksUseCurrentAccessInsteadOfScannerOwnership(bool visible, bool focused)
    {
        var local = await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        await using (var db = _factory.CreateDbContext())
        {
            (await db.LibraryTracks.SingleAsync()).OwnerUserId = _userB;
            await db.SaveChangesAsync();
        }
        var source = await SeedLinkSourceAsync();
        var access = new TestBackendLibraryAccess(_factory, "music");
        access.Permissions[_userA] = new BackendLibraryAccess(true, visible ? ["music"] : []);
        var service = new TrackIdentityService(_factory, _storageState, _clock, libraryAccess: access);

        var result = await service.LinkLibraryRecordingsAsync(Actor(_userA), "shared-library", source,
            focused ? local.Id : null);

        Assert.Equal(visible ? 1 : 0, result.Linked);
        await using var saved = _factory.CreateDbContext();
        Assert.Equal(visible ? source : (Guid?)null, (await saved.LibraryTracks.SingleAsync()).CanonicalRecordingId);
    }

    [Fact]
    public async Task SharedLibraryBackfillQueuesTheAuthorizedListenerWithoutAnOwnedIndexRow()
    {
        await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        await using (var db = _factory.CreateDbContext())
        {
            (await db.LibraryTracks.SingleAsync()).OwnerUserId = _userB;
            await db.SaveChangesAsync();
        }
        var access = new TestBackendLibraryAccess(_factory, "music");
        access.Permissions[_userB] = new(true, []);
        access.Permissions[_userC] = new(true, []);
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);

        Assert.Equal(1, await new LibraryRecordingLinkQueue(_factory, jobs, access).EnqueuePendingAsync(default));

        await using var saved = _factory.CreateDbContext();
        Assert.Equal(_userA, (await saved.Jobs.SingleAsync()).OwnerUserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPromotionConfirmationUsesTheListenersCurrentLibraryAccess(bool visible)
    {
        var source = await SeedLinkSourceAsync();
        var local = await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.LibraryTracks.SingleAsync();
            row.OwnerUserId = _userB;
            row.CanonicalRecordingId = source;
            await db.SaveChangesAsync();
        }
        var access = new TestBackendLibraryAccess(_factory, "music");
        access.Permissions[_userA] = new(true, visible ? ["music"] : []);
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        await jobs.EnqueueAsync(new DurableJobEnqueueRequest<LibraryRecordingLinkPayload>(LibraryRecordingLinkQueue.JobType,
            "promotion", new(_userA) { RecordingIds = [source], BackendLibraryId = "music" }, _userA));
        var claim = await jobs.ClaimNextAsync("library-link-worker", [LibraryRecordingLinkQueue.JobType]);
        var service = new TrackIdentityService(_factory, _storageState, _clock, libraryAccess: access);
        var handler = new LibraryRecordingLinkJobHandler(service, _factory, _clock, libraryAccess: access);

        var result = await handler.ExecuteAsync(new(claim!, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(visible ? DurableJobCompletionKind.Succeeded : DurableJobCompletionKind.Deferred, result.Kind);
        await using var saved = _factory.CreateDbContext();
        Assert.Equal(local.Id, (await saved.LibraryTracks.SingleAsync()).Id);
    }

    [Fact]
    public async Task LibraryRecordingLink_StoredIdentityConnectsAnExistingLocalIdentifier()
    {
        var actor = Actor(_userA);
        var local = await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000, "USRC17607839", createRecording: false);
        var source = (await _service.CreateRecordingAsync(actor, "source", isrc: "USRC17607839")).Recording;
        await _service.LinkAsync(Context(actor, "apple-download"), new(source.Id, Track("apple-download", "catalog-track"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "isrc-lookup", 1));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(source.Id, (await db.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
    }

    [Fact]
    public async Task LibraryRecordingLink_MetadataConnectsLocalizedCreditAndKeepsSavedPublicRoute()
    {
        var actor = Actor(_userA);
        var local = await SeedLinkLibraryTrackAsync("전정국", "Jung Kook", 210928,
            musicBrainzId: "61dc4fe7-f5e1-43b6-90d4-b568655aba87");
        var source = await _service.RememberSourceAsync(Context(actor, "apple-download"), Track("apple-download", "catalog-track"),
            "ext-apple-download-song-catalog-track", ProviderIdentityScope.Catalog);

        await _service.RememberPlaybackMetadataAsync(Context(actor, "apple-download"), Track("apple-download", "catalog-track"),
            new Song { Title = "Stay Alive (Prod. SUGA of BTS)", Artist = "JUNG KOOK", Duration = 210 });

        var restarted = new TrackIdentityService(_factory, _storageState, _clock);
        var recording = await restarted.ResolveRecordingAsync(actor, "ext-apple-download-song-catalog-track");
        await using var db = await _factory.CreateDbContextAsync();
        var linked = await db.LibraryTracks.SingleAsync(item => item.Id == local.Id);
        Assert.Equal(recording!.Id, linked.CanonicalRecordingId);
        Assert.Equal(local.MusicBrainzRecordingId, recording.MusicBrainzRecordingId);
        Assert.Equal(recording.Id, (await restarted.ResolveRecordingAsync(actor, source.ToString("D")))!.Id);
        Assert.Equal(2, await db.CanonicalRecordings.CountAsync());
        Assert.Single(await db.CanonicalRecordings.Where(item => item.MergedIntoId != null).ToArrayAsync());
        var (index, context) = LinkLibraryIndex();
        Assert.Equal(local.BackendItemId, await index.ResolveKnownTrackAsync(context, "ext-apple-download-song-catalog-track"));
        Assert.Equal(0, (await restarted.LinkLibraryRecordingsAsync(actor, "repeat")).Linked);
    }

    [Theory]
    [InlineData("Other Artist", null, 210000L)]
    [InlineData("Jung Kook", null, 213000L)]
    [InlineData("Jung Kook", null, null)]
    public async Task LibraryRecordingLink_RequiresArtistEvidenceAndCloseKnownDuration(string artist, string? albumArtist, long? duration)
    {
        var source = await SeedLinkSourceAsync();
        var local = await SeedLinkLibraryTrackAsync(artist, albumArtist, duration);
        var result = await _service.LinkLibraryRecordingsAsync(Actor(_userA), "evidence", source);
        Assert.Equal(0, result.Linked);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Null((await db.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
    }

    [Fact]
    public async Task LibraryRecordingLink_UsesExplicitProviderArtistAliases()
    {
        var source = await SeedLinkSourceAsync();
        var local = await SeedLinkLibraryTrackAsync("전정국", null, 212000);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "track");
            snapshot.Id = Guid.NewGuid();
            snapshot.SnapshotVersion++;
            snapshot.RetrievedAt = _clock.UtcNow.AddSeconds(1);
            snapshot.PayloadJson = JsonSerializer.Serialize(new
            {
                Title = "Stay Alive",
                Artists = new[] { new { Name = "Jung Kook", Aliases = new[] { "전정국" } } },
                DurationMilliseconds = 210000
            });
            snapshot.PayloadSha256 = CanonicalCatalogKeys.Hash(snapshot.PayloadJson);
            db.ExternalMetadataSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, (await _service.LinkLibraryRecordingsAsync(Actor(_userA), "aliases", source)).Linked);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await verify.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
    }

    [Fact]
    public async Task LibraryRecordingLink_PreservesAliasesWhenPlaybackMetadataIsStored()
    {
        var source = await SeedLinkSourceAsync();
        var local = await SeedLinkLibraryTrackAsync("전정국", null, 210000);
        await _service.RememberPlaybackMetadataAsync(Context(Actor(_userA), "apple-download"), Track("apple-download", "catalog-track"),
            new Song { Title = "Stay Alive", Artist = "Jung Kook", ArtistAliases = ["전정국"], Duration = 210 });
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await db.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
        Assert.Contains("전정국", (await _service.GetStoredSongsAsync(Actor(_userA), source.ToString("D"))).Single().ArtistAliases);
    }

    [Fact]
    public async Task LibraryRecordingLink_DoesNotTreatOneCollaboratingArtistAsTheWholeCredit()
    {
        var source = await SeedLinkSourceAsync();
        await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "track");
            snapshot.Id = Guid.NewGuid();
            snapshot.SnapshotVersion++;
            snapshot.RetrievedAt = _clock.UtcNow.AddSeconds(1);
            snapshot.PayloadJson = """{"Title":"Stay Alive","Artists":["Jung Kook","Guest"],"DurationMilliseconds":210000}""";
            snapshot.PayloadSha256 = CanonicalCatalogKeys.Hash(snapshot.PayloadJson);
            db.ExternalMetadataSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, (await _service.LinkLibraryRecordingsAsync(Actor(_userA), "credits", source)).Linked);
    }

    [Fact]
    public async Task LibraryRecordingLink_EnrichmentConnectsNewlyKnownIsrcToAnIndexedTrack()
    {
        var actor = Actor(_userA);
        var source = await _service.RememberSourceAsync(Context(actor, "apple-download"), Track("apple-download", "catalog-track"),
            "ext-apple-download-song-catalog-track", ProviderIdentityScope.Catalog, "USRC17607839");
        var local = await SeedLinkLibraryTrackAsync("Different display", null, null, "GBAYE0601498");
        await _service.EnrichRecordingAsync(actor, source, "USRC17607839", "61dc4fe7-f5e1-43b6-90d4-b568655aba87",
            ["USRC17607839", "GBAYE0601498"], "enrichment");
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await db.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
    }

    [Fact]
    public async Task LibraryRecordingLink_DoesNotJoinConflictingMusicBrainzRecordings()
    {
        var source = await SeedLinkSourceAsync();
        await _service.CreateRecordingAsync(Actor(_userA), "source-mbid", null,
            "11111111-1111-4111-8111-111111111111", "ext-apple-download-song-catalog-track");
        var local = await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000,
            musicBrainzId: "22222222-2222-4222-8222-222222222222");
        Assert.Equal(0, (await _service.LinkLibraryRecordingsAsync(Actor(_userA), "conflict", source)).Linked);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(local.CanonicalRecordingId, (await db.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
        Assert.All(await db.CanonicalRecordings.ToArrayAsync(), item => Assert.Null(item.MergedIntoId));
    }

    [Theory]
    [InlineData(ProviderUseMode.ManualOnly)]
    [InlineData(ProviderUseMode.Off)]
    public async Task LibraryRecordingLink_RespectsAutomaticSourcePolicy(ProviderUseMode mode)
    {
        var source = await SeedLinkSourceAsync();
        var local = await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        var policies = PolicyResolver(_userA, ("apple-musickit", mode));
        var service = new TrackIdentityService(_factory, _storageState, _clock, providerPolicies: policies.Object);
        Assert.Equal(0, (await service.LinkLibraryRecordingsAsync(Actor(_userA), "policy", source)).Linked);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Null((await db.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibraryRecordingLink_ExactIdentifiersTakePriorityOverLabels(bool useMusicBrainz)
    {
        const string mbid = "61dc4fe7-f5e1-43b6-90d4-b568655aba87";
        var actor = Actor(_userA);
        var recording = await _service.CreateRecordingAsync(actor, "exact", useMusicBrainz ? null : "USRC17607839",
            useMusicBrainz ? mbid : null);
        await Link(recording.Recording.Id, Context(actor, "apple-download"), "catalog-track");
        var local = await SeedLinkLibraryTrackAsync("Unrelated display", null, null,
            useMusicBrainz ? null : "USRC17607839", useMusicBrainz ? mbid : null, title: "Alternate display", createRecording: false);
        Assert.Equal(1, (await _service.LinkLibraryRecordingsAsync(actor, "exact")).Linked);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(recording.Recording.Id, (await db.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
    }

    [Theory]
    [InlineData(ManualOverrideDecision.Pin)]
    [InlineData(ManualOverrideDecision.Reject)]
    public async Task LibraryRecordingLink_PreservesActiveManualAuthority(ManualOverrideDecision decision)
    {
        var source = await SeedLinkSourceAsync();
        var local = await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var snapshot = await db.ExternalMetadataSnapshots.FirstAsync();
            db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userA,
                ExternalSnapshotId = snapshot.Id,
                SourceProviderId = "apple-download",
                SourceExternalIdHash = CanonicalCatalogKeys.Hash("catalog-track"),
                LibraryTrackId = local.Id,
                Decision = decision,
                Reason = "Saved choice",
                DecisionVersion = 1,
                CreatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, (await _service.LinkLibraryRecordingsAsync(Actor(_userA), "manual", source)).Linked);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Null((await verify.LibraryTracks.SingleAsync(item => item.Id == local.Id)).CanonicalRecordingId);
        Assert.Null((await verify.CanonicalRecordings.SingleAsync(item => item.Id == source)).MergedIntoId);
    }

    [Fact]
    public async Task LibraryRecordingLink_LeavesAmbiguousLocalRecordingsAndConflictingIdentifiersSeparate()
    {
        var source = await SeedLinkSourceAsync();
        await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        Assert.Equal(0, (await _service.LinkLibraryRecordingsAsync(Actor(_userA), "ambiguous", source)).Linked);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.All(await db.LibraryTracks.ToArrayAsync(), track => Assert.Null(track.CanonicalRecordingId));
    }

    [Fact]
    public async Task LibraryRecordingLink_UpsertConnectsExistingProviderMetadata()
    {
        var source = await SeedLinkSourceAsync();
        var (index, context) = LinkLibraryIndex();
        var indexed = await index.UpsertAsync(context, new("music", "native-track", "/music/track.flac", "Stay Alive",
            "전정국", "Album", "Jung Kook", 210928, "backend", _clock.UtcNow, null,
            "61dc4fe7-f5e1-43b6-90d4-b568655aba87", null, null, null, null, null, null, _clock.UtcNow));
        Assert.Equal((await _service.ResolveRecordingAsync(Actor(_userA), source.ToString("D")))!.Id, indexed.CanonicalRecordingId);
        Assert.Equal("native-track", await index.ResolveKnownTrackAsync(context, "ext-apple-download-song-catalog-track"));
    }

    [Fact]
    public async Task LibraryRecordingLink_BackfillIsDurableOneTimeAndReportsCounts()
    {
        await SeedLinkSourceAsync();
        await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000);
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        var queue = new LibraryRecordingLinkQueue(_factory, jobs);
        Assert.Equal(1, await queue.EnqueuePendingAsync(default));
        Assert.Equal(0, await new LibraryRecordingLinkQueue(_factory, jobs).EnqueuePendingAsync(default));
        var claim = await jobs.ClaimNextAsync("library-link-worker", [LibraryRecordingLinkQueue.JobType]);
        Assert.NotNull(claim);
        DurableJobProgressUpdate? progress = null;
        var execution = new DurableJobExecutionContext(claim!, Mock.Of<IServiceProvider>())
        { ReportProgressAsync = (update, _) => { progress = update; return Task.FromResult(true); } };
        var handler = new LibraryRecordingLinkJobHandler(_service, _factory, _clock);
        var result = await handler.ExecuteAsync(execution, default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Contains("1 linked", progress!.Message);
        await jobs.CompleteAsync(claim!, result);
        await using (var cleanup = await _factory.CreateDbContextAsync())
            await cleanup.Jobs.ExecuteDeleteAsync();
        var restartedJobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        Assert.Equal(0, await new LibraryRecordingLinkQueue(_factory, restartedJobs).EnqueuePendingAsync(default));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var restartedHandler = new LibraryRecordingLinkJobHandler(identities.Object, _factory, _clock);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await restartedHandler.ExecuteAsync(execution, default)).Kind);
        identities.VerifyNoOtherCalls();
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.LibraryTracks.ToArrayAsync());
        Assert.Single(await db.ProviderTrackIdentities.ToArrayAsync());
    }

    private async Task<Guid> SeedLinkSourceAsync()
    {
        var actor = Actor(_userA);
        var source = await _service.RememberSourceAsync(Context(actor, "apple-download"), Track("apple-download", "catalog-track"),
            "ext-apple-download-song-catalog-track", ProviderIdentityScope.Catalog);
        await _service.RememberPlaybackMetadataAsync(Context(actor, "apple-download"), Track("apple-download", "catalog-track"),
            new Song { Title = "Stay Alive", Artist = "Jung Kook", Duration = 210 });
        return source;
    }

    private async Task<LibraryTrackRecord> SeedLinkLibraryTrackAsync(string artist, string? albumArtist, long? duration,
        string? isrc = null, string? musicBrainzId = null, string title = "Stay Alive", bool createRecording = true)
    {
        Guid? canonicalId = musicBrainzId == null || !createRecording ? null : (await _service.CreateRecordingAsync(Actor(_userA),
            "local-recording", musicBrainzRecordingId: musicBrainzId)).Recording.Id;
        var track = new LibraryTrackRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = _userA,
            CanonicalRecordingId = canonicalId,
            BackendLibraryId = "music",
            Protocol = "jellyfin",
            BackendInstanceId = "fixture",
            BackendItemId = Guid.CreateVersion7().ToString("N"),
            FilePath = "/music/track.flac",
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist,
            DurationMilliseconds = duration,
            Isrc = isrc,
            MusicBrainzRecordingId = musicBrainzId,
            IndexedAt = _clock.UtcNow,
            SourceModifiedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await using var db = await _factory.CreateDbContextAsync();
        db.LibraryTracks.Add(track);
        await db.SaveChangesAsync();
        return track;
    }

    private (LibraryIndexService Index, ProtocolExecutionContext Context) LinkLibraryIndex()
    {
        var principal = new AllstarrPrincipal(_userA, "jellyfin", "fixture", _userA.ToString("N"), "User", false);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", _userA.ToString("N"), principal,
            "library-link", _clock.UtcNow.AddMinutes(5), default);
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        return (new(_factory, _storageState, _clock, _service, access.Object), context);
    }
}
