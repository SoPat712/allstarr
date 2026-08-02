using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class EndpointUsageAuditTests
{
    [Fact]
    public async Task RecordsSummarizesAndClearsOnlyRedactedEndpointEvents()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var context = new AllstarrDbContext(database.Options))
        {
            context.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                Category = "unrelated",
                Action = "keep",
                Outcome = "success",
                CorrelationId = "test",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var audit = new EndpointUsageAudit(new Factory(database.Options), TimeProvider.System);
        await audit.RecordAsync("get", "Items?api_key=private", null, null, "one");
        await audit.RecordAsync("GET", "Items?token=private", null, null, "two");
        await audit.RecordAsync("post", "Sessions", null, null, "three");

        var summary = await audit.SummarizeAsync(10, null);
        Assert.Equal(2, summary.TotalEndpoints);
        Assert.Equal(3, summary.TotalRequests);
        Assert.Equal(new EndpointUsageCount("GET /Items", 2), summary.Endpoints[0]);

        await using (var verification = new AllstarrDbContext(database.Options))
        {
            var details = await verification.AuditEvents
                .Where(item => item.Category == EndpointUsageAudit.Category)
                .Select(item => item.DetailsJson)
                .ToListAsync();
            Assert.DoesNotContain(details, item => item.Contains("private", StringComparison.Ordinal));
        }

        Assert.Equal(3, await audit.ClearAsync());
        await using var remaining = new AllstarrDbContext(database.Options);
        Assert.Equal("unrelated", (await remaining.AuditEvents.SingleAsync()).Category);
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
