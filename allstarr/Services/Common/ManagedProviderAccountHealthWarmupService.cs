using System.Text.Json;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

/// <summary>
/// Gives enabled managed accounts a truthful initial status after startup. Probes
/// run in the background so an unavailable optional provider never delays boot.
/// </summary>
public sealed class ManagedProviderAccountHealthWarmupService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    EncryptedSecretStore secretStore,
    ProviderStatusManager statusManager,
    ILogger<ManagedProviderAccountHealthWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        await ProbeAllAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProbeAllAsync(stoppingToken);
        }
    }

    private async Task ProbeAllAsync(CancellationToken stoppingToken)
    {
        await ProbeAccountFreeProvidersAsync(stoppingToken);

        await using var context = await contextFactory.CreateDbContextAsync(stoppingToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .Where(item => item.Enabled && item.SecretReferenceId != null)
            .OrderBy(item => item.ProviderId)
            .ThenBy(item => item.Id)
            .ToListAsync(stoppingToken);

        foreach (var account in accounts)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var secrets = await ReadSecretsAsync(account, stoppingToken);
                var capabilities = statusManager.GetAllManagedStatuses(account.ProviderId, account.Id, secrets)
                    .Where(item => item.IsSupported &&
                                   item.Configuration != ProviderConfigurationState.NeedsConfiguration &&
                                   statusManager.CanTestCapability(item.Provider, item.Capability))
                    .ToArray();
                foreach (var capability in capabilities)
                {
                    await statusManager.TestManagedProviderCapabilityAsync(
                        account.ProviderId,
                        capability.Capability,
                        account.Id,
                        secrets,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Managed provider account startup probe failed for {Provider} ({ExceptionType})",
                    account.ProviderId,
                    ex.GetType().Name);
            }
        }
    }

    private async Task ProbeAccountFreeProvidersAsync(CancellationToken stoppingToken)
    {
        var capabilities = statusManager.GetAllAccountFreeStatuses()
            .Where(item => item.IsSupported &&
                           item.IsEnabled &&
                           item.Configuration != ProviderConfigurationState.NeedsConfiguration &&
                           statusManager.CanTestCapability(item.Provider, item.Capability))
            .ToArray();

        foreach (var capability in capabilities)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await statusManager.TestAccountFreeProviderCapabilityAsync(
                    capability.Provider,
                    capability.Capability,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Provider background probe failed for {Provider}/{Capability} ({ExceptionType})",
                    capability.Provider,
                    capability.Capability,
                    ex.GetType().Name);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadSecretsAsync(
        ProviderAccountRecord account,
        CancellationToken cancellationToken)
    {
        using var lease = await secretStore.OpenAsync(
            account.SecretReferenceId!.Value,
            new SecretAccessContext(account.TenantId, AllowGlobal: account.TenantId == null),
            cancellationToken);
        using var document = JsonDocument.Parse(lease.Value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Provider account credential is not an object.");
        }

        return document.RootElement.EnumerateObject()
            .Select(property => new
            {
                Name = new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()),
                Value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Name, item => item.Value!, StringComparer.Ordinal);
    }
}
