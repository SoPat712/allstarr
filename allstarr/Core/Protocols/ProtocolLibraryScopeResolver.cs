using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Protocols;

public interface IProtocolLibraryScopeResolver
{
    Task<ProtocolExecutionContext> ResolveAsync(
        ProtocolExecutionContext context,
        string itemId,
        CancellationToken cancellationToken = default);
}

public sealed class ProtocolLibraryScopeResolver(IDbContextFactory<AllstarrDbContext> factory)
    : IProtocolLibraryScopeResolver
{
    public async Task<ProtocolExecutionContext> ResolveAsync(
        ProtocolExecutionContext context,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var actor = context.RequireActor();
        var owner = actor.EffectiveUserId ?? throw new UnauthorizedAccessException();
        if (!string.IsNullOrWhiteSpace(context.LibraryScopeId)) return context;
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("A library item is required.", nameof(itemId));

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var tracks = await db.LibraryTracks.AsNoTracking().Where(track =>
            track.TenantId == actor.TenantId &&
            track.OwnerUserId == owner &&
            track.Protocol == context.Protocol.ToString().ToLowerInvariant() &&
            track.BackendInstanceId == context.BackendInstanceId).ToListAsync(cancellationToken);
        var matchingScopes = tracks.Where(track => Matches(track, itemId))
            .Select(track => track.LibraryScopeId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scopes = matchingScopes.Length > 0
            ? matchingScopes
            : tracks.Select(track => track.LibraryScopeId).Distinct(StringComparer.Ordinal).ToArray();
        return scopes.Length switch
        {
            1 => context.WithLibraryScope(scopes[0]),
            0 => throw new InvalidOperationException("No indexed library scope is available for this user and backend."),
            _ => throw new InvalidOperationException("The item belongs to more than one library scope; choose a library explicitly.")
        };
    }

    internal static bool Matches(LibraryTrackRecord track, string itemId)
    {
        if (track.BackendItemId.Equals(itemId, StringComparison.Ordinal) ||
            track.Id.ToString("D").Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
            track.CanonicalRecordingId?.ToString("D").Equals(itemId, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        try
        {
            var providers = JsonSerializer.Deserialize<Dictionary<string, string>>(track.ProviderIdsJson);
            return providers?.Any(pair =>
                pair.Value.Equals(itemId, StringComparison.Ordinal) ||
                $"{pair.Key}:{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
                $"ext-{pair.Key}-song-{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
                $"ext-{pair.Key}-{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
