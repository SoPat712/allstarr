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
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class PlaylistOrchestrationIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
    private DbFactory _factory = null!;
    private FakeSource _source = null!;
    private FakeTarget _target = null!;
    private PlaylistOrchestrationService _service = null!;
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _account = Guid.CreateVersion7();
    private readonly Guid _link = Guid.CreateVersion7();
    private readonly Guid _credential = Guid.CreateVersion7();
    private Guid _identity;
    private Guid _trackOne;
    private Guid _trackTwo;
    private readonly DateTimeOffset _now = new(2026, 7, 12, 5, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new(new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "orchestration.db")}").Options);
        _source = new FakeSource();
        _target = new FakeTarget();
        var clock = new Clock(_now);
        var accountResolver = new ProviderAccountResolver(_factory, new ProviderPolicyOptions());
        var trackMatches = new TrackMatchCommandService(
            _factory,
            new TrackMatchDecisionEngine(),
            accountResolver,
            clock);
        _service = new(_factory, _source, new FakeTargetResolver(_target), new PlaylistMaterializationPlanner(),
            new TrackMatchDecisionEngine(), trackMatches, clock);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _identity = Guid.CreateVersion7(); _trackOne = Guid.CreateVersion7(); _trackTwo = Guid.CreateVersion7();
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
        db.LibraryTracks.AddRange(Local(_trackOne, "local-1", "source-1", "One"), Local(_trackTwo, "local-2", "source-2", "Two"));
        db.PlaylistLinks.Add(Link());
        await db.SaveChangesAsync();
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
        Assert.Single(await db.ExternalMetadataSnapshots.ToListAsync());
        Assert.Equal(2, await db.PlaylistSourceEntries.CountAsync());
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

    private ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "backend", "principal",
        new AllstarrPrincipal(_tenant, _user, "jellyfin", "backend", "principal", "Owner", false),
        "correlation", _now.AddMinutes(5), default, libraryScopeId: "music");
    private CollectedPlaylistSourceSnapshot Snapshot(string revision, params CollectedPlaylistSourceEntry[] entries) =>
        new("fixture", _account, Hash("playlist"), revision, $"etag-{revision}", "Provider Mix", "Description",
            "provider-artwork:stable:key", entries);
    private static CollectedPlaylistSourceEntry Entry(int position, string entry, string source, string title) =>
        new(position, Hash(entry), Hash(source), null, title, ["Artist"], "Album", TimeSpan.FromMinutes(3), null, false);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }
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
        public BackendPlaylistWriteRequest? LastWrite { get; private set; }
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
            var snapshot = ReadStatus == BackendPlaylistTargetStatus.Success ? Backend(backendPlaylistId) : null;
            return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistSnapshot>(ReadStatus, snapshot, ErrorCode: ErrorCode));
        }
        public Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> WriteAsync(BackendPlaylistTargetContext context, BackendPlaylistWriteRequest request, CancellationToken cancellationToken)
        {
            WriteCalls++; Contexts.Add(context); LastWrite = request;
            var receipt = WriteStatus == BackendPlaylistTargetStatus.Success
                ? new BackendPlaylistWriteReceipt(Backend(request.BackendPlaylistId ?? "target-created", request.OrderedBackendItemIds), true, [])
                : null;
            return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>(WriteStatus, receipt, ErrorCode: ErrorCode));
        }
        private static BackendPlaylistSnapshot Backend(string id, IEnumerable<string>? members = null)
        {
            var values = (members ?? []).Select(item => new BackendPlaylistMember(item, item)).ToArray();
            return new(id, "Provider Mix", values, BackendPlaylistSnapshot.ComputeFingerprint(id, "Provider Mix", values), "native-1");
        }
    }
}
