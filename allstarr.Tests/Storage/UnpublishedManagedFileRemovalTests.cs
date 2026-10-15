using System.Security.Cryptography;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class UnpublishedManagedFileRemovalTests : IAsyncLifetime
{
    private readonly string managedRoot = Path.Combine(Path.GetTempPath(), "allstarr-unpublished-managed", Guid.NewGuid().ToString("N"));
    private readonly string sourceRoot = Path.Combine(Path.GetTempPath(), "allstarr-unpublished-source", Guid.NewGuid().ToString("N"));
    private SqliteTestDatabase database = null!;
    private Guid owner;
    private Guid other;

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        Directory.CreateDirectory(managedRoot);
        Directory.CreateDirectory(sourceRoot);
        owner = Guid.CreateVersion7();
        other = Guid.CreateVersion7();

        await using var db = new AllstarrDbContext(database.Options);
        db.Users.AddRange(User(owner), User(other));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ZeroReferenceFile_IsRevokedAndDeleted_WhileSourceRemainsUntouched()
    {
        var sourcePath = Path.Combine(sourceRoot, "source.flac");
        var sourceBytes = "original provider source"u8.ToArray();
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var record = await SeedFileAsync(activeReferences: 0);

        var result = await RemoveAsync(record);

        Assert.True(result);
        Assert.False(File.Exists(record.CanonicalPath));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        await using var verify = new AllstarrDbContext(database.Options);
        var saved = await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id);
        Assert.NotNull(saved.RemovedAt);
        Assert.Equal(0, saved.ReferenceCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ActiveReferences_ProtectUnpublishedFile(int activeReferences)
    {
        var record = await SeedFileAsync(activeReferences);

        var result = await RemoveAsync(record);

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
        await using var verify = new AllstarrDbContext(database.Options);
        var saved = await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id);
        Assert.Null(saved.RemovedAt);
        Assert.Equal(activeReferences, saved.ReferenceCount);
    }

    [Fact]
    public async Task KeptRoute_ProtectsZeroReferenceFile()
    {
        var record = await SeedFileAsync(activeReferences: 0, household: true);
        await SeedKeptRouteAsync(record);

        var result = await RemoveAsync(record);

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.NotNull(await verify.Set<RecordingKeptRouteEntity>()
            .SingleOrDefaultAsync(item => item.ManagedFileId == record.Id));
        Assert.Null((await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id)).RemovedAt);
    }

    [Fact]
    public async Task WrongScope_DoesNotRevokeOrDeleteFile()
    {
        var record = await SeedFileAsync(activeReferences: 0);

        var result = await RemoveAsync(record, ManagedFileScopeKey.Create(other, record.RootId));

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Null((await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id)).RemovedAt);
    }

    [Fact]
    public async Task MissingOwnedPath_IsStillRevoked()
    {
        var record = await SeedFileAsync(activeReferences: 0, createPath: false);

        var result = await RemoveAsync(record);

        Assert.True(result);
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.NotNull((await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id)).RemovedAt);
    }

    [Fact]
    public async Task MissingPathThatAppearsDuringRevocation_IsNotDeleted()
    {
        var record = await SeedFileAsync(activeReferences: 0, createPath: false);
        await using var db = new AllstarrDbContext(database.Options);
        var store = new NewPathRemovalStore(new EfManagedFileOwnershipStore(db), record.CanonicalPath);
        var service = new ManagedFileRemovalService(store);

        var result = await service.RemoveUnpublishedAsync(record.Id, record.ScopeKey);

        Assert.True(result);
        Assert.True(File.Exists(record.CanonicalPath));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.NotNull((await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id)).RemovedAt);
    }

    [Fact]
    public async Task ReferenceAcquiredAfterRead_PreventsDeletion()
    {
        var record = await SeedFileAsync(activeReferences: 0);
        await using var db = new AllstarrDbContext(database.Options);
        var raceStore = new ReferenceAcquiringRemovalStore(
            new EfManagedFileOwnershipStore(db), database.Options);
        var service = new ManagedFileRemovalService(raceStore);

        var result = await service.RemoveUnpublishedAsync(record.Id, record.ScopeKey);

        Assert.False(result);
        Assert.True(raceStore.TryMarkCalled);
        Assert.True(File.Exists(record.CanonicalPath));
        await using var verify = new AllstarrDbContext(database.Options);
        var saved = await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id);
        Assert.Null(saved.RemovedAt);
        Assert.Equal(1, saved.ReferenceCount);
    }

    [Fact]
    public async Task AmbientTransaction_RefusesCleanupBeforePhysicalDeletion()
    {
        var record = await SeedFileAsync(activeReferences: 0);
        await using var db = new AllstarrDbContext(database.Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var service = new ManagedFileRemovalService(
            new EfManagedFileOwnershipStore(db), new PhysicalManagedFileOperations());

        var result = await service.RemoveUnpublishedAsync(record.Id, record.ScopeKey);

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
        await transaction.RollbackAsync();
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Null((await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id)).RemovedAt);
    }

    private async Task<bool> RemoveAsync(ManagedFileRecord record, string? scope = null)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var service = new ManagedFileRemovalService(
            new EfManagedFileOwnershipStore(db), new PhysicalManagedFileOperations());
        return await service.RemoveUnpublishedAsync(record.Id, scope ?? record.ScopeKey);
    }

    private async Task<ManagedFileRecord> SeedFileAsync(int activeReferences, bool createPath = true, bool household = false)
    {
        var id = Guid.CreateVersion7();
        var rootId = Guid.CreateVersion7();
        var targetRoot = Path.Combine(managedRoot, rootId.ToString("N"));
        var targetPath = Path.Combine(targetRoot, "track.flac");
        Directory.CreateDirectory(targetRoot);
        var payload = "managed audio"u8.ToArray();
        if (createPath)
            await File.WriteAllBytesAsync(targetPath, payload);

        var record = new ManagedFileRecord(
            id, rootId, targetPath, Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length,
            ManagedFilePlacementMethod.Copy,
            household ? null : owner, null, ManagedFileScopeKey.Create(household ? null : owner, rootId), 1, true, DateTimeOffset.UtcNow)
        {
            TargetRootPath = targetRoot
        };
        await using var db = new AllstarrDbContext(database.Options);
        var store = new EfManagedFileOwnershipStore(db);
        await store.AddAsync(record, Reference(record, "seed"), default);
        for (var index = 1; index < activeReferences; index++)
            await store.AddReferenceAsync(record.Id, Reference(record, $"active:{index}"), default);
        if (activeReferences == 0)
            await store.ReleaseReferenceAsync(record.Id, "seed", default);
        return record with { ReferenceCount = activeReferences };
    }

    private async Task SeedKeptRouteAsync(ManagedFileRecord record)
    {
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.CreateVersion7();
        var recordingId = Guid.CreateVersion7();
        var workspaceId = Guid.CreateVersion7();
        var artifactId = Guid.CreateVersion7();
        await using var db = new AllstarrDbContext(database.Options);
        db.Jobs.Add(new DurableJobRecord
        {
            Id = jobId,
            ScopeKey = $"user:{owner:N}",
            OwnerUserId = owner,
            ProviderCapability = "fixture",
            PolicySnapshotJson = "{}",
            RequestFingerprint = new string('b', 64),
            CorrelationId = "kept-route-fixture",
            Type = "recording.keep",
            PayloadJson = "{}",
            IdempotencyKey = $"route:{record.Id:N}",
            State = DurableJobState.Running,
            Priority = 1,
            AttemptCount = 1,
            MaxAttempts = 3,
            MaxDeferrals = 3,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        db.CanonicalRecordings.Add(new CanonicalRecordingRecord
        {
            Id = recordingId,
            CreatedByUserId = owner,
            Title = "Kept route fixture",
            PublicId = CanonicalRecordingRecord.DefaultPublicId(recordingId),
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        db.ProviderDownloadWorkspaces.Add(new ProviderDownloadWorkspaceEntity
        {
            Id = workspaceId,
            WorkspaceId = $"workspace-{workspaceId:N}",
            OwnerUserId = owner,
            DurableJobId = jobId,
            ProviderId = "fixture",
            IdempotencyKey = $"workspace:{workspaceId:N}",
            CreatedAt = now,
            Revision = 1
        });
        db.ProviderDownloadArtifacts.Add(new ProviderDownloadArtifactEntity
        {
            Id = artifactId,
            WorkspaceRecordId = workspaceId,
            WorkspaceId = $"workspace-{workspaceId:N}",
            OwnerUserId = owner,
            DurableJobId = jobId,
            ProviderId = "fixture",
            SourceExternalTrackId = "fixture-track",
            ProviderArtifactId = "fixture-artifact",
            RelativePath = "track.flac",
            ContentSha256 = new string('c', 64),
            Length = 12,
            MimeType = "audio/flac",
            Container = "flac",
            Codec = "flac",
            State = ProviderDownloadArtifactState.Verified,
            CreatedAt = now,
            VerifiedAt = now,
            Revision = 1
        });
        db.Set<RecordingKeptRouteEntity>().Add(new RecordingKeptRouteEntity
        {
            CanonicalRecordingId = recordingId,
            ManagedFileId = record.Id,
            PublishedByUserId = owner,
            PublicationJobId = jobId,
            SourceArtifactId = artifactId,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        await db.SaveChangesAsync();
    }

    private ManagedFileReference Reference(ManagedFileRecord record, string key) => new(
        Guid.CreateVersion7(), record.Id, record.OwnerUserId, record.ScopeKey, key, DateTimeOffset.UtcNow);

    private static UserRecord User(Guid id) => new()
    {
        Id = id,
        BackendType = "jellyfin",
        BackendInstanceId = "fixture",
        BackendPrincipalId = id.ToString("N"),
        DisplayName = "Managed-file test user",
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public async Task DisposeAsync()
    {
        if (database is not null) await database.DisposeAsync();
        if (Directory.Exists(managedRoot)) Directory.Delete(managedRoot, recursive: true);
        if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
    }

    private sealed class ReferenceAcquiringRemovalStore(
        EfManagedFileOwnershipStore inner,
        DbContextOptions<AllstarrDbContext> options) : IManagedFileRemovalStore
    {
        public bool TryMarkCalled { get; private set; }

        public Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            inner.GetAsync(id, cancellationToken);

        public Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken) =>
            inner.MarkRemovedAsync(id, cancellationToken);

        public async Task<bool> TryMarkUnpublishedRemovedAsync(
            ManagedFileRecord expected,
            CancellationToken cancellationToken)
        {
            TryMarkCalled = true;
            await using (var db = new AllstarrDbContext(options))
            {
                var store = new EfManagedFileOwnershipStore(db);
                await store.AddReferenceAsync(expected.Id, new(
                    Guid.CreateVersion7(), expected.Id, expected.OwnerUserId, expected.ScopeKey,
                    "race:reference", DateTimeOffset.UtcNow), cancellationToken);
            }
            return await inner.TryMarkUnpublishedRemovedAsync(expected, cancellationToken);
        }
    }

    private sealed class NewPathRemovalStore(
        EfManagedFileOwnershipStore inner,
        string path) : IManagedFileRemovalStore
    {
        public Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            inner.GetAsync(id, cancellationToken);

        public Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken) =>
            inner.MarkRemovedAsync(id, cancellationToken);

        public async Task<bool> TryMarkUnpublishedRemovedAsync(
            ManagedFileRecord expected,
            CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(path, "unrelated replacement"u8.ToArray(), cancellationToken);
            return await inner.TryMarkUnpublishedRemovedAsync(expected, cancellationToken);
        }
    }
}
