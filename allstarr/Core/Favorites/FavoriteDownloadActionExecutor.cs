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
    IDbContextFactory<AllstarrDbContext> factory,
    IProviderRouteDecisionStore routeDecisions) : IFavoriteActionExecutor
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
        var routeDecision = await routeDecisions.RecordPlanAsync(
            plan.Request,
            plan.Decision,
            action.IdempotencyKey,
            cancellationToken);
        if (plan.Candidates.Count == 0)
        {
            await RecordOutcomeAsync(
                routeDecision,
                action,
                sequence: 0,
                stage: "planning",
                providerId: null,
                providerAccountId: null,
                ProviderRouteOutcomeStatus.Stopped,
                "no-authorized-candidate",
                nextProviderId: null,
                cancellationToken);
            return FavoriteActionExecutionResult.Failure("favorite_download_route_unavailable",
                "No authorized managed download route is available.");
        }

        for (var index = 0; index < plan.Candidates.Count; index++)
        {
            var candidate = plan.Candidates[index];
            var prior = await artifacts.FindByJobAsync(favoriteEvent.TenantId, favoriteEvent.JobId,
                candidate.Provider.Id, cancellationToken);
            if (prior != null)
            {
                await RecordOutcomeAsync(
                    routeDecision,
                    action,
                    index,
                    "existing-artifact",
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    ProviderRouteOutcomeStatus.Succeeded,
                    "verified-artifact-reused",
                    nextProviderId: null,
                    cancellationToken);
                return FavoriteActionExecutionResult.Success();
            }
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
                    var fallback = router.EvaluateFallback(plan, index, error);
                    await RecordFallbackAsync(routeDecision, action, index, "availability", candidate, fallback,
                        cancellationToken);
                    if (fallback.NextCandidate != null) continue;
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
                var fallback = router.EvaluateFallback(plan, index, outcome.Error!);
                await RecordFallbackAsync(routeDecision, action, index, "download", candidate, fallback,
                    cancellationToken);
                if (fallback.NextCandidate != null) continue;
                return outcome.Error!.Kind is ProviderErrorKind.TransientFailure or ProviderErrorKind.RateLimited
                    ? FavoriteActionExecutionResult.Retry("favorite_download_temporary_failure",
                        "The managed download temporarily failed.")
                    : FavoriteActionExecutionResult.Failure("favorite_download_failed",
                        "The managed download failed.");
            }
            try
            {
                await artifacts.ResolveAsync(workspace.Reference, outcome.RequireValue(), cancellationToken);
                await RecordOutcomeAsync(
                    routeDecision,
                    action,
                    index,
                    "artifact-verification",
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    ProviderRouteOutcomeStatus.Succeeded,
                    "download-verified",
                    nextProviderId: null,
                    cancellationToken);
                return FavoriteActionExecutionResult.Success();
            }
            catch (IOException)
            {
                await RecordOutcomeAsync(
                    routeDecision,
                    action,
                    index,
                    "artifact-verification",
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    ProviderRouteOutcomeStatus.Stopped,
                    "artifact-io-failed",
                    nextProviderId: null,
                    cancellationToken);
                return FavoriteActionExecutionResult.Retry("favorite_download_artifact_io_failed",
                    "The downloaded artifact could not be verified.");
            }
            catch (InvalidOperationException)
            {
                await RecordOutcomeAsync(
                    routeDecision,
                    action,
                    index,
                    "artifact-verification",
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    ProviderRouteOutcomeStatus.Stopped,
                    "artifact-invalid",
                    nextProviderId: null,
                    cancellationToken);
                return FavoriteActionExecutionResult.Failure("favorite_download_artifact_invalid",
                    "The downloaded artifact failed verification.");
            }
        }
        return FavoriteActionExecutionResult.Failure("favorite_download_route_exhausted",
            "Authorized download providers were exhausted.");
    }

    private async Task RecordFallbackAsync(
        ProviderRouteDecisionHandle decision,
        FavoriteActionRecord action,
        int sequence,
        string stage,
        ProviderRouteCandidate<IProviderDownloadCapability> candidate,
        ProviderFallbackDecision<IProviderDownloadCapability> fallback,
        CancellationToken cancellationToken) =>
        await RecordOutcomeAsync(
            decision,
            action,
            sequence,
            stage,
            candidate.Provider.Id,
            candidate.Context.Account?.AccountId,
            fallback.Disposition == ProviderFallbackDisposition.Advance
                ? ProviderRouteOutcomeStatus.FallbackAdvanced
                : ProviderRouteOutcomeStatus.Stopped,
            fallback.ReasonCode,
            fallback.NextCandidate?.Provider.Id,
            cancellationToken);

    private async Task RecordOutcomeAsync(
        ProviderRouteDecisionHandle decision,
        FavoriteActionRecord action,
        int sequence,
        string stage,
        string? providerId,
        Guid? providerAccountId,
        ProviderRouteOutcomeStatus status,
        string reasonCode,
        string? nextProviderId,
        CancellationToken cancellationToken)
    {
        await routeDecisions.RecordOutcomeAsync(
            decision,
            new ProviderRouteExecutionOutcome(
                $"{action.Id:N}|attempt:{action.AttemptCount}|{sequence}|{stage}",
                sequence,
                stage,
                providerId,
                providerAccountId,
                status,
                reasonCode,
                nextProviderId),
            cancellationToken);
    }
}
