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
        Assert.Equal("20260802183000_AddPlaylistProjectionMode", context.Database.GetMigrations().Last());
    }
}
