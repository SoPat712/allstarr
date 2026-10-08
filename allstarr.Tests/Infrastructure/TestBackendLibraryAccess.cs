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
        var binding = await (from identity in db.BackendIdentities.AsNoTracking()
                             join user in db.Users on identity.UserId equals user.Id
                             where user.Id == userId && user.Status == PlatformUserStatus.Active &&
                                   (BackendType == null || identity.BackendType == BackendType) &&
                                   (BackendInstanceId == null || identity.BackendInstanceId == BackendInstanceId)
                             orderby identity.BackendType, identity.BackendInstanceId, identity.Id
                             select new { identity, user }).FirstOrDefaultAsync(cancellationToken);
        if (binding == null) return BackendLibraryAccessContext.Unavailable;
        var context = new ProtocolExecutionContext(
            Enum.Parse<ProtocolKind>(binding.identity.BackendType, true), binding.identity.BackendInstanceId,
            binding.identity.PrincipalId, new AllstarrPrincipal(binding.user.TenantId, userId,
                binding.identity.BackendType, binding.identity.BackendInstanceId, binding.identity.PrincipalId,
                binding.user.DisplayName, false), "test", DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
        return new(context, await ResolveAsync(context, cancellationToken));
    }

    public Task InvalidateAsync(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId) => Task.CompletedTask;
}
