using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class UnpublishedManagedFileRemovalTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ResumeRefusesCallerTransactionAfterPriorRevocation()
    {
        var record = await SeedHouseholdWithIdentityAsync();
        await RevokeWithCrashAsync(record);
        await using var db = new AllstarrDbContext(database.Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var removal = new ManagedFileRemovalService(new EfManagedFileOwnershipStore(db), new PhysicalManagedFileOperations());

        Assert.False(await removal.ResumeUnpublishedRemovalAsync(record.Id, record.ScopeKey));

        Assert.True(File.Exists(record.CanonicalPath));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ResumeRefusesSymbolicLinkEvenWhenItResolvesToTheOriginalInode(bool replaceParent)
    {
        var record = await SeedHouseholdWithIdentityAsync();
        var bytes = await File.ReadAllBytesAsync(record.CanonicalPath);
        await RevokeWithCrashAsync(record);
        if (replaceParent)
        {
            var moved = record.TargetRootPath + ".moved";
            Directory.Move(record.TargetRootPath, moved);
            Directory.CreateSymbolicLink(record.TargetRootPath, moved);
        }
        else
        {
            var moved = record.CanonicalPath + ".moved";
            File.Move(record.CanonicalPath, moved);
            File.CreateSymbolicLink(record.CanonicalPath, moved);
        }

        Assert.False(await ResumeAsync(record));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(record.CanonicalPath));
        Assert.True((File.GetAttributes(replaceParent ? record.TargetRootPath : record.CanonicalPath) &
                     FileAttributes.ReparsePoint) != 0);
    }
}
