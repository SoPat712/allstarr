using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class PlaylistOrchestrationIntegrationTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private DbFactory _factory = null!;
    private FakeSource _source = null!;
    private FakeTarget _target = null!;
    private PlaylistOrchestrationService _service = null!;
    private TrackMatchCommandService _trackMatches = null!;
    private TestMemoryApplicationCache _cache = null!;
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _account = Guid.CreateVersion7();
    private readonly Guid _link = Guid.CreateVersion7();
    private readonly Guid _credential = Guid.CreateVersion7();
    private Guid _identity;
    private Guid _canonical;
    private Guid _trackOne;
    private Guid _trackTwo;
    private readonly DateTimeOffset _now = new(2026, 7, 12, 5, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        _source = new FakeSource();
        _target = new FakeTarget();
        var clock = new Clock(_now);
        var accountResolver = new ProviderAccountResolver(_factory, new ProviderPolicyOptions());
        _trackMatches = new TrackMatchCommandService(
            _factory,
            new TrackMatchDecisionEngine(),
            accountResolver,
            clock);
        _cache = new TestMemoryApplicationCache();
        _service = new(_factory, _source, new FakeTargetResolver(_target), new PlaylistMaterializationPlanner(),
            new TrackMatchDecisionEngine(), _trackMatches, clock, _cache);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        _identity = Guid.CreateVersion7(); _canonical = Guid.CreateVersion7();
        _trackOne = Guid.CreateVersion7(); _trackTwo = Guid.CreateVersion7();
        db.Tenants.Add(new TenantRecord { Id = _tenant, Slug = "orchestration", Name = "Orchestration", CreatedAt = _now });
        db.Users.Add(new PlatformUserRecord { Id = _user, TenantId = _tenant, DisplayName = "Owner", Status = PlatformUserStatus.Active, CreatedAt = _now, UpdatedAt = _now });
        db.BackendIdentities.Add(new BackendIdentityRecord
        {
            Id = _identity,
            TenantId = _tenant,
            UserId = _user,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            PrincipalId = "principal",
            CreatedAt = _now,
            LastSeenAt = _now
        });
        db.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _account,
            TenantId = _tenant,
            OwnerUserId = _user,
            ProviderId = "fixture",
            DisplayName = "Fixture",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = _now,
            UpdatedAt = _now
        });
        db.CanonicalRecordings.Add(new CanonicalRecordingRecord
        {
            Id = _canonical,
            TenantId = _tenant,
            CreatedByUserId = _user,
            CreatedAt = _now,
            UpdatedAt = _now
        });
        db.ProviderTrackIdentities.AddRange(
            ProviderIdentity("source-1"),
            ProviderIdentity("source-alias"));
        db.LibraryTracks.AddRange(Local(_trackOne, "local-1", "source-1", "One"), Local(_trackTwo, "local-2", "source-2", "Two"));
        db.PlaylistLinks.Add(Link());
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Refresh_persists_duplicate_source_positions_with_one_external_snapshot()
    {
        var summaryKey = CacheKeyBuilder.BuildAdminPlaylistSummaryKey();
        await _cache.SetStringAsync(summaryKey, "{}");
        _source.Snapshot = Snapshot("revision-duplicates", Entry(0, "entry-0", "source-1", "One"), Entry(1, "entry-1", "source-1", "One"));
        var refresh = await _service.RefreshAsync(Context(), _link);

        Assert.False(await _cache.ExistsAsync(summaryKey));
        await using var db = await _factory.CreateDbContextAsync();
        var entries = await db.PlaylistSourceEntries.Where(item => item.PlaylistSourceSnapshotId == refresh.SnapshotId)
            .OrderBy(item => item.SourcePosition).ToListAsync();
        Assert.Equal([0, 1], entries.Select(item => item.SourcePosition));
        Assert.Equal(entries[0].ExternalMetadataSnapshotId, entries[1].ExternalMetadataSnapshotId);
        var external = Assert.Single(await db.ExternalMetadataSnapshots.ToListAsync());
        Assert.Contains("\"DurationMilliseconds\":180000", external.PayloadJson);
        Assert.Contains("\"durationProvenance\":\"fixture\"", external.PayloadJson);
        Assert.Equal(_now, external.RetrievedAt);
        Assert.Equal(2, await db.PlaylistSourceEntries.CountAsync());
    }

    [Fact]
    public async Task Refresh_BackfillsNewDurationWithoutMutatingThePriorGeneration()
    {
        var entry = Entry(0, "entry-duration", "source-1", "One");
        _source.Snapshot = Snapshot("revision-duration", entry with { DurationMilliseconds = null });
        var first = await _service.RefreshAsync(Context(), _link);

        _source.Snapshot = Snapshot("revision-duration", entry);
        var second = await _service.RefreshAsync(Context(), _link);
        var repeated = await _service.RefreshAsync(Context(), _link);

        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.Equal(1, first.SnapshotVersion);
        Assert.Equal(2, second.SnapshotVersion);
        Assert.Equal(second.SnapshotId, repeated.SnapshotId);
        await using var db = await _factory.CreateDbContextAsync();
        var external = await db.ExternalMetadataSnapshots
            .OrderBy(item => item.SnapshotVersion)
            .ToListAsync();
        Assert.Equal(2, external.Count);
        Assert.DoesNotContain("DurationMilliseconds\":180000", external[0].PayloadJson);
        Assert.Contains("DurationMilliseconds\":180000", external[1].PayloadJson);
    }

    [Fact]
    public async Task Manual_pin_and_reject_take_precedence_and_virtual_mode_never_calls_target()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("revision-manual", Entry(0, "entry-0", "source-1", "One"), Entry(1, "entry-1", "source-2", "Two"));
        var refresh = await _service.RefreshAsync(Context(), _link);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var externals = await db.ExternalMetadataSnapshots.OrderBy(item => item.ExternalIdHash).ToListAsync();
            var first = externals.Single(item => item.ExternalIdHash == Hash("source-1"));
            var second = externals.Single(item => item.ExternalIdHash == Hash("source-2"));
            db.ManualTrackOverrides.AddRange(
                Override(first.Id, ManualOverrideDecision.Pin, _trackTwo),
                Override(second.Id, ManualOverrideDecision.Reject, null));
            await db.SaveChangesAsync();
        }

        var result = await _service.RunAsync(Context(), new(_link, 1, refresh.SnapshotId));

        Assert.False(result.BackendWriteAttempted);
        Assert.Null(result.RunId);
        Assert.Equal(PlaylistPlanMode.Virtual, result.Plan.Mode);
        Assert.Equal("local-2", Assert.Single(result.Plan.OrderedBackendItemIds));
        Assert.Equal(PlaylistPreviewEntryStatus.Included, result.Plan.Entries[0].Status);
        Assert.Equal(PlaylistPreviewEntryStatus.Rejected, result.Plan.Entries[1].Status);
        Assert.Equal(0, _target.TotalCalls);
    }

    [Fact]
    public async Task Review_states_are_persisted_without_promoting_suggestions()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        var suggestedId = Guid.CreateVersion7();
        var ambiguousOneId = Guid.CreateVersion7();
        var ambiguousTwoId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var suggestedCandidate = Local(suggestedId, "suggested", "unused-1", "Suggestion");
            suggestedCandidate.ProviderIdsJson = "{}";
            suggestedCandidate.Album = null;
            suggestedCandidate.DurationMilliseconds = 999_000;
            var ambiguousOne = Local(ambiguousOneId, "ambiguous-1", "unused-2", "Ambiguous");
            var ambiguousTwo = Local(ambiguousTwoId, "ambiguous-2", "unused-3", "Ambiguous");
            ambiguousOne.ProviderIdsJson = ambiguousTwo.ProviderIdsJson = "{}";
            db.LibraryTracks.AddRange(suggestedCandidate, ambiguousOne, ambiguousTwo);
            await db.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot(
            "revision-states",
            Entry(0, "entry-suggested", "source-suggested", "Suggestion"),
            Entry(1, "entry-ambiguous", "source-ambiguous", "Ambiguous"),
            Entry(2, "entry-unresolved", "source-unresolved", "No Candidate"));

        await _service.RefreshAsync(Context(), _link);

        await using var verify = await _factory.CreateDbContextAsync();
        var snapshots = await verify.ExternalMetadataSnapshots
            .ToDictionaryAsync(item => item.ExternalIdHash);
        var decisions = await verify.TrackMatches.ToDictionaryAsync(item => item.ExternalSnapshotId);
        var suggested = decisions[snapshots[Hash("source-suggested")].Id];
        var ambiguous = decisions[snapshots[Hash("source-ambiguous")].Id];
        var unresolved = decisions[snapshots[Hash("source-unresolved")].Id];
        Assert.Equal(TrackMatchState.Suggested, suggested.State);
        Assert.Null(suggested.LibraryTrackId);
        Assert.Equal(TrackMatchDecisionEngine.AlgorithmVersion, suggested.MatcherVersion);
        Assert.Contains("NormalizedCandidateTitle", suggested.CandidateResultsJson);
        Assert.Contains("ArtistOverlap", suggested.CandidateResultsJson);
        Assert.Contains("DurationDeltaMilliseconds", suggested.CandidateResultsJson);
        Assert.Equal(suggestedId,
            TrackMatchOverridePolicy.TopCandidateLibraryTrackId(suggested.CandidateResultsJson));
        Assert.Equal(TrackMatchState.Ambiguous, ambiguous.State);
        Assert.Null(ambiguous.LibraryTrackId);
        Assert.Contains("ambiguous_top_candidates", ambiguous.WarningsJson);
        Assert.Equal(TrackMatchState.Unresolved, unresolved.State);
        Assert.Null(unresolved.LibraryTrackId);
        Assert.Contains("no_indexed_candidate", unresolved.WarningsJson);
    }

    [Fact]
    public async Task Accepted_canonical_mapping_is_reused_for_aliases_and_duplicate_rows()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot(
            "revision-canonical-source",
            Entry(0, "entry-source", "source-1", "One"));
        await _service.RefreshAsync(Context(), _link);

        _source.Snapshot = Snapshot(
            "revision-canonical-alias",
            Entry(0, "entry-alias-1", "source-alias", "Different metadata"),
            Entry(1, "entry-alias-2", "source-alias", "Different metadata"));
        var aliasRefresh = await _service.RefreshAsync(Context(), _link);
        var plan = await _service.RunAsync(Context(), new(_link, 2, aliasRefresh.SnapshotId));

        Assert.Equal(["local-1"], plan.Plan.OrderedBackendItemIds);
        await using var db = await _factory.CreateDbContextAsync();
        var aliasSnapshotIds = await db.ExternalMetadataSnapshots
            .Where(item => item.ExternalIdHash == Hash("source-alias"))
            .Select(item => item.Id)
            .ToArrayAsync();
        var aliasDecision = Assert.Single(await db.TrackMatches
            .Where(item => aliasSnapshotIds.Contains(item.ExternalSnapshotId))
            .ToListAsync());
        Assert.Equal(2, await db.PlaylistSourceEntries.CountAsync(item =>
            item.PlaylistSourceSnapshotId == aliasRefresh.SnapshotId));
        Assert.Equal(TrackMatchState.Accepted, aliasDecision.State);
        Assert.Equal(_trackOne, aliasDecision.LibraryTrackId);
        Assert.Equal(_canonical, aliasDecision.CanonicalRecordingId);
        Assert.Contains("canonical_recording_id_exact", aliasDecision.ReasonsJson);
    }

    [Fact]
    public async Task Stale_decisions_rescore_and_forced_rematch_preserves_manual_override()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("revision-freshness", Entry(0, "entry-0", "source-1", "One"));
        var refresh = await _service.RefreshAsync(Context(), _link);
        Guid externalId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            externalId = await db.PlaylistSourceEntries
                .Where(item => item.PlaylistSourceSnapshotId == refresh.SnapshotId)
                .Select(item => item.ExternalMetadataSnapshotId)
                .SingleAsync();
            db.ManualTrackOverrides.Add(Override(externalId, ManualOverrideDecision.Pin, _trackTwo));
            await db.SaveChangesAsync();
        }

        var unchanged = await _service.RunAsync(Context(), new(_link, 1, refresh.SnapshotId));
        Assert.Equal("local-2", Assert.Single(unchanged.Plan.OrderedBackendItemIds));
        Assert.Equal(1, await DecisionCount());

        await MakeLatestDecisionStale(match => match.MatcherVersion = "retired");
        await _service.RunAsync(Context(), new(_link, 2, refresh.SnapshotId));
        await MakeLatestDecisionStale(match => match.SourceSnapshotVersion = 0);
        await _service.RunAsync(Context(), new(_link, 3, refresh.SnapshotId));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            link.PolicyVersion = "policy-v2";
            await db.SaveChangesAsync();
        }
        await _service.RunAsync(Context(), new(_link, 4, refresh.SnapshotId));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var local = await db.LibraryTracks.SingleAsync(item => item.Id == _trackOne);
            local.Title = "One changed";
            await db.SaveChangesAsync();
        }
        await _service.RunAsync(Context(), new(_link, 5, refresh.SnapshotId));

        var actor = new TrackMatchActor(_tenant, _user, false);
        var rematch = await _trackMatches.RematchSnapshotAsync(
            actor, externalId, "forced-rematch");

        Assert.True(rematch.Succeeded);
        Assert.Equal(6, rematch.DecisionVersion);
        await AssertActiveOverride(ManualOverrideDecision.Pin);

        var rejected = await _trackMatches.ResolveSnapshotAsync(
            actor, externalId, new ResolveTrackMatchCommand("reject"), "reject");
        Assert.True(rejected.Succeeded);
        var rejectedRematch = await _trackMatches.RematchSnapshotAsync(
            actor, externalId, "forced-rematch-rejected");

        Assert.True(rejectedRematch.Succeeded);
        Assert.Equal(7, rejectedRematch.DecisionVersion);
        await AssertActiveOverride(ManualOverrideDecision.Reject);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(7, await verify.TrackMatches.CountAsync(item => item.ExternalSnapshotId == externalId));
            var latest = await verify.TrackMatches.Where(item => item.ExternalSnapshotId == externalId)
                .OrderByDescending(item => item.DecisionVersion).FirstAsync();
            Assert.Equal(TrackMatchDecisionEngine.AlgorithmVersion, latest.MatcherVersion);
            Assert.Equal(1, latest.SourceSnapshotVersion);
            Assert.NotEqual(0, latest.LibraryIndexRevision);
        }

        async Task AssertActiveOverride(ManualOverrideDecision decision)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var active = await db.ManualTrackOverrides.Where(item =>
                item.ExternalSnapshotId == externalId && item.RevokedAt == null).ToListAsync();
            Assert.Equal(decision, Assert.Single(active).Decision);
        }
    }

    [Fact]
    public async Task Reconcile_writes_order_records_skips_propagates_credential_and_same_generation_is_idempotent()
    {
        _source.Snapshot = Snapshot("revision-reconcile", Entry(0, "entry-0", "source-2", "Two"), Entry(1, "entry-1", "source-1", "One"), Entry(2, "entry-2", "missing", "Missing"));

        var first = await _service.RunAsync(Context(), new(_link, 7));
        var retry = await _service.RunAsync(Context(), new(_link, 7));

        Assert.True(first.BackendWriteAttempted);
        Assert.Equal(PlaylistSyncState.PartiallySucceeded, first.State);
        Assert.Equal(["local-2", "local-1"], _target.LastWrite!.OrderedBackendItemIds);
        Assert.Equal(BackendPlaylistWriteMode.Reconcile, _target.LastWrite.Mode);
        Assert.Equal(_credential.ToString(), _target.Contexts.Last().CredentialReference);
        Assert.Equal(_tenant, _target.Contexts.Last().TenantId);
        Assert.True(retry.ReusedRun);
        Assert.False(retry.BackendWriteAttempted);
        Assert.Equal(first.RunId, retry.RunId);
        Assert.Equal(1, _target.WriteCalls);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.PlaylistSyncRuns.ToListAsync());
        Assert.Equal(3, await db.PlaylistSyncEntryResults.CountAsync());
        Assert.Equal(2, await db.PlaylistTargetMemberships.CountAsync(item => item.Active));
        Assert.Equal("target-created", (await db.PlaylistLinks.SingleAsync()).TargetPlaylistId);
        var run = await db.PlaylistSyncRuns.SingleAsync();
        Assert.Equal(2, run.PlannedTargetTrackCount);
        Assert.Equal(360_000, run.PlannedTargetDurationMilliseconds);
        Assert.Equal(2, run.VerifiedTargetTrackCount);
        Assert.Equal(360_000, run.VerifiedTargetDurationMilliseconds);
        Assert.Equal("verified", run.VerificationCode);
        Assert.Equal(_now, run.VerifiedAt);
    }

    [Fact]
    public async Task Materialization_count_drift_is_persisted_and_actionable()
    {
        _target.ReportedTrackCountAdjustment = 1;
        _source.Snapshot = Snapshot(
            "revision-drift",
            Entry(0, "entry-drift", "source-1", "One"));

        var result = await _service.RunAsync(Context(), new(_link, 8));

        Assert.Equal(PlaylistSyncState.PartiallySucceeded, result.State);
        Assert.Equal("count_mismatch", result.ErrorCode);
        await using var db = await _factory.CreateDbContextAsync();
        var run = await db.PlaylistSyncRuns.SingleAsync();
        Assert.Equal(1, run.PlannedTargetTrackCount);
        Assert.Equal(2, run.VerifiedTargetTrackCount);
        Assert.Equal("count_mismatch", run.VerificationCode);
    }

    [Fact]
    public async Task Materialization_duration_drift_is_persisted_and_actionable()
    {
        _target.ReportedDurationAdjustmentMilliseconds = 5_000;
        _source.Snapshot = Snapshot(
            "revision-duration-drift",
            Entry(0, "entry-duration-drift", "source-1", "One"));

        var result = await _service.RunAsync(Context(), new(_link, 9));

        Assert.Equal(PlaylistSyncState.PartiallySucceeded, result.State);
        Assert.Equal("duration_mismatch", result.ErrorCode);
        await using var db = await _factory.CreateDbContextAsync();
        var run = await db.PlaylistSyncRuns.SingleAsync();
        Assert.Equal(180_000, run.PlannedTargetDurationMilliseconds);
        Assert.Equal(185_000, run.VerifiedTargetDurationMilliseconds);
        Assert.Equal("duration_mismatch", run.VerificationCode);
    }

    [Fact]
    public async Task Materialization_verification_read_failure_is_persisted_without_hiding_the_write()
    {
        _target.ReadStatus = BackendPlaylistTargetStatus.BackendFailure;
        _target.ErrorCode = "verification_unavailable";
        _source.Snapshot = Snapshot(
            "revision-verification-read",
            Entry(0, "entry-verification-read", "source-1", "One"));

        var result = await _service.RunAsync(Context(), new(_link, 10));

        Assert.True(result.BackendWriteAttempted);
        Assert.Equal(PlaylistSyncState.PartiallySucceeded, result.State);
        Assert.Equal("verification_read_verification_unavailable", result.ErrorCode);
        await using var db = await _factory.CreateDbContextAsync();
        var run = await db.PlaylistSyncRuns.SingleAsync();
        Assert.Equal("verification_read_verification_unavailable", run.VerificationCode);
        Assert.Null(run.VerifiedTargetTrackCount);
        Assert.Null(run.VerifiedTargetDurationMilliseconds);
    }

    [Fact]
    public async Task Recreate_and_target_conflicts_are_recorded_with_correct_attempt_accounting()
    {
        await SetLink(materialization: PlaylistMaterializationMode.Recreate);
        _source.Snapshot = Snapshot("revision-recreate", Entry(0, "entry-0", "source-1", "One"));
        var recreate = await _service.RunAsync(Context(), new(_link, 1));
        Assert.Equal(BackendPlaylistWriteMode.Recreate, _target.LastWrite!.Mode);
        Assert.Equal(PlaylistSyncState.PartiallySucceeded, recreate.State);

        _source.Snapshot = Snapshot("revision-conflict", Entry(0, "entry-0b", "source-1", "One"));
        _target.WriteStatus = BackendPlaylistTargetStatus.Conflict;
        _target.ErrorCode = "target_changed";
        var conflict = await _service.RunAsync(Context(), new(_link, 2));
        Assert.True(conflict.BackendWriteAttempted);
        Assert.Equal(PlaylistSyncState.Conflicted, conflict.State);
        Assert.Equal("target_changed", conflict.ErrorCode);

        _target.WriteStatus = BackendPlaylistTargetStatus.Success;
        _target.ReadStatus = BackendPlaylistTargetStatus.BackendFailure;
        _target.ErrorCode = "read_failed";
        var readFailure = await _service.RunAsync(Context(), new(_link, 3));
        Assert.False(readFailure.BackendWriteAttempted);
        Assert.Equal(PlaylistSyncState.Failed, readFailure.State);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(3, await db.PlaylistSyncRuns.CountAsync());
        Assert.Equal([PlaylistSyncState.PartiallySucceeded, PlaylistSyncState.Conflicted, PlaylistSyncState.Failed],
            await db.PlaylistSyncRuns.OrderBy(item => item.Generation).Select(item => item.State).ToListAsync());
    }

    [Fact]
    public async Task Foreign_tenant_cannot_load_link_or_snapshot_and_no_target_call_occurs()
    {
        _source.Snapshot = Snapshot("revision-scope", Entry(0, "entry-0", "source-1", "One"));
        var refresh = await _service.RefreshAsync(Context(), _link);
        var foreignTenant = Guid.CreateVersion7();
        var foreignUser = Guid.CreateVersion7();
        var foreign = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "foreign",
            new AllstarrPrincipal(foreignTenant, foreignUser, "jellyfin", "backend", "foreign", "Foreign", false),
            "foreign-correlation", _now.AddMinutes(2), default, libraryScopeId: "music");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.RunAsync(foreign, new(_link, 1, refresh.SnapshotId)));
        Assert.Equal(0, _target.TotalCalls);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.PlaylistSyncRuns.ToListAsync());
    }

    [Fact]
    public async Task Artwork_is_ephemeral_and_resolution_failure_does_not_block_membership()
    {
        _target.CanWriteArtwork = true;
        _source.Artwork = ProviderOutcome<ProviderPlaylistArtwork>.Success(
            new ProviderPlaylistArtwork([9, 8, 7], "image/webp"));
        _source.Snapshot = Snapshot("revision-art", Entry(0, "entry-art", "source-1", "One"));

        var success = await _service.RunAsync(Context(), new(_link, 41));

        Assert.Equal([9, 8, 7], _target.LastWrite!.Metadata.Artwork);
        Assert.Equal("image/webp", _target.LastWrite.Metadata.ArtworkContentType);
        Assert.Equal(PlaylistSyncState.Succeeded, success.State);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.DoesNotContain("CQgH", string.Join('|', await db.PlaylistSyncRuns.Select(item => item.ConflictCode).ToListAsync()));
        }

        _source.Artwork = ProviderOutcome<ProviderPlaylistArtwork>.Failure(
            new ProviderError(ProviderErrorKind.TransientFailure));
        _source.Snapshot = Snapshot("revision-art-failure", Entry(0, "entry-art-2", "source-1", "One"));
        var degraded = await _service.RunAsync(Context(), new(_link, 42));

        Assert.True(degraded.BackendWriteAttempted);
        Assert.Equal(PlaylistSyncState.PartiallySucceeded, degraded.State);
        Assert.Equal("artwork_transientfailure", degraded.ErrorCode);
        Assert.Null(_target.LastWrite!.Metadata.Artwork);
        Assert.Equal(["local-1"], _target.LastWrite.OrderedBackendItemIds);
    }

    [Fact]
    public async Task Durable_projection_reads_latest_generation_metrics_and_receipt()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var local = await db.LibraryTracks.SingleAsync(item => item.Id == _trackOne);
            local.DurationMilliseconds = 200_000;
            await db.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot(
            "revision-projection",
            Entry(0, "entry-projection-1", "source-1", "One"),
            Entry(1, "entry-projection-2", "source-2", "Two"));
        await _service.RunAsync(Context(), new(_link, 73));

        var projection = await new DurablePlaylistProjectionReader(_factory)
            .ReadByNameAsync(_tenant, _user, "Provider Mix");

        Assert.NotNull(projection);
        Assert.Equal(2, projection.Entries.Count);
        Assert.Equal(2, projection.LocalCount);
        Assert.Equal(0, projection.MissingCount);
        Assert.Equal(380000, projection.DurationMilliseconds);
        Assert.Equal(PlaylistSyncState.PartiallySucceeded, projection.SyncState);
        Assert.Equal(_now, projection.CompletedAt);
        Assert.Equal(2, projection.PlannedTargetTrackCount);
        Assert.Equal(380_000, projection.PlannedTargetDurationMilliseconds);
        Assert.Equal(2, projection.VerifiedTargetTrackCount);
        Assert.Equal(360_000, projection.VerifiedTargetDurationMilliseconds);
        Assert.Equal("duration_mismatch", projection.VerificationCode);
        Assert.Equal(_now, projection.VerifiedAt);
        Assert.All(projection.Entries, item =>
        {
            Assert.NotNull(item.BackendItemId);
            Assert.Equal("jellyfin", item.DurationProvenance);
            Assert.Equal(_now, item.DurationRetrievedAt);
        });
    }

    [Fact]
    public async Task Durable_projection_classifies_each_source_row_once()
    {
        var externalCanonical = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = externalCanonical,
                TenantId = _tenant,
                CreatedByUserId = _user,
                CreatedAt = _now,
                UpdatedAt = _now
            });
            var externalIdentity = ProviderIdentity("source-external");
            externalIdentity.CanonicalRecordingId = externalCanonical;
            var qobuzIdentity = ProviderIdentity("qobuz-external");
            qobuzIdentity.CanonicalRecordingId = externalCanonical;
            qobuzIdentity.ProviderId = "qobuz";
            qobuzIdentity.ExternalIdHash = Hash(qobuzIdentity.ExternalId);
            var deezerIdentity = ProviderIdentity("deezer-external");
            deezerIdentity.CanonicalRecordingId = externalCanonical;
            deezerIdentity.ProviderId = "deezer";
            deezerIdentity.ExternalIdHash = Hash(deezerIdentity.ExternalId);
            deezerIdentity.Verification = ProviderIdentityVerification.Pinned;
            db.ProviderTrackIdentities.AddRange(externalIdentity, qobuzIdentity, deezerIdentity);
            await db.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot(
            "revision-classification",
            Entry(0, "entry-local", "source-1", "One"),
            Entry(1, "entry-external", "source-external", "External"),
            Entry(2, "entry-unmatched", "source-unmatched", "Missing") with
            {
                DurationMilliseconds = null
            });
        await SetLink(mode: PlaylistLinkMode.Virtual);
        await _service.RefreshAsync(Context(), _link);

        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["qobuz", "deezer"]);
        var projection = await new DurablePlaylistProjectionReader(_factory, gateway.Object)
            .ReadByNameAsync(_tenant, _user, "Provider Mix");

        Assert.NotNull(projection);
        Assert.Equal(3, projection.TotalCount);
        Assert.Equal(1, projection.LocalCount);
        Assert.Equal(1, projection.ExternalCount);
        Assert.Equal(1, projection.MissingCount);
        Assert.Equal(projection.TotalCount,
            projection.LocalCount + projection.ExternalCount + projection.MissingCount);
        Assert.Equal(1, projection.UnknownDurationCount);
        Assert.Equal(360_000, projection.DurationMilliseconds);
        Assert.Equal(["local", "external", "unmatched"],
            projection.Entries.Select(item => item.RouteKind));
        Assert.Equal("qobuz", projection.Entries[1].RouteProviderId);
        Assert.Equal(["qobuz", "deezer"],
            projection.Entries[1].ProviderRoutes.Select(item => item.ProviderId));
        var virtualPlaylist = await new PlaylistVirtualizationService(
                _factory, _trackMatches, gateway.Object)
            .ReadAsync(Context(), PlaylistVirtualizationService.CreateProtocolId(_link));
        Assert.Equal("ext-qobuz-song-qobuz-external",
            virtualPlaylist!.Tracks.Single(item => item.SourcePosition == 1).BackendItemId);
    }

    [Fact]
    public async Task Durable_projection_keeps_the_published_generation_until_the_next_publish()
    {
        _source.Snapshot = Snapshot(
            "revision-published",
            Entry(0, "entry-published", "source-1", "One"));
        var first = await _service.RefreshAsync(Context(), _link);

        PlaylistSourceSnapshotRecord building;
        PlaylistSourceEntryRecord buildingEntry;
        TrackMatchRecord firstMatch;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var firstEntry = await db.PlaylistSourceEntries
                .SingleAsync(item => item.PlaylistSourceSnapshotId == first.SnapshotId);
            firstMatch = await db.TrackMatches
                .SingleAsync(item => item.Id == firstEntry.PublishedTrackMatchId);
            building = new PlaylistSourceSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                OwnerUserId = _user,
                PlaylistLinkId = _link,
                ProviderAccountId = _account,
                SnapshotVersion = first.SnapshotVersion + 1,
                ProviderRevision = "revision-building",
                Name = "Provider Mix",
                Description = "Building",
                ArtworkReferenceKey = "provider-artwork:building",
                PayloadSha256 = Hash("building"),
                CorrelationId = "building",
                RetrievedAt = _now.AddMinutes(1)
            };
            buildingEntry = new PlaylistSourceEntryRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                PlaylistSourceSnapshotId = building.Id,
                ExternalMetadataSnapshotId = firstEntry.ExternalMetadataSnapshotId,
                SourcePosition = 0,
                SourceEntryIdHash = Hash("building-entry")
            };
            db.AddRange(building, buildingEntry);
            await db.SaveChangesAsync();
        }
        var rejected = await _trackMatches.RecordDecisionAsync(Context(), new MatchDecisionInput(
            firstMatch.ExternalSnapshotId,
            null,
            firstMatch.CanonicalRecordingId,
            TrackMatchState.Rejected,
            0,
            firstMatch.Threshold,
            firstMatch.DecisionVersion + 1,
            firstMatch.SourceSnapshotVersion,
            firstMatch.LibraryIndexRevision,
            firstMatch.MatcherVersion,
            firstMatch.PolicyVersion,
            "[]",
            "[\"test\"]",
            "[]"));

        var reader = new DurablePlaylistProjectionReader(_factory);
        var active = await reader.ReadByNameAsync(_tenant, _user, "Provider Mix");
        Assert.NotNull(active);
        Assert.Equal(first.SnapshotId, active.SnapshotId);
        Assert.Equal("local", Assert.Single(active.Entries).RouteKind);
        Assert.Equal("provider-artwork:stable:key", active.ArtworkReferenceKey);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var snapshot = await db.PlaylistSourceSnapshots.SingleAsync(item => item.Id == building.Id);
            var entry = await db.PlaylistSourceEntries.SingleAsync(item => item.Id == buildingEntry.Id);
            entry.PublishedTrackMatchId = rejected.Id;
            snapshot.PublishedAt = _now.AddMinutes(2);
            await db.SaveChangesAsync();
        }

        active = await reader.ReadByNameAsync(_tenant, _user, "Provider Mix");
        Assert.NotNull(active);
        Assert.Equal(building.Id, active.SnapshotId);
        var publishedEntry = Assert.Single(active.Entries);
        Assert.Equal("external", publishedEntry.RouteKind);
        Assert.Equal(TrackMatchState.Rejected, publishedEntry.MatchState);
        Assert.Equal("provider-artwork:building", active.ArtworkReferenceKey);
    }

    private PlaylistLinkRecord Link() => new()
    {
        Id = _link,
        TenantId = _tenant,
        OwnerUserId = _user,
        ProviderAccountId = _account,
        LibraryScopeId = "music",
        SourceProviderId = "fixture",
        SourcePlaylistId = "playlist",
        SourcePlaylistIdHash = Hash("playlist"),
        TargetProtocol = "jellyfin",
        TargetBackendInstanceId = "backend",
        TargetCredentialReferenceId = _credential,
        Mode = PlaylistLinkMode.Materialized,
        MaterializationMode = PlaylistMaterializationMode.Reconcile,
        PreserveManualEntries = true,
        SyncName = true,
        SyncDescription = true,
        SyncArtwork = true,
        RuleVersion = "rules-v1",
        PolicyVersion = "policy-v1",
        CreatedAt = _now,
        UpdatedAt = _now
    };

    private LibraryTrackRecord Local(Guid id, string backendItem, string sourceId, string title) => new()
    {
        Id = id,
        TenantId = _tenant,
        OwnerUserId = _user,
        BackendIdentityId = _identity,
        LibraryScopeId = "music",
        Protocol = "jellyfin",
        BackendInstanceId = "backend",
        BackendItemId = backendItem,
        FilePath = $"/music/{backendItem}.flac",
        Title = title,
        Artist = "Artist",
        Album = "Album",
        DurationMilliseconds = 180000,
        ProviderIdsJson = $"{{\"fixture\":\"{Hash(sourceId)}\"}}",
        IndexedAt = _now,
        SourceModifiedAt = _now,
        UpdatedAt = _now
    };

    private ProviderTrackIdentityRecord ProviderIdentity(string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenant,
        CanonicalRecordingId = _canonical,
        ProviderId = "fixture",
        ResourceKind = ProviderResourceKind.Track,
        CatalogNamespace = "default",
        Scope = ProviderIdentityScope.Catalog,
        ExternalId = externalId,
        ExternalIdHash = Hash(externalId),
        Verification = ProviderIdentityVerification.Verified,
        VerificationMethod = "fixture",
        DecisionVersion = 1,
        VerifiedAt = _now,
        CreatedAt = _now,
        UpdatedAt = _now
    };

    private ManualTrackOverrideRecord Override(Guid external, ManualOverrideDecision decision, Guid? track) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenant,
        OwnerUserId = _user,
        ExternalSnapshotId = external,
        LibraryTrackId = track,
        LibraryScopeId = "music",
        Decision = decision,
        Reason = "reviewed",
        DecisionVersion = 1,
        CreatedAt = _now
    };

    private async Task SetLink(PlaylistLinkMode? mode = null, PlaylistMaterializationMode? materialization = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync();
        if (mode.HasValue) link.Mode = mode.Value;
        if (materialization.HasValue) link.MaterializationMode = materialization.Value;
        await db.SaveChangesAsync();
    }

    private async Task<int> DecisionCount()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.TrackMatches.CountAsync();
    }

    private async Task MakeLatestDecisionStale(Action<TrackMatchRecord> change)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var match = await db.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        change(match);
        await db.SaveChangesAsync();
    }

    private ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "backend", "principal",
        new AllstarrPrincipal(_tenant, _user, "jellyfin", "backend", "principal", "Owner", false),
        "correlation", _now.AddMinutes(5), default, libraryScopeId: "music");
    private CollectedPlaylistSourceSnapshot Snapshot(string revision, params CollectedPlaylistSourceEntry[] entries) =>
        new("fixture", _account, Hash("playlist"), revision, $"etag-{revision}", "Provider Mix", "Description",
            "provider-artwork:stable:key", entries);
    private static CollectedPlaylistSourceEntry Entry(int position, string entry, string source, string title) =>
        new(position, Hash(entry), Hash(source), null, title, ["Artist"], "Album", 180_000, null, false);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task DisposeAsync() => await _database.DisposeAsync();
    private sealed class Clock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow => now; }
    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class FakeSource : IProviderPlaylistSourceGateway
    {
        public CollectedPlaylistSourceSnapshot Snapshot { get; set; } = null!;
        public ProviderOutcome<ProviderPlaylistArtwork> Artwork { get; set; } =
            ProviderOutcome<ProviderPlaylistArtwork>.Failure(new ProviderError(ProviderErrorKind.CapabilityUnavailable));
        public Task<CollectedPlaylistSourceSnapshot> CollectAsync(ProtocolExecutionContext context, PlaylistLinkRecord link, CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
            ProtocolExecutionContext context, PlaylistLinkRecord link, ProviderPlaylistArtworkRequest request,
            CancellationToken cancellationToken) => Task.FromResult(Artwork);
    }
    private sealed class FakeTargetResolver(FakeTarget target) : IBackendPlaylistTargetResolver
    {
        public IBackendPlaylistTarget Resolve(string targetProtocol) => target;
    }
    private sealed class FakeTarget : IBackendPlaylistTarget
    {
        public BackendPlaylistFamily Family => BackendPlaylistFamily.Jellyfin;
        public bool CanWriteArtwork { get; set; }
        public BackendPlaylistTargetCapabilities Capabilities => new(true, true, true, true, true, true, CanWriteArtwork, true, true);
        public BackendPlaylistTargetStatus ReadStatus { get; set; } = BackendPlaylistTargetStatus.Success;
        public BackendPlaylistTargetStatus WriteStatus { get; set; } = BackendPlaylistTargetStatus.Success;
        public string? ErrorCode { get; set; }
        public int FindCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int TotalCalls => FindCalls + ReadCalls + WriteCalls;
        public int ReportedTrackCountAdjustment { get; set; }
        public long ReportedDurationAdjustmentMilliseconds { get; set; }
        public BackendPlaylistWriteRequest? LastWrite { get; private set; }
        private BackendPlaylistSnapshot? LastSnapshot { get; set; }
        public List<BackendPlaylistTargetContext> Contexts { get; } = [];
        public Task<BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>> ListAsync(BackendPlaylistTargetContext context, string? query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>(BackendPlaylistTargetStatus.Success, []));
        public Task<BackendPlaylistTargetResult<BackendPlaylistArtwork>> ReadArtworkAsync(BackendPlaylistTargetContext context, string backendPlaylistId, string? artworkReference, CancellationToken cancellationToken) =>
            Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistArtwork>(BackendPlaylistTargetStatus.NotFound));
        public Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot?>> FindByNameAsync(BackendPlaylistTargetContext context, string name, CancellationToken cancellationToken)
        {
            FindCalls++; Contexts.Add(context);
            return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistSnapshot?>(BackendPlaylistTargetStatus.NotFound, ErrorCode: ErrorCode));
        }
        public Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot>> ReadAsync(BackendPlaylistTargetContext context, string backendPlaylistId, CancellationToken cancellationToken)
        {
            ReadCalls++; Contexts.Add(context);
            var snapshot = ReadStatus == BackendPlaylistTargetStatus.Success
                ? LastSnapshot is { } last && last.BackendPlaylistId == backendPlaylistId
                    ? last
                    : Backend(backendPlaylistId)
                : null;
            return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistSnapshot>(ReadStatus, snapshot, ErrorCode: ErrorCode));
        }
        public Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> WriteAsync(BackendPlaylistTargetContext context, BackendPlaylistWriteRequest request, CancellationToken cancellationToken)
        {
            WriteCalls++; Contexts.Add(context); LastWrite = request;
            LastSnapshot = WriteStatus == BackendPlaylistTargetStatus.Success
                ? Backend(request.BackendPlaylistId ?? "target-created", request.OrderedBackendItemIds)
                : null;
            var receipt = LastSnapshot == null
                ? null
                : new BackendPlaylistWriteReceipt(LastSnapshot, true, []);
            return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>(WriteStatus, receipt, ErrorCode: ErrorCode));
        }
        private BackendPlaylistSnapshot Backend(string id, IEnumerable<string>? members = null)
        {
            var values = (members ?? [])
                .Select(item => new BackendPlaylistMember(item, item, 180_000))
                .ToArray();
            return new(id, "Provider Mix", values,
                BackendPlaylistSnapshot.ComputeFingerprint(id, "Provider Mix", values),
                "native-1",
                ReportedTrackCount: values.Length + ReportedTrackCountAdjustment,
                DurationMilliseconds: values.LongLength * 180_000 +
                                      ReportedDurationAdjustmentMilliseconds);
        }
    }
}
