using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Routing;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoriteDownloadActionExecutor(
    IProviderRouter router,
    IProviderRegistry providers,
    ProviderDownloadArtifactResolver artifacts,
    IPlatformClock clock,
    IDbContextFactory<AllstarrDbContext> factory) : IFavoriteActionExecutor
{
    public string ActionType => "download";

    public async Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action, CancellationToken cancellationToken)
    {
        var external = FavoriteMatchActionExecutor.ParseExternalTrack(favoriteEvent.ItemId);
        if (external == null)
            return FavoriteActionExecutionResult.Failure("favorite_download_external_id_required",
                "The favorite item has no provider track identity for download.");
        if (string.IsNullOrWhiteSpace(favoriteEvent.LibraryScopeId))
            return FavoriteActionExecutionResult.Failure("favorite_download_library_missing",
                "The favorite event has no authorized library scope for download.");
        if (await FavoriteMatchActionExecutor.HasLocalMatchAsync(factory, favoriteEvent, cancellationToken))
            return FavoriteActionExecutionResult.Success();
        var priority = providers.FindByCapability(ProviderCapabilityKind.Download, includeNonOperational: true)
            .Select(item => item.Id).ToArray();
        if (priority.Length == 0)
            return FavoriteActionExecutionResult.Failure("favorite_download_provider_unavailable",
                "No managed download provider is available.");
        var actor = new ProviderActorContext(favoriteEvent.TenantId, ProviderActorKind.SystemJob, null,
            durableJobId: favoriteEvent.JobId, actingForUserId: favoriteEvent.OwnerUserId);
        var quality = Enum.GetValues<ProviderAudioQuality>();
        ProviderRoutePlan<IProviderDownloadCapability> plan;
        try
        {
            plan = await router.PlanAsync<IProviderDownloadCapability>(new ProviderRouteRequest(
                ProviderCapabilityKind.Download, actor,
                new ProviderExecutionPolicy(new ProviderQualityPolicy(ProviderAudioQuality.Any,
                        ProviderAudioQuality.HighResolution, allowTranscode: false),
                    ProviderExplicitContentPolicy.Allow, allowFallback: true, allowSharedAccount: false,
                    allowManagedDownloads: true),
                "favorite-download", favoriteEvent.CorrelationId, clock.UtcNow.AddMinutes(30), priority,
                priority.Select(id => new ProviderRouteProviderState(id, availableQualities: quality)),
                new ProviderLibraryContext(favoriteEvent.TenantId, favoriteEvent.LibraryScopeId),
                new ProviderExternalResourceId(external.Value.Provider, ProviderResourceKind.Track, external.Value.Id),
                action.IdempotencyKey, cancellationToken));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or TimeoutException)
        {
            return FavoriteActionExecutionResult.Failure("favorite_download_route_denied",
                "No authorized managed download route is available.");
        }
        if (plan.Candidates.Count == 0)
            return FavoriteActionExecutionResult.Failure("favorite_download_route_unavailable",
                "No authorized managed download route is available.");

        for (var index = 0; index < plan.Candidates.Count; index++)
        {
            var candidate = plan.Candidates[index];
            var prior = await artifacts.FindByJobAsync(favoriteEvent.TenantId, favoriteEvent.JobId,
                candidate.Provider.Id, cancellationToken);
            if (prior != null) return FavoriteActionExecutionResult.Success();
            var workspace = await artifacts.CreateWorkspaceAsync(new ProviderDownloadWorkspaceRequest(
                favoriteEvent.TenantId, favoriteEvent.OwnerUserId, favoriteEvent.JobId, candidate.Provider.Id,
                candidate.Context.Account?.AccountId, action.IdempotencyKey)
            { LibraryScopeId = favoriteEvent.LibraryScopeId }, cancellationToken);
            var track = candidate.TrackId ?? new ProviderExternalResourceId(
                candidate.Provider.Id, ProviderResourceKind.Track, external.Value.Id);
            ProviderOutcome<ProviderDownloadedArtifact> outcome;
            try
            {
                var availability = await candidate.Implementation.CheckAvailabilityAsync(candidate.Context,
                    new ProviderDownloadAvailabilityRequest(track));
                if (!availability.IsSuccess || availability.RequireValue().State != ProviderDownloadAvailabilityState.Available)
                {
                    var error = availability.Error ?? new ProviderError(ProviderErrorKind.IncompatibleMedia);
                    if (router.EvaluateFallback(plan, index, error).NextCandidate != null) continue;
                    return FavoriteActionExecutionResult.Failure("favorite_download_unavailable",
                        "The track is unavailable from authorized download providers.");
                }
                outcome = await candidate.Implementation.DownloadAsync(candidate.Context,
                    new ProviderDownloadRequest(track, favoriteEvent.JobId, workspace.Reference,
                        ProviderAudioQuality.Any));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                outcome = ProviderOutcome<ProviderDownloadedArtifact>.Failure(new ProviderError(
                    ProviderErrorKind.TransientFailure));
            }
            if (!outcome.IsSuccess)
            {
                if (router.EvaluateFallback(plan, index, outcome.Error!).NextCandidate != null) continue;
                return outcome.Error!.Kind is ProviderErrorKind.TransientFailure or ProviderErrorKind.RateLimited
                    ? FavoriteActionExecutionResult.Retry("favorite_download_temporary_failure",
                        "The managed download temporarily failed.")
                    : FavoriteActionExecutionResult.Failure("favorite_download_failed",
                        "The managed download failed.");
            }
            try
            {
                await artifacts.ResolveAsync(workspace.Reference, outcome.RequireValue(), cancellationToken);
                return FavoriteActionExecutionResult.Success();
            }
            catch (IOException)
            {
                return FavoriteActionExecutionResult.Retry("favorite_download_artifact_io_failed",
                    "The downloaded artifact could not be verified.");
            }
            catch (InvalidOperationException)
            {
                return FavoriteActionExecutionResult.Failure("favorite_download_artifact_invalid",
                    "The downloaded artifact failed verification.");
            }
        }
        return FavoriteActionExecutionResult.Failure("favorite_download_route_exhausted",
            "Authorized download providers were exhausted.");
    }
}
