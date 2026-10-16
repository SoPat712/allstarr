using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Admin;

internal static class AdminSettingsAccess
{
    internal static async Task RequireActiveAsync(AllstarrDbContext db, AdminAuthSession actor, CancellationToken token)
    {
        if (!actor.IsAdministrator || actor.AllstarrUserId == null || !await db.Users.AnyAsync(item =>
            item.Id == actor.AllstarrUserId && item.Enabled && item.IsAdmin && item.BackendType == actor.BackendType.ToLowerInvariant() &&
            item.BackendInstanceId == actor.BackendInstanceId && item.BackendPrincipalId == actor.UserId, token))
            throw new UnauthorizedAccessException("Sign in with an active media-server administrator to manage access.");
    }

}
