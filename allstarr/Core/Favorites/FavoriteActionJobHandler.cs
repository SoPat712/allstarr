using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace allstarr.Core.Favorites;

public sealed class FavoriteActionJobHandler(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IEnumerable<IFavoriteActionExecutor> actionExecutors,
    IPlatformClock clock) : IDurableJobHandler
{
    private readonly IReadOnlyDictionary<string, IFavoriteActionExecutor> _executors = actionExecutors.ToDictionary(
        executor => executor.ActionType.Trim().ToLowerInvariant(), StringComparer.Ordinal);

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
                    item.TenantId == context.Claim.TenantId && item.OwnerUserId == context.Claim.OwnerUserId,
            cancellationToken);
        if (favoriteEvent == null)
            return DurableJobCompletion.Failure("favorite_event_missing", "The favorite event is unavailable.");
        if (favoriteEvent.State == FavoriteEventState.Succeeded)
            return DurableJobCompletion.Success();
        if (favoriteEvent.State == FavoriteEventState.Cancelled)
            return DurableJobCompletion.Cancelled();

        favoriteEvent.State = FavoriteEventState.Running;
        favoriteEvent.LastErrorCode = null;
        favoriteEvent.LastErrorMessage = null;
        favoriteEvent.UpdatedAt = clock.UtcNow;
        favoriteEvent.Revision++;
        await database.SaveChangesAsync(cancellationToken);

        var actions = await database.Set<FavoriteActionRecord>()
            .Where(item => item.EventId == favoriteEvent.Id && item.State != FavoriteActionState.Succeeded &&
                           item.State != FavoriteActionState.Cancelled)
            .OrderBy(item => item.ActionType == FavoriteActionPipeline.VirtualLikedAction ? 0 :
                             item.ActionType == "match" ? 1 :
                             item.ActionType == "download" ? 2 :
                             item.ActionType == "place" ? 3 :
                             item.ActionType == "enrich" ? 4 :
                             item.ActionType == "refresh" ? 5 : 99)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var action in actions)
        {
            if (cancellationToken.IsCancellationRequested)
                return await CancelAsync(database, favoriteEvent, cancellationToken);

            action.State = FavoriteActionState.Running;
            action.AttemptCount++;
            action.LastErrorCode = null;
            action.LastErrorMessage = null;
            action.UpdatedAt = clock.UtcNow;
            action.Revision++;
            await database.SaveChangesAsync(cancellationToken);

            FavoriteActionExecutionResult result;
            if (action.ActionType == FavoriteActionPipeline.VirtualLikedAction)
            {
                result = await ApplyVirtualLikedStateAsync(database, favoriteEvent, cancellationToken);
            }
            else if (_executors.TryGetValue(action.ActionType, out var executor))
            {
                try
                {
                    result = await executor.ExecuteAsync(favoriteEvent, action, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return await CancelAsync(database, favoriteEvent, cancellationToken);
                }
                catch
                {
                    result = FavoriteActionExecutionResult.Retry(
                        "favorite_action_exception", "The favorite action failed and can be retried.");
                }
            }
            else
            {
                result = FavoriteActionExecutionResult.Failure(
                    "favorite_action_unsupported", "The configured favorite action is not available.");
            }

            if (!result.Succeeded)
            {
                var code = SafeOperationalText.Sanitize(result.ErrorCode, 100) ?? "favorite_action_failed";
                var message = SafeOperationalText.Sanitize(result.SafeMessage) ?? "The favorite action did not complete.";
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
                await database.SaveChangesAsync(cancellationToken);
                return result.Retryable
                    ? DurableJobCompletion.Retry(code, message)
                    : DurableJobCompletion.Failure(code, message);
            }

            action.State = FavoriteActionState.Succeeded;
            action.CompletedAt = clock.UtcNow;
            action.UpdatedAt = clock.UtcNow;
            action.Revision++;
            await database.SaveChangesAsync(cancellationToken);
        }

        favoriteEvent.State = FavoriteEventState.Succeeded;
        favoriteEvent.CompletedAt = clock.UtcNow;
        favoriteEvent.UpdatedAt = clock.UtcNow;
        favoriteEvent.Revision++;
        database.OutboxMessages.Add(new OutboxMessageRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = favoriteEvent.TenantId,
            Type = "favorite.completed",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { eventId = favoriteEvent.Id }),
            State = OutboxMessageState.Pending,
            AvailableAt = clock.UtcNow,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        });
        await database.SaveChangesAsync(cancellationToken);
        return DurableJobCompletion.Success();
    }

    private async Task<FavoriteActionExecutionResult> ApplyVirtualLikedStateAsync(
        AllstarrDbContext database, FavoriteEventRecord favoriteEvent, CancellationToken cancellationToken)
    {
        var state = await database.Set<FavoriteStateRecord>().SingleOrDefaultAsync(item =>
            item.TenantId == favoriteEvent.TenantId && item.OwnerUserId == favoriteEvent.OwnerUserId &&
            item.Protocol == favoriteEvent.Protocol && item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
            item.ItemId == favoriteEvent.ItemId, cancellationToken);
        if (state == null)
        {
            state = new FavoriteStateRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = favoriteEvent.TenantId,
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
        await database.SaveChangesAsync(cancellationToken);
        return FavoriteActionExecutionResult.Success();
    }

    private async Task<DurableJobCompletion> CancelAsync(AllstarrDbContext database,
        FavoriteEventRecord favoriteEvent, CancellationToken cancellationToken)
    {
        favoriteEvent.State = FavoriteEventState.Cancelled;
        favoriteEvent.CompletedAt = clock.UtcNow;
        favoriteEvent.UpdatedAt = clock.UtcNow;
        favoriteEvent.Revision++;
        var actions = await database.Set<FavoriteActionRecord>()
            .Where(item => item.EventId == favoriteEvent.Id && item.State == FavoriteActionState.Pending)
            .ToListAsync(CancellationToken.None);
        foreach (var action in actions)
        {
            action.State = FavoriteActionState.Cancelled;
            action.CompletedAt = clock.UtcNow;
            action.UpdatedAt = clock.UtcNow;
            action.Revision++;
        }
        await database.SaveChangesAsync(CancellationToken.None);
        return DurableJobCompletion.Cancelled();
    }
}

