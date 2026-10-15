using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class BackgroundTaskActivityTests
{
    [Fact]
    public async Task MaintenanceObservation_UsesExistingLedgerAndRestoresLastRunAfterRestart()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var clock = new Clock();
        var activity = NewActivity();
        activity.Register("cleanup", "Cleanup", TimeSpan.FromHours(6), false);
        Assert.Equal(clock.Now.AddHours(6), Assert.Single(await activity.ReadAsync(default)).NextRunAt);
        activity.Started("cleanup");
        Assert.Equal("Running", Assert.Single(await activity.ReadAsync(default)).State);
        clock.Now = clock.Now.AddMinutes(1);
        await activity.CompletedAsync("cleanup", false, default);
        var failed = Assert.Single(await activity.ReadAsync(default));
        Assert.Equal(clock.Now, failed.LastRunAt);
        Assert.Equal(clock.Now.AddHours(6), failed.NextRunAt);
        Assert.Equal("Retrying", failed.State);
        var restarted = NewActivity();
        restarted.Register("cleanup", "Cleanup", TimeSpan.FromHours(6), false);
        Assert.Equal(clock.Now, Assert.Single(await restarted.ReadAsync(default)).LastRunAt);
        await using var db = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal("background-task", audit.Category);
        Assert.Null(audit.ActorUserId);
        Assert.Equal("failed", audit.Outcome);
        Assert.Empty(await db.Jobs.ToArrayAsync());
        BackgroundTaskActivity NewActivity() => new(factory, clock, NullLogger<BackgroundTaskActivity>.Instance);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
