using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class LibraryIndexServiceTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private LibraryIndexService _service = null!;
    private Guid _tenantId;
    private Guid _userA;
    private Guid _userB;
    private Guid _identityA;
    private Guid _identityB;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = _database.ConnectionString
        };
        _factory = new TestDbContextFactory(_database.Options);
        var now = new DateTimeOffset(2026, 7, 12, 2, 0, 0, TimeSpan.Zero);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
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
        _service = new LibraryIndexService(_factory, state, new FakeClock(now));
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
        var updated = await _service.UpsertAsync(context, input with { Title = "Updated title" });
        var listed = await _service.ListAsync(context, "music");
        var candidates = await _service.GetMatchCandidatesAsync(context, "music");

        Assert.Equal(created.Id, updated.Id);
        var item = Assert.Single(listed);
        Assert.Equal("Updated title", item.Title);
        Assert.Equal("/media/music/artist/song.flac", item.FilePath);
        Assert.Equal("deezer-track", item.ProviderTrackIds["deezer"]);
        var candidate = Assert.Single(candidates);
        Assert.Equal("local-item", candidate.BackendItemId);
        Assert.Equal(240, candidate.DurationSeconds);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.LibraryTracks.ToListAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync());
        Assert.DoesNotContain(
            db.Model.FindEntityType(typeof(LibraryTrackRecord))!.GetProperties(),
            property => property.ClrType == typeof(byte[]));
    }

    [Fact]
    public async Task IndexReads_CannotCrossUserLibraryOrUnlinkedIdentity()
    {
        await _service.UpsertAsync(Context(_userA, "principal-a", "music"), Input());

        Assert.Empty(await _service.ListAsync(Context(_userB, "principal-b", "music"), "music"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ListAsync(Context(_userA, "principal-a", "other"), "music"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ListAsync(UnlinkedContext(), "music"));
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