public static class FavoriteActionRegistration
{
    public static IServiceCollection AddFavoriteActions(this IServiceCollection services, IConfiguration configuration)
    {
        var policy = new FavoriteActionPolicyOptions();
        configuration.GetSection("FavoriteActions").Bind(policy);
        services.AddSingleton(policy);
        services.AddSingleton<IDurableFavoriteActionPolicyResolver, DurableFavoriteActionPolicyResolver>();
        services.AddSingleton<FavoriteActionPolicyStore>();
        services.AddSingleton<FavoriteActionPipeline>();
        services.AddSingleton<IFavoriteActionPipeline>(provider => provider.GetRequiredService<FavoriteActionPipeline>());
        services.AddSingleton<IFavoriteActionExecutor, FavoriteMatchActionExecutor>();
        services.AddSingleton<IFavoriteActionExecutor, FavoriteDownloadActionExecutor>();
        var placement = new FavoritePlacementOptions();
        configuration.GetSection("FavoriteActions:Placement").Bind(placement);
        services.AddSingleton(placement);
        services.AddSingleton<FavoriteTrackMetadataResolver>();
        services.AddSingleton<IFavoriteActionExecutor, FavoritePlaceActionExecutor>();
        services.AddSingleton<IFavoriteActionExecutor, FavoriteEnrichActionExecutor>();
        services.AddSingleton<IFavoriteActionExecutor, FavoriteRefreshActionExecutor>();
        services.AddHttpClient(LastFmFavoriteActionExecutor.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IFavoriteActionExecutor, LastFmFavoriteActionExecutor>();
        services.AddSingleton<IDurableJobHandler, FavoriteActionJobHandler>();
        return services;
    }
}
