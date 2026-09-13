using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Routing;

namespace allstarr.Core.Downloads;

public sealed class ManagedTrackPlacementOptions
{
    public static readonly Guid DefaultRootId = new("2eb55833-0e65-5d36-8ee9-684587b0657d");
    public Guid RootId { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string PathTemplate { get; set; } = "{albumArtist}/{album}/{track} - {title}{extension}";
}

public sealed record ManagedTrackDownloadCommand(
    Guid TenantId,
    Guid OwnerUserId,
    string LibraryScopeId,
    Guid DurableJobId,
    string ProviderId,
    string ExternalTrackId,
    string CorrelationId,
    string IdempotencyKey,
    int Attempt,
    string RoutePurpose,
    string? OutcomeKeyPrefix = null);

public sealed record ManagedTrackDownloadResult(
    bool Succeeded,
    bool Retryable = false,
    string? ErrorCode = null,
    string? SafeMessage = null,
    VerifiedProviderDownloadArtifact? Artifact = null)
{
    public static ManagedTrackDownloadResult Success(VerifiedProviderDownloadArtifact? artifact = null) =>
        new(true, Artifact: artifact);

    public static ManagedTrackDownloadResult Retry(string code, string message) =>
        new(false, true, code, message);

    public static ManagedTrackDownloadResult Failure(string code, string message) =>
        new(false, false, code, message);
}

public sealed class ManagedTrackDownloadService(
    IProviderRouter router,
    IProviderRegistry providers,
    ProviderDownloadArtifactResolver artifacts,
    IPlatformClock clock,
    IProviderRouteDecisionStore routeDecisions)
{
    public async Task<ManagedTrackDownloadResult> ExecuteAsync(
        ManagedTrackDownloadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command);

        if (string.IsNullOrWhiteSpace(command.ExternalTrackId) ||
            string.IsNullOrWhiteSpace(command.ProviderId))
        {
            return ManagedTrackDownloadResult.Failure(
                "managed_download_external_id_required",
                "The managed download command has no provider track identity.");
        }

        if (string.IsNullOrWhiteSpace(command.LibraryScopeId))
        {
            return ManagedTrackDownloadResult.Failure(
                "managed_download_library_missing",
                "The managed download command has no authorized library scope.");
        }

        var track = new ProviderExternalResourceId(
            command.ProviderId,
            ProviderResourceKind.Track,
            command.ExternalTrackId);
        var priority = providers.FindByCapability(ProviderCapabilityKind.Download, includeNonOperational: true)
            .Select(item => item.Id)
            .ToArray();
        if (priority.Length == 0)
        {
            return ManagedTrackDownloadResult.Failure(
                "managed_download_provider_unavailable",
                "No managed download provider is available.");
        }

        var actor = new ProviderActorContext(
            command.TenantId,
            ProviderActorKind.SystemJob,
            null,
            durableJobId: command.DurableJobId,
            actingForUserId: command.OwnerUserId);
        var quality = Enum.GetValues<ProviderAudioQuality>();
        ProviderRoutePlan<IProviderDownloadCapability> plan;
        try
        {
            plan = await router.PlanAsync<IProviderDownloadCapability>(new ProviderRouteRequest(
                ProviderCapabilityKind.Download,
                actor,
                new ProviderExecutionPolicy(
                    new ProviderQualityPolicy(
                        ProviderAudioQuality.Any,
                        ProviderAudioQuality.HighResolution,
                        allowTranscode: false),
                    ProviderExplicitContentPolicy.Allow,
                    allowFallback: true,
                    allowSharedAccount: false,
                    allowManagedDownloads: true),
                command.RoutePurpose,
                command.CorrelationId,
                clock.UtcNow.AddMinutes(30),
                priority,
                priority.Select(id => new ProviderRouteProviderState(id, availableQualities: quality)),
                new ProviderLibraryContext(command.TenantId, command.LibraryScopeId),
                track,
                command.IdempotencyKey,
                cancellationToken));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or TimeoutException)
        {
            return ManagedTrackDownloadResult.Failure(
                "managed_download_route_denied",
                "No authorized managed download route is available.");
        }

        var routeDecision = await routeDecisions.RecordPlanAsync(
            plan.Request,
            plan.Decision,
            command.IdempotencyKey,
            cancellationToken);
        if (plan.Candidates.Count == 0)
        {
            await RecordOutcomeAsync(
                routeDecision,
                command,
                sequence: 0,
                stage: "planning",
                providerId: null,
                providerAccountId: null,
                ProviderRouteOutcomeStatus.Stopped,
                "no-authorized-candidate",
                nextProviderId: null,
                cancellationToken);
            return ManagedTrackDownloadResult.Failure(
                "managed_download_route_unavailable",
                "No authorized managed download route is available.");
        }

        for (var index = 0; index < plan.Candidates.Count; index++)
        {
            var candidate = plan.Candidates[index];
            var prior = await artifacts.FindByJobAsync(
                command.TenantId,
                command.DurableJobId,
                candidate.Provider.Id,
                cancellationToken);
            if (prior != null)
            {
                await RecordOutcomeAsync(
                    routeDecision,
                    command,
                    index,
                    "existing-artifact",
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    ProviderRouteOutcomeStatus.Succeeded,
                    "verified-artifact-reused",
                    nextProviderId: null,
                    cancellationToken);
                return ManagedTrackDownloadResult.Success(prior);
            }

            var workspace = await artifacts.CreateWorkspaceAsync(new ProviderDownloadWorkspaceRequest(
                command.TenantId,
                command.OwnerUserId,
                command.DurableJobId,
                candidate.Provider.Id,
                candidate.Context.Account?.AccountId,
                command.IdempotencyKey)
            {
                LibraryScopeId = command.LibraryScopeId
            }, cancellationToken);
            var candidateTrack = candidate.TrackId ?? new ProviderExternalResourceId(
                candidate.Provider.Id,
                ProviderResourceKind.Track,
                command.ExternalTrackId);
            ProviderOutcome<ProviderDownloadedArtifact> outcome;
            try
            {
                var availability = await candidate.Implementation.CheckAvailabilityAsync(
                    candidate.Context,
                    new ProviderDownloadAvailabilityRequest(candidateTrack));
                if (!availability.IsSuccess ||
                    availability.RequireValue().State != ProviderDownloadAvailabilityState.Available)
                {
                    var error = availability.Error ?? new ProviderError(ProviderErrorKind.IncompatibleMedia);
                    var fallback = router.EvaluateFallback(plan, index, error);
                    await RecordFallbackAsync(routeDecision, command, index, "availability", candidate, fallback,
                        cancellationToken);
                    if (fallback.NextCandidate != null)
                        continue;
                    return ManagedTrackDownloadResult.Failure(
                        "managed_download_unavailable",
                        "The track is unavailable from authorized download providers.");
                }

                outcome = await candidate.Implementation.DownloadAsync(
                    candidate.Context,
                    new ProviderDownloadRequest(
                        candidateTrack,
                        command.DurableJobId,
                        workspace.Reference,
                        ProviderAudioQuality.Any));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                outcome = ProviderOutcome<ProviderDownloadedArtifact>.Failure(
                    new ProviderError(ProviderErrorKind.TransientFailure));
            }

            if (!outcome.IsSuccess)
            {
                var fallback = router.EvaluateFallback(plan, index, outcome.Error!);
                await RecordFallbackAsync(routeDecision, command, index, "download", candidate, fallback,
                    cancellationToken);
                if (fallback.NextCandidate != null)
                    continue;
                return outcome.Error!.Kind is ProviderErrorKind.TransientFailure or ProviderErrorKind.RateLimited
                    ? ManagedTrackDownloadResult.Retry(
                        "managed_download_temporary_failure",
                        "The managed download temporarily failed.")
                    : ManagedTrackDownloadResult.Failure(
                        "managed_download_failed",
                        "The managed download failed.");
            }

            try
            {
                var artifact = await artifacts.ResolveAsync(
                    workspace.Reference,
                    outcome.RequireValue(),
                    cancellationToken);
                await RecordOutcomeAsync(
                    routeDecision,
                    command,
                    index,
                    "artifact-verification",
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    ProviderRouteOutcomeStatus.Succeeded,
                    "download-verified",
                    nextProviderId: null,
                    cancellationToken);
                return ManagedTrackDownloadResult.Success(artifact);
            }
            catch (IOException)
            {
                await RecordOutcomeAsync(
                    routeDecision,
                    command,
                    index,
                    "artifact-verification",
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    ProviderRouteOutcomeStatus.Stopped,
                    "artifact-io-failed",
                    nextProviderId: null,
                    cancellationToken);
                return ManagedTrackDownloadResult.Retry(
                    "managed_download_artifact_io_failed",
                    "The downloaded artifact could not be verified.");
            }
            catch (InvalidOperationException)
            {
                await RecordOutcomeAsync(
                    routeDecision,
                    command,
                    index,
                    "artifact-verification",
                    candidate.Provider.Id,
                    candidate.Context.Account?.AccountId,
                    ProviderRouteOutcomeStatus.Stopped,
                    "artifact-invalid",
                    nextProviderId: null,
                    cancellationToken);
                return ManagedTrackDownloadResult.Failure(
                    "managed_download_artifact_invalid",
                    "The downloaded artifact failed verification.");
            }
        }

        return ManagedTrackDownloadResult.Failure(
            "managed_download_route_exhausted",
            "Authorized download providers were exhausted.");
    }

