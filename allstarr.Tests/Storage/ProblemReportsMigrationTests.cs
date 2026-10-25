using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

[Trait("Lane", "ReleaseCritical")]
public sealed class ProblemReportsMigrationTests
{
    private const string Before = "20261025020354_ScheduledBackups";
    private const string Objects = """
        SELECT type||'|'||name||'|'||sql AS Value FROM sqlite_schema WHERE sql IS NOT NULL
        AND name NOT LIKE 'sqlite_%' AND name<>'problem_reports' AND name NOT LIKE 'IX_problem_reports_%' ORDER BY type,name
        """;

    [Fact]
    public async Task PopulatedUpgradePreservesEveryExistingObjectAndValueAndEmptyDowngradeIsReversible()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        var owner = Guid.NewGuid();
        db.Users.Add(User(owner));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE report_fixture (Value TEXT NOT NULL);
            INSERT INTO report_fixture VALUES('existing');
            CREATE TRIGGER report_fixture_trigger AFTER UPDATE ON report_fixture BEGIN SELECT 1; END;
            CREATE VIEW report_fixture_view AS SELECT Value FROM report_fixture;
            CREATE INDEX report_fixture_index ON report_fixture(Value);
            """);
        var objects = await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync();
        await migrator.MigrateAsync();
        Assert.Equal(objects, await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync());
        Assert.Equal("Listener", (await db.Users.SingleAsync()).DisplayName);
        Assert.Equal("existing", await db.Database.SqlQueryRaw<string>("SELECT Value FROM report_fixture").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        await migrator.MigrateAsync(Before);
        Assert.Equal(objects, await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync());
        await migrator.MigrateAsync();
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }

    [Fact]
    public async Task PopulatedDowngradeRefusesBeforeMutationAndBoundsRemainEnforced()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = Guid.NewGuid();
        db.Users.Add(User(owner));
        var report = Record(owner);
        db.ProblemReports.Add(report);
        await db.SaveChangesAsync();
        var objects = await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync();
        var failure = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(Before));
        Assert.Contains("CK_problem_reports_require_restore", failure.Message);
        Assert.Equal(objects, await db.Database.SqlQueryRaw<string>(Objects).ToArrayAsync());
        Assert.Equal(report.Id, (await db.ProblemReports.SingleAsync()).Id);
        foreach (var invalid in new[] { "note", "kind", "status", "context", "bytes", "revision", "owner" })
        {
            await using var attempt = new AllstarrDbContext(database.Options);
            var candidate = Record(owner);
            if (invalid == "note") candidate.Note = new string('x', 2001);
            if (invalid == "kind") candidate.Kind = "invented";
            if (invalid == "status") candidate.Status = "invented";
            if (invalid == "context") candidate.ContextJson = "{}";
            if (invalid == "bytes") candidate.ContextJson = "{\"schemaVersion\":1,\"value\":\"" + new string('界', 12000) + "\"}";
            if (invalid == "revision") candidate.Revision = 0;
            if (invalid == "owner") candidate.ActorUserId = Guid.NewGuid();
            attempt.ProblemReports.Add(candidate);
            await Assert.ThrowsAsync<DbUpdateException>(() => attempt.SaveChangesAsync());
        }
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }
    private static UserRecord User(Guid id) => new() { Id = id, Enabled = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "listener", DisplayName = "Listener" };
    private static ProblemReportRecord Record(Guid owner) => new()
    {
        Id = Guid.NewGuid(),
        ActorUserId = owner,
        RequestId = Guid.NewGuid(),
        RequestFingerprint = new string('a', 64),
        Note = "Fixture",
        ReporterName = "Listener",
        ContextJson = "{\"schemaVersion\":1}",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
