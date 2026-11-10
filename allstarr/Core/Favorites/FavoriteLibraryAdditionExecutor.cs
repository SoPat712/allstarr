using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;

namespace allstarr.Core.Favorites;

public sealed class FavoriteLibraryAdditionExecutor(ClientLibraryAdditions additions) : IFavoriteActionExecutor
{
    public string ActionType => "add-to-library";

    public async Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent, FavoriteActionRecord action,
        int executionAttempt, CancellationToken cancellationToken)
    {
        var principal = new AllstarrPrincipal(favoriteEvent.OwnerUserId, favoriteEvent.Protocol,
            favoriteEvent.BackendInstanceId, favoriteEvent.BackendPrincipalId, "User", false);
        var context = new ProtocolExecutionContext(favoriteEvent.Protocol == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
            favoriteEvent.BackendInstanceId, favoriteEvent.BackendPrincipalId, principal, favoriteEvent.CorrelationId,
            DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
        await additions.AddAsync(context, new(favoriteEvent.ItemId, "song"), cancellationToken, $"heart:{favoriteEvent.Id:N}");
        return FavoriteActionExecutionResult.Success();
    }
}
