using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class MigrationModelSnapshotTests
{
    [Fact]
    public void CheckedInSnapshotMatchesTheRuntimeModel()
    {
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
            .Options;
        using var context = new AllstarrDbContext(options);

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal("20260803020000_AddListeningHistoryImports", context.Database.GetMigrations().Last());
    }
}
