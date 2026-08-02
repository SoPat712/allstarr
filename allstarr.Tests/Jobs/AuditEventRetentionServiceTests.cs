using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class AuditEventRetentionServiceTests : IAsyncLifetime
{
    private PostgresTestDatabase database = null!;
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        database = await PostgresTestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AllstarrDbContext>(options =>
            options.UseNpgsql(database.ConnectionString));
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

    private static AuditEventRecord Event(DateTimeOffset createdAt, string action) => new()
    {
        Id = Guid.NewGuid(),
        Category = "test",
        Action = action,
        Outcome = "success",
        CorrelationId = Guid.NewGuid().ToString("N"),
        DetailsJson = "{}",
        CreatedAt = createdAt
    };
}
