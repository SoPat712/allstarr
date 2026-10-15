using allstarr.Core.Playback;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class ScrobbleDeliveryMigrationTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradeAndRollback_PreserveLegacyReceiptsAndUnrelatedTriggers()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261015175251_RetainFavoriteDisplayMetadata");
        var user = Guid.CreateVersion7();
        var receipt = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new()
        {
            Id = user,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO playback_delivery_checkpoints (Id, OwnerUserId, OccurrenceKey, SignalKey, TargetId, Kind, State,
                DetailsJson, RequiresReauthentication, UpdatedAt)
            VALUES ({{receipt}}, {{user}}, 'occurrence', 'signal', 'lastfm', 'Completed', 'Delivered', '{}', 0, {{now.UtcTicks}});
            """);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE receipt_updates (Id TEXT);");
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER receipt_update AFTER UPDATE ON playback_delivery_checkpoints BEGIN INSERT INTO receipt_updates VALUES (NEW.Id); END;");
        await migrator.MigrateAsync();
        var migrated = await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleAsync();
        Assert.Equal(receipt, migrated.Id);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, migrated.State);
        Assert.Null(migrated.DestinationAccountId);
        Assert.Null(migrated.AttemptStartedAt);
        Assert.Equal(0, migrated.SendCount);
        await migrator.MigrateAsync("20261015175251_RetainFavoriteDisplayMetadata");
        await db.Database.ExecuteSqlRawAsync("UPDATE playback_delivery_checkpoints SET ProviderCode = 'preserved';");
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM receipt_updates").SingleAsync());
        await migrator.MigrateAsync();
        Assert.Equal("preserved", (await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleAsync()).ProviderCode);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
