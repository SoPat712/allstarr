using System.Collections.Concurrent;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// After a listener's recap is read again, as it is once they have played something, reads the recap's song rows too.
/// A song new to a top list then has its ids, artists and album ready before the listener's app asks for the recap.
/// </summary>
public sealed class V2StatsRowsWarmer(IServiceScopeFactory scopes, IDbContextFactory<AllstarrDbContext> factory,
    ListeningStatsService stats, IPlatformClock clock, ILogger<V2StatsRowsWarmer> logger) : IHostedService
{
    // A play makes several recaps read again within a few seconds; one pass after they settle covers them all.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<Guid, int> requested = new();
    private readonly CancellationTokenSource stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        stats.Rebuilt = Request;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        stats.Rebuilt = null;
        stopping.Cancel();
        return Task.CompletedTask;
    }

    internal void Request(Guid owner)
    {
        // The first request for a listener starts a pass; later ones while it runs make it go round once more.
        if (requested.AddOrUpdate(owner, 1, (_, count) => count + 1) == 1) _ = Task.Run(() => RunAsync(owner));
    }

    private async Task RunAsync(Guid owner)
    {
        try
        {
            while (true)
            {
                var seen = requested.GetValueOrDefault(owner);
                await Task.Delay(Settle, stopping.Token);
                await WarmAsync(owner, stopping.Token);
                if (requested.TryUpdate(owner, 0, seen)) break;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) { logger.LogDebug("Recap rows were not read ahead ({ErrorType})", error.GetType().Name); }
        finally { requested.TryRemove(owner, out _); }
    }

    private async Task WarmAsync(Guid owner, CancellationToken token)
    {
        UserRecord? user;
        await using (var db = await factory.CreateDbContextAsync(token))
            user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled && !item.IsSynthetic &&
                item.BackendType == "jellyfin", token);
        if (user == null) return;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, user.BackendInstanceId, user.BackendPrincipalId,
            new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
            "recap-rows", clock.UtcNow.AddMinutes(2), token)
        { IsReadOnly = true, UseStoredBackendCredentials = true };
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<V2StatsWarmUp>().WarmAsync(execution, await stats.SavedTimeZoneAsync(owner, token), token);
    }
}
