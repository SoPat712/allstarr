using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

/// <summary>
/// A song owner's media server account, so background work can act for them as a request of theirs would, with their
/// music services and libraries.
/// </summary>
internal sealed record OwnerAccount(
    string BackendType,
    string BackendInstanceId,
    string BackendPrincipalId,
    string DisplayName,
    bool IsAdministrator)
{
    internal static async Task<IReadOnlyDictionary<Guid, OwnerAccount>> LoadAsync(
        AllstarrDbContext db, IReadOnlyCollection<Guid> ownerUserIds, CancellationToken cancellationToken) =>
        (await db.Users.AsNoTracking().Where(item => item.Enabled && ownerUserIds.Contains(item.Id)).ToArrayAsync(cancellationToken))
            .ToDictionary(user => user.Id, user => new OwnerAccount(
                user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin));

    // Null when the song was saved through another server or account than the owner's current one.
    internal ProtocolExecutionContext? Execution(
        Guid ownerUserId, string protocol, string backendInstanceId, string backendPrincipalId,
        string correlationId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        ProtocolKind? kind = protocol.Trim().ToLowerInvariant() switch
        {
            "jellyfin" => ProtocolKind.Jellyfin,
            "subsonic" or "opensubsonic" => ProtocolKind.Subsonic,
            _ => null
        };
        if (!kind.HasValue) return null;
        var backendType = kind.Value == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic";
        if (BackendType != backendType || BackendInstanceId != backendInstanceId || BackendPrincipalId != backendPrincipalId)
            return null;
        return new(kind.Value, backendInstanceId, BackendPrincipalId,
            new AllstarrPrincipal(ownerUserId, backendType, backendInstanceId, BackendPrincipalId, DisplayName, IsAdministrator),
            correlationId, deadline, cancellationToken);
    }
}
