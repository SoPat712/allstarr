using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public async Task PromotionPreview_AlwaysPromisesTheNativeHandoverAndKeepsCleanupOptional()
    {
        var preview = await service.PreviewAsync(Context, Request());
        var handover = Assert.Single(preview.ProfileSteps, item => item.Key == "handover");
        Assert.True(handover.Enabled);
        Assert.StartsWith("Playlists, mixes, hearts and plays move to the new file.", handover.Description);
        Assert.Contains("Retain the kept copy.", handover.Description);
    }

    [Fact]
    public async Task PromotionRefresh_ImmediatelyIndexesAndConfirmsTheNativeRecording()
    {
        using var hub = new allstarr.Core.ClientEvents.ClientEventHub(new() { CoalesceWindow = TimeSpan.Zero });
        using var subscription = hub.Subscribe(owner, "listener")!;
        var batch = RequestService(clientEvents: hub);
        var additions = ClientAdditions(batch);
        var request = new allstarr.Core.ClientApi.AddClientLibraryItem("ext-deezer-song-track", "song");
        var addition = (await additions.AddIdempotentAsync(Context, request, "placement")).Addition;
        Assert.Null(addition.LibraryItemId);
        using var createdTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("libraryItemId", (await subscription.ReadAsync(createdTimeout.Token))!.Json);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Set<PromotionRunRecord>().SingleAsync(item => item.Id == addition.Id)).Outcome = "Placed";
            db.RecordingIdentifiers.Add(new()
            {
                Id = Guid.NewGuid(),
                RecordingId = recordingId,
                Kind = RecordingIdentifierKinds.Isrc,
                Value = "USRC17607839",
                Source = "fixture"
            });
            (await db.ProviderTrackIdentities.SingleAsync()).ExternalIdHash = CanonicalCatalogKeys.Hash("track");
            await db.SaveChangesAsync();
        }
        Assert.Null(Assert.Single(await additions.ListAsync(Context)).LibraryItemId);
        var clock = new SystemPlatformClock();
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        var access = new TestBackendLibraryAccess(factory, "music");
        var identities = new TrackIdentityService(factory, state, clock, libraryAccess: access);
        var index = new LibraryIndexService(factory, state, clock, identities, access);
        using var http = new HttpClient(new PromotionIndexBackend());
        var scanner = new JellyfinLibraryCatalogScanner(http, new JellyfinSettings { Url = "https://jellyfin.test", ApiKey = "fixture" }, index, clock);
        var backend = new Mock<IBackendLibraryRefresher>();
        backend.SetupGet(item => item.Protocol).Returns(ProtocolKind.Jellyfin);
        backend.Setup(item => item.RefreshAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<BackendLibraryRefreshRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryRefreshResult(true));
        await queue.EnqueueAsync(new DurableJobEnqueueRequest<BackendLibraryRefreshJobPayload>("library.refresh", "immediate-refresh",
            new("music", "backend", "principal") { RecordingIds = [recordingId] }, owner));
        var refresh = (await queue.ClaimNextAsync("promotion-test", ["library.refresh"]))!;
        var refresher = new BackendLibraryRefreshJobHandler(factory, new([backend.Object]), clock, access, queue);
        await queue.CompleteAsync(refresh, await refresher.ExecuteAsync(new(refresh, services), default));
        var link = (await queue.ClaimNextAsync("promotion-test", [LibraryRecordingLinkQueue.JobType]))!;
        var handler = new LibraryRecordingLinkJobHandler(identities, factory, clock, libraryAccess: access, scanners: new([scanner]), promotions: batch);

        var result = await handler.ExecuteAsync(new(link, services), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal("native-track", Assert.Single(await additions.ListAsync(Context)).LibraryItemId);
        Assert.Equal("native-track", (await additions.AddIdempotentAsync(Context, request, "placement")).Addition.LibraryItemId);
        using var updateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var update = (await subscription.ReadAsync(updateTimeout.Token))!;
        Assert.Equal("libraryAddition.updated", update.Type);
        Assert.Equal("native-track", JsonSerializer.Deserialize<JsonElement>(update.Json).GetProperty("libraryItemId").GetString());
        await using var verify = factory.CreateDbContext();
        var track = await verify.LibraryTracks.SingleAsync();
        Assert.Equal("native-track", track.BackendItemId);
        Assert.Equal("music", track.BackendLibraryId);
        Assert.Equal(recordingId, track.CanonicalRecordingId);
        Assert.Equal("native-track", await index.ResolveKnownTrackAsync(Context, "ext-deezer-song-track"));
        var offline = Context with
        {
            Policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<allstarr.Core.Capabilities.ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "High", .07)
            { UserId = owner, ProviderModes = ImmutableDictionary<string, allstarr.Core.Capabilities.ProviderUseMode>.Empty.Add("deezer", allstarr.Core.Capabilities.ProviderUseMode.Off) }
        };
        Assert.Equal("native-track", await index.ResolveKnownTrackAsync(offline, "ext-deezer-song-track"));
        var snapshotId = Guid.NewGuid();
        verify.ExternalMetadataSnapshots.Add(new()
        {
            Id = snapshotId,
            OwnerUserId = owner,
            ProviderId = "deezer",
            ResourceKind = "track",
            ExternalIdHash = CanonicalCatalogKeys.Hash("track"),
            SnapshotVersion = 1,
            PayloadJson = "{}",
            PayloadSha256 = CanonicalCatalogKeys.Hash("{}"),
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "principal",
            CorrelationId = "fixture"
        });
        verify.ManualTrackOverrides.Add(new()
        {
            Id = Guid.NewGuid(),
            ExternalSnapshotId = snapshotId,
            OwnerUserId = owner,
            SourceProviderId = "deezer",
            SourceExternalIdHash = CanonicalCatalogKeys.Hash("track"),
            Decision = ManualOverrideDecision.Reject,
            LibraryTrackId = track.Id,
            Reason = "Previous source choice",
            DecisionVersion = 1
        });
        await verify.SaveChangesAsync();
        Assert.Equal("native-track", await index.ResolveKnownTrackAsync(Context, "ext-deezer-song-track"));
    }

    [Fact]
    public async Task PromotionDedupe_UsesAnAccessibleLibraryCopyIndexedByAnotherListener()
    {
        await using (var db = factory.CreateDbContext())
        {
            var other = Guid.NewGuid();
            db.Users.Add(new()
            {
                Id = other,
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "scanner",
                DisplayName = "Scanner"
            });
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = other,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendLibraryId = "music",
                BackendItemId = "shared-native",
                FilePath = "/music/song.flac",
                Title = "Song",
                Artist = "Artist",
                CanonicalRecordingId = recordingId
            });
            await db.SaveChangesAsync();
        }
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song" } });
        var acquisition = new Mock<IPromotionAcquisition>(MockBehavior.Strict);
        var batch = BatchService(catalog.Object, acquisition.Object);

        var run = await batch.StartAsync(Context, Request() with { Unit = "song" });

        Assert.Equal("Skipped", Assert.Single(run.Tracks).Outcome);
        acquisition.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PromotionHandover_MovesEachListenersHeartAndHistoryOnlyAfterNativeAcceptance(bool accepted, bool revoke)
    {
        var other = Guid.NewGuid();
        var localId = Guid.NewGuid();
        const string external = "ext-deezer-song-track";
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = other,
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "other",
                DisplayName = "Other"
            });
            db.LibraryTracks.Add(new()
            {
                Id = localId,
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendLibraryId = "music",
                BackendItemId = "native",
                FilePath = "/music/song.flac",
                Title = "Song",
                Artist = "Artist",
                CanonicalRecordingId = recordingId
            });
            await db.SaveChangesAsync();
        }
        foreach (var user in new[] { owner, other })
        {
            var favoriteJob = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("favorite.actions", user.ToString(), new { }, user));
            await using var db = factory.CreateDbContext();
            var favorite = new FavoriteEventRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = user == owner ? "principal" : "other",
                ItemId = external,
                Operation = FavoriteOperation.Favorite,
                EventKey = user.ToString(),
                SourceRevision = user.ToString(),
                JobId = favoriteJob.JobId
            };
            db.FavoriteEvents.Add(favorite);
            db.FavoriteStates.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                ItemId = external,
                IsFavorite = true,
                LastEventId = favorite.Id
            });
            db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                OccurrenceKey = user.ToString(),
                TrackReference = external,
                CanonicalRecordingId = recordingId
            });
            await db.SaveChangesAsync();
        }
        if (accepted && !revoke)
        {
            await using var db = factory.CreateDbContext();
            var shared = Guid.NewGuid();
            var peer = new UserRecord
            {
                Id = Guid.NewGuid(),
                Enabled = true,
                BackendType = "subsonic",
                BackendInstanceId = "backend",
                BackendPrincipalId = "linked-peer",
                DisplayName = "Linked peer"
            };
            db.Users.Add(peer);
            var listener = await db.Users.SingleAsync(item => item.Id == owner);
            db.Add(new ProviderFavoriteLinkRecord
            {
                Id = shared,
                PairKey = FavoriteLinkScope.Hash(owner.ToString("N"), peer.Id.ToString("N")),
                ConfirmedByUserId = owner,
                ConfirmationVersion = 1,
                UnionAttempt = 1,
                Revision = 1,
                UnionAdministratorFingerprint = FavoriteLinkScope.Fingerprint(listener),
                UnionJobId = (await db.Jobs.FirstAsync()).Id,
                CompletedAt = DateTimeOffset.UtcNow
            });
            foreach (var user in new[] { listener, peer })
                db.Add(new ProviderFavoriteLinkMemberRecord
                {
                    UserId = user.Id,
                    LinkId = shared,
                    BackendInstanceId = user.BackendInstanceId,
                    BackendPrincipalId = user.BackendPrincipalId,
                    Protocol = user.BackendType,
                    IdentityFingerprint = FavoriteLinkScope.Fingerprint(user),
                    ConfirmedDisplayName = user.DisplayName
                });
            db.Add(new LinkedProviderFavoriteStateRecord
            {
                LinkId = shared,
                ProviderId = "deezer",
                ResourceKind = "track",
                ExternalId = "track",
                IsFavorite = true,
                Revision = 1,
                EffectiveAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var identities = new Mock<ITrackIdentityService>();
        var identity = new CanonicalRecordingIdentity(recordingId, owner, null, null, 1);
        identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<allstarr.Core.Capabilities.ProviderActorContext>(),
            recordingId.ToString("D"), It.IsAny<CancellationToken>())).ReturnsAsync(identity);
        identities.Setup(item => item.ResolveRecordingsAsync(It.IsAny<allstarr.Core.Capabilities.ProviderActorContext>(),
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, CanonicalRecordingIdentity?> { [external] = identity });
        var target = new Mock<IBackendPlaylistTarget>();
        target.SetupGet(item => item.Family).Returns(BackendPlaylistFamily.Jellyfin);
        var nativeHearts = new HashSet<string>();
        var undoAttempts = 0;
        target.Setup(item => item.FavoriteAsync(It.IsAny<BackendPlaylistTargetContext>(), "native", It.IsAny<CancellationToken>()))
            .Returns<BackendPlaylistTargetContext, string, CancellationToken>(async (backend, _, _) =>
            {
                if (accepted) nativeHearts.Add(backend.VerifiedPrincipalId);
                if (revoke && backend.VerifiedPrincipalId == "principal")
                    await new FavoriteActionPipeline(factory, queue, new SystemPlatformClock()).RecordAsync(new(Context, external, FavoriteOperation.Unfavorite, "removed-during-transfer"));
                return accepted;
            });
        target.Setup(item => item.UnfavoriteAsync(It.IsAny<BackendPlaylistTargetContext>(), "native", It.IsAny<CancellationToken>()))
            .Returns<BackendPlaylistTargetContext, string, CancellationToken>((backend, _, _) =>
            {
                if (++undoAttempts == 1) return Task.FromResult(false);
                nativeHearts.Remove(backend.VerifiedPrincipalId);
                return Task.FromResult(true);
            });
        await queue.EnqueueAsync(new DurableJobEnqueueRequest<LibraryRecordingLinkPayload>(LibraryRecordingLinkQueue.JobType,
            "move-listeners", new(owner) { RecordingIds = [recordingId], BackendLibraryId = "music" }, owner));
        var claim = (await queue.ClaimNextAsync("promotion-test", [LibraryRecordingLinkQueue.JobType]))!;
        var handler = new LibraryRecordingLinkJobHandler(identities.Object, factory, new SystemPlatformClock(),
            libraryAccess: new TestBackendLibraryAccess(factory, "music"), targets: new BackendPlaylistTargetResolver([target.Object]));

        var result = await handler.ExecuteAsync(new(claim, services), default);
        Assert.Equal(accepted && !revoke ? DurableJobCompletionKind.Succeeded : DurableJobCompletionKind.Deferred, result.Kind);
        foreach (var principal in new[] { "principal", "other" })
            target.Verify(item => item.FavoriteAsync(It.Is<BackendPlaylistTargetContext>(context => context.VerifiedPrincipalId == principal),
                "native", It.IsAny<CancellationToken>()), Times.Once);
        if (accepted)
        {
            Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(new(claim, services), default)).Kind);
            Assert.Equal(!revoke, nativeHearts.Contains("principal"));
            Assert.Contains("other", nativeHearts);
            if (revoke) Assert.Equal(2, undoAttempts);
        }

        await using var verify = factory.CreateDbContext();
        Assert.Single(await verify.AuditEvents.Where(item => item.Action == "promotion.library-linked").ToArrayAsync());
        Assert.Equal(accepted ? 1 : 0, await verify.AuditEvents.CountAsync(item => item.Action == "promotion.handover-confirmed"));
        Assert.All(await verify.FavoriteStates.ToArrayAsync(), state => Assert.Equal(!accepted, state.IsFavorite));
        if (accepted)
        {
            Assert.All(await verify.ListeningEvents.ToArrayAsync(), play => { Assert.Equal(localId, play.LibraryTrackId); Assert.Equal("native", play.TrackReference); });
            Assert.Equal(2, await verify.AuditEvents.CountAsync(item => item.Action == "promotion.favorite-moved"));
            foreach (var principal in new[] { "principal", "other" })
                target.Verify(item => item.FavoriteAsync(It.Is<BackendPlaylistTargetContext>(context => context.VerifiedPrincipalId == principal),
                    "native", It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task PromotionAlbumReview_AllowsTheOwnerAndAdministratorButRefusesAnotherListener()
    {
        var second = Guid.NewGuid();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "First" }, new Song { CanonicalRecordingId = second, Title = "Second" } });
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(null, false, "No source meets the floor."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request());
        await ExecuteBatchAsync(batch);
        var other = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = other,
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "other",
                DisplayName = "Other"
            });
            await db.SaveChangesAsync();
        }
        var foreign = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "other",
            new AllstarrPrincipal(other, "jellyfin", "backend", "other", "Other", false), "review", DateTimeOffset.UtcNow.AddMinutes(5), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.ReviewAsync(foreign, run.Id, recordingId, "skip", null));
        Assert.Equal("Skipped", (await batch.ReviewAsync(Context, run.Id, recordingId, "skip", null)).Tracks[0].Outcome);
        await using (var db = factory.CreateDbContext()) { (await db.Users.SingleAsync(item => item.Id == other)).IsAdmin = true; await db.SaveChangesAsync(); }
        Assert.Equal("Skipped", (await batch.ReviewAsync(foreign, run.Id, second, "skip", null)).Tracks[1].Outcome);
    }

    [Fact]
    public async Task PromotionRoot_RefusesAnotherLibraryBeforeAcquisition()
    {
        var value = JsonSerializer.Serialize(new[] { new PromotionRoot(rootId, "Music", Path.Combine(directory, "library", "music"), BackendLibraryId: "other") });
        settings.Setup(item => item.GetAsync(PromotionRoots.Key, It.IsAny<CancellationToken>())).ReturnsAsync(
            new EffectiveRuntimeSetting(PromotionRoots.Key, RuntimeSettingValueType.String, value, value,
                RuntimeSettingOrigin.Durable, 1, "test", null, null));
        var catalog = new Mock<IPromotionCatalog>(MockBehavior.Strict);
        var acquisition = new Mock<IPromotionAcquisition>(MockBehavior.Strict);
        var batch = BatchService(catalog.Object, acquisition.Object);

        await Assert.ThrowsAsync<PromotionException>(() => batch.StartAsync(Context, Request()));
        await Assert.ThrowsAsync<PromotionException>(() => batch.PreviewAsync(Context, Request()));
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<PromotionRunRecord>().ToArrayAsync());
        catalog.VerifyNoOtherCalls();
        acquisition.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionLibraryLink_IndexesTheSelectedLibraryAndBoundsConfirmation(bool timeout)
    {
        var clock = new LinkClock();
        var scanner = new Mock<IBackendLibraryCatalogScanner>();
        scanner.SetupGet(item => item.Protocol).Returns(ProtocolKind.Jellyfin);
        scanner.Setup(item => item.ScanAsync(It.IsAny<ProtocolExecutionContext>(),
                It.Is<LibraryCatalogScanRequest>(request => request.BackendLibraryId == "music"), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (!timeout)
                {
                    await using var db = factory.CreateDbContext();
                    db.LibraryTracks.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        OwnerUserId = owner,
                        Protocol = "jellyfin",
                        BackendInstanceId = "backend",
                        BackendLibraryId = "music",
                        BackendItemId = "native",
                        FilePath = "/music/song.flac",
                        Title = "Song",
                        Artist = "Artist",
                        CanonicalRecordingId = recordingId
                    });
                    await db.SaveChangesAsync();
                }
                return new LibraryCatalogScanResult(1, 1, 0, 0, 1);
            });
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<allstarr.Core.Capabilities.ProviderActorContext>(),
                recordingId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanonicalRecordingIdentity(recordingId, owner, null, null, 1));
        identities.Setup(item => item.LinkLibraryRecordingsAsync(It.IsAny<allstarr.Core.Capabilities.ProviderActorContext>(),
                It.IsAny<string>(), recordingId, null, It.IsAny<CancellationToken>())).ReturnsAsync(new LibraryRecordingLinkResult(1, 0, 0));
        await queue.EnqueueAsync(new DurableJobEnqueueRequest<LibraryRecordingLinkPayload>(LibraryRecordingLinkQueue.JobType,
            "focused-link", new(owner) { RecordingIds = [recordingId], BackendLibraryId = "music", ConfirmBy = clock.UtcNow.AddMinutes(10) }, owner));
        var claim = (await queue.ClaimNextAsync("promotion-test", [LibraryRecordingLinkQueue.JobType]))!;
        var handler = new LibraryRecordingLinkJobHandler(identities.Object, factory, clock, scanners: new([scanner.Object]));
        var execution = new DurableJobExecutionContext(claim, services);

        var result = await handler.ExecuteAsync(execution, default);
        Assert.Equal(timeout ? DurableJobCompletionKind.Deferred : DurableJobCompletionKind.Succeeded, result.Kind);
        scanner.VerifyAll();
        if (timeout)
        {
            clock.UtcNow = clock.UtcNow.AddMinutes(10);
            result = await handler.ExecuteAsync(execution, default);
            Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
            Assert.Contains("ten minutes", result.SafeMessage);
        }
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task PromotionLossySource_WithUnknownBitDepthIsNeverConvertedToFlac(int rate)
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        {
            NamingConfirmed = true,
            NamingScript = "%title%.%_extension%",
            FakeLosslessCheck = false,
            ReplayGain = false,
            CoverMode = "none",
            LyricsMode = "off",
            AllowLossy = true,
            QualityFloor = new("lossy", 16, 44100),
            HiResCeiling = new(16, 44100)
        });
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AppleProgressive", $"input-aac-{rate}.m4a");
        var bytes = await File.ReadAllBytesAsync(path);
        var source = new VerifiedProviderDownloadArtifact(Guid.NewGuid(), Guid.NewGuid(), path,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, owner, Guid.NewGuid(), "deezer", null,
            ProviderDownloadArtifactState.Verified, null)
        { Codec = "aac", SampleRate = rate };
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Lossy song", Artist = "Artist" } });
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = BatchService(catalog.Object, acquisition.Object);
        var run = await batch.StartAsync(Context, Request() with { Unit = "song" });

        await ExecuteBatchAsync(batch);

        var track = Assert.Single((await batch.GetAsync(Context, run.Id)).Tracks);
        Assert.Equal("Placed", track.Outcome);
        Assert.EndsWith(".m4a", track.DestinationPath);
        Assert.Equal("aac", track.Song.Codec);
        Assert.Null(track.Song.BitDepth);
    }

    [Fact]
    public async Task PromotionPreview_LossyHighRateKeepsItsContainer()
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        {
            NamingConfirmed = true,
            NamingScript = "%title%-%_quality%.%_extension%",
            AllowLossy = true,
            QualityFloor = new("lossy", 16, 44100),
            HiResCeiling = new(16, 44100)
        });
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song", ExternalProvider = "deezer", ExternalId = "track",
                Codec = "aac", Container = "m4a", BitDepth = 16, SampleRate = 48000 } });
        var batch = BatchService(catalog.Object, new Mock<IPromotionAcquisition>().Object);

        Assert.EndsWith("Song-AAC 16-48.m4a", (await batch.PreviewAsync(Context, Request() with { Unit = "song" })).DestinationPath);
    }

    private sealed class LinkClock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class PromotionIndexBackend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/UserViews" => """{"Items":[{"Id":"music","CollectionType":"music"},{"Id":"other","CollectionType":"music"}]}""",
                "/Items" => """{"Items":[{"Id":"native-track","Name":"Song","Artists":["Artist"],"Path":"/music/song.flac","DateCreated":"2026-11-09T12:00:00Z","RunTimeTicks":10000000,"ProviderIds":{"Isrc":"USRC17607839"}}],"TotalRecordCount":1}""",
                _ => throw new InvalidOperationException("Unexpected catalog route.")
            };
            if (request.RequestUri.AbsolutePath == "/Items") Assert.Contains("ParentId=music", request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
