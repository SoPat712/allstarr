using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class AuditEventRetentionServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AllstarrDbContext>(options =>
            options.UseSqlite(database.ConnectionString));
        provider = services.BuildServiceProvider();

    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        if (database is not null) await database.DisposeAsync();
    }

    [Fact]
    public async Task PruneNowAsync_RemovesExpiredEventsAndKeepsRecentEvents()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = Event(now.AddDays(-31), "expired");
        var recent = Event(now.AddDays(-29), "recent");

        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            database.AuditEvents.AddRange(expired, recent);
            await database.SaveChangesAsync();
        }

        await Worker(retentionDays: 30, maximumRows: 1000).PruneNowAsync();

        await using var verification = provider.CreateAsyncScope();
        var remaining = await verification.ServiceProvider.GetRequiredService<AllstarrDbContext>()
            .AuditEvents.AsNoTracking().Select(item => item.Action).ToListAsync();
        Assert.Equal(["recent"], remaining);
    }

    [Fact]
    public async Task PruneNowAsync_RemovesOldestOverflowAndKeepsConfiguredMaximum()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            database.AuditEvents.AddRange(Enumerable.Range(0, 1005)
                .Select(index => Event(now.AddSeconds(index), $"event-{index:D4}")));
            await database.SaveChangesAsync();
        }

        await Worker(retentionDays: 365, maximumRows: 1000).PruneNowAsync();

        await using var verification = provider.CreateAsyncScope();
        var events = await verification.ServiceProvider.GetRequiredService<AllstarrDbContext>()
            .AuditEvents.AsNoTracking().OrderBy(item => item.CreatedAt).ToListAsync();
        Assert.Equal(1000, events.Count);
        Assert.Equal("event-0005", events[0].Action);
        Assert.Equal("event-1004", events[^1].Action);
    }

    [Theory]
    [InlineData("duplicates.complete")]
    [InlineData("recording-equivalence.complete")]
    public async Task PruneNowAsync_PreservesCatalogRepairCompletionThroughExpiryAndOverflow(string completionAction)
    {
        var now = DateTimeOffset.UtcNow;
        var completed = Event(now.AddDays(-60), completionAction, "catalog-identity-maintenance");
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            database.AuditEvents.Add(completed);
            database.AuditEvents.Add(Event(now.AddDays(-60), "detail", "catalog-identity-maintenance"));
            database.AuditEvents.AddRange(Enumerable.Range(0, 1001)
                .Select(index => Event(now.AddSeconds(index), $"recent-{index}")));
            await database.SaveChangesAsync();
        }
        await Worker(retentionDays: 30, maximumRows: 1000).PruneNowAsync();
        await using var verification = provider.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(item => item.Id == completed.Id));
        Assert.Equal(1000, await db.AuditEvents.CountAsync());
        Assert.Single(await db.AuditEvents.Where(item => item.Category == "catalog-identity-maintenance").ToArrayAsync());
    }

    [Fact]
    public async Task PruneNowAsync_RemovesOnlyExpiredFinishedPlaybackJobs()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            var expired = Job("playback.signal.process", DurableJobState.Succeeded, now.AddDays(-31), "expired");
            database.Jobs.AddRange(
                expired,
                Job("playback.signal.process", DurableJobState.Cancelled, now.AddDays(-31), "cancelled"),
                Job("playback.signal.process", DurableJobState.Succeeded, now.AddDays(-29), "recent"),
                Job("playback.signal.process", DurableJobState.Failed, now.AddDays(-31), "failed"),
                Job("playback.signal.process", DurableJobState.Pending, now.AddDays(-31), "pending"),
                Job("playlist.sync", DurableJobState.Succeeded, now.AddDays(-31), "other-type"));
            database.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.NewGuid(),
                Category = "job-attempt",
                Action = "attempt-completed",
                SubjectType = "job",
                SubjectId = expired.Id.ToString("D"),
                Outcome = "succeeded",
                DetailsJson = "{}",
                CorrelationId = "retention-fixture",
                CreatedAt = now.AddDays(-31)
            });
            await database.SaveChangesAsync();
        }

        await Worker(retentionDays: 30, maximumRows: 1000).PruneNowAsync();

        await using var verification = provider.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        var remaining = await context.Jobs.AsNoTracking()
            .OrderBy(item => item.IdempotencyKey).Select(item => item.IdempotencyKey).ToListAsync();
        Assert.Equal(["failed", "other-type", "pending", "recent"], remaining);
        Assert.Empty(await context.AuditEvents.Where(item => item.Category == "job-attempt").ToListAsync());
    }

    [Fact]
    public async Task PruneNowAsync_RemovesExpiredFinishedTagJobsAcrossBatchesAndPreservesReferences()
    {
        var now = DateTimeOffset.UtcNow;
        var referenced = Job("recording.tags.refresh", DurableJobState.Succeeded, now.AddDays(-40), "referenced");
        var audited = Job("recording.tags.refresh", DurableJobState.Succeeded, now.AddDays(-40), "audited");
        var audit = Event(now.AddDays(-1), "retained-evidence");
        audit.SubjectType = "job";
        audit.SubjectId = audited.Id.ToString("D");
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            context.Jobs.AddRange(Enumerable.Range(0, 105).Select(index =>
                Job("recording.tags.refresh", DurableJobState.Succeeded, now.AddDays(-31), $"expired-{index:D3}")));
            context.Jobs.AddRange(
                referenced, audited,
                Job("recording.tags.refresh", DurableJobState.Cancelled, now.AddDays(-31), "cancelled"),
                Job("recording.tags.refresh", DurableJobState.Succeeded, now.AddDays(-29), "recent"),
                Job("recording.tags.refresh", DurableJobState.Failed, now.AddDays(-31), "failed"),
                Job("recording.tags.refresh", DurableJobState.Pending, now.AddDays(-31), "pending"),
                Job("recording.tags.refresh", DurableJobState.Running, now.AddDays(-31), "running"),
                Job("recording.tags.refresh", DurableJobState.RetryScheduled, now.AddDays(-31), "retry"));
            context.BackupArtifacts.Add(new() { Id = Guid.NewGuid(), JobId = referenced.Id, CreatedAt = now.AddDays(-40) });
            context.AuditEvents.Add(audit);
            await context.SaveChangesAsync();
        }

        await Worker(retentionDays: 30, maximumRows: 1000).PruneNowAsync();

        await using var verification = provider.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        var remaining = await database.Jobs.AsNoTracking().OrderBy(item => item.IdempotencyKey)
            .Select(item => item.IdempotencyKey).ToListAsync();
        Assert.Equal(["audited", "failed", "pending", "recent", "referenced", "retry", "running"], remaining);
        Assert.True(await database.BackupArtifacts.AnyAsync(item => item.JobId == referenced.Id));
        Assert.True(await database.AuditEvents.AnyAsync(item => item.Id == audit.Id));
    }

    [Theory]
    [InlineData("job-attempt")]
    [InlineData("managed-storage")]
    public async Task PruneNowAsync_ExpiresJobHistorySoonerThanOtherEvents(string category)
    {
        var now = DateTimeOffset.UtcNow;
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            database.AuditEvents.AddRange(
                Event(now.AddDays(-8), "admin-old"),
                Event(now.AddDays(-8), "job-old", category),
                Event(now.AddDays(-6), "job-recent", category));
            await database.SaveChangesAsync();
        }

        await Worker(retentionDays: 30, maximumRows: 1000).PruneNowAsync();

        await using var verification = provider.CreateAsyncScope();
        var remaining = await verification.ServiceProvider.GetRequiredService<AllstarrDbContext>()
            .AuditEvents.AsNoTracking().OrderBy(item => item.Action).Select(item => item.Action).ToListAsync();
        Assert.Equal(["admin-old", "job-recent"], remaining);
    }

    [Theory]
    [InlineData("job-attempt")]
    [InlineData("managed-storage")]
    public async Task PruneNowAsync_TrimsJobHistoryBeforeOtherEventsWhenOverTheRowLimit(string category)
    {
        var now = DateTimeOffset.UtcNow;
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            database.AuditEvents.AddRange(Enumerable.Range(0, 1000)
                .Select(index => Event(now.AddDays(-2).AddSeconds(index), $"admin-{index:D4}")));
            database.AuditEvents.AddRange(Enumerable.Range(0, 10)
                .Select(index => Event(now.AddSeconds(index), $"job-{index:D2}", category)));
            await database.SaveChangesAsync();
        }

        await Worker(retentionDays: 365, maximumRows: 1000).PruneNowAsync();

        await using var verification = provider.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        Assert.Equal(1000, await context.AuditEvents.CountAsync(item => item.Category == "test"));
        Assert.Empty(await context.AuditEvents.Where(item => item.Category == category).ToListAsync());
    }

    private AuditEventRetentionService Worker(int retentionDays, int maximumRows)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:EventLog:RetentionDays"] = retentionDays.ToString(),
            ["Operations:EventLog:MaximumRows"] = maximumRows.ToString(),
            ["Operations:EventLog:CleanupBatchSize"] = "100"
        }).Build();

        return new AuditEventRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            TimeProvider.System,
            NullLogger<AuditEventRetentionService>.Instance);
    }

    private static DurableJobRecord Job(string type, DurableJobState state, DateTimeOffset finishedAt, string key) => new()
    {
        Id = Guid.NewGuid(),
        ScopeKey = "user:test",
        Type = type,
        IdempotencyKey = key,
        State = state,
        MaxAttempts = 5,
        MaxDeferrals = 96,
        AvailableAt = finishedAt,
        CompletedAt = state is DurableJobState.Pending ? null : finishedAt,
        CreatedAt = finishedAt,
        UpdatedAt = finishedAt
    };

    private static AuditEventRecord Event(DateTimeOffset createdAt, string action, string category = "test") => new()
    {
        Id = Guid.NewGuid(),
        Category = category,
        Action = action,
        Outcome = "success",
        CorrelationId = Guid.NewGuid().ToString("N"),
        DetailsJson = "{}",
        CreatedAt = createdAt
    };
}
