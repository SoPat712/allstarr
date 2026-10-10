using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
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
    private Guid _userA;
    private Guid _userB;
    private FakeClock _clock = null!;
    private TrackIdentityService _identities = null!;
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
            _userA = Guid.CreateVersion7();
            _userB = Guid.CreateVersion7();
            db.Users.AddRange(User(_userA, "A", now), User(_userB, "B", now));
            await db.SaveChangesAsync();
        }

        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        _clock = new FakeClock(now);
        _identities = new TrackIdentityService(_factory, state, _clock);
        _libraryAccess.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext context, CancellationToken _) =>
                new BackendLibraryAccess(true, context.Principal!.UserId == _userA ? ["music"] : []));
        _service = new LibraryIndexService(_factory, state, _clock, _identities, _libraryAccess.Object);
    }

    [Fact]
    public async Task Upsert_IsScopedIdempotentAndReturnsMatchCandidatesWithoutMediaPayloads()
    {
        var context = Context(_userA, "principal-a");
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
        await _service.UpsertAsync(Context(_userA, "principal-a"), Input());

        Assert.Empty(await _service.ListAsync(Context(_userB, "principal-b"), "music"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.UpsertAsync(Context(_userA, "forged-principal"), Input()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ListAsync(UnlinkedContext(), "music"));
    }

    [Fact]
    public async Task IndexReads_UseBackendAccessInsteadOfIndexProvenanceAndFailClosed()
    {
        var indexed = await _service.UpsertAsync(Context(_userA, "principal-a"), Input());
        var viewer = Context(_userB, "principal-b");
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
        await _service.UpsertAsync(Context(_userA, "principal-a"), input);
        _libraryAccess.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        await _service.UpsertAsync(Context(_userB, "principal-b"), input);
        var rescanned = await _service.UpsertAsync(Context(_userA, "principal-a"), Input() with
        {
            MusicBrainzRecordingId = "16ba7915-2acf-42b2-8c87-ed67090dca91"
        });
        Assert.Equal(canonicalId, rescanned.CanonicalRecordingId);

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verification.LibraryTracks.CountAsync());
        Assert.Empty(await verification.RecordingIdentifiers.ToListAsync());
        Assert.All(await verification.LibraryTracks.ToListAsync(), track => Assert.Equal(1, track.AcceptedDecisionVersion));
        Assert.True((await verification.CanonicalRecordings.FindAsync(canonicalId))!.IsProvisional);
    }

    [Fact]
    public async Task LostAssignment_IsNotRecoveredWithoutExactSignals()
    {
        var context = Context(_userA, "principal-a");
        var canonical = await _identities.CreateRecordingAsync(context.RequireActor(), "existing-native");
        var indexed = await _service.UpsertAsync(context, Input() with { CanonicalRecordingId = canonical.Recording.Id });
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.FindAsync(indexed.Id);
            track!.CanonicalRecordingId = null;
            await db.SaveChangesAsync();
        }

        var rescanned = await _service.UpsertAsync(context, Input());
        Assert.Null(rescanned.CanonicalRecordingId);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Single(await verification.CanonicalRecordings.ToListAsync());
        Assert.Empty(await verification.RecordingIdentifiers.ToListAsync());
    }

    [Fact]
    public async Task NativeRecording_EnrichesProviderIdentityWithoutChangingNativeMetadata()
    {
        const string mbid = "16ba7915-2acf-42b2-8c87-ed67090dca91";
        var context = Context(_userA, "principal-a");
        var original = await _identities.CreateRecordingAsync(context.RequireActor(), "provider-first", Input().Isrc);

        var indexed = await _service.UpsertAsync(context, Input() with { MusicBrainzRecordingId = mbid });
        var rescanned = await _service.UpsertAsync(context, Input() with { MusicBrainzRecordingId = mbid });
        Assert.Equal(original.Recording.Id, indexed.CanonicalRecordingId);
        Assert.Equal(indexed.CanonicalRecordingId, rescanned.CanonicalRecordingId);
        Assert.Equal(Input().BackendItemId, indexed.BackendItemId);
        Assert.Equal(Input().Title, indexed.Title);
        Assert.Equal(Input().FilePath, indexed.FilePath);
        await using var db = await _factory.CreateDbContextAsync();
        var canonical = Assert.Single(await db.CanonicalRecordings.Include(item => item.Identifiers).ToListAsync());
        Assert.Equal(mbid, canonical.Identifiers.Single(item => item.Kind == RecordingIdentifierKinds.MusicBrainz).Value);
        Assert.False(canonical.IsProvisional);
        Assert.Equal(2, await db.RecordingIdentifiers.CountAsync());
        Assert.All(await db.RecordingIdentifiers.ToListAsync(), item => Assert.Equal(canonical.Id, item.RecordingId));
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task NativeRecording_ConflictingSignalsRemainIndexedWithoutMergingOrQueuing()
    {
        var context = Context(_userA, "principal-a");
        var first = await _identities.CreateRecordingAsync(context.RequireActor(), "isrc-first", Input().Isrc);
        var secondId = Guid.CreateVersion7();
        const string mbid = "16ba7915-2acf-42b2-8c87-ed67090dca91";
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = secondId,
                CreatedByUserId = _userA,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                Identifiers =
                [
                    new RecordingIdentifierRecord
                    {
                        Id = Guid.CreateVersion7(),
                        RecordingId = secondId,
                        Kind = RecordingIdentifierKinds.MusicBrainz,
                        Value = mbid,
                        Source = "fixture",
                        CreatedAt = _clock.UtcNow
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var indexed = await _service.UpsertAsync(context, Input() with { MusicBrainzRecordingId = mbid });
        Assert.Null(indexed.CanonicalRecordingId);
        Assert.Equal(mbid, indexed.MusicBrainzRecordingId);
        Assert.Single(await _service.GetMatchCandidatesAsync(context, "music"));
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verification.CanonicalRecordings.CountAsync());
        Assert.False(await verification.RecordingIdentifiers.AnyAsync(item =>
            item.RecordingId == first.Recording.Id && item.Kind == RecordingIdentifierKinds.MusicBrainz));
        var audit = await verification.AuditEvents.SingleAsync(item => item.Category == "library-index");
        Assert.Contains("\"canonicalEnrichment\":\"deferred\"", audit.DetailsJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-mbid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task NativeRecording_WithoutValidMbidStillIndexes(string? mbid)
    {
        var indexed = await _service.UpsertAsync(Context(_userA, "principal-a"),
            Input() with { MusicBrainzRecordingId = mbid });
        Assert.Null(indexed.CanonicalRecordingId);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.CanonicalRecordings.ToListAsync());
        Assert.Single(await db.LibraryTracks.ToListAsync());
    }

    [Fact]
    public async Task IndexRejectsSignedUrlsAndSecretLikeProviderIds()
    {
        var context = Context(_userA, "principal-a");

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

    private ProtocolExecutionContext Context(Guid userId, string principalId) => new(
        ProtocolKind.Jellyfin,
        "backend",
        principalId,
        new AllstarrPrincipal(
            userId,
            "jellyfin",
            "backend",
            principalId,
            principalId,
            IsAdministrator: false),
        "correlation",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);

    private ProtocolExecutionContext UnlinkedContext() => new(
        ProtocolKind.Jellyfin,
        "backend",
        "unlinked",
        null,
        "correlation",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);

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

    private UserRecord User(Guid id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        DisplayName = name,
        Enabled = true,
        BackendType = "jellyfin",
        BackendInstanceId = "backend",
        BackendPrincipalId = "principal-" + name.ToLowerInvariant(),
        CreatedAt = now,
        UpdatedAt = now,
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
