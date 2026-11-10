using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

internal static class FeedReadCache
{
    private sealed record Entry<T>(T Value, DateTimeOffset ReadAt, long LibraryRevision) where T : class;

    internal static async Task<T?> ReadAsync<T>(IDbContextFactory<AllstarrDbContext> factory,
        IApplicationCache cache, ApplicationCacheRequestCoalescer coalescer, IPlatformClock clock,
        ProtocolExecutionContext context, string scope, BackendLibraryAccess? access, HttpContext? http,
        Func<HttpContext?, ProtocolExecutionContext, Task<T?>> read) where T : class
    {
        if (access?.Succeeded == false) return await read(http, context);
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken);
        var key = db.FeedReadKey(context, scope, access);
        var cached = await cache.GetAsync<Entry<T>>(key);
        if (cached != null && clock.UtcNow - cached.ReadAt < TimeSpan.FromSeconds(60))
        {
            if (clock.UtcNow - cached.ReadAt >= TimeSpan.FromSeconds(30) || cached.LibraryRevision != db.FeedReadLibraryRevisionFor(context.Actor?.EffectiveUserId))
            {
                var services = http?.RequestServices.GetRequiredService<IServiceScopeFactory>();
                var snapshot = http == null ? null : CopyRequest(http);
                var logger = http?.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("FeedReadCache");
                _ = RefreshAsync();

                async Task RefreshAsync()
                {
                    var started = false;
                    try
                    {
                        await coalescer.RunAsync(key, () =>
                        {
                            started = true;
                            // The refresh owns its scope and deadline; request cancellation and timing do not flow into it.
                            using (ExecutionContext.SuppressFlow())
                                return Task.Run(async () =>
                                {
                                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                    var refresh = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId,
                                        context.VerifiedBackendPrincipalId, context.Principal, context.CorrelationId,
                                        clock.UtcNow.AddSeconds(30), deadline.Token, context.Client)
                                    {
                                        Policy = context.Policy,
                                        IsReadOnly = context.IsReadOnly,
                                        UseStoredBackendCredentials = context.UseStoredBackendCredentials,
                                        BackendCredentialReferenceId = context.BackendCredentialReferenceId
                                    };
                                    if (services == null) return await RecomputeAsync(null, refresh);
                                    await using var background = services.CreateAsyncScope();
                                    DefaultHttpContext backgroundHttp = snapshot!;
                                    backgroundHttp.RequestServices = background.ServiceProvider;
                                    backgroundHttp.RequestAborted = deadline.Token;
                                    backgroundHttp.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = refresh;
                                    var accessor = background.ServiceProvider.GetService<IHttpContextAccessor>();
                                    if (accessor != null) accessor.HttpContext = backgroundHttp;
                                    try { return await RecomputeAsync(backgroundHttp, refresh); }
                                    finally { if (accessor != null) accessor.HttpContext = null; }
                                });
                        }, CancellationToken.None);
                    }
                    catch (Exception error)
                    {
                        if (started) logger?.LogWarning(error, "Could not refresh a feed read");
                    }
                }
            }
            return cached.Value;
        }
        return await coalescer.RunAsync(key, () => RecomputeAsync(http, context), CancellationToken.None)
            .WaitAsync(context.CancellationToken);

        async Task<T?> RecomputeAsync(HttpContext? request, ProtocolExecutionContext execution)
        {
            await using var current = await factory.CreateDbContextAsync(execution.CancellationToken);
            var existing = await cache.GetAsync<Entry<T>>(key);
            if (existing != null && clock.UtcNow - existing.ReadAt < TimeSpan.FromSeconds(30) &&
                existing.LibraryRevision == current.FeedReadLibraryRevisionFor(execution.Actor?.EffectiveUserId)) return existing.Value;
            var revision = current.FeedReadLibraryRevisionFor(execution.Actor?.EffectiveUserId);
            var readAt = clock.UtcNow;
            var result = await read(request, execution);
            if (result != null && key == current.FeedReadKey(execution, scope, access))
                await cache.SetAsync(key, new Entry<T>(result, readAt, revision), TimeSpan.FromSeconds(60));
            return result;
        }
    }

    private static DefaultHttpContext CopyRequest(HttpContext source)
    {
        var copy = new DefaultHttpContext();
        copy.Request.Scheme = source.Request.Scheme;
        copy.Request.Host = source.Request.Host;
        copy.Request.PathBase = source.Request.PathBase;
        copy.Request.Path = source.Request.Path;
        copy.Request.QueryString = source.Request.QueryString;
        foreach (var header in source.Request.Headers) copy.Request.Headers[header.Key] = header.Value.ToArray();
        return copy;
    }
}
