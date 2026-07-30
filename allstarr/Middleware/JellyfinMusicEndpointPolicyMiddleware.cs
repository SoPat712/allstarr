using System.Text.Json;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;

namespace allstarr.Middleware;

public sealed class JellyfinMusicEndpointPolicyMiddleware(
    RequestDelegate next,
    ILogger<JellyfinMusicEndpointPolicyMiddleware> logger)
{
    private static readonly TimeSpan ItemTypeCacheDuration = TimeSpan.FromMinutes(5);

    public async Task InvokeAsync(
        HttpContext context,
        JellyfinProxyService proxyService,
        IApplicationCache cache)
    {
        // The administration application is separately protected by its port, network,
        // and session middleware. This policy governs only the public Jellyfin proxy.
        if (context.Connection.LocalPort == 5275 || IsInfrastructureRoute(context.Request.Path))
        {
            await next(context);
            return;
        }

        var decision = JellyfinMusicEndpointPolicy.Evaluate(context.Request);
        context.Items[typeof(JellyfinEndpointDecision)] = decision;
        if (decision.Access == JellyfinEndpointAccess.RequiresMusicItem)
        {
            var itemId = JellyfinMusicEndpointPolicy.ReferencedItemId(context.Request.Path.Value);
            if (itemId == null || !await IsMusicItemAsync(itemId, proxyService, cache))
            {
                await DenyAsync(context, "The referenced Jellyfin item is not music-related.");
                return;
            }
        }

        if (decision.Allowed)
        {
            await next(context);
            return;
        }

        await DenyAsync(context, decision.Reason);
    }

    private async Task<bool> IsMusicItemAsync(
        string itemId,
        JellyfinProxyService proxyService,
        IApplicationCache cache)
    {
        // All synthesized resources use explicit music resource identifiers.
        if (JellyfinMusicEndpointPolicy.IsSynthesizedMusicItemId(itemId)) return true;

        var cacheKey = CacheKeyBuilder.BuildJellyfinItemTypeKey(itemId);
        var cached = await cache.GetAsync<ItemTypeCacheEntry>(cacheKey);
        if (cached != null) return cached.IsMusic;

        // Resolve the item type with Allstarr's internal Jellyfin credential. Public
        // artwork requests intentionally have no client token, while authenticated
        // requests are still checked by JellyfinAuthFilter after this policy gate.
        var (item, statusCode) = await proxyService.GetItemAsync(Uri.EscapeDataString(itemId));
        using (item)
        {
            var isMusic = statusCode == StatusCodes.Status200OK && item != null &&
                          item.RootElement.TryGetProperty("Type", out var type) &&
                          JellyfinMusicEndpointPolicy.IsMusicItemType(type.GetString());
            await cache.SetAsync(cacheKey, new ItemTypeCacheEntry(isMusic), ItemTypeCacheDuration);
            return isMusic;
        }
    }

    private async Task DenyAsync(HttpContext context, string reason)
    {
        logger.LogWarning(
            "Blocked non-music Jellyfin route {Method} {Path}; reason={Reason}",
            context.Request.Method,
            context.Request.Path.Value,
            reason);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            error = "This Allstarr endpoint only exposes music-related Jellyfin operations.",
            route = context.Request.Path.Value
        }, cancellationToken: context.RequestAborted);
    }

    private static bool IsInfrastructureRoute(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");

    private sealed record ItemTypeCacheEntry(bool IsMusic);
}
