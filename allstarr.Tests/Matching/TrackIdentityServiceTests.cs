using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class TrackIdentityServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableStorageState _storageState = null!;
    private FakeClock _clock = null!;
    private TrackIdentityService _service = null!;
    private Guid _userA;
    private Guid _userB;
    private Guid _userC;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        var storage = new StorageOptions
        {
            DataDirectory = _database.StorageOptions.DataDirectory,
            DatabaseFileName = _database.StorageOptions.DatabaseFileName
        };
        _factory = new TestDbContextFactory(_database.Options);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            _userA = Guid.CreateVersion7();
            _userB = Guid.CreateVersion7();
            _userC = Guid.CreateVersion7();
            var now = new DateTimeOffset(2026, 7, 11, 14, 0, 0, TimeSpan.Zero);
            context.Users.AddRange(
                User(_userA, "User A", now),
                User(_userB, "User B", now),
                User(_userC, "User C", now));
            await context.SaveChangesAsync();
        }

        _storageState = new DurableStorageState(storage);
        _storageState.Set(DurableStorageReadiness.Ready, "fixture");
        _clock = new FakeClock(new DateTimeOffset(2026, 7, 11, 14, 0, 0, TimeSpan.Zero));
        _service = new TrackIdentityService(_factory, _storageState, _clock);
    }

    [Fact]
    public async Task DisabledCatalog_CreatesIdentityWithoutEnqueuingRemoteWork()
    {
        var queue = new Mock<IMusicBrainzCatalogRefreshQueue>(MockBehavior.Strict);
        var service = new TrackIdentityService(_factory, _storageState, _clock, queue.Object,
            Options.Create(new MusicBrainzSettings { Enabled = false }));
        var created = await service.CreateRecordingAsync(Actor(_userA), "disabled-catalog",
            musicBrainzRecordingId: "16ba7915-2acf-42b2-8c87-ed67090dca91");
        Assert.True(created.Created);
        Assert.NotNull(created.Recording.MusicBrainzRecordingId);
        queue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OneCanonicalRecording_LinksManyProvidersAndTranslatesExactly()
    {
        var actor = Actor(_userA);
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
        var aliases = await context.CanonicalCatalogAliases
            .Where(item => item.EntityKind == CanonicalCatalogEntityKind.Recording)
            .ToListAsync();
        Assert.Equal(4, aliases.Count);
        Assert.Equal(
            ["3135556", "qobuz-track-9", "spotify-track-1"],
            aliases.Where(item => item.Namespace.StartsWith("provider:", StringComparison.Ordinal))
                .Select(item => item.ExternalId)
                .Order()
                .ToArray());
        Assert.All(aliases, alias => Assert.Equal(recording.Recording.Id, alias.CanonicalEntityId));
    }

    [Fact]
    public async Task BatchResolution_PrefersAuthorizedAccountScopeWithoutLeakingOtherAccounts()
    {
        var actorA = Actor(_userA);
        var actorB = Actor(_userB);
        var accountA = await SeedUserAccount("deezer", _userA);
        var accountB = await SeedUserAccount("deezer", _userB);
        var catalogRecording = (await _service.CreateRecordingAsync(actorA, "catalog-recording")).Recording.Id;
        var accountRecording = (await _service.CreateRecordingAsync(actorA, "account-recording")).Recording.Id;
        var catalogContext = Context(actorA, "deezer");
        var contextA = Context(actorA, "deezer", accountA);
        var contextB = Context(actorB, "deezer", accountB);
        await Link(catalogRecording, catalogContext, "shared-track");
        await Link(accountRecording, contextA, "shared-track", ProviderIdentityScope.Account);

        var forA = await _service.ResolveManyAsync([
            new(contextA, Track("deezer", "shared-track")),
            new(contextA, Track("deezer", "missing-track"))]);
        var forB = await _service.ResolveManyAsync([
            new(contextB, Track("deezer", "shared-track"))]);

        Assert.Equal(accountRecording, forA[0]?.CanonicalRecordingId);
        Assert.Null(forA[1]);
        Assert.Equal(catalogRecording, forB[0]?.CanonicalRecordingId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ResolveManyAsync([
                new(contextA, Track("deezer", "shared-track")),
                new(contextB, Track("deezer", "shared-track"))]));
    }

    [Fact]
    public async Task RecordingWithMusicBrainzIdentity_QueuesIdempotentCatalogDiscovery()
    {
        const string recordingMbid = "11111111-1111-4111-8111-111111111111";
        var actor = Actor(_userA);
        var queue = new Mock<IMusicBrainzCatalogRefreshQueue>(MockBehavior.Strict);
        queue.Setup(item => item.EnqueueRecordingAsync(
                actor,
                recordingMbid,
                "catalog-discovery",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableJobEnqueueResult(Guid.CreateVersion7(), true));
        var service = new TrackIdentityService(_factory, _storageState, _clock, queue.Object);

        var first = await service.CreateRecordingAsync(
            actor, "catalog-discovery", musicBrainzRecordingId: recordingMbid);
        var repeated = await service.CreateRecordingAsync(
            actor, "catalog-discovery", musicBrainzRecordingId: recordingMbid);

        Assert.True(first.Created);
        Assert.False(repeated.Created);
        Assert.Equal(first.Recording.Id, repeated.Recording.Id);
        queue.Verify(item => item.EnqueueRecordingAsync(
            actor,
            recordingMbid,
            "catalog-discovery",
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdditionalExactSignal_EnrichesSameRecordingAndPreservesPinnedRoutes(bool isrcFirst)
    {
        const string isrc = "USRC17607839";
        const string mbid = "11111111-1111-4111-8111-111111111111";
        var actor = Actor(_userA);
        var first = await _service.CreateRecordingAsync(actor, "initial-signal",
            isrcFirst ? isrc : null, isrcFirst ? null : mbid);
        var link = await _service.LinkAsync(Context(actor, "deezer"), new(
            first.Recording.Id, Track("deezer", "pinned-track"), ProviderIdentityScope.Catalog,
            ProviderIdentityVerification.Pinned, "manual-review", 1));
        var queue = new Mock<IMusicBrainzCatalogRefreshQueue>(MockBehavior.Strict);
        queue.Setup(item => item.EnqueueRecordingAsync(actor, mbid, "enrich", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableJobEnqueueResult(Guid.CreateVersion7(), true));
        var service = new TrackIdentityService(_factory, _storageState, _clock, queue.Object);

        var enriched = await service.CreateRecordingAsync(actor, "enrich", isrc, mbid);
        var repeated = await service.CreateRecordingAsync(actor, "enrich", isrc, mbid);

        Assert.False(enriched.Created);
        Assert.Equal(first.Recording.Id, enriched.Recording.Id);
        Assert.Equal(isrc, enriched.Recording.Isrc);
        Assert.Equal(mbid, enriched.Recording.MusicBrainzRecordingId);
        Assert.Equal(first.Recording.Revision + 1, enriched.Recording.Revision);
        Assert.Equal(enriched.Recording, repeated.Recording);
        queue.Verify(item => item.EnqueueRecordingAsync(actor, mbid, "enrich", It.IsAny<CancellationToken>()), Times.Exactly(2));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False((await db.CanonicalRecordings.SingleAsync()).IsProvisional);
        var pinned = await db.ProviderTrackIdentities.SingleAsync();
        Assert.Equal(link.LinkId, pinned.Id);
        Assert.Equal(first.Recording.Id, pinned.CanonicalRecordingId);
        Assert.Equal(ProviderIdentityVerification.Pinned, pinned.Verification);
        Assert.Equal(3, await db.CanonicalCatalogAliases.CountAsync());
        Assert.Single(await db.AuditEvents.Where(item => item.Outcome == "enriched").ToArrayAsync());
    }

    [Fact]
    public async Task AdditionalExactSignal_AliasConflictDoesNotPartiallyEnrichRecording()
    {
        const string isrc = "USRC17607839";
        const string mbid = "11111111-1111-4111-8111-111111111111";
        var actor = Actor(_userA);
        var first = await _service.CreateRecordingAsync(actor, "first", isrc);
        var other = await _service.CreateRecordingAsync(actor, "other");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.CanonicalCatalogAliases.Add(new CanonicalCatalogAliasRecord
            {
                Id = Guid.CreateVersion7(),
                EntityKind = CanonicalCatalogEntityKind.Recording,
                CanonicalEntityId = other.Recording.Id,
                Namespace = "musicbrainz",
                ExternalId = mbid,
                ExternalIdHash = CanonicalCatalogKeys.Hash(mbid),
                CreatedAt = _clock.UtcNow,
                LastSeenAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<CanonicalCatalogAliasConflictException>(() =>
            _service.CreateRecordingAsync(actor, "conflicting-alias", isrc, mbid));

        await using var verification = await _factory.CreateDbContextAsync();
        var unchanged = await verification.CanonicalRecordings.SingleAsync(item => item.Id == first.Recording.Id);
        Assert.Null(unchanged.MusicBrainzRecordingId);
        Assert.True(unchanged.IsProvisional);
        Assert.Equal(first.Recording.Revision, unchanged.Revision);
        Assert.Empty(await verification.AuditEvents.Where(item => item.Outcome == "enriched").ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentExactSignals_CoalesceCompatibleWritesAndRejectConflicts(bool existing, bool conflicting)
    {
        const string isrc = "USRC17607839";
        const string mbid = "11111111-1111-4111-8111-111111111111";
        var actor = Actor(_userA);
        var original = existing ? await _service.CreateRecordingAsync(actor, "seed", isrc) : null;
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(new ConcurrentSaveGate()).Options;
        var service = new TrackIdentityService(new TestDbContextFactory(options), _storageState, _clock);
        async Task<CanonicalRecordingCreationResult?> Create(string recordingMbid)
        {
            try { return await service.CreateRecordingAsync(actor, "concurrent", isrc, recordingMbid); }
            catch (InvalidOperationException) { return null; }
        }

        var results = await Task.WhenAll(Create(mbid), Create(conflicting
            ? "22222222-2222-4222-8222-222222222222" : mbid));

        var succeeded = results.OfType<CanonicalRecordingCreationResult>().ToArray();
        Assert.Equal(conflicting ? 1 : 2, succeeded.Length);
        await using var db = await _factory.CreateDbContextAsync();
        var recording = await db.CanonicalRecordings.SingleAsync();
        Assert.All(succeeded, item => Assert.Equal(recording.Id, item.Recording.Id));
        if (original != null) Assert.Equal(original.Recording.Id, recording.Id);
        Assert.Equal(2, await db.CanonicalCatalogAliases.CountAsync());
        Assert.All(await db.CanonicalCatalogAliases.ToArrayAsync(), item => Assert.Equal(recording.Id, item.CanonicalEntityId));
        Assert.Equal(existing ? 1 : 0, await db.AuditEvents.CountAsync(item => item.Outcome == "enriched"));
    }

    private sealed class ConcurrentSaveGate : SaveChangesInterceptor
    {
        private int _arrivals;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var arrival = Interlocked.Increment(ref _arrivals);
            if (arrival == 2) _ready.TrySetResult();
            if (arrival <= 2) await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    [Fact]
    public async Task MissingVerifiedLink_RemainsUnresolvedAndNeverGuesses()
    {
        var actor = Actor(_userA);
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
        var actor = Actor(_userA);
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
        var actor = Actor(_userA);
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
        var account = await SeedUserAccount("spotify", _userA);
        var actorA = Actor(_userA);
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

        await using (var database = await _factory.CreateDbContextAsync())
        {
            var aliases = await database.CanonicalCatalogAliases
                .Where(item => item.ExternalId == "overlapping-id")
                .OrderBy(item => item.Namespace)
                .ToListAsync();
            Assert.Equal(2, aliases.Count);
            Assert.Equal(2, aliases.Select(item => item.Namespace).Distinct().Count());
            Assert.Contains(aliases, item => item.CanonicalEntityId == catalogRecording.Recording.Id);
            Assert.Contains(aliases, item => item.CanonicalEntityId == accountRecording.Recording.Id);
        }

        var forgedSnapshot = new ProviderAccountContext(
            account.Id,
            account.ProviderId,
            ProviderAccountScope.Personal,
            account.Revision,
            ownerUserId: _userB);
        var forged = Context(Actor(_userB), "spotify", forgedSnapshot);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ResolveAsync(forged, Track("spotify", "overlapping-id")));
    }

    [Fact]
    public async Task ExactCatalogIdentity_IsGlobalAcrossUsers_WhileAccountAliasesRemainPrivate()
    {
        var actorA = Actor(_userA);
        var actorC = Actor(_userC);
        var first = await _service.CreateRecordingAsync(actorA, "user-a-recording", "USRC17607839");
        var reused = await _service.CreateRecordingAsync(actorC, "user-c-recording", "USRC17607839");
        Assert.False(reused.Created);
        Assert.Equal(first.Recording.Id, reused.Recording.Id);
        await Link(first.Recording.Id, Context(actorA, "qobuz"), "shared-provider-id");
        Assert.Equal(first.Recording.Id,
            (await _service.ResolveAsync(
                Context(actorC, "qobuz"),
                Track("qobuz", "shared-provider-id")))!.CanonicalRecordingId);

        var accountA = await SeedUserAccount("qobuz", _userA);
        var accountC = await SeedUserAccount("qobuz", _userC);
        var privateA = await _service.CreateRecordingAsync(actorA, "user-a-private");
        var privateC = await _service.CreateRecordingAsync(actorC, "user-c-private");
        var contextA = Context(actorA, "qobuz", accountA);
        var contextC = Context(actorC, "qobuz", accountC);
        await Link(privateA.Recording.Id, contextA, "private-provider-id", ProviderIdentityScope.Account);
        await Link(privateC.Recording.Id, contextC, "private-provider-id", ProviderIdentityScope.Account);

        Assert.Equal(privateA.Recording.Id,
            (await _service.ResolveAsync(contextA, Track("qobuz", "private-provider-id")))!.CanonicalRecordingId);
        Assert.Equal(privateC.Recording.Id,
            (await _service.ResolveAsync(contextC, Track("qobuz", "private-provider-id")))!.CanonicalRecordingId);
        Assert.Throws<UnauthorizedAccessException>(() =>
            Context(actorC, "qobuz", new ProviderAccountContext(
                accountA.Id,
                accountA.ProviderId,
                accountA.Scope,
                accountA.Revision,
                ownerUserId: accountA.OwnerUserId)));
    }

    [Fact]
    public async Task MissingCanonicalRecordingForeignKey_IsRejectedByDatabase()
    {
        await using var context = await _factory.CreateDbContextAsync();
        context.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            CanonicalRecordingId = Guid.CreateVersion7(),
            ProviderId = "spotify",
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = "missing-canonical-id",
            ExternalIdHash = Hash("missing-canonical-id"),
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
        var actor = Actor(_userA);
        var recording = await _service.CreateRecordingAsync(actor, "collision-recording");
        var requested = "requested-external-id";
        await using (var context = await _factory.CreateDbContextAsync())
        {
            context.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
            {
                Id = Guid.CreateVersion7(),
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
        var actor = Actor(_userA);
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
        var actor = Actor(_userA);
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

        await using (var database = await _factory.CreateDbContextAsync())
        {
            var aliases = await database.CanonicalCatalogAliases
                .Where(item => item.CanonicalEntityId == first.Recording.Id)
                .OrderBy(item => item.Namespace)
                .ToListAsync();
            Assert.Collection(
                aliases,
                item =>
                {
                    Assert.Equal("isrc", item.Namespace);
                    Assert.Equal("USRC17607839", item.ExternalId);
                },
                item =>
                {
                    Assert.Equal("musicbrainz", item.Namespace);
                    Assert.Equal(mbid.ToString("D"), item.ExternalId);
                });
            Assert.False(await database.CanonicalRecordings
                .Where(item => item.Id == first.Recording.Id)
                .Select(item => item.IsProvisional)
                .SingleAsync());
        }

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
        await using (var database = await _factory.CreateDbContextAsync())
        {
            Assert.True(await database.CanonicalRecordings
                .Where(item => item.Id == isrcOnly.Recording.Id)
                .Select(item => item.IsProvisional)
                .SingleAsync());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateRecordingAsync(
            actor,
            "signals-conflict",
            "GBAYE6800011",
            otherMbid.ToString()));
    }

    [Fact]
    public async Task AutomatedJob_CannotCreatePinnedIdentity()
    {
        var creator = Actor(_userA);
        var recording = await _service.CreateRecordingAsync(creator, "pin-recording");
        var jobActor = new ProviderActorContext(
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
        var actor = Actor(_userA);
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
        Guid userId)
    {
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = userId,
            ProviderId = providerId,
            DisplayName = $"{providerId} personal",
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
                ownerUserId: account.OwnerUserId));

    private ProviderExecutionContext Context(
        ProviderActorContext actor,
        string providerId,
        ProviderAccountContext? account)
    {
        return new ProviderExecutionContext(
            actor,
            providerId,
            account,
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

    private static ProviderActorContext Actor(Guid userId) => new(
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

    private static UserRecord User(
        Guid id,
        string name,
        DateTimeOffset now) => new()
        {
            Id = id,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = id.ToString("N"),
            DisplayName = name,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
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
