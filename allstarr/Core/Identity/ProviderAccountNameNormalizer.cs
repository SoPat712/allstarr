using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Identity;

public sealed class ProviderAccountNameNormalizer(
    IDbContextFactory<AllstarrDbContext> factory,
    IProviderRegistry registry,
    DurableStorageState state,
    ILogger<ProviderAccountNameNormalizer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (state.GetSnapshot().Readiness != DurableStorageReadiness.Ready) return;
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var account in accounts)
        {
            var name = ProviderAccountNames.DisplayName(account, registry);
            if (account.DisplayName == name) continue;
            account.DisplayName = name;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            account.Revision++;
            changed++;
        }
        if (changed == 0) return;
        context.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            Category = "provider-account",
            Action = "provider-account.default-names-updated",
            Outcome = "succeeded",
            CorrelationId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { count = changed }),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Updated default names for {Count} provider accounts", changed);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
