using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class HomeNotableActivityMigrationTests
{
    [Fact]
    public async Task MigrationAddsOnlyIndexesAndKeepsExistingEvents()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261106120000_LyricsRefresh");
        var routine = new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            Category = "history",
            Action = "history-resolution",
            Outcome = "running",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var problem = new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            Category = "history",
            Action = "history-resolution",
            Outcome = "failed",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AuditEvents.AddRange(routine, problem);
        await db.SaveChangesAsync();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' ORDER BY name").ToArrayAsync();
        var columns = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('audit_events') ORDER BY name").ToArrayAsync();

        await migrator.MigrateAsync();

        Assert.Equal(tables, await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' ORDER BY name").ToArrayAsync());
        Assert.Equal(columns, await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('audit_events') ORDER BY name").ToArrayAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync());
        Assert.Equal(problem.Id, Assert.Single(await db.AuditEvents.FromSqlRaw(HomeActivityPolicy.AuditQuery).ToArrayAsync()).Id);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type = 'index' AND name IN ('IX_home_notable_jobs', 'IX_home_notable_audits')").SingleAsync());
        var operations = db.GetService<IMigrationsAssembly>();
        var migration = operations.CreateMigration(operations.Migrations.Last().Value, db.Database.ProviderName!);
        Assert.All(migration.UpOperations, operation => Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation>(operation));
    }

    [Theory]
    [InlineData("history-resolution", "running", "{}", false)]
    [InlineData("history-resolution", "failed", "{}", true)]
    [InlineData("history-resolution", "blocked", "{}", true)]
    [InlineData("automatic.review", "rematched", "{}", false)]
    [InlineData("automatic.review", "unresolved", "{}", true)]
    [InlineData("recording.odesli.enrich", "success", "{}", false)]
    [InlineData("run-completed", "success", "{\"name\":\"History resolution\"}", false)]
    [InlineData("run-completed", "failed", "{\"name\":\"History resolution\"}", true)]
    [InlineData("refreshed", "success", "{}", true)]
    [InlineData("refreshed", "success", "{invalid", true)]
    public async Task NotableAuditIndexPreservesProblemsAndNamedMaintenance(string action, string outcome, string details, bool notable)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        db.AuditEvents.Add(new()
        {
            Id = Guid.CreateVersion7(),
            Category = action == "run-completed" ? "background-task" : "history",
            Action = action,
            Outcome = outcome,
            DetailsJson = details,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.Equal(notable ? 1 : 0, await db.AuditEvents.FromSqlRaw(HomeActivityPolicy.AuditQuery).CountAsync());
    }

    [Theory]
    [InlineData("listening-history.resolve", "Succeeded", "succeeded", false)]
    [InlineData("listening-history.resolve", "Failed", "failed", true)]
    [InlineData("listening-history.resolve", "RetryScheduled", "deferred", false)]
    [InlineData("listening-history.resolve", "RetryScheduled", null, true)]
    [InlineData("recording.odesli.enrich", "Succeeded", "succeeded", false)]
    [InlineData("recording.odesli.enrich", "Failed", "failed", true)]
    [InlineData("recording.odesli.enrich", "RetryScheduled", "deferred", false)]
    [InlineData("track-match.rematch", "Succeeded", "succeeded", false)]
    [InlineData("favorite.process", "Succeeded", "succeeded", true)]
    [InlineData("favorite.process", "Pending", null, false)]
    public async Task NotableJobIndexTracksBulkStateChanges(string type, string state, string? outcome, bool notable)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var job = new DurableJobRecord
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            ScopeKey = "household",
            IdempotencyKey = "fixture",
            State = Enum.Parse<DurableJobState>(state),
            LastAttemptOutcome = outcome,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        var selected = db.Jobs.FromSqlRaw(HomeActivityPolicy.JobQuery).AsNoTracking();
        Assert.Equal(notable ? 1 : 0, await selected.CountAsync());

        await db.Jobs.ExecuteUpdateAsync(update => update.SetProperty(item => item.State, DurableJobState.Failed));

        Assert.Equal(job.Id, (await selected.SingleAsync()).Id);
    }
}
