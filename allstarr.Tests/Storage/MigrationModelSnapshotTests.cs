using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class MigrationModelSnapshotTests
{
    [Fact]
    public void CheckedInSnapshotMatchesTheRuntimeModel()
    {
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new AllstarrDbContext(options);

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal("20261008135915_SqliteBaseline", Assert.Single(context.Database.GetMigrations()));
    }
}
