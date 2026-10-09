using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class PlaylistPersistenceServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private TrackMatchCommandService _matches = null!;
    private PlaylistPersistenceService _playlists = null!;
    private TestBackendLibraryAccess _access = null!;
    private Guid _userA;
    private Guid _userB;
    private Guid _accountA;
    private Guid _localTrack;
    private readonly DateTimeOffset _now = new(2026, 7, 12, 3, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        _userA = Guid.CreateVersion7(); _userB = Guid.CreateVersion7(); _accountA = Guid.CreateVersion7(); _localTrack = Guid.CreateVersion7();
        await using var db = await _factory.CreateDbContextAsync();
        db.Users.AddRange(User(_userA, "A"), User(_userB, "B"));
        db.ProviderAccounts.Add(new ProviderAccountRecord { Id = _accountA, OwnerUserId = _userA, ProviderId = "fixture", DisplayName = "A", Enabled = true, CreatedAt = _now, UpdatedAt = _now });
        db.LibraryTracks.Add(new LibraryTrackRecord { Id = _localTrack, OwnerUserId = _userA, BackendLibraryId = "music", Protocol = "jellyfin", BackendInstanceId = "backend", BackendItemId = "local-1", FilePath = "/media/Music/local.flac", Title = "Local", Artist = "Artist", DurationMilliseconds = 1000, ProviderIdsJson = "{}", IndexedAt = _now, SourceModifiedAt = _now, UpdatedAt = _now });
        await db.SaveChangesAsync();
        _access = new TestBackendLibraryAccess(_factory, "music");
        var resolver = new ProviderAccountResolver(_factory); var clock = new PersistenceClock(_now);
        _matches = new TrackMatchCommandService(_factory, new TrackMatchDecisionEngine(), resolver, clock, _access);
        _playlists = new PlaylistPersistenceService(_factory, resolver, clock, _matches, _access);
    }

    [Fact]
    public async Task LocalMatches_UseViewerPermissionsAcrossIndexOwnersAndStopAfterRevocation()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var local = await db.LibraryTracks.SingleAsync();
            local.OwnerUserId = _userB;
            local.BackendLibraryId = "second-library";
            await db.SaveChangesAsync();
        }
        _access.Permissions[_userA] = new(true, ["second-library"]);
        var context = Context(_userA, "principal-a");
        var actor = new TrackMatchActor(_userA, false);
        var snapshot = await _matches.CaptureSnapshotAsync(context, Snapshot(1, "track-1"));
        var decision = new MatchDecisionInput(snapshot.Id, _localTrack, null, TrackMatchState.Accepted,
            .95, .8, 1, snapshot.SnapshotVersion, 1, TrackMatchDecisionEngine.AlgorithmVersion,
            "policy-v1", "[]", "[\"exact\"]", "[]");
        await _matches.RecordDecisionAsync(context, decision);
        await _matches.SetOverrideAsync(context, new ManualOverrideInput(snapshot.Id,
            ManualOverrideDecision.Pin, _localTrack, "confirmed"));
        Assert.Equal(_localTrack, Assert.Single(await _matches.SearchLocalTracksAsync(actor, "Local")).Id);
        Assert.Equal(_localTrack, Assert.Single((await _matches.GetReviewDataAsync(actor)).LibraryTracks).Id);
        var link = await _playlists.CreateLinkAsync(context, Link());
        var source = await _playlists.CaptureSourceSnapshotAsync(context, link.Id,
            new PlaylistSourceSnapshotInput(1, "rev-1", "etag-1", "Provider list", null, null, Hash("playlist"),
                [new PlaylistSourceEntryInput(0, snapshot.Id, Hash("entry-1"))]));
        Assert.Equal("local-1", Assert.Single((await _playlists.ReadPreviewAsync(context, link.Id, source.Id)).Entries)
            .ResolvedRoute!.BackendItemId);

        _access.Permissions[_userA] = BackendLibraryAccess.Unavailable;
        Assert.Empty(await _matches.SearchLocalTracksAsync(actor, "Local"));
        Assert.Empty((await _matches.GetReviewDataAsync(actor)).LibraryTracks);
        Assert.Empty((await _matches.GetDetailAsync(actor, "fixture", "track-1", "local-1")).LocalTracks);
        var denied = Assert.Single((await _playlists.ReadPreviewAsync(context, link.Id, source.Id)).Entries);
        Assert.False(denied.TargetEligible);
        Assert.Null(denied.ResolvedRoute?.BackendItemId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _matches.RecordDecisionAsync(context,
            decision with { DecisionVersion = 2 }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _matches.SetOverrideAsync(context,
            new ManualOverrideInput(snapshot.Id, ManualOverrideDecision.Pin, _localTrack, "denied")));
    }

    [Fact]
    public async Task SnapshotMatchOverridePreviewAndRun_AreScopedOrderedAndIdempotent()
    {
        var context = Context(_userA, "principal-a");
        var first = await _matches.CaptureSnapshotAsync(context, Snapshot(1, "track-1"));
        var duplicate = await _matches.CaptureSnapshotAsync(context, Snapshot(1, "track-1"));
        var second = await _matches.CaptureSnapshotAsync(context, Snapshot(1, "track-2"));
        Assert.Equal(first.Id, duplicate.Id);
        var decisionInput = new MatchDecisionInput(
            first.Id, _localTrack, null, TrackMatchState.Accepted, .95, .8, 1,
            first.SnapshotVersion, 1, TrackMatchDecisionEngine.AlgorithmVersion,
            "policy-v1", "[]", "[\"exact\"]", "[]");
        var decision = await _matches.RecordDecisionAsync(context, decisionInput);
        var restartedMatches = new TrackMatchCommandService(
            _factory,
            new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(_factory),
            new PersistenceClock(_now), new TestBackendLibraryAccess(_factory, "music"));
        Assert.Equal(
            decision.Id,
            (await restartedMatches.RecordDecisionAsync(context, decisionInput)).Id);
        var concurrentReads = await Task.WhenAll(
            Enumerable.Range(0, 4)
                .Select(_ => restartedMatches.RecordDecisionAsync(context, decisionInput)));
        Assert.All(concurrentReads, item => Assert.Equal(decision.Id, item.Id));
        var rejected = await _matches.SetOverrideAsync(context, new ManualOverrideInput(first.Id, ManualOverrideDecision.Reject, null, "wrong edition"));
        Assert.Equal(1, rejected.DecisionVersion);
        Assert.Equal(_localTrack, rejected.LibraryTrackId);
        Assert.Equal(TrackMatchDecisionEngine.AlgorithmVersion, rejected.MatcherVersion);

        var link = await _playlists.CreateLinkAsync(context, Link());
        Assert.Equal(link.Id, (await _playlists.CreateLinkAsync(context, Link())).Id);
        var source = await _playlists.CaptureSourceSnapshotAsync(context, link.Id, new PlaylistSourceSnapshotInput(1, "rev-1", "etag-1", "Provider list", "description", "fixture:art:1", Hash("playlist"),
            [new PlaylistSourceEntryInput(0, first.Id, Hash("entry-1")), new PlaylistSourceEntryInput(1, second.Id, Hash("entry-2"))]));
        var preview = await _playlists.ReadPreviewAsync(context, link.Id, source.Id);
        Assert.Equal([0, 1], preview.Entries.Select(item => item.Position));
        Assert.Equal(TrackMatchState.Rejected, preview.Entries[0].State);
        Assert.Equal("fixture", preview.Entries[0].SourceIdentity!.ProviderId);
        Assert.Equal("track-1", preview.Entries[0].SourceMetadata!.Title);
        Assert.Equal(PlaylistMaterializationOutcomeCodes.SkippedRejected, preview.Entries[0].OutcomeCode);
        Assert.False(preview.Entries[0].TargetEligible);

        await _matches.RevokeOverrideAsync(context, rejected.Id, rejected.Revision);
        preview = await _playlists.ReadPreviewAsync(context, link.Id, source.Id);
        Assert.Equal(TrackMatchState.Accepted, preview.Entries[0].State);
        Assert.True(preview.Entries[0].TargetEligible);
        Assert.Equal(PlaylistMaterializationOutcomeCodes.IncludedNativeBackendItem, preview.Entries[0].OutcomeCode);
        Assert.Equal(TrackRouteKind.Local, preview.Entries[0].ResolvedRoute!.Kind);
        var pinned = await _matches.SetOverrideAsync(context, new ManualOverrideInput(first.Id, ManualOverrideDecision.Pin, _localTrack, "confirmed"));
        Assert.Equal(2, pinned.DecisionVersion);
        preview = await _playlists.ReadPreviewAsync(context, link.Id, source.Id);
        Assert.Equal(TrackMatchState.Pinned, preview.Entries[0].State);

        await using var db = await _factory.CreateDbContextAsync();
        var sourceEntry = await db.PlaylistSourceEntries.OrderBy(item => item.SourcePosition).FirstAsync();
        var runInput = new PlaylistRunInput(source.Id, 1, "run-one", "rules-v1", PlaylistMaterializationMode.Reconcile, PlaylistSyncState.Succeeded, "target-v1");
        var result = new PlaylistRunEntryInput(sourceEntry.Id, decision.Id, _localTrack, 0, 0, PlaylistEntryOutcome.Reused, null, "{}");
        var run = await _playlists.RecordRunAsync(context, link.Id, runInput, [result]);
        Assert.Equal(run.Id, (await _playlists.RecordRunAsync(context, link.Id, runInput, [result])).Id);
        Assert.Single(await db.PlaylistSyncEntryResults.ToListAsync());
    }

    [Fact]
    public async Task SearchLocalTracks_MatchesArtistAndTitleAcrossFields()
    {
        var tracks = await _matches.SearchLocalTracksAsync(
            new TrackMatchActor(_userA, false),
            "Artist Local",
            "music");
        var sourceAware = await _matches.SearchLocalTracksAsync(
            new TrackMatchActor(_userA, false),
            "words absent from the library",
            "music",
            source: new ExternalTrackMatchSnapshot(
                "source", "spotify", "track", "Local", "Artist", null, null,
                1_000, null, null, null));

        Assert.Equal(_localTrack, Assert.Single(tracks).Id);
        Assert.Equal(_localTrack, Assert.Single(sourceAware).Id);
    }

    [Theory]
    [InlineData("foreign-owner")]
    [InlineData("provider")]
    [InlineData("account")]
    [InlineData("hash")]
    [InlineData("catalog")]
    [InlineData("missing")]
    [InlineData("snapshot-kind")]
    public async Task SnapshotIdentity_MustMatchTheExactSourceScope(string mismatch)
    {
        var identity = await SeedSourceIdentityAsync(mismatch);
        var input = Snapshot(1, "track-safe") with
        {
            ProviderTrackIdentityId = mismatch == "missing" ? Guid.CreateVersion7() : identity.Id,
            ResourceKind = mismatch == "snapshot-kind" ? "album" : "track"
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _matches.CaptureSnapshotAsync(Context(_userA, "principal-a"), input));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ExternalMetadataSnapshots.ToListAsync());
        var existing = await _matches.CaptureSnapshotAsync(
            Context(_userA, "principal-a"), input with { ProviderTrackIdentityId = null });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _matches.CaptureSnapshotAsync(Context(_userA, "principal-a"), input));
        Assert.Equal(existing.Id, (await db.ExternalMetadataSnapshots.SingleAsync()).Id);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("catalog-scope")]
    public async Task SnapshotIdentity_AllowsAuthorizedAccountAndCatalogLinks(string scope)
    {
        var identity = await SeedSourceIdentityAsync(scope);
        var input = Snapshot(1, "track-safe") with { ProviderTrackIdentityId = identity.Id };
        var context = Context(_userA, "principal-a");
        var first = await _matches.CaptureSnapshotAsync(context, input);
        var repeated = await _matches.CaptureSnapshotAsync(context, input with { ResourceKind = " Track " });

        Assert.Equal(identity.Id, first.ProviderTrackIdentityId);
        Assert.Equal(first.Id, repeated.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _matches.CaptureSnapshotAsync(
            context, input with { ProviderTrackIdentityId = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _matches.CaptureSnapshotAsync(
            Context(_userA, "different-principal"), input));
    }

    private async Task<ProviderTrackIdentityRecord> SeedSourceIdentityAsync(string variant)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var owner = _userA;
        if (variant == "foreign-owner")
        {
            owner = Guid.CreateVersion7();
            var user = User(owner, "Foreign");
            db.Users.Add(user);
        }

        var account = _accountA;
        if (variant is "account" or "foreign-owner")
        {
            account = Guid.CreateVersion7();
            db.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = account,
                OwnerUserId = variant == "account" ? _userB : owner,
                ProviderId = "fixture",
                DisplayName = "Other account",
                Enabled = true,
                CreatedAt = _now,
                UpdatedAt = _now
            });
        }

        var recording = new CanonicalRecordingRecord
        {
            Id = Guid.CreateVersion7(),
            CreatedByUserId = owner,
            IsProvisional = true,
            CreatedAt = _now,
            UpdatedAt = _now
        };
        db.CanonicalRecordings.Add(recording);
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            CanonicalRecordingId = recording.Id,
            ProviderId = variant == "provider" ? "other" : "fixture",
            ProviderAccountId = variant is "catalog-scope" or "provider" ? null : account,
            Scope = variant is "catalog-scope" or "provider" ? ProviderIdentityScope.Catalog : ProviderIdentityScope.Account,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = variant == "catalog" ? "other" : "default",
            ExternalId = variant == "hash" ? "different-track" : "track-safe",
            ExternalIdHash = Hash(variant == "hash" ? "different-track" : "track-safe"),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "source-snapshot",
            DecisionVersion = 1,
            VerifiedAt = _now,
            CreatedAt = _now,
            UpdatedAt = _now
        };
        db.ProviderTrackIdentities.Add(identity);
        await db.SaveChangesAsync();
        return identity;
    }

    [Fact]
    public async Task OwnerConcurrencyAndPayloadGuards_DenyUnsafeAccess()
    {
        var context = Context(_userA, "principal-a");
        var snapshot = await _matches.CaptureSnapshotAsync(context, Snapshot(1, "track-safe"));
        var link = await _playlists.CreateLinkAsync(context, Link());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _playlists.GetLinkAsync(Context(_userB, "principal-b"), link.Id));
        Assert.Equal(link.Id, (await _playlists.GetLinkAsync(Context(_userB, "principal-b", admin: true), link.Id)).Id);
        var foreignContext = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "foreign", new AllstarrPrincipal(Guid.CreateVersion7(), "jellyfin", "backend", "foreign", "foreign", false), "correlation", _now.AddMinutes(1), CancellationToken.None);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _playlists.GetLinkAsync(foreignContext, link.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _matches.GetActiveOverrideAsync(Context(_userB, "principal-b"), snapshot.Id));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _playlists.UpdateLinkAsync(context, link.Id, new PlaylistLinkUpdate(99, link.Mode, link.MaterializationMode, "rules-v2", "policy-v2", null, null, false, true, true, true, true)));
        await Assert.ThrowsAsync<ArgumentException>(() => _matches.CaptureSnapshotAsync(context, Snapshot(2, "unsafe") with { PayloadJson = "{\"accessToken\":\"raw-secret\"}" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _matches.CaptureSnapshotAsync(context, Snapshot(3, "audio") with { PayloadJson = "{\"audio\":\"data:audio/flac;base64,AAAA\"}" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _playlists.CaptureSourceSnapshotAsync(context, link.Id, new PlaylistSourceSnapshotInput(1, "rev", null, "List", null, "https://example.invalid/signed", Hash("x"), [])));

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.ExternalMetadataSnapshots.SingleAsync(); stored.PayloadJson = "{\"changed\":true}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var persistedPayloads = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Select(item => item.PayloadJson)
            .ToListAsync();
        Assert.DoesNotContain(persistedPayloads,
            payload => payload.Contains("raw-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(persistedPayloads,
            payload => payload.Contains("data:audio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_same_revision_link_updates_allow_one_winner()
    {
        var context = Context(_userA, "principal-a");
        var link = await _playlists.CreateLinkAsync(context, Link());
        var firstUpdate = new PlaylistLinkUpdate(
            link.Revision, PlaylistLinkMode.Virtual, link.MaterializationMode,
            "rules-first", "policy-first", null, null, false, true, true, true, true);
        var secondUpdate = firstUpdate with
        {
            RuleVersion = "rules-second",
            PolicyVersion = "policy-second"
        };

        async Task<(PlaylistLinkRecord? Value, Exception? Error)> ObserveAsync(
            Task<PlaylistLinkRecord> pending)
        {
            try
            {
                return (await pending, null);
            }
            catch (Exception exception)
            {
                return (null, exception);
            }
        }

        var outcomes = await Task.WhenAll(
            ObserveAsync(_playlists.UpdateLinkAsync(context, link.Id, firstUpdate)),
            ObserveAsync(_playlists.UpdateLinkAsync(context, link.Id, secondUpdate)));

        var winner = Assert.Single(outcomes, item => item.Value != null).Value!;
        var loser = Assert.Single(outcomes, item => item.Error != null).Error!;
        Assert.IsType<DbUpdateConcurrencyException>(loser);
        Assert.Equal(link.Revision + 1, winner.Revision);

        await using var db = await _factory.CreateDbContextAsync();
        var persisted = await db.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == link.Id);
        Assert.Equal(winner.Revision, persisted.Revision);
        Assert.Equal(winner.RuleVersion, persisted.RuleVersion);
        Assert.Equal(winner.PolicyVersion, persisted.PolicyVersion);
    }

    [Fact]
    public async Task ListLinks_ReturnsOwnedLinksOrAllLinksForAdministrators()
    {
        var owned = await _playlists.CreateLinkAsync(Context(_userA, "principal-a"), Link());
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PlaylistLinks.Add(new PlaylistLinkRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userB,
                ProviderAccountId = _accountA,
                SourceProviderId = "fixture",
                SourcePlaylistId = "other-playlist",
                SourcePlaylistIdHash = Hash("other-playlist"),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "backend",
                Mode = PlaylistLinkMode.Virtual,
                MaterializationMode = PlaylistMaterializationMode.Reconcile,
                PreserveManualEntries = true,
                SyncName = true,
                SyncDescription = true,
                SyncArtwork = true,
                RuleVersion = "rules-v1",
                PolicyVersion = "policy-v1",
                CreatedAt = _now,
                UpdatedAt = _now
            });
            await db.SaveChangesAsync();
        }

        var userContext = Context(_userA, "principal-a");
        Assert.Equal(owned.Id, Assert.Single(await _playlists.ListLinksAsync(userContext)).Id);

        var administratorContext = Context(_userA, "principal-a", admin: true);
        Assert.Equal(2, (await _playlists.ListLinksAsync(administratorContext)).Count);
    }

    [Fact]
    public async Task ImportAndRetentionPolicies_PersistAndLegacyUpdatesPreserveThem()
    {
        var context = Context(_userA, "principal-a");
        await Assert.ThrowsAsync<ArgumentException>(() => _playlists.CreateLinkAsync(context, Link() with
        {
            ImportMode = PlaylistImportMode.OneTime,
            ScheduleId = Guid.CreateVersion7()
        }));
        var created = await _playlists.CreateLinkAsync(context, Link() with
        {
            ImportMode = PlaylistImportMode.OneTime,
            TrackRetention = PlaylistTrackRetention.KeepAll
        });

        Assert.Equal(PlaylistImportMode.OneTime, created.ImportMode);
        Assert.Equal(PlaylistTrackRetention.KeepAll, created.TrackRetention);

        var preserved = await _playlists.UpdateLinkAsync(context, created.Id, new(
            created.Revision,
            created.Mode,
            created.MaterializationMode,
            "rules-v2",
            "policy-v2",
            null,
            created.TargetPlaylistId,
            created.MirrorStaleEntries,
            created.PreserveManualEntries,
            created.SyncName,
            created.SyncDescription,
            created.SyncArtwork));
        Assert.Equal(PlaylistImportMode.OneTime, preserved.ImportMode);
        Assert.Equal(PlaylistTrackRetention.KeepAll, preserved.TrackRetention);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _playlists.UpdateLinkAsync(context, created.Id, new(
            preserved.Revision,
            preserved.Mode,
            preserved.MaterializationMode,
            preserved.RuleVersion,
            preserved.PolicyVersion,
            null,
            preserved.TargetPlaylistId,
            preserved.MirrorStaleEntries,
            preserved.PreserveManualEntries,
            preserved.SyncName,
            preserved.SyncDescription,
            preserved.SyncArtwork,
            ImportMode: PlaylistImportMode.Linked,
            TrackRetention: PlaylistTrackRetention.OnDemand)));
        await Assert.ThrowsAsync<ArgumentException>(() => _playlists.UpdateLinkAsync(context, created.Id, new(
            preserved.Revision,
            preserved.Mode,
            preserved.MaterializationMode,
            preserved.RuleVersion,
            preserved.PolicyVersion,
            Guid.CreateVersion7(),
            preserved.TargetPlaylistId,
            preserved.MirrorStaleEntries,
            preserved.PreserveManualEntries,
            preserved.SyncName,
            preserved.SyncDescription,
            preserved.SyncArtwork)));
        var changed = await _playlists.UpdateLinkAsync(context, created.Id, new(
            preserved.Revision,
            preserved.Mode,
            preserved.MaterializationMode,
            preserved.RuleVersion,
            preserved.PolicyVersion,
            null,
            preserved.TargetPlaylistId,
            preserved.MirrorStaleEntries,
            preserved.PreserveManualEntries,
            preserved.SyncName,
            preserved.SyncDescription,
            preserved.SyncArtwork,
            TrackRetention: PlaylistTrackRetention.OnDemand));
        Assert.Equal(PlaylistImportMode.OneTime, changed.ImportMode);
        Assert.Equal(PlaylistTrackRetention.OnDemand, changed.TrackRetention);
    }

    private ExternalSnapshotInput Snapshot(int version, string id) => new(_accountA, "fixture", "track", Hash(id), version, $"rev-{id}", $"{{\"title\":\"{id}\"}}", Hash($"payload-{id}"));
    private PlaylistLinkInput Link() => new(_accountA, "fixture", "playlist-1", Hash("playlist-1"), "jellyfin", "backend", PlaylistLinkMode.Materialized, PlaylistMaterializationMode.Reconcile, "rules-v1", "policy-v1");
    private ProtocolExecutionContext Context(Guid user, string principal, bool admin = false) => new(ProtocolKind.Jellyfin, "backend", principal, new AllstarrPrincipal(user, "jellyfin", "backend", principal, principal, admin), "correlation", _now.AddMinutes(1), CancellationToken.None);
    private UserRecord User(Guid id, string name) => new() { Id = id, DisplayName = name, Enabled = true, BackendType = "jellyfin", BackendInstanceId = "backend", BackendPrincipalId = "principal-" + name.ToLowerInvariant(), CreatedAt = _now, UpdatedAt = _now };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
    private sealed class PersistenceClock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow { get; } = now; }
    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
