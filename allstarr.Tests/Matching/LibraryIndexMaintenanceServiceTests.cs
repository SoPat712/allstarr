using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class LibraryIndexMaintenanceServiceTests : IAsyncLifetime
{
    private readonly DateTimeOffset _now = new(2026, 8, 26, 18, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock;
    private SqliteTestDatabase _database = null!;
    private TestFactory _factory = null!;

    public LibraryIndexMaintenanceServiceTests() => _clock = new TestClock(_now);

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        _factory = new TestFactory(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task EnqueueStaleIndexes_UsesNewestExactSubsonicCredential()
    {
        var userId = Guid.CreateVersion7();
        var jellyfinIdentity = Identity(Guid.CreateVersion7(), "jellyfin", "jellyfin-user");
        var subsonicIdentity = Identity(userId, "subsonic", "subsonic-user");
        var olderCredential = Credential(subsonicIdentity.Id, _now.AddMinutes(-5));
        var newestCredential = Credential(subsonicIdentity.Id, _now);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.AddRange(jellyfinIdentity, subsonicIdentity);
            db.SecretReferences.AddRange(olderCredential, newestCredential);
            db.LibraryTracks.Add(Track(jellyfinIdentity, _now));
            await db.SaveChangesAsync();
        }

        var service = Service();
        Assert.Equal(1, await service.EnqueueStaleIndexesAsync(default));

        await using var verification = await _factory.CreateDbContextAsync();
        var job = Assert.Single(await verification.Jobs.AsNoTracking().ToListAsync());
        var payload = JsonSerializer.Deserialize<LibraryIndexJobPayload>(job.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(newestCredential.Id, payload.CredentialReferenceId);
        Assert.Equal(subsonicIdentity.BackendInstanceId, payload.BackendInstanceId);
        Assert.Equal(subsonicIdentity.BackendPrincipalId, payload.BackendPrincipalId);
        Assert.DoesNotContain("username", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnqueueStaleIndexes_SkipsRevokedOrWrongIdentityCredentials()
    {
        var userId = Guid.CreateVersion7();
        var jellyfinIdentity = Identity(Guid.CreateVersion7(), "jellyfin", "jellyfin-user");
        var subsonicIdentity = Identity(userId, "subsonic", "subsonic-user");
        var wrongIdentityCredential = Credential(jellyfinIdentity.Id, _now);
        var revokedCredential = Credential(subsonicIdentity.Id, _now);
        revokedCredential.RevokedAt = _now;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.AddRange(jellyfinIdentity, subsonicIdentity);
            db.SecretReferences.AddRange(wrongIdentityCredential, revokedCredential);
            db.LibraryTracks.Add(Track(jellyfinIdentity, _now));
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await Service().EnqueueStaleIndexesAsync(default));
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.Jobs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task JobHandler_RejectsCredentialFromAnotherBackendIdentity()
    {
        var userId = Guid.CreateVersion7();
        var requestedIdentity = Identity(userId, "subsonic", "requested-user");
        var otherIdentity = Identity(Guid.CreateVersion7(), "subsonic", "other-user");
        var credential = Credential(otherIdentity.Id, _now);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.AddRange(requestedIdentity, otherIdentity);
            db.SecretReferences.Add(credential);
            await db.SaveChangesAsync();
        }

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new LibraryIndexJobPayload(
            "music", requestedIdentity.BackendInstanceId, requestedIdentity.BackendPrincipalId, credential.Id)));
        using var policy = JsonDocument.Parse("{}");
        var claim = new DurableJobClaim(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "library.index",
            payload.RootElement.Clone(), userId, null, null, policy.RootElement.Clone(),
            "credential-scope-test", "worker", _now.AddMinutes(5));
        var handler = new LibraryIndexJobHandler(_factory,
            new BackendLibraryCatalogScannerResolver([]), _clock);
        await using var services = new ServiceCollection().BuildServiceProvider();

        var result = await handler.ExecuteAsync(new DurableJobExecutionContext(claim, services), default);

        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
        Assert.Equal("library_index_credential_unavailable", result.ErrorCode);
    }

    private LibraryIndexMaintenanceService Service()
    {
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        var storage = new DurableStorageState(new StorageOptions());
        return new LibraryIndexMaintenanceService(_factory, queue, storage, _clock,
            NullLogger<LibraryIndexMaintenanceService>.Instance);
    }

    private UserRecord Identity(Guid userId, string backendType, string principalId) => new()
    {
        Id = userId,
        BackendType = backendType,
        BackendInstanceId = "primary",
        BackendPrincipalId = principalId,
        DisplayName = principalId,
        Enabled = true,
        CreatedAt = _now,
        UpdatedAt = _now,
        LastSeenAt = _now
    };

    private SecretReferenceRecord Credential(Guid userId, DateTimeOffset updatedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Purpose = BackendCredentialScope.SubsonicPurpose,
        ActiveVersion = 1,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt
    };

    private LibraryTrackRecord Track(UserRecord identity,
        DateTimeOffset indexedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = identity.Id,
            BackendLibraryId = "music",
            Protocol = identity.BackendType,
            BackendInstanceId = identity.BackendInstanceId,
            BackendItemId = Guid.CreateVersion7().ToString("N"),
            FilePath = "/music/track.flac",
            Title = "Track",
            Artist = "Artist",
            ProviderIdsJson = "{}",
            IndexedAt = indexedAt,
            SourceModifiedAt = indexedAt,
            UpdatedAt = indexedAt
        };

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
