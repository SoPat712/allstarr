using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed class ProviderCtsWarmupService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IProviderRegistry providers,
    ProviderCtsDiagnosticRunner runner,
    TimeProvider timeProvider,
    ILogger<ProviderCtsWarmupService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, timeProvider, stoppingToken);
        await MeasureAllAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await MeasureAllAsync(stoppingToken);
        }
    }

    private async Task MeasureAllAsync(CancellationToken cancellationToken)
    {
        ProviderCtsAccount[] accounts;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var enabledAccounts = await db.ProviderAccounts
                .AsNoTracking()
                .Where(account => account.Enabled)
                .OrderBy(account => account.ProviderId)
                .ThenBy(account => account.Id)
                .ToArrayAsync(cancellationToken);
            var identities = await db.BackendIdentities
                .AsNoTracking()
                .OrderByDescending(identity => identity.LastSeenAt)
                .ToArrayAsync(cancellationToken);

            var managed = enabledAccounts
                .Select(account =>
                {
                    var identity = identities.FirstOrDefault(candidate =>
                        (account.TenantId == null || candidate.TenantId == account.TenantId) &&
                        (account.OwnerUserId == null || candidate.UserId == account.OwnerUserId));
                    return identity == null
                        ? null
                        : new ProviderCtsAccount(
                            account.Id,
                            account.ProviderId,
                            identity.TenantId,
                            account.OwnerUserId ?? identity.UserId,
                            identity.BackendType,
                            identity.BackendInstanceId,
                            identity.PrincipalId,
                            identity.LastSeenAt);
                })
                .Where(account => account != null)
                .Cast<ProviderCtsAccount>()
                .ToArray();
            var accountFreeProviders = providers.FindByCapability(ProviderCapabilityKind.Streaming)
                .Where(provider => provider.Capabilities.Single(capability =>
                    capability.Capability == ProviderCapabilityKind.Streaming).AccountRequirement ==
                    ProviderAccountRequirement.None)
                .ToArray();
            var accountFree = identities
                .GroupBy(identity => new { identity.TenantId, identity.UserId })
                .Select(group => group.First())
                .SelectMany(identity => accountFreeProviders.Select(provider => new ProviderCtsAccount(
                    null,
                    provider.Id,
                    identity.TenantId,
                    identity.UserId,
                    identity.BackendType,
                    identity.BackendInstanceId,
                    identity.PrincipalId,
                    identity.LastSeenAt)))
                .ToArray();
            accounts = [.. managed, .. accountFree];
        }

        foreach (var account in accounts
                     .GroupBy(item => new { item.ProviderId, item.AccountId, item.TenantId, item.OwnerUserId })
                     .Select(group => group.First()))
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (!providers.TryGetCapability<IProviderStreamingCapability>(
                    account.ProviderId, ProviderCapabilityKind.Streaming, out _)) continue;

            try
            {
                var actor = new ProviderActorContext(
                    account.TenantId,
                    ProviderActorKind.SystemJob,
                    null,
                    new ProviderBackendPrincipal(
                        account.BackendType,
                        account.BackendInstanceId,
                        account.PrincipalId),
                    durableJobId: Guid.CreateVersion7(),
                    actingForUserId: account.OwnerUserId);
                var result = await runner.MeasureAsync(
                    actor,
                    account.ProviderId,
                    account.AccountId,
                    ProviderAudioQuality.Any,
                    $"cts-warmup-{account.AccountId?.ToString("N") ?? "account-free"}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                    cancellationToken: cancellationToken);
                if (!result.Succeeded)
                {
                    logger.LogInformation(
                        "Cold CTS probe did not complete for {Provider} ({Stage}/{Error})",
                        account.ProviderId,
                        result.Stage,
                        result.Error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Cold CTS probe failed for {Provider} ({ExceptionType})",
                    account.ProviderId,
                    exception.GetType().Name);
            }
        }
    }

    private sealed record ProviderCtsAccount(
        Guid? AccountId,
        string ProviderId,
        Guid TenantId,
        Guid OwnerUserId,
        string BackendType,
        string BackendInstanceId,
        string PrincipalId,
        DateTimeOffset LastSeenAt);
}
