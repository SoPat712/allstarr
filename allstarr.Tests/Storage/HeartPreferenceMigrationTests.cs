using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class HeartPreferenceMigrationTests
{
    [Fact]
    public async Task PreferenceMigration_PreservesSettingsAndGuardsPopulatedDowngrade()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261106072734_AddPromotionRequests");
        var owner = Guid.CreateVersion7();
        await SqliteTestDatabase.InsertHistoricalUserAsync(db, new() { Id = owner, BackendPrincipalId = "preference-listener", DisplayName = "Listener" });
        var existing = new RuntimeSettingRecord
        {
            Id = Guid.CreateVersion7(), OwnerUserId = owner, Key = ListeningPreferenceKeys.ShowExternalLabel,
            ValueType = RuntimeSettingValueType.Boolean, ValueJson = "false", Source = "personal-preferences", Revision = 3
        };
        db.RuntimeSettings.Add(existing);
        await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        Assert.Equal("false", (await db.RuntimeSettings.AsNoTracking().SingleAsync()).ValueJson);
        Assert.Equal(3, (await db.RuntimeSettings.AsNoTracking().SingleAsync()).Revision);
        db.RuntimeSettings.Add(new()
        {
            Id = Guid.CreateVersion7(), OwnerUserId = owner, Key = ListeningPreferenceKeys.HeartAction,
            ValueType = RuntimeSettingValueType.String, ValueJson = "\"heart\"", Source = "personal-preferences", Revision = 1
        });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => migrator.MigrateAsync("20261106072734_AddPromotionRequests"));
        Assert.Contains("CK_heart_preference_requires_backup_restore", error.Message);
        Assert.Equal(2, await db.RuntimeSettings.CountAsync());
    }
}
