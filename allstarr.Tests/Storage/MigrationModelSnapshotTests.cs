using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
            "20261015045010_RecordingLyricsSourceChoices", "20261015064546_SimplifyDurableJobs", "20261015072034_HouseholdKeptRecordingRoutes", "20261015083606_RecordFavoriteIntentAtAcceptance", "20261015090448_RetainKeptRecordingDisplayMetadata", "20261015165744_SupersedeDownloadArtifacts", "20261015175251_RetainFavoriteDisplayMetadata", "20261015203644_BindScrobbleDestinations", "20261015214512_VerifyUncertainLastFmDelivery", "20261015225842_AdoptLegacyMedia", "20261016012900_VerifyCopiedManagedFiles", "20261020173403_PersonalProviderUseModes", "20261021090000_AudioQualityChecks", "20261021120000_SubsonicPlayQueues", "20261021180000_ProviderUserData", "20261023010000_PersonalPlaybackStart", "20261023030000_SyntheticHealthChecks", "20261024010000_ManagedAudioCacheIdentity", "20261025020354_ScheduledBackups", "20261025040000_ProblemReports"],
            context.Database.GetMigrations());
        var migrations = context.GetService<IMigrationsAssembly>();
        var latest = migrations.CreateMigration(migrations.Migrations.Last().Value, context.Database.ProviderName!);
        Assert.NotNull(latest.TargetModel.FindEntityType(typeof(SubsonicPlayQueueRecord)));
        Assert.NotNull(latest.TargetModel.FindEntityType(typeof(allstarr.Core.Downloads.ProviderDownloadArtifactEntity))!
            .FindProperty("QualityCheckJson"));
    }
}
