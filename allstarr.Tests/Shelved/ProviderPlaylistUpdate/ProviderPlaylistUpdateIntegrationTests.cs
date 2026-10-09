using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ProviderPlaylistUpdateIntegrationTests : IAsyncLifetime
{
    private readonly DateTimeOffset _now = new(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
    private readonly Guid _owner = Guid.CreateVersion7();
    private readonly Guid _otherOwner = Guid.CreateVersion7();
    private readonly Guid _account = Guid.CreateVersion7();
    private readonly Guid _sharedAccount = Guid.CreateVersion7();
    private readonly Guid _otherAccount = Guid.CreateVersion7();
    private readonly Guid _link = Guid.CreateVersion7();
    private readonly Guid _canonicalA = Guid.CreateVersion7();
    private readonly Guid _canonicalB = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private DbFactory _factory = null!;
    private StatefulPlaylistCapability _provider = null!;
    private StatefulTarget _target = null!;
    private FakeRouter _router = null!;
    private ProviderPlaylistUpdateService _service = null!;
    private Clock _clock = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        _provider = new("fixture");
        _target = new();
        var registry = new ProviderRegistry([Registration(_provider)]);
        _router = new(registry, AccountContext(
            _account, ProviderAccountScope.Personal, _owner));
        _clock = new(_now);
        _service = new(
            _factory,
            registry,
            _router,
            new TargetResolver(_target),
            _clock, new TestBackendLibraryAccess(_factory, "music"));

        await using var db = await _factory.CreateDbContextAsync();
        db.Users.AddRange(User(_owner, "Owner"), User(_otherOwner, "Other owner"));
        db.ProviderAccounts.AddRange(
            Account(_account, ProviderAccountScope.Personal, _owner),
            Account(_otherAccount, ProviderAccountScope.Personal, _otherOwner),
            Account(_sharedAccount, ProviderAccountScope.Shared, null));
        db.CanonicalRecordings.AddRange(
            Canonical(_canonicalA),
            Canonical(_canonicalB));
        db.ProviderTrackIdentities.AddRange(
            Identity(_canonicalA, "track-a"),
            Identity(_canonicalB, "track-b"));
        db.LibraryTracks.AddRange(
            LocalTrack("local-a", "A", _canonicalA),
            LocalTrack("local-b", "B", _canonicalB));
        db.PlaylistLinks.Add(Link());
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Preview_scope_denials_and_cross_provider_route_make_no_external_calls()
    {
        await AssertDeniedAsync(
            () => _service.PreviewAsync(Actor(_owner), Guid.CreateVersion7(), "foreign", default),
            typeof(KeyNotFoundException));
        await AssertDeniedAsync(
            () => _service.PreviewAsync(Actor(_otherOwner), _link, "owner", default),
            typeof(ProviderPlaylistUpdateException), "playlist-owner-required");


        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync(item => item.Id == _account);
            account.OwnerUserId = _otherOwner;
            await db.SaveChangesAsync();
        }
        await AssertDeniedAsync(
            () => _service.PreviewAsync(Actor(_owner), _link, "account-scope", default),
            typeof(ProviderPlaylistUpdateException), "provider-account-unavailable");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync(item => item.Id == _account);
            account.OwnerUserId = _owner;
            var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
            link.ProviderAccountId = _otherAccount;
            await db.SaveChangesAsync();
        }
        await AssertDeniedAsync(
            () => _service.PreviewAsync(Actor(_owner), _link, "foreign-account", default),
            typeof(ProviderPlaylistUpdateException), "provider-account-unavailable");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
            link.ProviderAccountId = _account;
            await db.SaveChangesAsync();
        }

        _router.Account = AccountContext(
            _account, ProviderAccountScope.Personal, _owner, revision: 1);
        await AssertDeniedAsync(
            () => _service.PreviewAsync(Actor(_owner), _link, "stale-route-account", default),
            typeof(ProviderPlaylistUpdateException), "provider-route-unavailable");
        _router.Account = AccountContext(
            _account, ProviderAccountScope.Personal, _owner);

        _router.ActorOverride = Actor(_otherOwner);
        await AssertDeniedAsync(
            () => _service.PreviewAsync(Actor(_owner), _link, "route-owner", default),
            typeof(UnauthorizedAccessException), null);
        _router.ActorOverride = null;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(user => user.Id == _owner)).Enabled = false;
            await db.SaveChangesAsync();
        }
        await AssertDeniedAsync(
            () => _service.PreviewAsync(Actor(_owner), _link, "disabled-owner", default),
            typeof(ProviderPlaylistUpdateException), "backend-identity-unavailable");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(user => user.Id == _owner)).Enabled = true;
            await db.SaveChangesAsync();
        }

        _router.CrossProviderCandidate = true;
        await AssertDeniedAsync(
            () => _service.PreviewAsync(Actor(_owner), _link, "cross-provider", default),
            typeof(ProviderPlaylistUpdateException), "provider-route-unavailable");
        _router.CrossProviderCandidate = false;
    }

    [Fact]
    public async Task Shared_and_personal_accounts_support_preview_and_apply()
    {
        await SetLinkAccountAsync(_sharedAccount);

        var plan = await _service.PreviewAsync(
            Actor(_owner), _link, "shared-account", default);
        var result = await _service.ApplyAsync(plan, default);

        Assert.True(result.Applied);
        Assert.Equal(1, _provider.MutationCalls);
        Assert.Equal(_sharedAccount, _router.Account.AccountId);

        _provider.ResetSource();
        await SetLinkAccountAsync(_account);
        plan = await _service.PreviewAsync(
            Actor(_owner), _link, "personal-account", default);
        result = await _service.ApplyAsync(plan, default);
        Assert.True(result.Applied);
        Assert.Equal(2, _provider.MutationCalls);
        Assert.Equal(_account, _router.Account.AccountId);
    }

    [Fact]
    public async Task Stale_provider_revision_is_retryable_and_fresh_plan_recovers()
    {
        var plan = await PreviewAsync("stale-before-apply");
        _provider.Revision = "source-r2";

        var stale = await Assert.ThrowsAsync<ProviderPlaylistUpdateException>(
            () => _service.ApplyAsync(plan, default));
        Assert.Equal("provider-source-update-failed", stale.Code);
        Assert.True(stale.Retryable);
        Assert.Equal(1, _provider.MutationCalls);

        var fresh = await PreviewAsync("stale-recovered");
        var result = await _service.ApplyAsync(fresh, default);
        Assert.True(result.Applied);
        Assert.Equal(2, _provider.MutationCalls);
    }

    [Fact]
    public async Task Cancellation_before_mutation_and_after_mutation_stop_without_extra_calls()
    {
        var plan = await PreviewAsync("cancel-before");
        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _service.ApplyAsync(plan, before.Token));
        }
        Assert.Equal(0, _provider.MutationCalls);

        using var after = new CancellationTokenSource();
        _provider.CancelAfterMutation = after;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.ApplyAsync(plan, after.Token));
        Assert.Equal(1, _provider.MutationCalls);
        Assert.Equal(0, _provider.TrackReads - 1);
    }

    [Fact]
    public async Task Receipt_and_post_write_order_mismatches_are_source_verification_failures()
    {
        foreach (var mismatch in new[] { "id", "count" })
        {
            var receiptPlan = await PreviewAsync($"receipt-{mismatch}-mismatch");
            _provider.ReceiptIdMismatch = mismatch == "id";
            _provider.ReceiptCountMismatch = mismatch == "count";
            var receipt = await Assert.ThrowsAsync<ProviderPlaylistUpdateException>(
                () => _service.ApplyAsync(receiptPlan, default));
            Assert.Equal("provider-source-verification-mismatch", receipt.Code);
            _provider.ReceiptIdMismatch = false;
            _provider.ReceiptCountMismatch = false;
            _provider.ResetSource();
        }

        var rereadPlan = await PreviewAsync("reread-mismatch");
        _provider.RereadOrderMismatch = true;
        var reread = await Assert.ThrowsAsync<ProviderPlaylistUpdateException>(
            () => _service.ApplyAsync(rereadPlan, default));
        Assert.Equal("provider-source-verification-mismatch", reread.Code);
        Assert.Equal(3, _provider.MutationCalls);
    }

    [Fact]
    public async Task Uncertain_apply_retries_idempotently_and_audits_both_outcomes()
    {
        var plan = await PreviewAsync("uncertain");
        var claim = Claim(new ProviderPlaylistUpdateJobPayload(
            _link,
            plan.LinkRevision,
            plan.ConfirmationId,
            plan.TargetFingerprint,
            plan.DesiredFingerprint));
        _provider.FailAfterApplyOnce = true;
        var handler = new ProviderPlaylistUpdateJobHandler(_factory, _service, _clock);

        var retry = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Retry, retry.Kind);
        Assert.Equal("provider-source-update-failed", retry.ErrorCode);
        Assert.Equal(1, _provider.MutationCalls);

        var success = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, success.Kind);
        Assert.Equal(1, _provider.MutationCalls);

        await using var db = await _factory.CreateDbContextAsync();
        var outcomes = await db.AuditEvents.AsNoTracking()
            .Where(item => item.Action == "provider-source-update")
            .Select(item => item.Outcome)
            .ToListAsync();
        Assert.Equal(["retry", "succeeded"], outcomes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Durable_handler_guards_link_target_and_source_confirmation_without_mutation()
    {
        var linkPlan = await PreviewAsync("guard-link");
        var linkClaim = Claim(new ProviderPlaylistUpdateJobPayload(
            _link, linkPlan.LinkRevision, linkPlan.ConfirmationId,
            linkPlan.TargetFingerprint, linkPlan.DesiredFingerprint));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
            link.Revision++;
            await db.SaveChangesAsync();
        }
        var handler = new ProviderPlaylistUpdateJobHandler(_factory, _service, _clock);
        var linkConflict = await handler.ExecuteAsync(new(linkClaim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Failed, linkConflict.Kind);
        Assert.Equal("provider-source-update-link-changed", linkConflict.ErrorCode);
        Assert.Equal(0, _provider.MutationCalls);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
            link.Revision = linkPlan.LinkRevision;
            await db.SaveChangesAsync();
        }
        var targetPlan = await PreviewAsync("guard-target");
        var targetClaim = Claim(new ProviderPlaylistUpdateJobPayload(
            _link, targetPlan.LinkRevision, targetPlan.ConfirmationId,
            targetPlan.TargetFingerprint, targetPlan.DesiredFingerprint));
        _target.Rename("Changed target");
        var targetConflict = await handler.ExecuteAsync(new(targetClaim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Failed, targetConflict.Kind);
        Assert.Equal("provider-source-update-target-changed", targetConflict.ErrorCode);
        Assert.Equal(0, _provider.MutationCalls);

        _target.Rename("Target");
        var sourcePlan = await PreviewAsync("guard-source");
        var confirmationConflict = await handler.ExecuteAsync(new(Claim(new ProviderPlaylistUpdateJobPayload(
            _link, sourcePlan.LinkRevision, new string('0', 64),
            sourcePlan.TargetFingerprint, sourcePlan.DesiredFingerprint)), EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Failed, confirmationConflict.Kind);
        Assert.Equal("provider-source-update-source-changed", confirmationConflict.ErrorCode);
        Assert.Equal(0, _provider.MutationCalls);

        var fingerprintConflict = await handler.ExecuteAsync(new(Claim(new ProviderPlaylistUpdateJobPayload(
            _link, sourcePlan.LinkRevision, sourcePlan.ConfirmationId,
            sourcePlan.TargetFingerprint, new string('0', 64))), EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Failed, fingerprintConflict.Kind);
        Assert.Equal("provider-source-update-source-changed", fingerprintConflict.ErrorCode);
        Assert.Equal(0, _provider.MutationCalls);

        var sourceClaim = Claim(new ProviderPlaylistUpdateJobPayload(
            _link, sourcePlan.LinkRevision, sourcePlan.ConfirmationId,
            sourcePlan.TargetFingerprint, sourcePlan.DesiredFingerprint));
        _provider.Revision = "source-r2";
        var sourceConflict = await handler.ExecuteAsync(new(sourceClaim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Failed, sourceConflict.Kind);
        Assert.Equal("provider-source-update-source-changed", sourceConflict.ErrorCode);
        Assert.Equal(0, _provider.MutationCalls);
    }

    private async Task<ProviderPlaylistUpdatePlan> PreviewAsync(string correlation) =>
        await _service.PreviewAsync(Actor(_owner), _link, correlation, default);

    private async Task SetLinkAccountAsync(Guid accountId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
        link.ProviderAccountId = accountId;
        await db.SaveChangesAsync();
        var account = await db.ProviderAccounts.SingleAsync(item => item.Id == accountId);
        _router.Account = AccountContext(
            account.Id,
            account.Scope,
            account.OwnerUserId,
            account.Revision);
    }

    private async Task AssertDeniedAsync(
        Func<Task> action,
        Type exceptionType,
        string? code = null)
    {
        ResetCalls();
        var exception = await Record.ExceptionAsync(action);
        Assert.NotNull(exception);
        Assert.IsType(exceptionType, exception);
        if (code != null) Assert.Equal(code, ((ProviderPlaylistUpdateException)exception!).Code);
        Assert.Equal(0, _provider.TotalCalls);
        Assert.Equal(0, _target.ReadCalls);
    }

    private void ResetCalls()
    {
        _provider.TrackReads = 0;
        _provider.MutationCalls = 0;
        _target.ReadCalls = 0;
    }

    private DurableJobClaim Claim(ProviderPlaylistUpdateJobPayload payload) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        1,
        "playlist.provider-source-update",
        JsonSerializer.SerializeToElement(payload),
        _owner,
        _account,
        "playlist",
        JsonSerializer.SerializeToElement(new { }),
        "provider-playlist-update",
        "test-worker",
        _now.AddMinutes(5));

    private ProviderActorContext Actor(Guid user) => new(
        ProviderActorKind.User,
        user,
        new ProviderBackendPrincipal("jellyfin", "backend", "principal"));

    private PlaylistLinkRecord Link() => new()
    {
        Id = _link,
        OwnerUserId = _owner,
        ProviderAccountId = _account,
        SourceProviderId = "fixture",
        SourcePlaylistId = "playlist",
        SourcePlaylistIdHash = Hash("playlist"),
        TargetProtocol = "jellyfin",
        TargetBackendInstanceId = "backend",
        TargetPlaylistId = "target-1",
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

    private ProviderAccountRecord Account(
        Guid id,
        ProviderAccountScope scope,
        Guid? owner) => new()
        {
            Id = id,
            OwnerUserId = owner,
            ProviderId = "fixture",
            DisplayName = scope.ToString(),
            Enabled = true,
            CreatedAt = _now,
            UpdatedAt = _now
        };

    private ProviderAccountContext AccountContext(
        Guid id,
        ProviderAccountScope scope,
        Guid? owner,
        long revision = 0) => new(
        id,
        "fixture",
        scope,
        revision,
        ownerUserId: owner);

    private UserRecord User(Guid id, string name) => new()
    {
        Id = id,
        DisplayName = name,
        Enabled = true,
        BackendType = "jellyfin",
        BackendInstanceId = "backend",
        BackendPrincipalId = id == _owner ? "principal" : "other-principal",
        CreatedAt = _now,
        UpdatedAt = _now
    };

    private CanonicalRecordingRecord Canonical(Guid id) => new()
    {
        Id = id,
        CreatedByUserId = _owner,
        CreatedAt = _now,
        UpdatedAt = _now
    };

    private ProviderTrackIdentityRecord Identity(Guid canonical, string externalId)
    {
        var resource = new ProviderExternalResourceId("fixture", ProviderResourceKind.Track, externalId);
        return new()
        {
            Id = Guid.CreateVersion7(),
            CanonicalRecordingId = canonical,
            ProviderId = "fixture",
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = externalId,
            ExternalIdHash = ProviderPlaylistSnapshotCollector.HashResource(resource),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            VerifiedAt = _now,
            CreatedAt = _now,
            UpdatedAt = _now
        };
    }

    private LibraryTrackRecord LocalTrack(string backendItem, string title, Guid canonical) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = _owner,
        CanonicalRecordingId = canonical,
        Protocol = "jellyfin",
        BackendInstanceId = "backend",
        BackendLibraryId = "music",
        BackendItemId = backendItem,
        FilePath = $"/music/{backendItem}.flac",
        Title = title,
        Artist = "Artist",
        Album = "Album",
        DurationMilliseconds = 180000,
        ProviderIdsJson = "{}",
        IndexedAt = _now,
        SourceModifiedAt = _now,
        UpdatedAt = _now
    };

    private static ProviderRegistration Registration(StatefulPlaylistCapability capability) => new(
        new ProviderDescriptor(
            capability.ProviderId,
            "Fixture provider",
            "Fixture provider for playlist source updates",
            ProviderOrigin.BuiltIn,
            "1",
            "1.0",
            [new ProviderCapabilityDescriptor(
                ProviderCapabilityKind.Playlist,
                ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.Required,
                "1.0",
                ["getUserPlaylists", "getPlaylistTracks", "mutatePlaylist"],
                [ProviderAccountScope.Shared, ProviderAccountScope.Personal])],
            new ProviderPermissionDescriptor()),
        [capability]);

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class Clock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static EmptyServices Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class TargetResolver(StatefulTarget target) : IBackendPlaylistTargetResolver
    {
        public IBackendPlaylistTarget Resolve(string targetProtocol) => target;
    }

    private sealed class StatefulTarget : IBackendPlaylistTarget
    {
        public int ReadCalls { get; set; }
        public BackendPlaylistFamily Family => BackendPlaylistFamily.Jellyfin;
        public BackendPlaylistTargetCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, true, true, true);
        public BackendPlaylistSnapshot Snapshot { get; private set; } = CreateSnapshot("Target");

        public void Rename(string name) =>
            Snapshot = Snapshot with
            {
                Name = name,
                Fingerprint = BackendPlaylistSnapshot.ComputeFingerprint(
                    Snapshot.BackendPlaylistId,
                    name,
                    Snapshot.Members)
            };

        public Task<BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>> ListAsync(
            BackendPlaylistTargetContext context,
            string? query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>(
                BackendPlaylistTargetStatus.Success, []));

        public Task<BackendPlaylistTargetResult<BackendPlaylistArtwork>> ReadArtworkAsync(
            BackendPlaylistTargetContext context,
            string backendPlaylistId,
            string? artworkReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistArtwork>(
                BackendPlaylistTargetStatus.Unsupported));

        public Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot?>> FindByNameAsync(
            BackendPlaylistTargetContext context,
            string name,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistSnapshot?>(
                BackendPlaylistTargetStatus.NotFound));

        public Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot>> ReadAsync(
            BackendPlaylistTargetContext context,
            string backendPlaylistId,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistSnapshot>(
                backendPlaylistId == Snapshot.BackendPlaylistId
                    ? BackendPlaylistTargetStatus.Success
                    : BackendPlaylistTargetStatus.NotFound,
                Snapshot));
        }

        public Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> WriteAsync(
            BackendPlaylistTargetContext context,
            BackendPlaylistWriteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>(
                BackendPlaylistTargetStatus.Unsupported));

        private static BackendPlaylistSnapshot CreateSnapshot(string name)
        {
            var members = new BackendPlaylistMember[]
            {
                new("local-b", "entry-b"),
                new("local-a", "entry-a")
            };
            return new(
                "target-1",
                name,
                members,
                BackendPlaylistSnapshot.ComputeFingerprint("target-1", name, members),
                "target-r1");
        }
    }

    private sealed class StatefulPlaylistCapability(string providerId) : IProviderPlaylistCapability
    {
        private readonly ProviderExternalResourceId _playlist =
            new(providerId, ProviderResourceKind.Playlist, "playlist");
        private IReadOnlyList<string> _tracks = ["track-a"];
        private bool _rereadOrderMismatch;

        public string ProviderId { get; } = providerId;
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;
        public ProviderPlaylistMutationSupport MutationSupport { get; } = new(false, true);
        public string Revision { get; set; } = "source-r1";
        public int TrackReads { get; set; }
        public int MutationCalls { get; set; }
        public int TotalCalls => TrackReads + MutationCalls;
        public bool ReceiptIdMismatch { get; set; }
        public bool ReceiptCountMismatch { get; set; }
        public bool RereadOrderMismatch { get; set; }
        public bool FailAfterApplyOnce { get; set; }
        public CancellationTokenSource? CancelAfterMutation { get; set; }
        public Guid ExpectedAccountId { get; set; }

        public void ResetSource()
        {
            _tracks = ["track-a"];
            Revision = "source-reset";
        }

        public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
            ProviderExecutionContext context,
            ProviderUserPlaylistsRequest request) =>
            Task.FromResult(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(
                new ProviderPage<ProviderPlaylistSummary>(ProviderId, [])));

        public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
            ProviderExecutionContext context,
            ProviderPlaylistTracksRequest request)
        {
            TrackReads++;
            if (request.ExpectedRevision != null && request.ExpectedRevision != Revision)
                return Task.FromResult(ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                    new ProviderError(ProviderErrorKind.TransientFailure)));

            var values = _tracks.ToArray();
            if (_rereadOrderMismatch)
            {
                values = values.Reverse().ToArray();
                _rereadOrderMismatch = false;
            }
            var tracks = values.Select((id, position) =>
                new ProviderPlaylistTrack(
                    position,
                    new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Track, id),
                    metadata: new ProviderTrackMetadata(
                        new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Track, id),
                        id,
                        [new ProviderArtistCredit("Artist")]))).ToArray();
            var summary = new ProviderPlaylistSummary(
                _playlist,
                "Source playlist",
                new ProviderPlaylistOwner("fixture-user"),
                Revision,
                "Description",
                trackCount: tracks.Length);
            var page = new ProviderPage<ProviderPlaylistTrack>(
                ProviderId, tracks, snapshotVersion: Revision);
            return Task.FromResult(ProviderOutcome<ProviderPlaylistTrackPage>.Success(
                new ProviderPlaylistTrackPage(summary, page)));
        }

        public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
            ProviderExecutionContext context,
            ProviderPlaylistSearchRequest request) =>
            Task.FromResult(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(
                new ProviderPage<ProviderPlaylistSummary>(ProviderId, [])));

        public Task<ProviderOutcome<ProviderPlaylistMutationReceipt>> MutatePlaylistAsync(
            ProviderExecutionContext context,
            ProviderPlaylistMutationRequest request)
        {
            MutationCalls++;
            Assert.Equal(ProviderId, context.ProviderId);
            Assert.Equal(ExpectedAccountId, context.Account?.AccountId);
            Assert.Equal(ProviderId, request.ProviderId);
            Assert.Equal(ProviderPlaylistConflictBehavior.FailIfChanged, request.ConflictBehavior);
            Assert.Equal(_playlist, request.ExistingPlaylistId);
            Assert.Equal(["track-b", "track-a"], request.OrderedTrackIds.Select(item => item.Value));
            if (request.ExpectedRevision != Revision)
                return Task.FromResult(ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(
                    new ProviderError(ProviderErrorKind.PermanentFailure)));

            _tracks = request.OrderedTrackIds.Select(item => item.Value).ToArray();
            Revision = $"{Revision}-next";
            if (RereadOrderMismatch) _rereadOrderMismatch = true;
            if (CancelAfterMutation != null) CancelAfterMutation.Cancel();
            if (FailAfterApplyOnce)
            {
                FailAfterApplyOnce = false;
                return Task.FromResult(ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(
                    new ProviderError(ProviderErrorKind.TransientFailure)));
            }

            var playlistId = ReceiptIdMismatch
                ? new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Playlist, "other")
                : _playlist;
            var receipt = new ProviderPlaylistMutationReceipt(
                playlistId,
                Revision,
                ReceiptCountMismatch ? _tracks.Count + 1 : _tracks.Count,
                true);
            return Task.FromResult(ProviderOutcome<ProviderPlaylistMutationReceipt>.Success(receipt));
        }
    }

    private sealed class FakeRouter(
        ProviderRegistry registry,
        ProviderAccountContext account) : IProviderRouter
    {
        public ProviderAccountContext Account { get; set; } = account;
        public bool CrossProviderCandidate { get; set; }
        public ProviderActorContext? ActorOverride { get; set; }

        public Task<ProviderRoutePlan<TCapability>> PlanAsync<TCapability>(ProviderRouteRequest request)
            where TCapability : class, IProviderCapability
        {
            if (typeof(TCapability) != typeof(IProviderPlaylistCapability))
                throw new ArgumentException("The fixture only routes playlist capabilities.");
            var provider = registry.GetRequired(request.ProviderPriority[0]);
            var implementation = registry.GetRequiredCapability<IProviderPlaylistCapability>(
                provider.Id, ProviderCapabilityKind.Playlist);
            var context = new ProviderExecutionContext(
                ActorOverride ?? request.Actor,
                provider.Id,
                Account,
                request.Policy,
                request.OperationId,
                request.CorrelationId,
                request.Deadline,
                request.CancellationToken);
            ((StatefulPlaylistCapability)implementation).ExpectedAccountId = Account.AccountId;
            var candidate = new ProviderRouteCandidate<IProviderPlaylistCapability>(
                0,
                provider,
                provider.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Playlist),
                implementation,
                context,
                null);
            if (CrossProviderCandidate)
            {
                var wrong = new StatefulPlaylistCapability("other");
                candidate = candidate with
                {
                    Provider = new ProviderDescriptor(
                        "other",
                        "Other provider",
                        "Cross-provider fixture",
                        ProviderOrigin.BuiltIn,
                        "1",
                        "1.0",
                        [new ProviderCapabilityDescriptor(
                            ProviderCapabilityKind.Playlist,
                            ProviderCapabilitySupportState.Supported,
                            ProviderAccountRequirement.Required,
                            "1.0",
                            ["mutatePlaylist"],
                            [ProviderAccountScope.Personal])],
                        new ProviderPermissionDescriptor()),
                    Implementation = wrong
                };
            }
            var plan = new ProviderRoutePlan<IProviderPlaylistCapability>(
                request,
                [candidate],
                new ProviderRouteDecisionRecord(
                    request.CorrelationId,
                    request.Capability,
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    [new ProviderRouteCandidateDecision(
                        candidate.Provider.Id,
                        candidate.Context.Account?.AccountId,
                        ProviderRouteDecisionStatus.Accepted,
                        "selected",
                        0)]));
            return Task.FromResult((ProviderRoutePlan<TCapability>)(object)plan);
        }

        public ProviderFallbackDecision<TCapability> EvaluateFallback<TCapability>(
            ProviderRoutePlan<TCapability> plan,
            int failedCandidateIndex,
            ProviderError error)
            where TCapability : class, IProviderCapability =>
            throw new NotSupportedException();
    }
}
