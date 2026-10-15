using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace allstarr.Core.Favorites;

public sealed class FavoriteActionJobHandler(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IEnumerable<IFavoriteActionExecutor> actionExecutors,
    IPlatformClock clock,
    IRecommendationSignalWriter? recommendationSignals = null,
    ILogger<FavoriteActionJobHandler>? logger = null,
    IProtocolProviderGateway? providerGateway = null) : IDurableJobHandler
{
    private const string SupersededCode = "favorite_action_superseded";

    private readonly IReadOnlyDictionary<string, IFavoriteActionExecutor> _executors = actionExecutors
        .ToDictionary(executor => executor.ActionType.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    public string JobType => FavoriteActionPipeline.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var payload = context.Claim.Payload.Deserialize<FavoriteJobPayload>();
        if (payload == null || payload.EventId == Guid.Empty)
            return DurableJobCompletion.Failure("favorite_payload_invalid", "The favorite event payload is invalid.");

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var favoriteEvent = await database.Set<FavoriteEventRecord>().SingleOrDefaultAsync(
            item => item.Id == payload.EventId && item.JobId == context.Claim.JobId &&
                    item.OwnerUserId == context.Claim.OwnerUserId,
            cancellationToken);
        if (favoriteEvent == null)
            return DurableJobCompletion.Failure("favorite_event_missing", "The favorite event is unavailable.");
        if (favoriteEvent.State == FavoriteEventState.Succeeded)
            return DurableJobCompletion.Success();
        if (favoriteEvent.State == FavoriteEventState.Cancelled)
            return DurableJobCompletion.Cancelled();

        try
        {
            if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                return DurableJobCompletion.Cancelled();

            favoriteEvent.State = FavoriteEventState.Running;
            favoriteEvent.LastErrorCode = null;
            favoriteEvent.LastErrorMessage = null;
            favoriteEvent.UpdatedAt = clock.UtcNow;
            favoriteEvent.Revision++;
            await SaveFencedAsync(database, context.Claim, cancellationToken);

            var actions = await database.Set<FavoriteActionRecord>()
                .Where(item => item.EventId == favoriteEvent.Id && item.State != FavoriteActionState.Succeeded &&
                               item.State != FavoriteActionState.Cancelled)
                .OrderBy(item => item.ActionType == FavoriteActionPipeline.VirtualLikedAction ? 0 :
                                 item.ActionType == "download" ? 1 : 99)
                .ThenBy(item => item.CreatedAt)
                .ToListAsync(cancellationToken);

            var retired = actions.Where(action => action.ActionType is
                "match" or "place" or "enrich" or "refresh" or "lastfm").ToArray();
            if (retired.Length > 0)
            {
                if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                    return DurableJobCompletion.Cancelled();

                foreach (var action in retired)
                {
                    action.State = FavoriteActionState.Cancelled;
                    action.LastErrorCode = "favorite_action_retired";
                    action.LastErrorMessage = "This favorite action is no longer supported.";
                    action.CompletedAt = clock.UtcNow;
                    action.UpdatedAt = clock.UtcNow;
                    action.Revision++;
                }

                await SaveFencedAsync(database, context.Claim, cancellationToken);
            }

            foreach (var action in actions.Where(action => action.State != FavoriteActionState.Cancelled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                    return DurableJobCompletion.Cancelled();

                action.State = FavoriteActionState.Running;
                action.AttemptCount++;
                action.LastErrorCode = null;
                action.LastErrorMessage = null;
                action.UpdatedAt = clock.UtcNow;
                action.Revision++;
                await SaveFencedAsync(database, context.Claim, cancellationToken);

                FavoriteActionExecutionResult result;
                if (action.ActionType == FavoriteActionPipeline.VirtualLikedAction)
                {
                    if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                        return DurableJobCompletion.Cancelled();

                    result = await ApplyVirtualLikedStateAsync(database, favoriteEvent, context.Claim, cancellationToken);
                    if (result.ErrorCode == SupersededCode)
                        return DurableJobCompletion.Cancelled();

                    if (result.Succeeded && providerGateway != null &&
                        favoriteEvent.Operation == FavoriteOperation.Favorite)
                    {
                        if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                            return DurableJobCompletion.Cancelled();

                        var protocol = favoriteEvent.Protocol == "jellyfin"
                            ? ProtocolKind.Jellyfin
                            : ProtocolKind.Subsonic;
                        var execution = new ProtocolExecutionContext(
                            protocol,
                            favoriteEvent.BackendInstanceId,
                            favoriteEvent.BackendPrincipalId,
                            new AllstarrPrincipal(
                                favoriteEvent.OwnerUserId,
                                favoriteEvent.Protocol,
                                favoriteEvent.BackendInstanceId,
                                favoriteEvent.BackendPrincipalId,
                                "User",
                                false),
                            favoriteEvent.CorrelationId,
                            clock.UtcNow.AddSeconds(30),
                            cancellationToken);
                        await providerGateway.RememberTrackAsync(execution, favoriteEvent.ItemId);

                        if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                            return DurableJobCompletion.Cancelled();
                    }
                }
                else if (action.ActionType == "download" &&
                         _executors.TryGetValue(action.ActionType, out var executor))
                {
                    try
                    {
                        result = await executor.ExecuteAsync(favoriteEvent, action, context.Claim.AttemptNumber, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return await CancelAsync(context.Claim, favoriteEvent.Id);
                    }
                    catch
                    {
                        result = FavoriteActionExecutionResult.Retry(
                            "favorite_action_exception", "The favorite action failed and can be retried.");
                    }

                    if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                        return DurableJobCompletion.Cancelled();
                }
                else
                {
                    result = FavoriteActionExecutionResult.Failure(
                        "favorite_action_unsupported", "The configured favorite action is not available.");
                }

                if (!result.Succeeded)
                {
                    if (result.ErrorCode == SupersededCode ||
                        !await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                        return DurableJobCompletion.Cancelled();

                    var code = SafeOperationalText.Sanitize(result.ErrorCode, 100) ?? "favorite_action_failed";
                    var message = SafeOperationalText.Sanitize(result.SafeMessage) ??
                                  "The favorite action did not complete.";
                    action.State = FavoriteActionState.Failed;
                    action.LastErrorCode = code;
                    action.LastErrorMessage = message;
                    action.UpdatedAt = clock.UtcNow;
                    action.Revision++;
                    favoriteEvent.State = FavoriteEventState.Failed;
                    favoriteEvent.LastErrorCode = code;
                    favoriteEvent.LastErrorMessage = message;
                    favoriteEvent.UpdatedAt = clock.UtcNow;
                    favoriteEvent.Revision++;
                    await SaveFencedAsync(database, context.Claim, cancellationToken);
                    return result.Retryable
                        ? DurableJobCompletion.Retry(code, message)
                        : DurableJobCompletion.Failure(code, message);
                }

                if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                    return DurableJobCompletion.Cancelled();

                action.State = FavoriteActionState.Succeeded;
                action.CompletedAt = clock.UtcNow;
                action.UpdatedAt = clock.UtcNow;
                action.Revision++;
                await SaveFencedAsync(database, context.Claim, cancellationToken);
            }

            if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                return DurableJobCompletion.Cancelled();
            await RecordRecommendationSignalAsync(favoriteEvent, cancellationToken);
            if (!await IsCurrentAsync(context.Claim, favoriteEvent.Id, cancellationToken))
                return DurableJobCompletion.Cancelled();

            favoriteEvent.State = FavoriteEventState.Succeeded;
            favoriteEvent.CompletedAt = clock.UtcNow;
            favoriteEvent.UpdatedAt = clock.UtcNow;
            favoriteEvent.Revision++;
            await SaveFencedAsync(database, context.Claim, cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CancelAsync(context.Claim, favoriteEvent.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DurableJobCompletion.Cancelled();
        }
    }

    private async Task SaveFencedAsync(AllstarrDbContext database, DurableJobClaim claim,
        CancellationToken cancellationToken, bool allowCancellation = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var job = await database.Jobs.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == claim.JobId, cancellationToken);
            if (job == null || job.Type != JobType || job.OwnerUserId != claim.OwnerUserId ||
                job.State != DurableJobState.Running || job.AttemptCount != claim.AttemptNumber ||
                (!allowCancellation && job.CancellationRequestedAt != null))
                throw new DbUpdateConcurrencyException("The favorite execution is no longer current.");

            // Fence the action writes against recovery, re-claim and cancellation in
            // the same SaveChanges transaction, without changing the job's revision.
            database.Attach(job).Property(item => item.Revision).IsModified = true;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                // A progress write may change only the job revision. Retry with a
                // fresh fence; a changed generation or cancellation fails above.
            }
            finally
            {
                database.Entry(job).State = EntityState.Detached;
            }
        }
    }

    private async Task<bool> IsCurrentAsync(
        DurableJobClaim claim,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        if (claim.Type != FavoriteActionPipeline.JobType || !claim.OwnerUserId.HasValue)
            return false;

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await database.Jobs.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == claim.JobId && item.Type == FavoriteActionPipeline.JobType,
            cancellationToken);
        if (job == null || job.OwnerUserId != claim.OwnerUserId || job.State != DurableJobState.Running ||
            job.AttemptCount != claim.AttemptNumber || job.CancellationRequestedAt != null)
            return false;

        var favoriteEvent = await database.Set<FavoriteEventRecord>().AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == eventId && item.JobId == claim.JobId && item.OwnerUserId == claim.OwnerUserId,
            cancellationToken);
        if (favoriteEvent == null || favoriteEvent.State is FavoriteEventState.Succeeded or FavoriteEventState.Cancelled)
            return false;

        var protocol = favoriteEvent.Protocol.Trim().ToLowerInvariant();
        if (!await database.Users.AsNoTracking().AnyAsync(item =>
                item.Id == favoriteEvent.OwnerUserId && item.Enabled &&
                item.BackendType == protocol && item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
                item.BackendPrincipalId == favoriteEvent.BackendPrincipalId, cancellationToken))
            return false;

        var state = await database.Set<FavoriteStateRecord>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.OwnerUserId == favoriteEvent.OwnerUserId && item.Protocol == favoriteEvent.Protocol &&
            item.BackendInstanceId == favoriteEvent.BackendInstanceId && item.ItemId == favoriteEvent.ItemId,
            cancellationToken);
        if (state != null)
        {
            return state.LastEventId == favoriteEvent.Id &&
                   state.IsFavorite == (favoriteEvent.Operation == FavoriteOperation.Favorite);
        }

        var latestEventId = await database.Set<FavoriteEventRecord>().AsNoTracking()
            .Where(item => item.OwnerUserId == favoriteEvent.OwnerUserId &&
                           item.Protocol == favoriteEvent.Protocol &&
                           item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
                           item.ItemId == favoriteEvent.ItemId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return latestEventId == favoriteEvent.Id;
    }

    private async Task RecordRecommendationSignalAsync(
        FavoriteEventRecord favoriteEvent,
        CancellationToken cancellationToken)
    {
        if (recommendationSignals == null) return;
        var scope = new IntelligenceScope(
            favoriteEvent.OwnerUserId,
            favoriteEvent.Protocol,
            favoriteEvent.BackendInstanceId);
        var value = favoriteEvent.Operation == FavoriteOperation.Favorite ? 1d : -1d;
        try
        {
            if (recommendationSignals is IIdempotentRecommendationSignalWriter idempotent)
            {
                await idempotent.WriteIdempotentAsync(
                    scope,
                    "favorite",
                    favoriteEvent.ItemId,
                    value,
                    clock.UtcNow,
                    favoriteEvent.EventKey,
                    favoriteEvent.JobId,
                    cancellationToken);
            }
            else
            {
                await recommendationSignals.WriteAsync(
                    scope,
                    "favorite",
                    favoriteEvent.ItemId,
                    value,
                    clock.UtcNow,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Favorite state was saved, but its optional recommendation signal could not be recorded");
        }
    }

    private async Task<FavoriteActionExecutionResult> ApplyVirtualLikedStateAsync(
        AllstarrDbContext database,
        FavoriteEventRecord favoriteEvent,
        DurableJobClaim claim,
        CancellationToken cancellationToken)
    {
        var state = await database.Set<FavoriteStateRecord>().SingleOrDefaultAsync(item =>
            item.OwnerUserId == favoriteEvent.OwnerUserId && item.Protocol == favoriteEvent.Protocol &&
            item.BackendInstanceId == favoriteEvent.BackendInstanceId && item.ItemId == favoriteEvent.ItemId,
            cancellationToken);
        if (state != null && state.LastEventId != Guid.Empty && state.LastEventId != favoriteEvent.Id)
            return FavoriteActionExecutionResult.Failure(
                SupersededCode,
                "The favorite request was superseded by a newer request.");
        if (state != null && state.LastEventId == favoriteEvent.Id)
            return FavoriteActionExecutionResult.Success();

        if (state == null)
        {
            state = new FavoriteStateRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = favoriteEvent.OwnerUserId,
                Protocol = favoriteEvent.Protocol,
                BackendInstanceId = favoriteEvent.BackendInstanceId,
                ItemId = favoriteEvent.ItemId
            };
            database.Set<FavoriteStateRecord>().Add(state);
        }

        state.IsFavorite = favoriteEvent.Operation == FavoriteOperation.Favorite;
        state.LastEventId = favoriteEvent.Id;
        state.UpdatedAt = clock.UtcNow;
        state.Revision++;
        await SaveFencedAsync(database, claim, cancellationToken);
        return FavoriteActionExecutionResult.Success();
    }

    private async Task<DurableJobCompletion> CancelAsync(
        DurableJobClaim claim,
        Guid eventId)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await using var database = await contextFactory.CreateDbContextAsync(CancellationToken.None);
                var job = await database.Jobs.AsNoTracking().SingleOrDefaultAsync(
                    item => item.Id == claim.JobId && item.Type == FavoriteActionPipeline.JobType,
                    CancellationToken.None);
                var favoriteEvent = await database.Set<FavoriteEventRecord>().SingleOrDefaultAsync(
                    item => item.Id == eventId && item.JobId == claim.JobId && item.OwnerUserId == claim.OwnerUserId,
                    CancellationToken.None);
                if (job == null || favoriteEvent == null || job.OwnerUserId != claim.OwnerUserId ||
                    job.State != DurableJobState.Running || job.AttemptCount != claim.AttemptNumber)
                    return DurableJobCompletion.Cancelled();
                if (favoriteEvent.State is FavoriteEventState.Succeeded or FavoriteEventState.Cancelled)
                    return DurableJobCompletion.Cancelled();

                var now = clock.UtcNow;
                favoriteEvent.State = FavoriteEventState.Cancelled;
                favoriteEvent.CompletedAt = now;
                favoriteEvent.UpdatedAt = now;
                favoriteEvent.Revision++;
                var actions = await database.Set<FavoriteActionRecord>()
                    .Where(item => item.EventId == favoriteEvent.Id &&
                                   item.State != FavoriteActionState.Succeeded &&
                                   item.State != FavoriteActionState.Cancelled)
                    .ToListAsync(CancellationToken.None);
                foreach (var action in actions)
                {
                    action.State = FavoriteActionState.Cancelled;
                    action.CompletedAt = now;
                    action.UpdatedAt = now;
                    action.Revision++;
                }

                await SaveFencedAsync(database, claim, CancellationToken.None, allowCancellation: true);
                return DurableJobCompletion.Cancelled();
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                // Reload all rows on the second pass. A superseding event may have
                // changed either the event or an action while the handler was waiting.
            }
        }

        return DurableJobCompletion.Cancelled();
    }
}

public static class FavoriteActionRegistration
{
    public static IServiceCollection AddFavoriteActions(this IServiceCollection services)
    {
        services.AddSingleton<FavoriteViewReader>();
        services.AddSingleton<FavoriteActionPipeline>();
        services.AddSingleton<IFavoriteActionPipeline>(provider => provider.GetRequiredService<FavoriteActionPipeline>());
        services.AddSingleton<FavoriteDownloadActionExecutor>();
        services.AddSingleton<IFavoriteActionExecutor>(provider =>
            provider.GetRequiredService<FavoriteDownloadActionExecutor>());
        services.AddSingleton<IDurableJobHandler, FavoriteActionJobHandler>();
        return services;
    }
}
