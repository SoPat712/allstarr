using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

[Trait("Lane", "ReleaseCritical")]
public sealed class SharedTagMigrationTests
{
    [Fact]
    public async Task UpgradeAndEmptyDowngradePreservePromotionSchemaAndExistingData()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        const string before = "20261028180000_LibraryPromotions";
        const string target = "20261028200000_SharedTagProfiles";
        await migrator.MigrateAsync(before);
        var owner = Guid.NewGuid();
        db.Users.Add(new() { Id = owner, BackendPrincipalId = "fixture", DisplayName = "Listener" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE tag_upgrade_fixture (Value TEXT NOT NULL); INSERT INTO tag_upgrade_fixture VALUES('preserved');");

        await migrator.MigrateAsync(target);
        Assert.Equal(0, await db.SharedTagProfiles.CountAsync());
        Assert.Equal(owner, (await db.Users.SingleAsync()).Id);
        Assert.Equal("preserved", await db.Database.SqlQueryRaw<string>("SELECT Value FROM tag_upgrade_fixture").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM promotion_runs").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM promotion_steps").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());

        await migrator.MigrateAsync(before);
        Assert.Equal(owner, (await db.Users.SingleAsync()).Id);
        Assert.Equal("preserved", await db.Database.SqlQueryRaw<string>("SELECT Value FROM tag_upgrade_fixture").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM promotion_runs").SingleAsync());
        await migrator.MigrateAsync(target);
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }
}
