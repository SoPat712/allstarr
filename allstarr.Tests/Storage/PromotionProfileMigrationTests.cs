using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class PromotionProfileMigrationTests
{
    [Fact]
    public async Task UpgradePreservesUsersAndExistingRowsAndMatchesTheRuntimeModel()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261030120000_CollectionMigration");
        var owner = Guid.CreateVersion7();
        await SqliteTestDatabase.InsertHistoricalUserAsync(db, new() { Id = owner, BackendPrincipalId = "profile-fixture", DisplayName = "Listener" });
        db.SharedTagProfiles.Add(new() { Key = "profile-fixture", PayloadJson = "{}" });
        await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        Assert.Empty(await db.PromotionProfiles.ToArrayAsync());
        Assert.Equal(owner, await db.Users.Select(item => item.Id).SingleAsync());
        Assert.Equal("profile-fixture", (await db.SharedTagProfiles.SingleAsync()).Key);
        Assert.False(db.Database.HasPendingModelChanges());
        var assembly = db.GetService<IMigrationsAssembly>();
        var latest = assembly.CreateMigration(assembly.Migrations.Last().Value, db.Database.ProviderName!);
        Assert.NotNull(latest.TargetModel.FindEntityType(typeof(PromotionProfileRecord)));
        Assert.NotNull(latest.TargetModel.FindEntityType(typeof(CollectionMigrationRunRecord)));
        Assert.NotNull(latest.TargetModel.FindEntityType(typeof(TasteProfileRecord)));
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }

    [Fact]
    public async Task PopulatedDowngradeRefusesToDiscardProfiles()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        db.PromotionProfiles.Add(new() { Key = "household", PayloadJson = "{}", Revision = 1 });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261030120000_CollectionMigration"));
        Assert.Equal("{}", (await db.PromotionProfiles.SingleAsync()).PayloadJson);
    }
}
