using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Admin;

public sealed class AdminProtocolExecutionContextFactory(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IPlatformClock clock)
{
    public async Task<ProtocolExecutionContext> CreateAsync(
        AdminAuthSession session,
        string? libraryScopeId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var tenantId = session.TenantId ?? throw new UnauthorizedAccessException();
        var userId = session.AllstarrUserId ?? throw new UnauthorizedAccessException();
        var backendType = session.BackendType.Trim().ToLowerInvariant();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await db.BackendIdentities.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           item.UserId == userId &&
                           item.BackendType == backendType &&
                           item.PrincipalId == session.UserId)
            .OrderByDescending(item => item.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The linked backend identity is unavailable.");
        var protocol = backendType switch
        {
            "jellyfin" => ProtocolKind.Jellyfin,
            "subsonic" or "navidrome" or "opensubsonic" => ProtocolKind.Subsonic,
            _ => throw new UnauthorizedAccessException("Unsupported backend identity.")
        };
        var principal = new AllstarrPrincipal(
            tenantId,
            userId,
            protocol.ToString().ToLowerInvariant(),
            identity.BackendInstanceId,
            identity.PrincipalId,
            session.UserName,
            session.IsAdministrator);
        return new ProtocolExecutionContext(
            protocol,
            identity.BackendInstanceId,
            identity.PrincipalId,
            principal,
            correlationId.Length <= 100 ? correlationId : correlationId[..100],
            clock.UtcNow.AddMinutes(5),
            cancellationToken,
            libraryScopeId: string.IsNullOrWhiteSpace(libraryScopeId) ? null : libraryScopeId.Trim());
    }
}
