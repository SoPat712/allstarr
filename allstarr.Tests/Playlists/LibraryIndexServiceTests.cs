using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class LibraryIndexServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private LibraryIndexService _service = null!;
    private Guid _tenantId;
    private Guid _userA;
    private Guid _userB;
    private Guid _identityA;
    private Guid _identityB;
    private FakeClock _clock = null!;
    private TrackIdentityService _identities = null!;
    private readonly Mock<IMusicBrainzCatalogRefreshQueue> _catalogQueue = new(MockBehavior.Strict);
    private readonly Mock<IBackendLibraryAccessResolver> _libraryAccess = new(MockBehavior.Strict);

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        var options = new StorageOptions
        {
            DataDirectory = _database.StorageOptions.DataDirectory,
            DatabaseFileName = _database.StorageOptions.DatabaseFileName
        };
        _factory = new TestDbContextFactory(_database.Options);
        var now = new DateTimeOffset(2026, 7, 12, 2, 0, 0, TimeSpan.Zero);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            _tenantId = Guid.CreateVersion7();
            _userA = Guid.CreateVersion7();
            _userB = Guid.CreateVersion7();
            _identityA = Guid.CreateVersion7();
            _identityB = Guid.CreateVersion7();
            db.Tenants.Add(new TenantRecord
            {
                Id = _tenantId,
                Slug = "fixture",
                Name = "Fixture",
                CreatedAt = now
            });
            db.Users.AddRange(User(_userA, "A", now), User(_userB, "B", now));
            db.BackendIdentities.AddRange(
                Identity(_identityA, _userA, "principal-a", now),
                Identity(_identityB, _userB, "principal-b", now));
            await db.SaveChangesAsync();
        }

        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        _clock = new FakeClock(now);
        _identities = new TrackIdentityService(_factory, state, _clock, _catalogQueue.Object);
        _libraryAccess.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext context, CancellationToken _) =>
                new BackendLibraryAccess(true, context.Principal!.UserId == _userA ? ["music"] : []));
        _service = new LibraryIndexService(_factory, state, _clock, _identities, _libraryAccess.Object);
    }

    [Fact]
    public async Task Upsert_IsScopedIdempotentAndReturnsMatchCandidatesWithoutMediaPayloads()
    {
        var context = Context(_userA, "principal-a", "music");
        var input = Input() with
        {
            ProviderTrackIds = new Dictionary<string, string>
            {
                ["spotify"] = "spotify-track",
                ["deezer"] = "deezer-track"
            }
        };

        var created = await _service.UpsertAsync(context, input);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(15);
        var updated = await _service.UpsertAsync(context, input with { Title = "Updated title" });
        var listed = await _service.ListAsync(context, "music");
        var candidates = await _service.GetMatchCandidatesAsync(context, "music");

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(_clock.UtcNow, updated.IndexedAt);
        var item = Assert.Single(listed);
        Assert.Equal("Updated title", item.Title);
        Assert.Equal("/media/music/artist/song.flac", item.FilePath);
        Assert.Equal("deezer-track", item.ProviderTrackIds["deezer"]);
        Assert.Equal("jellyfin", item.DurationProvenance);
        Assert.Equal(input.DurationRetrievedAt, item.DurationRetrievedAt);
        var candidate = Assert.Single(candidates);
        Assert.Equal("local-item", candidate.BackendItemId);
        Assert.Equal(240_000, candidate.DurationMilliseconds);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.LibraryTracks.ToListAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync());
        Assert.DoesNotContain(
            db.Model.FindEntityType(typeof(LibraryTrackRecord))!.GetProperties(),
            property => property.ClrType == typeof(byte[]));
    }

    [Fact]
    public async Task IndexReads_RequireBackendLibraryAccessAndLinkedIdentity()
    {
        await _service.UpsertAsync(Context(_userA, "principal-a", "music"), Input());

        Assert.Empty(await _service.ListAsync(Context(_userB, "principal-b", "music"), "music"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ListAsync(Context(_userA, "principal-a", "other"), "music"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ListAsync(UnlinkedContext(), "music"));
    }

    [Fact]
    public async Task IndexReads_UseBackendAccessInsteadOfIndexProvenanceAndFailClosed()
    {
        var indexed = await _service.UpsertAsync(Context(_userA, "principal-a", "music"), Input());
        var viewer = Context(_userB, "principal-b", "music");
        _libraryAccess.Setup(service => service.ResolveAsync(viewer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        Assert.Equal(indexed.Id, Assert.Single(await _service.ListAsync(viewer, "music")).Id);

        _libraryAccess.Setup(service => service.ResolveAsync(viewer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendLibraryAccess.Unavailable);
        Assert.Empty(await _service.ListAsync(viewer, "music"));
        Assert.Empty(await _service.GetMatchCandidatesAsync(viewer, "music"));
    }

    [Fact]
    public async Task CanonicalTrack_ProjectsOneBackendAliasAcrossAuthorizedUsers()
    {
        var canonicalId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = canonicalId,
                TenantId = _tenantId,
                CreatedByUserId = _userA,
                Title = "Song",
                IsProvisional = true,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var input = Input() with
        {
            CanonicalRecordingId = canonicalId,
            AcceptedDecisionVersion = 1
        };
        await _service.UpsertAsync(Context(_userA, "principal-a", "music"), input);
        await _service.UpsertAsync(Context(_userB, "principal-b", "music"), input);
        var rescanned = await _service.UpsertAsync(Context(_userA, "principal-a", "music"), Input() with
        {
            MusicBrainzRecordingId = "16ba7915-2acf-42b2-8c87-ed67090dca91"
        });
        Assert.Equal(canonicalId, rescanned.CanonicalRecordingId);

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verification.LibraryTracks.CountAsync());
        var alias = Assert.Single(await verification.CanonicalCatalogAliases.ToListAsync());
        Assert.Equal(
            CanonicalCatalogKeys.NativeTrackNamespace("jellyfin", "backend"),
            alias.Namespace);
        Assert.Equal("local-item", alias.ExternalId);
        Assert.Equal(canonicalId, alias.CanonicalEntityId);
        Assert.All(await verification.LibraryTracks.ToListAsync(), track => Assert.Equal(1, track.AcceptedDecisionVersion));
        Assert.Null((await verification.CanonicalRecordings.FindAsync(canonicalId))!.MusicBrainzRecordingId);
        _catalogQueue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NativeAlias_RecoversAssignmentLostByLegacyRescan()
    {
        var context = Context(_userA, "principal-a", "music");
        var canonical = await _identities.CreateRecordingAsync(context.RequireActor(), "existing-native");
        var indexed = await _service.UpsertAsync(context, Input() with { CanonicalRecordingId = canonical.Recording.Id });
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.FindAsync(indexed.Id);
            track!.CanonicalRecordingId = null;
            await db.SaveChangesAsync();
        }

        var rescanned = await _service.UpsertAsync(context, Input());
        Assert.Equal(canonical.Recording.Id, rescanned.CanonicalRecordingId);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Single(await verification.CanonicalRecordings.ToListAsync());
        Assert.Single(await verification.CanonicalCatalogAliases.ToListAsync());
        _catalogQueue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NativeRecording_EnrichesProviderIdentityAndQueuesCatalogWithoutChangingNativeMetadata()
    {
        const string mbid = "16ba7915-2acf-42b2-8c87-ed67090dca91";
        var context = Context(_userA, "principal-a", "music");
        var original = await _identities.CreateRecordingAsync(context.RequireActor(), "provider-first", Input().Isrc);
        _catalogQueue.Setup(queue => queue.EnqueueRecordingAsync(
                It.Is<ProviderActorContext>(actor => actor.TenantId == _tenantId && actor.UserId == _userA),
                mbid, context.CorrelationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableJobEnqueueResult(Guid.CreateVersion7(), true));

        var indexed = await _service.UpsertAsync(context, Input() with { MusicBrainzRecordingId = mbid });
        var rescanned = await _service.UpsertAsync(context, Input() with { MusicBrainzRecordingId = mbid });
        Assert.Equal(original.Recording.Id, indexed.CanonicalRecordingId);
        Assert.Equal(indexed.CanonicalRecordingId, rescanned.CanonicalRecordingId);
        Assert.Equal(Input().BackendItemId, indexed.BackendItemId);
        Assert.Equal(Input().Title, indexed.Title);
        Assert.Equal(Input().FilePath, indexed.FilePath);
        await using var db = await _factory.CreateDbContextAsync();
        var canonical = Assert.Single(await db.CanonicalRecordings.ToListAsync());
        Assert.Equal(mbid, canonical.MusicBrainzRecordingId);
        Assert.False(canonical.IsProvisional);
        Assert.Equal(3, await db.CanonicalCatalogAliases.CountAsync());
        Assert.All(await db.CanonicalCatalogAliases.ToListAsync(), alias => Assert.Equal(canonical.Id, alias.CanonicalEntityId));
        _catalogQueue.Verify(queue => queue.EnqueueRecordingAsync(
            It.IsAny<ProviderActorContext>(), mbid, context.CorrelationId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task NativeRecording_ConflictingSignalsRemainIndexedWithoutMergingOrQueuing()
    {
        var context = Context(_userA, "principal-a", "music");
        var first = await _identities.CreateRecordingAsync(context.RequireActor(), "isrc-first", Input().Isrc);
        var secondId = Guid.CreateVersion7();
        const string mbid = "16ba7915-2acf-42b2-8c87-ed67090dca91";
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = secondId,
                TenantId = _tenantId,
                CreatedByUserId = _userA,
                MusicBrainzRecordingId = mbid,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var indexed = await _service.UpsertAsync(context, Input() with { MusicBrainzRecordingId = mbid });
        Assert.Null(indexed.CanonicalRecordingId);
        Assert.Equal(mbid, indexed.MusicBrainzRecordingId);
        Assert.Single(await _service.GetMatchCandidatesAsync(context, "music"));
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verification.CanonicalRecordings.CountAsync());
        Assert.Null((await verification.CanonicalRecordings.FindAsync(first.Recording.Id))!.MusicBrainzRecordingId);
        var audit = await verification.AuditEvents.SingleAsync(item => item.Category == "library-index");
        Assert.Contains("\"canonicalEnrichment\":\"deferred\"", audit.DetailsJson);
        _catalogQueue.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-mbid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task NativeRecording_WithoutValidMbidStillIndexes(string? mbid)
    {
        var indexed = await _service.UpsertAsync(Context(_userA, "principal-a", "music"),
            Input() with { MusicBrainzRecordingId = mbid });
        Assert.Null(indexed.CanonicalRecordingId);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.CanonicalRecordings.ToListAsync());
        Assert.Single(await db.LibraryTracks.ToListAsync());
        _catalogQueue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IndexRejectsSignedUrlsAndSecretLikeProviderIds()
    {
        var context = Context(_userA, "principal-a", "music");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpsertAsync(
            context,
            Input() with { CoverArtReference = "https://signed.example/art?token=secret" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpsertAsync(
            context,
            Input() with
            {
                ProviderTrackIds = new Dictionary<string, string>
                {
                    ["spotify"] = "track?token=secret"
                }
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpsertAsync(
            context,
            Input() with { DurationMilliseconds = null }));
        var unknown = await _service.UpsertAsync(
            context,
            Input() with
            {
                DurationMilliseconds = null,
                DurationProvenance = null,
                DurationRetrievedAt = null
            });
        Assert.Null(unknown.DurationMilliseconds);
        Assert.Null(unknown.DurationProvenance);
        Assert.Null(unknown.DurationRetrievedAt);
    }

    private ProtocolExecutionContext Context(Guid userId, string principalId, string libraryScope) => new(
        ProtocolKind.Jellyfin,
        "backend",
        principalId,
        new AllstarrPrincipal(
            _tenantId,
            userId,
            "jellyfin",
            "backend",
            principalId,
            principalId,
            IsAdministrator: false),
        "correlation",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None,
        libraryScopeId: libraryScope);

    private ProtocolExecutionContext UnlinkedContext() => new(
        ProtocolKind.Jellyfin,
        "backend",
        "unlinked",
        null,
        "correlation",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None,
        libraryScopeId: "music");

    private LibraryTrackIndexInput Input() => new(
        "music",
        "local-item",
        "/media/music/artist/song.flac",
        "Song",
        "Artist",
        "Album",
        "Artist",
        240_000,
        "jellyfin",
        new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero),
        "US-RC1-76-07839",
        null,
        null,
        null,
        null,
        null,
        null,
        "backend:art-1",
        new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));

    private PlatformUserRecord User(Guid id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = _tenantId,
        DisplayName = name,
        Status = PlatformUserStatus.Active,
        CreatedAt = now,
        UpdatedAt = now
    };

    private BackendIdentityRecord Identity(
        Guid id,
        Guid userId,
        string principalId,
        DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = _tenantId,
            UserId = userId,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            PrincipalId = principalId,
            DisplayName = principalId,
            CreatedAt = now,
            LastSeenAt = now
        };

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
