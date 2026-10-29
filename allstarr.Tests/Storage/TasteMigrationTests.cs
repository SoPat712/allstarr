using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

[Trait("Lane", "ReleaseCritical")]
public sealed class TasteMigrationTests
{
    [Fact]
    public async Task UpgradeAndEmptyDowngradePreserveSharedTagsAndExistingUsers()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        const string before = "20261028200000_SharedTagProfiles";
        const string target = "20261028210000_TasteProfiles";
        await migrator.MigrateAsync(before);
        var owner = Guid.NewGuid();
        db.Users.Add(new() { Id = owner, BackendPrincipalId = "fixture", DisplayName = "Listener" });
        db.SharedTagProfiles.Add(new() { Key = "fixture:tag", PayloadJson = "{}" });
        await db.SaveChangesAsync();
        await migrator.MigrateAsync(target);
        Assert.Empty(await db.TasteProfiles.ToArrayAsync());
        await migrator.MigrateAsync(before);
        Assert.Equal("fixture:tag", (await db.SharedTagProfiles.AsNoTracking().SingleAsync()).Key);
        Assert.Equal(owner, (await db.Users.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM promotion_runs").SingleAsync());
        await migrator.MigrateAsync(target);
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }

    [Fact]
    public async Task PopulatedDowngradeRefusesBeforeChangingExistingOwners()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = Guid.NewGuid();
        const string payload = "{\"Paused\":true,\"Nudges\":[{\"Kind\":\"tag\",\"Name\":\"rock\",\"Mode\":\"more\"}]}";
        db.Users.Add(new() { Id = owner, BackendPrincipalId = "fixture", DisplayName = "Listener" });
        db.SharedTagProfiles.Add(new() { Key = "fixture:tag", PayloadJson = "{}" });
        db.TasteProfiles.Add(new() { OwnerUserId = owner, PayloadJson = payload, Revision = 1 });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261028120000_ProblemAlerts"));
        Assert.Equal(payload, (await db.TasteProfiles.AsNoTracking().SingleAsync()).PayloadJson);
        Assert.Equal("fixture:tag", (await db.SharedTagProfiles.AsNoTracking().SingleAsync()).Key);
        Assert.Equal(owner, (await db.Users.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM promotion_runs").SingleAsync());
    }
}
