using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Identity;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Protocols;

public sealed record BackendLibraryAccess(bool Succeeded, string[] LibraryIds)
{
    public static BackendLibraryAccess Unavailable { get; } = new(false, []);
    public bool Allows(string libraryId) => Succeeded && LibraryIds.Contains(libraryId, StringComparer.Ordinal);
}

public interface IBackendLibraryAccessResolver
{
    Task<BackendLibraryAccess> ResolveAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default);
    Task<BackendLibraryAccessContext> ResolveUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task InvalidateAsync(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId);
}

public sealed record BackendLibraryAccessContext(ProtocolExecutionContext? Context, BackendLibraryAccess Access)
{
    public static BackendLibraryAccessContext Unavailable { get; } = new(null, BackendLibraryAccess.Unavailable);
}

public interface IBackendLibraryPermissionSource
{
    Task<HttpRequestMessage?> CreateRequestAsync(ProtocolExecutionContext context, CancellationToken cancellationToken);
}

public sealed class BackendLibraryAccessResolver(
    IBackendLibraryPermissionSource source,
    IHttpClientFactory clients,
    IApplicationCache cache,
    IPlatformClock clock,
    IConfiguration configuration,
    IDbContextFactory<AllstarrDbContext>? contexts = null) : IBackendLibraryAccessResolver
{
    public const string HttpClientName = "BackendLibraryAccess";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public async Task<BackendLibraryAccessContext> ResolveUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (contexts == null) return BackendLibraryAccessContext.Unavailable;
        var backendType = string.Equals(configuration["Backend:Type"], "Subsonic", StringComparison.OrdinalIgnoreCase)
            ? "subsonic" : "jellyfin";
        var backendInstance = configuration["Identity:BackendInstanceId"] ?? "primary";
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user =>
            user.Id == userId && user.Enabled && user.BackendType == backendType &&
            user.BackendInstanceId == backendInstance, cancellationToken);
        if (user == null) return BackendLibraryAccessContext.Unavailable;
        var context = new ProtocolExecutionContext(backendType == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
            backendInstance, user.BackendPrincipalId,
            new AllstarrPrincipal(userId, backendType, backendInstance,
                user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
            "library-access", clock.UtcNow.Add(Lifetime), cancellationToken);
        return new(context, await ResolveAsync(context, cancellationToken));
    }

    public async Task<BackendLibraryAccess> ResolveAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Principal == null) return BackendLibraryAccess.Unavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        var remaining = context.Deadline - clock.UtcNow;
        if (remaining <= TimeSpan.Zero) return BackendLibraryAccess.Unavailable;
        deadline.CancelAfter(remaining < Lifetime ? remaining : Lifetime);
        try
        {
            using var request = await source.CreateRequestAsync(context, deadline.Token);
            if (request == null) return BackendLibraryAccess.Unavailable;
            var authentication = JsonSerializer.Serialize(new
            {
                endpoint = request.RequestUri!.AbsoluteUri,
                headers = request.Headers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { pair.Key, Values = pair.Value.ToArray() }),
                body = request.Content == null ? null : await request.Content.ReadAsStringAsync(deadline.Token),
                selection = configuration[BackendMusicLibraries.SelectionKey],
                legacySelection = configuration["Jellyfin:LibraryId"]
            });
            var key = Prefix(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId) + Digest(authentication);
            var cached = await cache.GetAsync<PermissionEntry>(key);
            if (cached != null && cached.ExpiresAt > clock.UtcNow)
                return new(true, cached.LibraryIds);

            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode) return BackendLibraryAccess.Unavailable;
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
            var libraries = BackendMusicLibraries.Select(
                BackendMusicLibraries.Read(context.Protocol, document.RootElement), configuration, context.Protocol);
            await cache.SetAsync(key, new PermissionEntry(libraries, clock.UtcNow.Add(Lifetime)), Lifetime);
            return new(true, libraries);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return BackendLibraryAccess.Unavailable;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return BackendLibraryAccess.Unavailable;
        }
    }

    public async Task InvalidateAsync(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId) =>
        await cache.DeleteByPatternAsync(Prefix(protocol, backendInstanceId, backendPrincipalId) + "*");

    private static string Prefix(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId) =>
        "backend:libraries:v1:" + Digest(JsonSerializer.Serialize(new { protocol, backendInstanceId, backendPrincipalId })) + ":";

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record PermissionEntry(string[] LibraryIds, DateTimeOffset ExpiresAt);
}