    private static void ValidateScope(ManagedTrackDownloadCommand command)
    {
        if (command.TenantId == Guid.Empty)
            throw new ArgumentException("A tenant ID is required.", nameof(command));
        if (command.OwnerUserId == Guid.Empty)
            throw new ArgumentException("An owner user ID is required.", nameof(command));
        if (command.DurableJobId == Guid.Empty)
            throw new ArgumentException("A durable job ID is required.", nameof(command));
        if (command.Attempt < 0)
            throw new ArgumentOutOfRangeException(nameof(command), "Attempt cannot be negative.");
        if (string.IsNullOrWhiteSpace(command.CorrelationId))
            throw new ArgumentException("A correlation ID is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.RoutePurpose))
            throw new ArgumentException("A route purpose is required.", nameof(command));
    }

    private async Task RecordFallbackAsync(
        ProviderRouteDecisionHandle decision,
        ManagedTrackDownloadCommand command,
        int sequence,
        string stage,
        ProviderRouteCandidate<IProviderDownloadCapability> candidate,
        ProviderFallbackDecision<IProviderDownloadCapability> fallback,
        CancellationToken cancellationToken) =>
        await RecordOutcomeAsync(
            decision,
            command,
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

    private Task RecordOutcomeAsync(
        ProviderRouteDecisionHandle decision,
        ManagedTrackDownloadCommand command,
        int sequence,
        string stage,
        string? providerId,
        Guid? providerAccountId,
        ProviderRouteOutcomeStatus status,
        string reasonCode,
        string? nextProviderId,
        CancellationToken cancellationToken) =>
        routeDecisions.RecordOutcomeAsync(
            decision,
            new ProviderRouteExecutionOutcome(
                $"{command.OutcomeKeyPrefix ?? command.IdempotencyKey}|attempt:{command.Attempt}|{sequence}|{stage}",
                sequence,
                stage,
                providerId,
                providerAccountId,
                status,
                reasonCode,
                nextProviderId),
            cancellationToken);
}
