using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Identity;

public sealed class IdentityBootstrapper : IHostedService
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageState _storageState;
    private readonly BackendIdentityResolver _resolver;

    public IdentityBootstrapper(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageState storageState,
        BackendIdentityResolver resolver)
    {
        _contextFactory = contextFactory;
        _storageState = storageState;
        _resolver = resolver;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        _ = await _resolver.EnsureDefaultTenantAsync(context, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
