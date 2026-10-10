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
        Assert.Equal(["20261008235223_UsersBaseline", "20261009043555_VolatileProviderHealth",
            "20261010035223_ProviderAccountSettings", "20261010061013_RecordingIdentifiers",
            "20261010083000_CancelLegacyRematchJobs"],
            context.Database.GetMigrations());
    }
}
