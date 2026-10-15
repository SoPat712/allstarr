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
            "20261010083000_CancelLegacyRematchJobs", "20261014145512_ConfirmPlaylistMembershipRemoval",
            "20261014162517_PreservePlaylistDestinationMode",
            "20261014170945_PublicPlaylistSources",
            "20261014174328_BackfillPlaylistRemovalConfirmations", "20261015022113_PersonalLyricsOrder",
            "20261015045010_RecordingLyricsSourceChoices", "20261015064546_SimplifyDurableJobs", "20261015072034_HouseholdKeptRecordingRoutes", "20261015083606_RecordFavoriteIntentAtAcceptance", "20261015090448_RetainKeptRecordingDisplayMetadata", "20261015165744_SupersedeDownloadArtifacts"],
            context.Database.GetMigrations());
    }
}
