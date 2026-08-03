using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Spotify;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class PlaylistOrchestrationIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private DbFactory _factory = null!;
    private FakeSource _source = null!;
    private FakeTarget _target = null!;
    private PlaylistOrchestrationService _service = null!;
    private TrackMatchCommandService _trackMatches = null!;
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _account = Guid.CreateVersion7();
    private readonly Guid _link = Guid.CreateVersion7();
    private readonly Guid _credential = Guid.CreateVersion7();
    private Guid _identity;
    private Guid _canonical;
    private Guid _trackOne;
    private Guid _trackTwo;
    private readonly List<string> _logs = [];
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
        _service = new(_factory, _source, new FakeTargetResolver(_target), new PlaylistMaterializationPlanner(),
            new TrackMatchDecisionEngine(), _trackMatches, clock,
            new CollectingLogger<PlaylistOrchestrationService>(_logs));
        await using var db = await _factory.CreateDbContextAsync();
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
    public async Task PostgreSql_playlist_baseline_is_chunk_bounded_at_100_1000_and_10000_tracks()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        var commands = new CommandCounter();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(_database.ConnectionString)
            .AddInterceptors(commands)
            .Options;
        var factory = new DbFactory(options);
        var service = new PlaylistOrchestrationService(
            factory,
            _source,
            new FakeTargetResolver(_target),
            new PlaylistMaterializationPlanner(),
            new TrackMatchDecisionEngine(),
            new TrackMatchCommandService(
                factory,
                new TrackMatchDecisionEngine(),
                new ProviderAccountResolver(factory, new ProviderPolicyOptions()),
                new Clock(_now)),
            new Clock(_now));
        var baselines = new List<(int Count, int Commands, long Allocated, long ElapsedTicks)>();

        foreach (var count in new[] { 100, 1_000, 10_000 })
        {
            _source.Snapshot = Snapshot(
                $"scale-{count}",
                Enumerable.Range(0, count)
                    .Select(index => Entry(
                        index,
                        $"scale-{count}-entry-{index}",
                        $"scale-{count}-source-{index}",
                        "One") with
                    {
                        CanonicalRecordingId = _canonical
                    })
                    .ToArray());
            commands.Reset();
            var allocatedBefore = GC.GetTotalAllocatedBytes();
            var timer = Stopwatch.StartNew();
            var refresh = await service.RefreshAsync(Context(), _link);
            timer.Stop();
            var elapsedTicks = timer.ElapsedTicks;
            var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;

            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(
                    count,
                    await db.PlaylistSourceEntries.CountAsync(
                        item => item.PlaylistSourceSnapshotId == refresh.SnapshotId));
            }
            var measuredCommands = commands.Count - 1;
            Assert.True(
                measuredCommands <= 15 +
                    (int)Math.Ceiling(count / 20d) * 2 +
                    (int)Math.Ceiling(count / 500d),
                $"{measuredCommands} SQL commands exceeded the chunk budget for {count} tracks.");

            commands.Reset();
            Assert.Equal(
                refresh.SnapshotId,
                (await service.RefreshAsync(Context(), _link)).SnapshotId);
            // Exact-canonical resolution adds a fixed identity lookup set; it remains constant across scale.
            Assert.InRange(commands.Count, 1, 24);

            commands.Reset();
            allocatedBefore = GC.GetTotalAllocatedBytes();
            timer.Restart();
            var run = await service.RunAsync(
                Context(), new PlaylistOrchestrationRequest(_link, 1, refresh.SnapshotId));
            timer.Stop();
            elapsedTicks += timer.ElapsedTicks;
            allocated += GC.GetTotalAllocatedBytes() - allocatedBefore;
            measuredCommands += commands.Count;
            Assert.Equal(count, run.Plan.Entries.Count);
            Assert.Equal(PlaylistPreviewEntryStatus.Included, run.Plan.Entries[0].Status);
            Assert.All(
                run.Plan.Entries.Skip(1),
                item => Assert.Equal(PlaylistPreviewEntryStatus.Duplicate, item.Status));
            Assert.Equal(["local-1"], run.Plan.OrderedBackendItemIds);
            Assert.True(
                commands.Count <= 15 + (int)Math.Ceiling(count / 20d),
                $"{commands.Count} matching SQL commands exceeded the chunk budget for {count} tracks.");

            baselines.Add((count, measuredCommands, allocated, elapsedTicks));
            output.WriteLine(
                $"postgres-playlist tracks={count} commands={measuredCommands} returned_rows={run.Plan.Entries.Count} accepted_routes={run.Plan.OrderedBackendItemIds.Count} allocated_bytes={allocated} elapsed_ticks={elapsedTicks}");
        }

        Assert.All(baselines.Zip(baselines.Skip(1)), pair =>
            Assert.True(pair.Second.Commands < pair.First.Commands * 30,
                $"SQL command growth from {pair.First.Count} to {pair.Second.Count} tracks was quadratic."));
    }

    [Fact]
    public async Task Refresh_persists_duplicate_source_positions_with_one_external_snapshot()
    {
        _source.Snapshot = Snapshot("revision-duplicates", Entry(0, "entry-0", "source-1", "One"), Entry(1, "entry-1", "source-1", "One"));
        var refresh = await _service.RefreshAsync(Context(), _link);

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
    public async Task Refresh_does_not_reuse_metadata_from_another_library_scope()
    {
        _source.Snapshot = Snapshot("revision-scope", Entry(0, "entry-0", "source-1", "One"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            link.LibraryScopeId = "old-scope";
            await db.SaveChangesAsync();
        }
        var oldScope = await _service.RefreshAsync(Context("old-scope"), _link);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            link.LibraryScopeId = "music";
            await db.SaveChangesAsync();
        }
        var currentScope = await _service.RefreshAsync(Context(), _link);

        await using var verify = await _factory.CreateDbContextAsync();
        var snapshots = await verify.ExternalMetadataSnapshots.OrderBy(item => item.SnapshotVersion).ToListAsync();
        Assert.Equal(["old-scope", "music"], snapshots.Select(item => item.LibraryScopeId));
        Assert.NotEqual(oldScope.SnapshotId, currentScope.SnapshotId);
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
    public async Task Refresh_reuses_identical_content_when_only_provider_revision_changes()
    {
        var entry = Entry(0, "stable-entry", "source-1", "One");
        _source.Snapshot = Snapshot("revision-a", entry);
        var first = await _service.RefreshAsync(Context(), _link);

        _source.Snapshot = Snapshot("revision-b", entry);
        var second = await _service.RefreshAsync(Context(), _link);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.SnapshotVersion, second.SnapshotVersion);
        Assert.Equal("revision-a", second.SourceRevision);
    }

    [Fact]
    public async Task Failed_collection_logs_retained_last_good_without_private_metadata()
    {
        _source.Snapshot = Snapshot(
            "retained-revision",
            Entry(0, "retained-entry", "source-1", "Private title"));
        var retained = await _service.RefreshAsync(Context(), _link);
        _source.FailureCode = "capability-unavailable";

        await Assert.ThrowsAsync<PlaylistSourceUnavailableException>(
            () => _service.RefreshAsync(Context(), _link));

        var message = Assert.Single(_logs, item =>
            item.Contains("retained-last-good", StringComparison.Ordinal));
        Assert.Contains($"SnapshotVersion: {retained.SnapshotVersion}", message);
        Assert.Contains("ReasonCode: capability-unavailable", message);
        Assert.DoesNotContain("Private title", message);
        Assert.DoesNotContain(_account.ToString(), message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Same_revision_replacement_and_reorder_create_new_snapshot_and_materialization_key()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot(
            "stable-revision",
            Entry(0, "entry-a", "source-1", "One"),
            Entry(1, "entry-b", "source-2", "Two"),
            Entry(2, "entry-c", "source-3", "Three"));
        var first = await _service.RefreshAsync(Context(), _link);

        _source.Snapshot = Snapshot(
            "stable-revision",
            Entry(0, "entry-c-moved", "source-3", "Three"),
            Entry(1, "entry-new", "source-new", "New"),
            Entry(2, "entry-a-moved", "source-1", "One"));
        var second = await _service.RefreshAsync(Context(), _link);
        var repeated = await _service.RefreshAsync(Context(), _link);
        var firstPlan = await _service.RunAsync(Context(), new(_link, 1, first.SnapshotId));
        var secondPlan = await _service.RunAsync(Context(), new(_link, 2, second.SnapshotId));

        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.SnapshotVersion + 1, second.SnapshotVersion);
        Assert.Equal(second.SnapshotId, repeated.SnapshotId);
        Assert.True(firstPlan.Plan.SourceSnapshotIsStale);
        Assert.False(secondPlan.Plan.SourceSnapshotIsStale);
        Assert.NotEqual(firstPlan.Plan.IdempotencyKey, secondPlan.Plan.IdempotencyKey);

        var projection = await new DurablePlaylistProjectionReader(_factory)
            .ReadByLinkIdAsync(_tenant, _user, _link);
        var reconciliation = Assert.IsType<DurablePlaylistReconciliation>(
            projection!.Reconciliation);
        Assert.Equal(3, reconciliation.ProviderAdvertisedRows);
        Assert.Equal(3, reconciliation.RawRows);
        Assert.Equal(3, reconciliation.MappedRows);
        Assert.Equal(3, reconciliation.PersistedSourceRows);
        Assert.Equal(3, reconciliation.PublishedRows);
        Assert.Equal([1], reconciliation.AddedPositions);
        Assert.Equal([1], reconciliation.RemovedPositions);
        Assert.Equal([0, 2], reconciliation.MovedPositions);
        Assert.Empty(reconciliation.ChangedPositions);
        Assert.Equal(
            reconciliation.PublishedRows,
            reconciliation.Accepted +
            reconciliation.Tentative +
            reconciliation.Rejected +
            reconciliation.Unresolved);
        Assert.Equal(reconciliation.PublishedRows, reconciliation.ProtocolVisibleRows);
    }

    [Fact]
    public async Task Reconciliation_ignores_artwork_refreshes_but_reports_metadata_changes()
    {
        _source.Snapshot = Snapshot(
            "stable-revision",
            Entry(0, "entry-a", "source-1", "One") with { ArtworkUrl = "https://art/old" });
        await _service.RefreshAsync(Context(), _link);

        _source.Snapshot = Snapshot(
            "stable-revision",
            Entry(0, "entry-a", "source-1", "One") with { ArtworkUrl = "https://art/new" });
        await _service.RefreshAsync(Context(), _link);
        var artworkOnly = await new DurablePlaylistProjectionReader(_factory)
            .ReadByLinkIdAsync(_tenant, _user, _link);
        Assert.Empty(artworkOnly!.Reconciliation!.ChangedPositions);

        _source.Snapshot = Snapshot(
            "stable-revision",
            Entry(0, "entry-a", "source-1", "Renamed") with { ArtworkUrl = "https://art/new" });
        await _service.RefreshAsync(Context(), _link);
        var renamed = await new DurablePlaylistProjectionReader(_factory)
            .ReadByLinkIdAsync(_tenant, _user, _link);
        Assert.Equal([0], renamed!.Reconciliation!.ChangedPositions);
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
            suggestedCandidate.Title = "Suggestion Song";
            suggestedCandidate.Album = null;
            suggestedCandidate.DurationMilliseconds = 999_000;
            var ambiguousOne = Local(ambiguousOneId, "ambiguous-1", "unused-2", "Ambiguous");
            var ambiguousTwo = Local(ambiguousTwoId, "ambiguous-2", "unused-3", "Ambiguous");
            ambiguousOne.ProviderIdsJson = ambiguousTwo.ProviderIdsJson = "{}";
            ambiguousOne.MusicBrainzRecordingId = Guid.CreateVersion7().ToString();
            ambiguousTwo.MusicBrainzRecordingId = Guid.CreateVersion7().ToString();
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
        Assert.Equal(suggestedId, suggested.LibraryTrackId);
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
    public async Task Provider_selection_bootstraps_an_unidentified_source_snapshot()
    {
        _source.Snapshot = Snapshot(
            "revision-provider-review",
            Entry(0, "entry-provider-review", "unindexed-source", "Manual target"));
        var refresh = await _service.RefreshAsync(Context(), _link);
        Guid externalSnapshotId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            externalSnapshotId = await db.PlaylistSourceEntries
                .Where(item => item.PlaylistSourceSnapshotId == refresh.SnapshotId)
                .Select(item => item.ExternalMetadataSnapshotId)
                .SingleAsync();
            Assert.Null((await db.ExternalMetadataSnapshots.SingleAsync(item =>
                item.Id == externalSnapshotId)).ProviderTrackIdentityId);
        }

        var result = await _trackMatches.ResolveSnapshotAsync(
            new TrackMatchActor(_tenant, _user, false),
            externalSnapshotId,
            new ResolveTrackMatchCommand(
                "provider",
                ExternalProvider: "deezer",
                ExternalId: "manual-deezer-track"),
            "manual-provider-review");

        Assert.True(result.Succeeded);
        Assert.True((await _trackMatches.ResolveSnapshotAsync(
            new TrackMatchActor(_tenant, _user, false),
            externalSnapshotId,
            new ResolveTrackMatchCommand(
                "provider",
                ExternalProvider: "deezer",
                ExternalId: "manual-deezer-track"),
            "manual-provider-review-repeat")).Succeeded);
        await using var verify = await _factory.CreateDbContextAsync();
        var snapshot = await verify.ExternalMetadataSnapshots.SingleAsync(item =>
            item.Id == externalSnapshotId);
        var source = await verify.ProviderTrackIdentities.SingleAsync(item =>
            item.ProviderId == snapshot.ProviderId &&
            item.ExternalIdHash == snapshot.ExternalIdHash);
        var selected = await verify.ProviderTrackIdentities.SingleAsync(item =>
            item.ProviderId == "deezer" && item.ExternalId == "manual-deezer-track");
        Assert.Null(snapshot.ProviderTrackIdentityId);
        Assert.Equal(source.CanonicalRecordingId, selected.CanonicalRecordingId);
        Assert.Equal(ProviderIdentityVerification.Verified, source.Verification);
        Assert.Equal(ProviderIdentityVerification.Pinned, selected.Verification);
        var projection = await new DurablePlaylistProjectionReader(_factory)
            .ReadByLinkIdAsync(_tenant, _user, _link);
        Assert.Equal("external", Assert.Single(projection!.Entries).RouteKind);
        Assert.Equal("deezer", Assert.Single(projection.Entries).RouteProviderId);
    }

    [Fact]
    public async Task Provider_selection_rejects_metadata_only_routes()
    {
        _source.Snapshot = Snapshot(
            "revision-metadata-only-review",
            Entry(0, "entry-metadata-only-review", "unindexed-source", "Manual target"));
        var refresh = await _service.RefreshAsync(Context(), _link);
        Guid externalSnapshotId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            externalSnapshotId = await db.PlaylistSourceEntries
                .Where(item => item.PlaylistSourceSnapshotId == refresh.SnapshotId)
                .Select(item => item.ExternalMetadataSnapshotId)
                .SingleAsync();
        }

        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download"]);
        var matcher = new TrackMatchDecisionEngine();
        var matches = new TrackMatchCommandService(
            _factory,
            matcher,
            new ProviderAccountResolver(_factory, new ProviderPolicyOptions()),
            new Clock(_now),
            new PlaylistPlayableSearchService(
                gateway.Object,
                matcher,
                null!,
                new IdentityOptions(),
                Options.Create(new JellyfinSettings()),
                NullLogger<PlaylistPlayableSearchService>.Instance));

        var result = await matches.ResolveSnapshotAsync(
            new TrackMatchActor(_tenant, _user, false),
            externalSnapshotId,
            new ResolveTrackMatchCommand(
                "provider",
                ExternalProvider: "musicbrainz",
                ExternalId: "release-id"),
            "manual-metadata-only-review");

        Assert.False(result.Succeeded);
        Assert.Equal(TrackMatchCommandFailure.Invalid, result.Failure);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.DoesNotContain(
            await verify.ProviderTrackIdentities.ToListAsync(),
            item => item.ProviderId == "musicbrainz");
    }

    [Fact]
    public async Task Automatic_suggestion_persists_ranked_provider_fallbacks_as_one_playable_track()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot(
            "revision-provider-suggestion",
            Entry(0, "entry-provider-suggestion", "unindexed-source", "Feels"));
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Feels", 60))
            .ReturnsAsync(
            [
                new Song
                {
                    ExternalProvider = "apple-download",
                    ExternalId = "apple-feels",
                    Title = "Feels (Live)",
                    Artist = "Artist",
                    Album = "Album",
                    Duration = 180
                },
                new Song
                {
                    ExternalProvider = "deezer",
                    ExternalId = "deezer-feels",
                    Title = "Feels (Live)",
                    Artist = "Artist",
                    Album = "Album",
                    Duration = 180
                }
            ]);
        var matcher = new TrackMatchDecisionEngine();
        var trackMatches = new TrackMatchCommandService(
            _factory,
            matcher,
            new ProviderAccountResolver(_factory, new ProviderPolicyOptions()),
            new Clock(_now),
            new PlaylistPlayableSearchService(
                gateway.Object,
                matcher,
                null!,
                new IdentityOptions(),
                Options.Create(new JellyfinSettings()),
                NullLogger<PlaylistPlayableSearchService>.Instance));
        var service = new PlaylistOrchestrationService(
            _factory,
            _source,
            new FakeTargetResolver(_target),
            new PlaylistMaterializationPlanner(),
            matcher,
            trackMatches,
            new Clock(_now));

        await service.RefreshAsync(Context(), _link);

        await using var db = await _factory.CreateDbContextAsync();
        var decision = await db.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(TrackMatchState.Suggested, decision.State);
        Assert.Null(decision.LibraryTrackId);
        Assert.NotNull(decision.CanonicalRecordingId);
        Assert.Null((await db.ExternalMetadataSnapshots.SingleAsync()).ProviderTrackIdentityId);
        var routes = await db.ProviderTrackIdentities
            .Where(item =>
                item.CanonicalRecordingId == decision.CanonicalRecordingId &&
                (item.ProviderId == "apple-download" || item.ProviderId == "deezer"))
            .OrderBy(item => item.ProviderId)
            .ToListAsync();
        Assert.Equal(2, routes.Count);
        Assert.All(routes, item => Assert.Equal("automatic-suggestion", item.VerificationMethod));
        var projection = await new DurablePlaylistProjectionReader(_factory, gateway.Object)
            .ReadByLinkIdAsync(_tenant, _user, _link);
        Assert.NotNull(projection);
        Assert.Equal(1, projection.TotalCount);
        Assert.Equal(1, projection.PlayableCount);
        Assert.Equal(1, projection.ExternalCount);
        Assert.Equal(1, projection.ReviewCount);
        Assert.Equal(["apple-download", "deezer"],
            Assert.Single(projection.Entries).ProviderRoutes.Select(item => item.ProviderId));
    }

    [Fact]
    public async Task Concurrent_external_rematches_coalesce_identity_and_decision_writes()
    {
        var sourceHash = Hash("concurrent-external-source");
        var snapshotIds = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var payload = """
            {"Title":"Concurrent external","Artist":"Artist","Album":"Album","DurationMilliseconds":180000}
            """;
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            setup.ExternalMetadataSnapshots.AddRange(snapshotIds.Select((id, index) =>
                new ExternalMetadataSnapshotRecord
                {
                    Id = id,
                    TenantId = _tenant,
                    OwnerUserId = _user,
                    ProviderAccountId = _account,
                    LibraryScopeId = "music",
                    BackendInstanceId = "backend",
                    BackendPrincipalId = "principal",
                    Protocol = "jellyfin",
                    ProviderId = "fixture",
                    ResourceKind = "track",
                    ExternalIdHash = sourceHash,
                    SnapshotVersion = index + 1,
                    ProviderRevision = $"concurrent-{index + 1}",
                    PayloadJson = payload,
                    PayloadSha256 = Hash(payload),
                    CorrelationId = "concurrent-external",
                    RetrievedAt = _now
                }));
            await setup.SaveChangesAsync();
        }

        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download"]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Concurrent external", 60))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref entered) == snapshotIds.Length)
                    release.SetResult();
                await release.Task;
                return
                [
                    new Song
                    {
                        ExternalProvider = "apple-download",
                        ExternalId = "apple-concurrent",
                        Title = "Concurrent external",
                        Artist = "Artist",
                        Album = "Album",
                        Duration = 180
                    }
                ];
            });
        var matcher = new TrackMatchDecisionEngine();
        var trackMatches = new TrackMatchCommandService(
            _factory,
            matcher,
            new ProviderAccountResolver(_factory, new ProviderPolicyOptions()),
            new Clock(_now),
            new PlaylistPlayableSearchService(
                gateway.Object,
                matcher,
                null!,
                new IdentityOptions(),
                Options.Create(new JellyfinSettings()),
                NullLogger<PlaylistPlayableSearchService>.Instance));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            trackMatches.RematchSnapshotAsync(
                Context(),
                snapshotIds[index % snapshotIds.Length],
                $"concurrent-external-{index}",
                "concurrent-policy")));

        Assert.All(results, result =>
        {
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.DecisionVersion);
        });
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.TrackMatches.CountAsync());
        Assert.Equal(1, await verify.ProviderTrackIdentities.CountAsync(item =>
            item.Scope == ProviderIdentityScope.Account &&
            item.ExternalIdHash == sourceHash));
        Assert.Equal(1, await verify.ProviderTrackIdentities.CountAsync(item =>
            item.Scope == ProviderIdentityScope.Catalog &&
            item.ProviderId == "apple-download" &&
            item.ExternalIdHash == Hash("apple-concurrent")));
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

        var reader = new DurablePlaylistProjectionReader(_factory);
        var projection = await reader.ReadByNameAsync(_tenant, _user, "Provider Mix");
        var projectionByLink = await reader.ReadByLinkIdAsync(_tenant, null, _link);

        Assert.NotNull(projection);
        Assert.NotNull(projectionByLink);
        Assert.Equal(projection.SnapshotId, projectionByLink.SnapshotId);
        Assert.Equal(2, projectionByLink.LocalCount);
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

        _source.Snapshot = Snapshot(
            "revision-after-sync",
            Entry(0, "entry-after-sync", "source-1", "One"));
        await _service.RefreshAsync(Context(), _link);
        var refreshed = await reader.ReadByLinkIdAsync(_tenant, _user, _link);
        Assert.Equal(_now, refreshed!.CompletedAt);
        Assert.Equal(_now, refreshed.LastMatchedAt);
    }

    [Fact]
    public async Task Duplicate_provider_track_rows_keep_their_own_raw_metadata_provenance()
    {
        _source.Snapshot = Snapshot(
            "duplicate-provenance",
            Entry(0, "duplicate-entry-0", "same-provider-track", "First metadata"),
            Entry(1, "duplicate-entry-1", "same-provider-track", "Second metadata"));

        var result = await _service.RefreshAsync(Context(), _link);

        await using var db = await _factory.CreateDbContextAsync();
        var entries = await db.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.PlaylistSourceSnapshotId == result.SnapshotId)
            .OrderBy(item => item.SourcePosition)
            .ToListAsync();
        var externalIds = entries.Select(item => item.ExternalMetadataSnapshotId).ToArray();
        var externals = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => externalIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id);

        Assert.Equal(2, entries.Count);
        Assert.NotEqual(entries[0].ExternalMetadataSnapshotId, entries[1].ExternalMetadataSnapshotId);
        Assert.Contains("First metadata", externals[entries[0].ExternalMetadataSnapshotId].PayloadJson);
        Assert.Contains("Second metadata", externals[entries[1].ExternalMetadataSnapshotId].PayloadJson);
        Assert.All(externals.Values, item => Assert.Equal(Hash("same-provider-track"), item.ExternalIdHash));
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
            .Returns(["deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["qobuz"]);
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
        Assert.Equal(TrackMatchState.Accepted, projection.Entries[1].MatchState);
        Assert.Equal("deezer", projection.Entries[1].RouteProviderId);
        Assert.Equal(["deezer", "qobuz"],
            projection.Entries[1].ProviderRoutes.Select(item => item.ProviderId));
        var virtualization = new PlaylistVirtualizationService(
            _factory, new DurablePlaylistProjectionReader(_factory, gateway.Object));
        var virtualPlaylist = await virtualization.ReadAsync(
            Context(), PlaylistVirtualizationService.CreateProtocolId(_link));
        var sourceAlias = await virtualization.ReadBySourceAsync(
            Context(), "fixture", "playlist");
        Assert.Equal(projection.TotalCount, virtualPlaylist!.Tracks.Count);
        Assert.Equal(virtualPlaylist.ProtocolId, sourceAlias!.ProtocolId);
        Assert.Equal(virtualPlaylist.Tracks.Count, sourceAlias.Tracks.Count);
        var localTrack = virtualPlaylist.Tracks.Single(item => item.SourcePosition == 0);
        var externalTrack = virtualPlaylist.Tracks.Single(item => item.SourcePosition == 1);
        var unresolvedTrack = virtualPlaylist.Tracks.Single(item => item.SourcePosition == 2);
        Assert.Equal(TrackRouteKind.Local, localTrack.RouteKind);
        Assert.Equal("fixture", localTrack.SourceProviderId);
        Assert.Null(localTrack.SourceExternalId);
        Assert.Equal(TrackRouteKind.External, externalTrack.RouteKind);
        Assert.Equal("ext-deezer-song-deezer-external", externalTrack.BackendItemId);
        Assert.Equal("deezer", externalTrack.SourceProviderId);
        Assert.Equal("deezer-external", externalTrack.SourceExternalId);
        Assert.Equal(TrackRouteKind.Unresolved, unresolvedTrack.RouteKind);
        Assert.StartsWith(PlaylistVirtualizationService.UnresolvedItemPrefix,
            unresolvedTrack.BackendItemId, StringComparison.Ordinal);
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
        Assert.True(active.HasNewerSourceGeneration);
        Assert.Equal(building.SnapshotVersion, active.LatestSourceSnapshotVersion);
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
        Assert.False(active.HasNewerSourceGeneration);
        var publishedEntry = Assert.Single(active.Entries);
        Assert.Equal("external", publishedEntry.RouteKind);
        Assert.Equal(TrackMatchState.Rejected, publishedEntry.MatchState);
        Assert.Equal("provider-artwork:building", active.ArtworkReferenceKey);
    }

    [Fact]
    public async Task Concurrent_rematch_through_durable_job_survives_connection_restart()
    {
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            var local = await setup.LibraryTracks.SingleAsync(item => item.Id == _trackOne);
            local.CanonicalRecordingId = _canonical;
            await setup.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot(
            "revision-job-restart",
            Entry(0, "entry-job-restart", "source-1", "One"));
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(
            _factory,
            options,
            new JobPayloadPolicy(options),
            new Clock(_now));
        var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            queue.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
                "playlist.materialize",
                "playlist-restart-fixture",
                new(_link, 81),
                _tenant,
                _user))));

        Assert.Single(requests, item => item.Created);
        Assert.Single(requests.Select(item => item.JobId).Distinct());
        var claim = Assert.IsType<DurableJobClaim>(
            await queue.ClaimNextAsync("playlist-restart-worker"));
        var handler = new PlaylistMaterializationJobHandler(
            _factory,
            _service,
            new Clock(_now));
        var completion = await handler.ExecuteAsync(
            new DurableJobExecutionContext(claim, EmptyServices.Instance)
            {
                ReportProgressAsync = (update, token) =>
                    queue.ReportProgressAsync(claim, update, token)
            },
            default);
        await queue.CompleteAsync(claim, completion);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(1, _target.WriteCalls);
        Guid snapshotId;
        await using (var beforeRestart = await _factory.CreateDbContextAsync())
        {
            snapshotId = await beforeRestart.ExternalMetadataSnapshots
                .Select(item => item.Id)
                .SingleAsync();
        }
        var actor = new TrackMatchActor(_tenant, _user, false);
        var rematches = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            _trackMatches.RematchSnapshotAsync(
                actor,
                snapshotId,
                $"job-rematch-{index}")));
        Assert.All(rematches, result =>
        {
            Assert.True(result.Succeeded);
            Assert.Equal(2, result.DecisionVersion);
        });
        NpgsqlConnection.ClearAllPools();

        var restartedFactory = new DbFactory(_database.Options);
        var projection = await new DurablePlaylistProjectionReader(restartedFactory)
            .ReadByNameAsync(_tenant, _user, "Provider Mix");
        Assert.NotNull(projection);
        Assert.Equal(1, projection.TotalCount);
        Assert.Equal(1, projection.LocalCount);
        Assert.Equal(0, projection.ExternalCount);
        Assert.Equal(0, projection.MissingCount);
        Assert.Equal("local", Assert.Single(projection.Entries).RouteKind);

        await using var db = await restartedFactory.CreateDbContextAsync();
        var decisions = await db.TrackMatches
            .OrderBy(item => item.DecisionVersion)
            .ToListAsync();
        Assert.Equal([1, 2], decisions.Select(item => item.DecisionVersion));
        Assert.All(decisions, item => Assert.Equal(_canonical, item.CanonicalRecordingId));
        Assert.Single(await db.CanonicalRecordings.ToListAsync());
        Assert.Single(await db.PlaylistSyncRuns.ToListAsync());
        Assert.Equal(
            DurableJobState.Succeeded,
            (await db.Jobs.SingleAsync()).State);
        Assert.Contains(
            await db.AuditEvents
                .Where(item => item.Category == "job-progress")
                .Select(item => item.Action)
                .ToListAsync(),
            eventType => eventType.Contains("playlist.complete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_sync_claims_once_and_failed_claim_retries_with_the_same_run()
    {
        _source.Snapshot = Snapshot(
            "revision-sync-claim",
            Entry(0, "entry-sync-claim", "source-1", "One"));
        var refresh = await _service.RefreshAsync(Context(), _link);
        var request = new PlaylistOrchestrationRequest(_link, 91, refresh.SnapshotId);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _target.WriteStarted = writeStarted;
        _target.WriteBlock = releaseWrite.Task;

        var first = _service.RunAsync(Context(), request);
        await writeStarted.Task;
        var duplicate = await _service.RunAsync(Context(), request);

        Assert.True(duplicate.ReusedRun);
        Assert.Equal(PlaylistSyncState.Running, duplicate.State);
        Assert.Equal(1, _target.WriteCalls);

        releaseWrite.SetResult();
        var completed = await first;
        Assert.Equal(completed.RunId, duplicate.RunId);
        Assert.Equal(PlaylistSyncState.PartiallySucceeded, completed.State);

        var retryRequest = request with { Generation = 92 };
        _target.WriteStarted = null;
        _target.WriteBlock = null;
        _target.WriteStatus = BackendPlaylistTargetStatus.BackendFailure;
        var failed = await _service.RunAsync(Context(), retryRequest);
        Assert.Equal(PlaylistSyncState.Failed, failed.State);

        _target.WriteStatus = BackendPlaylistTargetStatus.Success;
        var retried = await _service.RunAsync(Context(), retryRequest);
        Assert.Equal(failed.RunId, retried.RunId);
        Assert.Equal(PlaylistSyncState.PartiallySucceeded, retried.State);
        Assert.False(retried.ReusedRun);
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
        Enabled = true,
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

    private ProtocolExecutionContext Context(string libraryScopeId = "music") => new(ProtocolKind.Jellyfin, "backend", "principal",
        new AllstarrPrincipal(_tenant, _user, "jellyfin", "backend", "principal", "Owner", false),
        "correlation", _now.AddMinutes(5), default, libraryScopeId: libraryScopeId);
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
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);
        private void Increment() => Interlocked.Increment(ref _count);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Increment();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Increment();
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Increment();
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
    private sealed class EmptyServices : IServiceProvider
    {
        public static EmptyServices Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }
    private sealed class FakeSource : IProviderPlaylistSourceGateway
    {
        public CollectedPlaylistSourceSnapshot Snapshot { get; set; } = null!;
        public string? FailureCode { get; set; }
        public ProviderOutcome<ProviderPlaylistArtwork> Artwork { get; set; } =
            ProviderOutcome<ProviderPlaylistArtwork>.Failure(new ProviderError(ProviderErrorKind.CapabilityUnavailable));
        public Task<CollectedPlaylistSourceSnapshot> CollectAsync(ProtocolExecutionContext context, PlaylistLinkRecord link, CancellationToken cancellationToken) =>
            FailureCode == null
                ? Task.FromResult(Snapshot)
                : Task.FromException<CollectedPlaylistSourceSnapshot>(
                    new PlaylistSourceUnavailableException(FailureCode));
        public Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
            ProtocolExecutionContext context, PlaylistLinkRecord link, ProviderPlaylistArtworkRequest request,
            CancellationToken cancellationToken) => Task.FromResult(Artwork);
    }
    private sealed class CollectingLogger<T>(List<string> messages) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
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
        public TaskCompletionSource? WriteStarted { get; set; }
        public Task? WriteBlock { get; set; }
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
        public async Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> WriteAsync(BackendPlaylistTargetContext context, BackendPlaylistWriteRequest request, CancellationToken cancellationToken)
        {
            WriteCalls++; Contexts.Add(context); LastWrite = request;
            WriteStarted?.TrySetResult();
            if (WriteBlock != null)
                await WriteBlock.WaitAsync(cancellationToken);
            LastSnapshot = WriteStatus == BackendPlaylistTargetStatus.Success
                ? Backend(request.BackendPlaylistId ?? "target-created", request.OrderedBackendItemIds)
                : null;
            var receipt = LastSnapshot == null
                ? null
                : new BackendPlaylistWriteReceipt(LastSnapshot, true, []);
            return new BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>(
                WriteStatus, receipt, ErrorCode: ErrorCode);
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
