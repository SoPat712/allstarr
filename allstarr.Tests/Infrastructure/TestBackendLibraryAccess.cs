using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

internal sealed class TestBackendLibraryAccess(
    IDbContextFactory<AllstarrDbContext> factory,
    params string[] libraryIds) : IBackendLibraryAccessResolver
{
    public string? BackendType { get; init; }
    public string? BackendInstanceId { get; init; }
    public Dictionary<Guid, BackendLibraryAccess> Permissions { get; } = [];

    public Task<BackendLibraryAccess> ResolveAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(context.Principal == null ? BackendLibraryAccess.Unavailable :
            Permissions.GetValueOrDefault(context.Principal.UserId, new(true, libraryIds)));

    public async Task<BackendLibraryAccessContext> ResolveUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user =>
            user.Id == userId && user.Enabled &&
            (BackendType == null || user.BackendType == BackendType) &&
            (BackendInstanceId == null || user.BackendInstanceId == BackendInstanceId), cancellationToken);
        if (user == null) return BackendLibraryAccessContext.Unavailable;
        var context = new ProtocolExecutionContext(
            Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
            user.BackendPrincipalId, new AllstarrPrincipal(userId,
                user.BackendType, user.BackendInstanceId, user.BackendPrincipalId,
                user.DisplayName, user.IsAdmin), "test", DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
        return new(context, await ResolveAsync(context, cancellationToken));
    }

    public Task InvalidateAsync(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId) => Task.CompletedTask;
}
