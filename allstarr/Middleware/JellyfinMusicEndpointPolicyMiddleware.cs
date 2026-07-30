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
        if (decision.Access is JellyfinEndpointAccess.RequiresMusicItem or
            JellyfinEndpointAccess.RequiresPlaylistItem)
        {
            var itemId = JellyfinMusicEndpointPolicy.ReferencedItemId(context.Request.Path.Value);
            var allowed = itemId != null && (decision.Access switch
            {
                JellyfinEndpointAccess.RequiresMusicItem =>
                    JellyfinMusicEndpointPolicy.IsSynthesizedMusicItemId(itemId)
                        ? JellyfinMusicEndpointPolicy.SupportsSynthesizedItemRoute(context.Request, itemId)
                        : (await GetItemTypeAsync(itemId, proxyService, cache)).IsMusic,
                JellyfinEndpointAccess.RequiresPlaylistItem =>
                    !JellyfinMusicEndpointPolicy.IsSynthesizedMusicItemId(itemId) &&
                    (await GetItemTypeAsync(itemId, proxyService, cache, requireType: true))
                        .ItemType?.Equals("Playlist", StringComparison.OrdinalIgnoreCase) == true,
                _ => false
            });
            if (!allowed)
            {
                await DenyAsync(
                    context,
                    decision.Access == JellyfinEndpointAccess.RequiresPlaylistItem
                        ? "The referenced Jellyfin item is not a playlist."
                        : "The referenced Jellyfin item is not music-related.");
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

    private async Task<ItemTypeCacheEntry> GetItemTypeAsync(
        string itemId,
        JellyfinProxyService proxyService,
        IApplicationCache cache,
        bool requireType = false)
    {
        var cacheKey = CacheKeyBuilder.BuildJellyfinItemTypeKey(itemId);
        var cached = await cache.GetAsync<ItemTypeCacheEntry>(cacheKey);
        if (cached != null && (!requireType || cached.ItemType != null)) return cached;

        // Resolve the item type with Allstarr's internal Jellyfin credential. Public
        // artwork requests intentionally have no client token, while authenticated
        // requests are still checked by JellyfinAuthFilter after this policy gate.
        var (item, statusCode) = await proxyService.GetItemAsync(Uri.EscapeDataString(itemId));
        using (item)
        {
            var itemType = statusCode == StatusCodes.Status200OK && item != null &&
                           item.RootElement.TryGetProperty("Type", out var type)
                ? type.GetString()
                : null;
            var result = new ItemTypeCacheEntry(
                JellyfinMusicEndpointPolicy.IsMusicItemType(itemType),
                itemType);
            await cache.SetAsync(cacheKey, result, ItemTypeCacheDuration);
            return result;
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

    private sealed record ItemTypeCacheEntry(bool IsMusic, string? ItemType = null);
}
