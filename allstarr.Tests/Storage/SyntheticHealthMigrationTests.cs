using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

[Trait("Lane", "ReleaseCritical")]
public sealed class SyntheticHealthMigrationTests
{
    [Fact]
    public async Task SafeDowngradePreservesListenersAndUnrelatedUserTriggers()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        const string before = "20261023010000_PersonalPlaybackStart";
        await migrator.MigrateAsync(before);
        var listener = Guid.CreateVersion7();
        var administrator = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id, BackendType, BackendInstanceId, BackendPrincipalId, DisplayName, IsAdmin, Enabled, CreatedAt, UpdatedAt, LastSeenAt)
            VALUES ({listener}, 'jellyfin', 'fixture', 'listener', 'Listener', 0, 1, {now.UtcTicks}, {now.UtcTicks}, {now.UtcTicks}),
                   ({administrator}, 'jellyfin', 'fixture', 'administrator', 'Administrator', 1, 0, {now.UtcTicks}, {now.UtcTicks}, 0);
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE listener_checks (OwnerUserId TEXT NOT NULL, DisplayName TEXT NOT NULL);
            CREATE TRIGGER listener_check_owner BEFORE INSERT ON listener_checks
            WHEN NOT EXISTS (SELECT 1 FROM users WHERE Id = NEW.OwnerUserId)
            BEGIN SELECT RAISE(ABORT, 'listener_missing'); END;
            CREATE TRIGGER listener_display_update AFTER UPDATE OF DisplayName ON users
            BEGIN INSERT INTO listener_checks (OwnerUserId, DisplayName) VALUES (NEW.Id, NEW.DisplayName); END;
            """);
        const string usersSql = """
            SELECT json_object('Id', Id, 'BackendType', BackendType, 'BackendInstanceId', BackendInstanceId,
                'BackendPrincipalId', BackendPrincipalId, 'DisplayName', DisplayName, 'IsAdmin', IsAdmin,
                'Enabled', Enabled, 'CreatedAt', CreatedAt, 'UpdatedAt', UpdatedAt, 'LastSeenAt', LastSeenAt) AS Value
            FROM users ORDER BY Id
            """;
        const string triggersSql = """
            SELECT name || char(10) || sql AS Value FROM sqlite_master WHERE type = 'trigger'
                AND name IN ('TR_legacy_media_provenance', 'listener_check_owner', 'listener_display_update') ORDER BY name
            """;
        var users = await db.Database.SqlQueryRaw<string>(usersSql).ToArrayAsync();
        var triggers = await db.Database.SqlQueryRaw<string>(triggersSql).ToArrayAsync();
        Assert.Equal(2, users.Length);
        Assert.Equal(3, triggers.Length);

        await migrator.MigrateAsync();
        Assert.All(await db.Users.AsNoTracking().ToArrayAsync(), user => Assert.False(user.IsSynthetic));
        await migrator.MigrateAsync(before);

        Assert.Equal(users, await db.Database.SqlQueryRaw<string>(usersSql).ToArrayAsync());
        Assert.Equal(triggers, await db.Database.SqlQueryRaw<string>(triggersSql).ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('users') WHERE name = 'IsSynthetic'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'health_smoke_runs'").SingleAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET DisplayName = 'Updated listener' WHERE Id = {listener}");
        Assert.Equal("Updated listener", await db.Database.SqlQueryRaw<string>(
            "SELECT DisplayName AS Value FROM listener_checks").SingleAsync());
        var missing = Guid.CreateVersion7();
        var failure = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO listener_checks (OwnerUserId, DisplayName) VALUES ({missing}, 'Missing')
            """));
        Assert.Contains("listener_missing", failure.Message);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        await migrator.MigrateAsync();
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.All(await db.Users.AsNoTracking().ToArrayAsync(), user => Assert.False(user.IsSynthetic));
    }

    [Fact]
    public async Task ExistingListenersRemainOrdinaryAndDesignationPreventsLossyDowngrade()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261023010000_PersonalPlaybackStart");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id, BackendType, BackendInstanceId, BackendPrincipalId, DisplayName, IsAdmin, Enabled, CreatedAt, UpdatedAt, LastSeenAt)
            VALUES ({id}, 'jellyfin', 'fixture', 'fixture-listener', 'Listener', 0, 1, 0, 0, 0)
            """);
        await migrator.MigrateAsync();
        Assert.False((await db.Users.SingleAsync()).IsSynthetic);
        await db.Users.ExecuteUpdateAsync(set => set.SetProperty(user => user.IsSynthetic, true));
        var failure = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => migrator.MigrateAsync("20261023010000_PersonalPlaybackStart"));
        Assert.Contains("CK_synthetic_listeners_require_backup_restore", failure.Message);
        Assert.True((await db.Users.AsNoTracking().SingleAsync()).IsSynthetic);
    }
}
