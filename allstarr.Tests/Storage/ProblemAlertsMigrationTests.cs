using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

[Trait("Lane", "ReleaseCritical")]
public sealed class ProblemAlertsMigrationTests
{
    private const string Before = "20261025080051_SharedProviderHearts";
    private const string Target = "20261028120000_ProblemAlerts";

    [Fact]
    public async Task UpgradePreservesExistingDataAndEmptyDowngradeIsReversible()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        var owner = Guid.NewGuid();
        db.Users.Add(new() { Id = owner, BackendPrincipalId = "fixture", DisplayName = "Listener" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE alert_fixture (Value TEXT NOT NULL); INSERT INTO alert_fixture VALUES('preserved');");
        await migrator.MigrateAsync(Target);
        Assert.Equal("Listener", (await db.Users.SingleAsync()).DisplayName);
        Assert.Equal("preserved", await db.Database.SqlQueryRaw<string>("SELECT Value FROM alert_fixture").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        await migrator.MigrateAsync(Before);
        await migrator.MigrateAsync(Target);
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }

    [Fact]
    public async Task IncidentIdentityIsUniqueAndPopulatedDowngradeRefusesBeforeMutation()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        db.ProblemIncidents.Add(new() { Id = Guid.NewGuid(), ScopeKey = "household", Resource = "disk", Kind = "disk-space" });
        await db.SaveChangesAsync();
        await using (var duplicate = new AllstarrDbContext(database.Options))
        {
            duplicate.ProblemIncidents.Add(new() { Id = Guid.NewGuid(), ScopeKey = "household", Resource = "disk", Kind = "disk-space" });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }
        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(Before));
        Assert.Single(await db.ProblemIncidents.ToArrayAsync());
    }
}
