using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class TrackIdentityServiceTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableStorageState _storageState = null!;
    private FakeClock _clock = null!;
    private TrackIdentityService _service = null!;
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _userA;
    private Guid _userB;
    private Guid _userOtherTenant;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        var storage = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = _database.ConnectionString
        };
        _factory = new TestDbContextFactory(_database.Options);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
            _tenantA = Guid.CreateVersion7();
            _tenantB = Guid.CreateVersion7();
            _userA = Guid.CreateVersion7();
            _userB = Guid.CreateVersion7();
            _userOtherTenant = Guid.CreateVersion7();
            var now = new DateTimeOffset(2026, 7, 11, 14, 0, 0, TimeSpan.Zero);
            context.Tenants.AddRange(
                new TenantRecord { Id = _tenantA, Slug = "tenant-a", Name = "Tenant A", CreatedAt = now },
                new TenantRecord { Id = _tenantB, Slug = "tenant-b", Name = "Tenant B", CreatedAt = now });
            context.Users.AddRange(
                User(_userA, _tenantA, "User A", now),
                User(_userB, _tenantA, "User B", now),
                User(_userOtherTenant, _tenantB, "Other tenant", now));
            await context.SaveChangesAsync();
        }

        _storageState = new DurableStorageState(storage);
        _storageState.Set(DurableStorageReadiness.Ready, "fixture");
        _clock = new FakeClock(new DateTimeOffset(2026, 7, 11, 14, 0, 0, TimeSpan.Zero));
        _service = new TrackIdentityService(_factory, _storageState, _clock);
    }

    [Fact]
    public async Task OneCanonicalRecording_LinksManyProvidersAndTranslatesExactly()
    {
        var actor = Actor(_tenantA, _userA);
        var recording = await _service.CreateRecordingAsync(
            actor,
            "multi-provider-create",
            "US-RC1-76-07839");
        var spotify = Context(actor, "spotify");
        var deezer = Context(actor, "deezer");
        var qobuz = Context(actor, "qobuz");

        await Link(recording.Recording.Id, spotify, "spotify-track-1");
        await Link(recording.Recording.Id, deezer, "3135556");
        await Link(recording.Recording.Id, qobuz, "qobuz-track-9", catalog: "us");

        var translated = await _service.TranslateAsync(
            spotify,
            Track("spotify", "spotify-track-1"),
            deezer,
            new ProviderTrackIdentityTarget("deezer"));

        Assert.Equal(TrackIdentityTranslationStatus.Translated, translated.Status);
        Assert.Equal(recording.Recording.Id, translated.CanonicalRecordingId);
        Assert.Equal("3135556", translated.Target!.ExternalId.Value);
        await using var context = await _factory.CreateDbContextAsync();
        var links = await context.ProviderTrackIdentities.ToListAsync();
        Assert.Equal(3, links.Count);
        Assert.All(links, link => Assert.Matches("^[0-9a-f]{64}$", link.ExternalIdHash));
        Assert.Single(await context.CanonicalRecordings.ToListAsync());
    }

    [Fact]
    public async Task MissingVerifiedLink_RemainsUnresolvedAndNeverGuesses()
    {
        var actor = Actor(_tenantA, _userA);
        var recording = await _service.CreateRecordingAsync(actor, "no-guess-create");
        var spotify = Context(actor, "spotify");
        var deezer = Context(actor, "deezer");
        await Link(recording.Recording.Id, spotify, "same title by same artist");

        var missingTarget = await _service.TranslateAsync(
            spotify,
            Track("spotify", "same title by same artist"),
            deezer,
            new ProviderTrackIdentityTarget("deezer"));
        var missingSource = await _service.TranslateAsync(
            spotify,
            Track("spotify", "unlinked source"),
            deezer,
            new ProviderTrackIdentityTarget("deezer"));

        Assert.Equal(TrackIdentityTranslationStatus.TargetNotLinked, missingTarget.Status);
        Assert.Equal(TrackIdentityTranslationStatus.SourceNotLinked, missingSource.Status);
        Assert.Null(missingTarget.Target);
        Assert.Null(missingSource.CanonicalRecordingId);
    }

    [Fact]
    public async Task ExactIdentityConflict_DoesNotRemapExistingRecording()
    {
        var actor = Actor(_tenantA, _userA);
        var first = await _service.CreateRecordingAsync(actor, "conflict-first");
        var second = await _service.CreateRecordingAsync(actor, "conflict-second");
        var spotify = Context(actor, "spotify");
        var externalId = "spotify-conflict-sensitive-id";
        await Link(first.Recording.Id, spotify, externalId);

        var conflict = await Link(second.Recording.Id, spotify, externalId);

        Assert.Equal(TrackIdentityLinkStatus.Conflict, conflict.Status);
        Assert.Equal(first.Recording.Id, conflict.ConflictingCanonicalRecordingId);
        var resolution = await _service.ResolveAsync(spotify, Track("spotify", externalId));
        Assert.Equal(first.Recording.Id, resolution!.CanonicalRecordingId);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Single(await context.ProviderTrackIdentities.ToListAsync());
        var audit = await context.AuditEvents.SingleAsync(item => item.Outcome == "conflict");
        Assert.DoesNotContain(externalId, audit.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelinkingSameExactIdentity_IsIdempotent()
    {
        var actor = Actor(_tenantA, _userA);
        var recording = await _service.CreateRecordingAsync(actor, "idempotent-create");
        var context = Context(actor, "deezer");

        var created = await Link(recording.Recording.Id, context, "idempotent-id");
        var existing = await Link(recording.Recording.Id, context, "idempotent-id");

        Assert.Equal(TrackIdentityLinkStatus.Created, created.Status);
        Assert.Equal(TrackIdentityLinkStatus.AlreadyLinked, existing.Status);
        Assert.Equal(created.LinkId, existing.LinkId);
        await using var database = await _factory.CreateDbContextAsync();
        Assert.Single(await database.ProviderTrackIdentities.ToListAsync());
    }

    [Fact]
    public async Task AccountScopedIdentity_IsPreferredButCannotCrossUserScope()
    {
        var account = await SeedUserAccount("spotify", _tenantA, _userA);
        var actorA = Actor(_tenantA, _userA);
        var catalogContext = Context(actorA, "spotify");
        var accountContext = Context(actorA, "spotify", account);
        var catalogRecording = await _service.CreateRecordingAsync(actorA, "catalog-recording");
        var accountRecording = await _service.CreateRecordingAsync(actorA, "account-recording");
        await Link(catalogRecording.Recording.Id, catalogContext, "overlapping-id");
        await Link(
            accountRecording.Recording.Id,
            accountContext,
            "overlapping-id",
            ProviderIdentityScope.Account);

        var catalogResolution = await _service.ResolveAsync(
            catalogContext,
            Track("spotify", "overlapping-id"));
        var accountResolution = await _service.ResolveAsync(
            accountContext,
            Track("spotify", "overlapping-id"));

        Assert.Equal(catalogRecording.Recording.Id, catalogResolution!.CanonicalRecordingId);
        Assert.Equal(accountRecording.Recording.Id, accountResolution!.CanonicalRecordingId);

        var forgedSnapshot = new ProviderAccountContext(
            account.Id,
            account.ProviderId,
            ProviderAccountScope.User,
            account.Revision,
            tenantId: _tenantA,
            ownerUserId: _userB);
        var forged = Context(Actor(_tenantA, _userB), "spotify", forgedSnapshot);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ResolveAsync(forged, Track("spotify", "overlapping-id")));
    }

    [Fact]
    public async Task SameExternalId_IsIsolatedByTenant()
    {
        var actorA = Actor(_tenantA, _userA);
        var actorB = Actor(_tenantB, _userOtherTenant);
        var recordingA = await _service.CreateRecordingAsync(actorA, "tenant-a-recording");
        var recordingB = await _service.CreateRecordingAsync(actorB, "tenant-b-recording");
        var contextA = Context(actorA, "qobuz");
        var contextB = Context(actorB, "qobuz");
        await Link(recordingA.Recording.Id, contextA, "shared-provider-id");
        await Link(recordingB.Recording.Id, contextB, "shared-provider-id");

        var resolvedA = await _service.ResolveAsync(contextA, Track("qobuz", "shared-provider-id"));
        var resolvedB = await _service.ResolveAsync(contextB, Track("qobuz", "shared-provider-id"));

        Assert.Equal(recordingA.Recording.Id, resolvedA!.CanonicalRecordingId);
        Assert.Equal(recordingB.Recording.Id, resolvedB!.CanonicalRecordingId);
        Assert.NotEqual(resolvedA.CanonicalRecordingId, resolvedB.CanonicalRecordingId);
    }

    [Fact]
    public async Task CrossTenantCanonicalForeignKey_IsRejectedByDatabase()
    {
        var actor = Actor(_tenantA, _userA);
        var recording = await _service.CreateRecordingAsync(actor, "foreign-key-recording");
        await using var context = await _factory.CreateDbContextAsync();
        context.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantB,
            CanonicalRecordingId = recording.Recording.Id,
            ProviderId = "spotify",
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = "cross-tenant-id",
            ExternalIdHash = Hash("cross-tenant-id"),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            VerifiedAt = _clock.UtcNow,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task StoredHashCollision_IsRejectedInsteadOfAccepted()
    {
        var actor = Actor(_tenantA, _userA);
        var recording = await _service.CreateRecordingAsync(actor, "collision-recording");
        var requested = "requested-external-id";
        await using (var context = await _factory.CreateDbContextAsync())
        {
            context.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantA,
                CanonicalRecordingId = recording.Recording.Id,
                ProviderId = "spotify",
                ResourceKind = ProviderResourceKind.Track,
                CatalogNamespace = "default",
                Scope = ProviderIdentityScope.Catalog,
                ExternalId = "different-value-with-forced-hash",
                ExternalIdHash = Hash(requested),
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "fixture",
                DecisionVersion = 1,
                VerifiedAt = _clock.UtcNow,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var execution = Context(actor, "spotify");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ResolveAsync(execution, Track("spotify", requested)));
    }

    [Fact]
    public async Task MultipleTargetIdsInSameScope_AreReportedAsAmbiguous()
    {
        var actor = Actor(_tenantA, _userA);
        var recording = await _service.CreateRecordingAsync(actor, "ambiguous-target");
        var spotify = Context(actor, "spotify");
        var deezer = Context(actor, "deezer");
        await Link(recording.Recording.Id, spotify, "source-id");
        await Link(recording.Recording.Id, deezer, "target-one");
        await Link(recording.Recording.Id, deezer, "target-two");

        var translated = await _service.TranslateAsync(
            spotify,
            Track("spotify", "source-id"),
            deezer,
            new ProviderTrackIdentityTarget("deezer"));

        Assert.Equal(TrackIdentityTranslationStatus.TargetAmbiguous, translated.Status);
        Assert.Null(translated.Target);
    }

    [Fact]
    public async Task CanonicalSignals_AreNormalizedReusedAndNeverSilentlyMerged()
    {
        var actor = Actor(_tenantA, _userA);
        var mbid = Guid.NewGuid();
        var first = await _service.CreateRecordingAsync(
            actor,
            "signals-first",
            "US-RC1-76-07839",
            mbid.ToString("B").ToUpperInvariant());
        var reused = await _service.CreateRecordingAsync(
            actor,
            "signals-reused",
            "usrc17607839",
            mbid.ToString("D"));
        Assert.False(reused.Created);
        Assert.Equal(first.Recording.Id, reused.Recording.Id);
        Assert.Equal("USRC17607839", reused.Recording.Isrc);
        Assert.Equal(mbid.ToString("D"), reused.Recording.MusicBrainzRecordingId);

        var isrcOnly = await _service.CreateRecordingAsync(
            actor,
            "signals-isrc-only",
            "GBAYE6800011");
        var otherMbid = Guid.NewGuid();
        var mbidOnly = await _service.CreateRecordingAsync(
            actor,
            "signals-mbid-only",
            musicBrainzRecordingId: otherMbid.ToString());
        Assert.NotEqual(isrcOnly.Recording.Id, mbidOnly.Recording.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateRecordingAsync(
            actor,
            "signals-conflict",
            "GBAYE6800011",
            otherMbid.ToString()));
    }

    [Fact]
    public async Task AutomatedJob_CannotCreatePinnedIdentity()
    {
        var creator = Actor(_tenantA, _userA);
        var recording = await _service.CreateRecordingAsync(creator, "pin-recording");
        var jobActor = new ProviderActorContext(
            _tenantA,
            ProviderActorKind.SystemJob,
            userId: null,
            durableJobId: Guid.CreateVersion7());
        var jobContext = Context(jobActor, "spotify");
        var request = new TrackIdentityLinkRequest(
            recording.Recording.Id,
            Track("spotify", "pin-id"),
            ProviderIdentityScope.Catalog,
            ProviderIdentityVerification.Pinned,
            "manual",
            1);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.LinkAsync(jobContext, request));
    }

    [Fact]
    public async Task DurableStorageOutage_BlocksIdentityReadsAndWrites()
    {
        var actor = Actor(_tenantA, _userA);
        _storageState.Set(DurableStorageReadiness.Unavailable, errorCode: "fixture");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateRecordingAsync(actor, "storage-down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ResolveAsync(Context(actor, "spotify"), Track("spotify", "track")));
    }

    private async Task<TrackIdentityLinkResult> Link(
        Guid canonicalRecordingId,
        ProviderExecutionContext context,
        string externalId,
        ProviderIdentityScope scope = ProviderIdentityScope.Catalog,
        string? catalog = null) => await _service.LinkAsync(
        context,
        new TrackIdentityLinkRequest(
            canonicalRecordingId,
            Track(context.ProviderId, externalId, catalog),
            scope,
            ProviderIdentityVerification.Verified,
            "exact-provider-id",
            1));

    private async Task<ProviderAccountRecord> SeedUserAccount(
        string providerId,
        Guid tenantId,
        Guid userId)
    {
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderId = providerId,
            DisplayName = $"{providerId} personal",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await using var context = await _factory.CreateDbContextAsync();
        context.ProviderAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private ProviderExecutionContext Context(
        ProviderActorContext actor,
        string providerId,
        ProviderAccountRecord? account = null) => Context(
        actor,
        providerId,
        account == null
            ? null
            : new ProviderAccountContext(
                account.Id,
                account.ProviderId,
                account.Scope,
                account.Revision,
                enabled: account.Enabled,
                tenantId: account.TenantId,
                ownerUserId: account.OwnerUserId,
                libraryScopeId: account.LibraryScopeId));

    private ProviderExecutionContext Context(
        ProviderActorContext actor,
        string providerId,
        ProviderAccountContext? account)
    {
        var library = account?.Scope == ProviderAccountScope.Library
            ? new ProviderLibraryContext(actor.TenantId, account.LibraryScopeId!)
            : null;
        return new ProviderExecutionContext(
            actor,
            providerId,
            account,
            library,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(
                    ProviderAudioQuality.Any,
                    ProviderAudioQuality.HighResolution,
                    allowTranscode: true),
                ProviderExplicitContentPolicy.Allow,
                allowFallback: true,
                allowSharedAccount: true,
                allowManagedDownloads: false,
                allowedProviderIds: [providerId]),
            operationId: $"identity-{providerId}",
            correlationId: $"correlation-{providerId}",
            deadline: _clock.UtcNow.AddMinutes(5),
            cancellationToken: CancellationToken.None);
    }

    private static ProviderActorContext Actor(Guid tenantId, Guid userId) => new(
        tenantId,
        ProviderActorKind.User,
        userId,
        new ProviderBackendPrincipal("jellyfin", "fixture", userId.ToString("N")));

    private static ProviderExternalResourceId Track(
        string providerId,
        string externalId,
        string? catalog = null) => new(
        providerId,
        ProviderResourceKind.Track,
        externalId,
        catalog);

    private static PlatformUserRecord User(
        Guid id,
        Guid tenantId,
        string name,
        DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = tenantId,
            DisplayName = name,
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
