using allstarr.Core.Enrichment;

namespace allstarr.Core.Favorites;

public sealed class FavoriteRefreshActionExecutor(BackendLibraryRefreshOrchestrator refresh)
    : IFavoriteActionExecutor
{
    public string ActionType => "refresh";

    public async Task<FavoriteActionExecutionResult> ExecuteAsync(
        FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(favoriteEvent.LibraryScopeId))
            return FavoriteActionExecutionResult.Failure(
                "favorite_refresh_library_missing",
                "The favorite event has no authorized library scope for refresh.");
        try
        {
            await refresh.EnqueueAsync(
                favoriteEvent.TenantId,
                favoriteEvent.OwnerUserId,
                new BackendLibraryRefreshJobPayload(
                    favoriteEvent.LibraryScopeId,
                    favoriteEvent.BackendInstanceId,
                    favoriteEvent.BackendPrincipalId,
                    favoriteEvent.TargetCredentialReferenceId),
                action.IdempotencyKey,
                favoriteEvent.CorrelationId,
                cancellationToken);
            return FavoriteActionExecutionResult.Success();
        }
        catch (InvalidOperationException)
        {
            return FavoriteActionExecutionResult.Failure(
                "favorite_refresh_not_configured",
                "The backend library refresh action is not configured.");
        }
    }
}
