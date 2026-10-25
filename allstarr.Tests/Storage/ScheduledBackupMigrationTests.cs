using System.Data.Common;
using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

[Trait("Lane", "ReleaseCritical")]
public sealed class ScheduledBackupMigrationTests
{
    private const string Before = "20261024010000_ManagedAudioCacheIdentity";
    private static async Task<(Guid User, Guid Schedule)> SeedAsync(AllstarrDbContext db)
    {
        var user = Guid.NewGuid();
        var schedule = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id,BackendType,BackendInstanceId,BackendPrincipalId,DisplayName,IsAdmin,Enabled,CreatedAt,UpdatedAt,LastSeenAt)
            VALUES ({user},'jellyfin','fixture','administrator','Administrator',1,1,0,0,0);
            INSERT INTO job_schedules (Id,OwnerUserId,JobType,CronExpression,TimeZoneId,OverlapPolicy,MisfirePolicy,RetryPolicyJson,PayloadTemplateJson,Enabled,CreatedAt,UpdatedAt,Revision)
            VALUES ({schedule},{user},'playlist.materialize','0 3 * * *','UTC','Skip','RunOnce',{"{}"},{"{}"},1,0,0,1);
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE schedule_fixture_audit (Id TEXT NOT NULL);
            CREATE TRIGGER fixture_schedule_audit AFTER UPDATE OF Enabled ON job_schedules
                BEGIN INSERT INTO schedule_fixture_audit VALUES(NEW.Id); END;
            CREATE VIEW fixture_schedule_view AS SELECT Id,OwnerUserId FROM job_schedules;
            CREATE INDEX fixture_schedule_cron ON job_schedules(CronExpression);
            """);
        return (user, schedule);
    }
    private const string ScheduleRows = """
        SELECT json_object('Id',Id,'OwnerUserId',OwnerUserId,'JobType',JobType,'CronExpression',CronExpression,'TimeZoneId',TimeZoneId,
            'OverlapPolicy',OverlapPolicy,'MisfirePolicy',MisfirePolicy,'RetryPolicyJson',RetryPolicyJson,'PayloadTemplateJson',PayloadTemplateJson,
            'NextRunAt',NextRunAt,'Enabled',Enabled,'CreatedAt',CreatedAt,'UpdatedAt',UpdatedAt,'Revision',Revision) AS Value FROM job_schedules ORDER BY Id
        """;
    private const string Objects = """
        SELECT type || '|' || name || '|' || sql AS Value FROM sqlite_schema
        WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND name<>'job_schedules' ORDER BY type,name
        """;

    [Fact]
    public async Task PopulatedUpgradeEmptyDowngradeAndReupgradePreserveEveryExistingObjectAndValue()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        var (user, schedule) = await SeedAsync(db);
        var rows = await db.Database.SqlQueryRaw<string>(ScheduleRows).ToArrayAsync();
        var objects = await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync();
        var ddl = await TableSqlAsync(db);
        await migrator.MigrateAsync();
        Assert.Equal(rows, await db.Database.SqlQueryRaw<string>(ScheduleRows).ToArrayAsync());
        Assert.All(objects, item => Assert.Contains(item, db.Database.SqlQueryRaw<string>(Objects).ToArray()));
        Assert.Equal(ddl.Replace("\"OwnerUserId\" TEXT NOT NULL", "\"OwnerUserId\" TEXT NULL", StringComparison.Ordinal), await TableSqlAsync(db));
        await db.Database.CloseConnectionAsync();
        await using (var fresh = new AllstarrDbContext(database.Options))
        {
            await fresh.Database.OpenConnectionAsync();
            await fresh.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON");
            await AssertOwnerConstraintsAsync(fresh, user);
            Assert.Equal(0, await fresh.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        }
        Assert.Equal(rows, await db.Database.SqlQueryRaw<string>(ScheduleRows).ToArrayAsync());
        await migrator.MigrateAsync(Before);
        Assert.Equal(ddl, await TableSqlAsync(db));
        Assert.Equal(rows, await db.Database.SqlQueryRaw<string>(ScheduleRows).ToArrayAsync());
        Assert.Equal(objects, await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE job_schedules SET Enabled=0 WHERE Id={schedule}");
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM schedule_fixture_audit").SingleAsync());
        await migrator.MigrateAsync();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT writable_schema AS Value FROM pragma_writable_schema").SingleAsync());
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DowngradeRefusesHouseholdSchedulesAndArtifactRowsBeforeChangingAnything(bool artifact)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync("20261025020354_ScheduledBackups");
        if (artifact)
        {
            db.BackupArtifacts.Add(new() { Id = Guid.NewGuid(), Origin = "manual", CreatedAt = DateTimeOffset.UtcNow, Directory = "fixture", StorageBinding = "fixture" });
            await db.SaveChangesAsync();
        }
        else await InsertAsync(db, Guid.NewGuid(), null, "storage.backup");
        var before = await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync();
        var ddl = await TableSqlAsync(db);
        var failure = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(Before));
        Assert.Contains("CK_backups_require_restore", failure.Message);
        Assert.Equal(ddl, await TableSqlAsync(db));
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT writable_schema AS Value FROM pragma_writable_schema").SingleAsync());
        Assert.Equal(artifact ? 1 : 0, await db.BackupArtifacts.CountAsync());
        Assert.Equal(artifact ? 0 : 1, await db.JobSchedules.CountAsync());
    }

    [Fact]
    public async Task UnexpectedOldDefinitionFailsWithoutMutationAndPooledConnectionRemainsUsable()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);
        await SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE job_schedules ADD COLUMN OperatorNote TEXT NULL");
        var ddl = await TableSqlAsync(db);
        var rows = await db.Database.SqlQueryRaw<string>(ScheduleRows).ToArrayAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync());
        Assert.Equal(ddl, await TableSqlAsync(db));
        Assert.Equal(rows, await db.Database.SqlQueryRaw<string>(ScheduleRows).ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT writable_schema AS Value FROM pragma_writable_schema").SingleAsync());
        await db.Database.CloseConnectionAsync();
        await using var fresh = new AllstarrDbContext(database.Options);
        Assert.Equal(0, await fresh.Database.SqlQueryRaw<int>("SELECT writable_schema AS Value FROM pragma_writable_schema").SingleAsync());
        Assert.Equal(1, await fresh.Users.CountAsync());
        Assert.Equal("ok", await fresh.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }

    [Fact]
    public async Task FailureAfterEnablingSchemaWritesResetsThePooledConnectionAndPreservesAllRows()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using (var setup = new AllstarrDbContext(database.Options))
        {
            await setup.GetService<IMigrator>().MigrateAsync(Before);
            await SeedAsync(setup);
        }
        var inject = new FailSchemaChange();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(inject).Options;
        await using (var db = new AllstarrDbContext(options))
        {
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT writable_schema AS Value FROM pragma_writable_schema").SingleAsync());
            Assert.Equal(1, await db.Users.CountAsync());
            Assert.Equal(1, await db.JobSchedules.CountAsync());
        }
        await using var fresh = new AllstarrDbContext(database.Options);
        Assert.Equal(0, await fresh.Database.SqlQueryRaw<int>("SELECT writable_schema AS Value FROM pragma_writable_schema").SingleAsync());
        Assert.Equal(1, await fresh.Users.CountAsync());
        await fresh.GetService<IMigrator>().MigrateAsync();
        Assert.Equal(1, await fresh.JobSchedules.CountAsync());
    }

    private static async Task AssertOwnerConstraintsAsync(AllstarrDbContext db, Guid user)
    {
        var household = Guid.NewGuid();
        await InsertAsync(db, household, null, "storage.backup");
        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, Guid.NewGuid(), null, "playlist.materialize"));
        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, Guid.NewGuid(), user, "storage.backup"));
        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, Guid.NewGuid(), null, "Storage.Backup"));
        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, Guid.NewGuid(), null, "storage.backup"));
        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, Guid.NewGuid(), Guid.NewGuid(), "playlist.materialize"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE job_schedules SET JobType='playlist.materialize' WHERE Id={household}"));
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM job_schedules WHERE Id={household}");
    }
    private static Task<int> InsertAsync(AllstarrDbContext db, Guid id, Guid? owner, string type) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO job_schedules (Id,OwnerUserId,JobType,CronExpression,TimeZoneId,OverlapPolicy,MisfirePolicy,RetryPolicyJson,PayloadTemplateJson,Enabled,CreatedAt,UpdatedAt,Revision)
        VALUES ({id},{owner},{type},'0 3 * * *','UTC','Skip','RunOnce',{"{}"},{"{}"},1,0,0,1)
        """);
    private static Task<string> TableSqlAsync(AllstarrDbContext db) => db.Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_schema WHERE name='job_schedules'").SingleAsync();
    private sealed class FailSchemaChange : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("/* backup-owner-constraint */", StringComparison.Ordinal))
                command.CommandText = "/* backup-owner-constraint */ PRAGMA writable_schema=ON; SELECT * FROM missing_backup_migration_fixture; PRAGMA writable_schema=RESET;";
            return ValueTask.FromResult(result);
        }
    }
}
