using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class UnpublishedManagedFileRemovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CrashAfterRevocation_ResumeDeletesHouseholdFileAndLeavesSourceUntouched(bool usePlacement)
    {
        var record = await SeedFileAsync(activeReferences: 0, household: true);
        var sourcePath = Path.Combine(sourceRoot, "provider-original.flac");
        var sourceBytes = await File.ReadAllBytesAsync(record.CanonicalPath);
        Assert.True(new PhysicalManagedFileOperations().TryCreateHardLink(sourcePath, record.CanonicalPath));
        record = await CaptureRecordedIdentityAsync(record);

        await RevokeWithCrashAsync(record);

        var result = await ResumeAsync(record, usePlacement: usePlacement);

        Assert.True(result);
        Assert.False(File.Exists(record.CanonicalPath));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CrashAfterRevocation_ResumeNeverDeletesChangedOrReplacedFile(bool replaceInode)
    {
        var record = await SeedHouseholdWithIdentityAsync();
        await RevokeWithCrashAsync(record);

        var replacement = "unrelated replacement bytes"u8.ToArray();
        if (replaceInode)
        {
            File.Delete(record.CanonicalPath);
            await File.WriteAllBytesAsync(record.CanonicalPath, replacement);
        }
        else
        {
            await File.WriteAllBytesAsync(record.CanonicalPath, replacement);
        }

        var result = await ResumeAsync(record);

        Assert.False(result);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(record.CanonicalPath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CrashAfterRevocation_ResumeRejectsSameBytesWithDifferentInode()
    {
        var record = await SeedHouseholdWithIdentityAsync();
        var originalBytes = await File.ReadAllBytesAsync(record.CanonicalPath);
        await RevokeWithCrashAsync(record);

        var movedPath = record.CanonicalPath + ".original";
        File.Move(record.CanonicalPath, movedPath);
        await File.WriteAllBytesAsync(record.CanonicalPath, originalBytes);
        var operations = new PhysicalManagedFileOperations();
        Assert.True(operations.TryGetFileIdentity(record.CanonicalPath, out var replacementIdentity));
        Assert.NotEqual(record.FileSystemFileId, replacementIdentity.FileId);

        var result = await ResumeAsync(record);

        Assert.False(result);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(record.CanonicalPath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RevokedHouseholdFile_WrongScopeStaysUntouched()
    {
        var record = await SeedHouseholdWithIdentityAsync();
        await RevokeWithCrashAsync(record);

        var result = await ResumeAsync(record, ManagedFileScopeKey.Create(owner, record.RootId));

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RevokedPrivateFile_IsNotEligibleForHouseholdResume()
    {
        var record = await SeedFileAsync(activeReferences: 0, household: false);
        await MarkRevokedForFixtureAsync(record);

        var result = await ResumeAsync(record, record.ScopeKey);

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RevokedHouseholdFileWithoutIdentity_ExistingPathIsNotResumed()
    {
        var record = await SeedFileAsync(activeReferences: 0, household: true);
        await MarkRevokedForFixtureAsync(record);

        var result = await ResumeAsync(record);

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RevokedHouseholdFile_AlreadyAbsentIsCompleted()
    {
        var record = await SeedFileAsync(activeReferences: 0, createPath: false, household: true);
        await MarkRevokedForFixtureAsync(record);

        var result = await ResumeAsync(record);

        Assert.True(result);
        Assert.False(File.Exists(record.CanonicalPath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ActiveReference_BlocksRevocationAndResume()
    {
        var record = await SeedFileAsync(activeReferences: 1, household: true);
        await Assert.ThrowsAsync<DbUpdateException>(() => MarkRevokedForFixtureAsync(record));

        var result = await ResumeAsync(record);

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
        await using var verify = new AllstarrDbContext(database.Options);
        var saved = await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id);
        Assert.Null(saved.RemovedAt);
        Assert.Equal(1, saved.ReferenceCount);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeptRoute_BlocksRevocationAndResume()
    {
        var record = await SeedFileAsync(activeReferences: 0, household: true);
        await SeedKeptRouteAsync(record);
        await Assert.ThrowsAsync<DbUpdateException>(() => MarkRevokedForFixtureAsync(record));

        var result = await ResumeAsync(record);

        Assert.False(result);
        Assert.True(File.Exists(record.CanonicalPath));
        await using var verify = new AllstarrDbContext(database.Options);
        var saved = await verify.ManagedFiles.SingleAsync(item => item.Id == record.Id);
        Assert.Null(saved.RemovedAt);
    }

    private async Task<ManagedFileRecord> SeedHouseholdWithIdentityAsync()
    {
        var record = await SeedFileAsync(activeReferences: 0, household: true);
        return await CaptureRecordedIdentityAsync(record);
    }

    private async Task<ManagedFileRecord> CaptureRecordedIdentityAsync(ManagedFileRecord record)
    {
        var operations = new PhysicalManagedFileOperations();
        Assert.True(operations.TryGetFileIdentity(record.CanonicalPath, out var identity));
        await using var db = new AllstarrDbContext(database.Options);
        var entity = await db.ManagedFiles.SingleAsync(item => item.Id == record.Id);
        entity.FileSystemDeviceId = identity.DeviceId;
        entity.FileSystemFileId = identity.FileId;
        entity.FileSystemLinkCount = identity.LinkCount;
        await db.SaveChangesAsync();
        return record with
        {
            FileSystemDeviceId = identity.DeviceId,
            FileSystemFileId = identity.FileId,
            FileSystemLinkCount = identity.LinkCount
        };
    }

    private async Task RevokeWithCrashAsync(ManagedFileRecord record)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var store = new CrashAfterRevocationStore(new EfManagedFileOwnershipStore(db));
        var service = new ManagedFileRemovalService(store, new PhysicalManagedFileOperations());

        await Assert.ThrowsAsync<SimulatedProcessDeathException>(() =>
            service.RemoveUnpublishedAsync(record.Id, record.ScopeKey));

        await using var verify = new AllstarrDbContext(database.Options);
        var active = await new EfManagedFileOwnershipStore(verify).GetAsync(record.Id, default);
        Assert.Null(active);
        Assert.True(File.Exists(record.CanonicalPath));
    }

    private async Task<bool> ResumeAsync(ManagedFileRecord record, string? scope = null, bool usePlacement = false)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var store = new EfManagedFileOwnershipStore(db);
        var files = new PhysicalManagedFileOperations();
        var service = new ManagedFileRemovalService(store, files,
            usePlacement ? new FilePlacementService(store, files) : null);
        return await service.ResumeUnpublishedRemovalAsync(record.Id, scope ?? record.ScopeKey);
    }

    private async Task MarkRevokedForFixtureAsync(ManagedFileRecord record)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var entity = await db.ManagedFiles.SingleAsync(item => item.Id == record.Id);
        entity.RemovedAt = DateTimeOffset.UtcNow;
        entity.Revision++;
        await db.SaveChangesAsync();
    }

    private sealed class SimulatedProcessDeathException : Exception;

    private sealed class CrashAfterRevocationStore(EfManagedFileOwnershipStore inner) : IManagedFileRemovalStore
    {
        public Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            inner.GetAsync(id, cancellationToken);

        public Task<ManagedFileRecord?> GetRevokedUnpublishedAsync(Guid id, CancellationToken cancellationToken) =>
            inner.GetRevokedUnpublishedAsync(id, cancellationToken);

        public Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken) =>
            inner.MarkRemovedAsync(id, cancellationToken);

        public async Task<bool> TryMarkUnpublishedRemovedAsync(
            ManagedFileRecord expected,
            CancellationToken cancellationToken)
        {
            var marked = await inner.TryMarkUnpublishedRemovedAsync(expected, cancellationToken);
            if (marked) throw new SimulatedProcessDeathException();
            return false;
        }
    }
}
