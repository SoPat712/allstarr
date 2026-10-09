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
        string correlationId,
        CancellationToken cancellationToken)
    {
        var userId = session.AllstarrUserId ?? throw new UnauthorizedAccessException();
        var backendType = session.BackendType.Trim().ToLowerInvariant();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == userId && item.Enabled && item.BackendType == backendType &&
            item.BackendInstanceId == session.BackendInstanceId &&
            item.BackendPrincipalId == session.UserId && item.IsAdmin == session.IsAdministrator,
            cancellationToken) ?? throw new UnauthorizedAccessException("The linked backend identity is unavailable.");
        var protocol = backendType switch
        {
            "jellyfin" => ProtocolKind.Jellyfin,
            "subsonic" or "navidrome" or "opensubsonic" => ProtocolKind.Subsonic,
            _ => throw new UnauthorizedAccessException("Unsupported backend identity.")
        };
        var principal = new AllstarrPrincipal(
            userId,
            protocol.ToString().ToLowerInvariant(),
            user.BackendInstanceId,
            user.BackendPrincipalId,
            user.DisplayName,
            user.IsAdmin);
        return new ProtocolExecutionContext(
            protocol,
            user.BackendInstanceId,
            user.BackendPrincipalId,
            principal,
            correlationId.Length <= 100 ? correlationId : correlationId[..100],
            clock.UtcNow.AddMinutes(5),
            cancellationToken);
    }
}
