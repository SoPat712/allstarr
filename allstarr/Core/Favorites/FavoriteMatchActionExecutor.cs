using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Core.Operations;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

/// <summary>
/// Resolves an existing local track inside the favorite event's exact owner, backend, and library scope.
/// An unmatched result is successful: the following opt-in download action decides whether to acquire it.
/// </summary>
public sealed class FavoriteMatchActionExecutor(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock)
    : IFavoriteActionExecutor
{
    public string ActionType => "match";

    public async Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(favoriteEvent.LibraryScopeId))
            return FavoriteActionExecutionResult.Failure("favorite_match_library_missing",
                "The favorite event has no authorized library scope for matching.");
        var external = ParseExternalTrack(favoriteEvent.ItemId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var identityExists = await db.BackendIdentities.AsNoTracking().AnyAsync(item =>
            item.TenantId == favoriteEvent.TenantId && item.UserId == favoriteEvent.OwnerUserId &&
            item.BackendType == favoriteEvent.Protocol && item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
            item.PrincipalId == favoriteEvent.BackendPrincipalId, cancellationToken);
        if (!identityExists)
            return FavoriteActionExecutionResult.Failure("favorite_match_identity_unavailable",
                "The linked backend identity is no longer available.");

        var candidates = await db.LibraryTracks.AsNoTracking().Where(item =>
            item.TenantId == favoriteEvent.TenantId && item.OwnerUserId == favoriteEvent.OwnerUserId &&
            item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
            item.LibraryScopeId == favoriteEvent.LibraryScopeId).ToListAsync(cancellationToken);
        var match = external == null
            ? candidates.SingleOrDefault(item => item.BackendItemId == favoriteEvent.ItemId)
            : candidates.FirstOrDefault(item => ProviderIdMatches(item.ProviderIdsJson, external.Value.Provider, external.Value.Id));
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = favoriteEvent.TenantId,
            ActorUserId = favoriteEvent.OwnerUserId,
            Category = "favorite-action",
            Action = "match",
            Outcome = match == null ? "unmatched" : "matched",
            CorrelationId = favoriteEvent.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                favoriteEventId = favoriteEvent.Id,
                favoriteActionId = action.Id,
                libraryTrackId = match?.Id,
                favoriteEvent.LibraryScopeId,
                favoriteEvent.BackendInstanceId
            }),
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return FavoriteActionExecutionResult.Success();
    }

    internal static (string Provider, string Id)? ParseExternalTrack(string itemId)
    {
        if (!itemId.StartsWith("ext-", StringComparison.Ordinal)) return null;
        var parts = itemId.Split('-', StringSplitOptions.None);
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[1])) return null;
        var offset = parts.Length >= 4 && parts[2] == "song" ? 3 : 2;
        var id = string.Join('-', parts.Skip(offset));
        return string.IsNullOrWhiteSpace(id) ? null : (parts[1].ToLowerInvariant(), id);
    }

    internal static bool ProviderIdMatches(string json, string provider, string externalId)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values != null && values.TryGetValue(provider, out var value) &&
                   value.Equals(externalId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static async Task<bool> HasLocalMatchAsync(IDbContextFactory<AllstarrDbContext> factory,
        FavoriteEventRecord favoriteEvent, CancellationToken cancellationToken)
    {
        var external = ParseExternalTrack(favoriteEvent.ItemId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.LibraryTracks.AsNoTracking().Where(item =>
            item.TenantId == favoriteEvent.TenantId && item.OwnerUserId == favoriteEvent.OwnerUserId &&
            item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
            item.LibraryScopeId == favoriteEvent.LibraryScopeId).ToListAsync(cancellationToken);
        return external == null
            ? candidates.Any(item => item.BackendItemId == favoriteEvent.ItemId)
            : candidates.Any(item => ProviderIdMatches(item.ProviderIdsJson, external.Value.Provider, external.Value.Id));
    }
}
