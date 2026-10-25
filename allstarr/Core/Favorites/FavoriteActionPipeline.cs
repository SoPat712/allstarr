using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed record FavoriteMutationRequest(
    ProtocolExecutionContext ExecutionContext,
    string ItemId,
    FavoriteOperation Operation,
    string SourceRevision);

public sealed record FavoriteEventReceipt(Guid EventId, Guid JobId, bool Created, FavoriteEventState State);

public sealed record FavoriteActionStatus(
    string ActionType,
    FavoriteActionState State,
    int AttemptCount,
    string? ErrorCode,
    string? SafeMessage);

public sealed record FavoriteEventStatus(
    Guid EventId,
    Guid JobId,
    FavoriteOperation Operation,
    FavoriteEventState State,
    string Protocol,
    string BackendInstanceId,
    string ItemId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? SafeMessage,
    IReadOnlyList<FavoriteActionStatus> Actions);

public interface IFavoriteActionPipeline
{
    Task<FavoriteEventReceipt> RecordAsync(FavoriteMutationRequest request, CancellationToken cancellationToken = default);
    Task<FavoriteEventStatus?> GetStatusAsync(Guid userId, Guid eventId, CancellationToken cancellationToken = default);
}

public sealed partial class FavoriteActionPipeline(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableJobQueue jobs,
    IPlatformClock clock,
    RecordingKeptFiles? keptFiles = null,
    IProtocolProviderGateway? metadata = null) : IFavoriteActionPipeline
{
    public const string JobType = "favorite.process";
    public const string VirtualLikedAction = "virtual-liked";
    public async Task<FavoriteEventReceipt> RecordAsync(
        FavoriteMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var execution = request.ExecutionContext;
        var actor = execution.RequireActor();
        var userId = actor.EffectiveUserId ?? throw new UnauthorizedAccessException("A canonical user is required.");
        var itemId = Required(request.ItemId, nameof(request.ItemId), 500);
        var sourceRevision = Required(request.SourceRevision, nameof(request.SourceRevision), 300);
        var display = await CaptureDisplayAsync(request, itemId, userId, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            try { return await RecordOnceAsync(request, itemId, sourceRevision, display, cancellationToken); }
            catch (Exception error) when (FavoriteLinkService.Retryable(error) && attempt < 3) { }
        }
    }

    private async Task<FavoriteEventReceipt> RecordOnceAsync(FavoriteMutationRequest request, string itemId,
        string sourceRevision, string? display, CancellationToken cancellationToken)
    {
        var execution = request.ExecutionContext;
        var userId = execution.RequireActor().EffectiveUserId!.Value;
        var protocol = execution.Protocol.ToString().ToLowerInvariant();
        var backend = execution.BackendInstanceId;
        var now = clock.UtcNow;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId && item.Enabled &&
                item.BackendType == protocol && item.BackendInstanceId == backend &&
                item.BackendPrincipalId == execution.VerifiedBackendPrincipalId, cancellationToken)
            ?? throw new UnauthorizedAccessException("The favorite principal is no longer authorized.");
        var synthetic = await SyntheticActivityPolicy.IsExcludedAsync(context, new IntelligenceScope(userId, protocol, backend), itemId, cancellationToken);
        if (await context.Users.Where(item => item.Id == userId && item.Enabled && item.BackendType == protocol &&
                item.BackendInstanceId == backend && item.BackendPrincipalId == execution.VerifiedBackendPrincipalId && item.IsSynthetic == user.IsSynthetic)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), cancellationToken) != 1)
            throw new DbUpdateConcurrencyException("The favorite principal changed.");
        var key = FavoriteLinkScope.Key(itemId);
        var real = key != null && !synthetic;
        var member = real ? await FavoriteLinkScope.MemberAsync(context, user, cancellationToken) : null;
        LinkedProviderFavoriteStateRecord? shared = null;
        var changed = false;
        if (member != null)
        {
            shared = await FavoriteLinkScope.StateQuery(context, member.LinkId, key!).SingleOrDefaultAsync(cancellationToken);
            if (shared == null) { shared = FavoriteLinkScope.NewState(member.LinkId, key!, request.Operation == FavoriteOperation.Favorite, now); context.Add(shared); }
            else if (shared.IsFavorite != (request.Operation == FavoriteOperation.Favorite))
            {
                shared.IsFavorite = !shared.IsFavorite;
                shared.LifecycleVersion++;
                shared.Revision++;
                shared.EffectiveAt = now;
                changed = true;
            }
        }
        var scopedRevision = member != null ? $"{sourceRevision}:link:{member.LinkId:N}:{shared!.LifecycleVersion}" :
            key != null ? $"{sourceRevision}:{(synthetic ? "synthetic" : "private")}:{FavoriteLinkScope.Fingerprint(user)}" : sourceRevision;
        var eventKey = HashKey(userId, protocol, backend, itemId, request.Operation, scopedRevision);
        var currentState = await context.Set<FavoriteStateRecord>().SingleOrDefaultAsync(item =>
            item.OwnerUserId == userId && item.Protocol == protocol && item.BackendInstanceId == backend &&
            item.ItemId == itemId, cancellationToken);
        var acceptedEvents = context.Set<FavoriteEventRecord>().AsNoTracking().Where(item =>
            item.OwnerUserId == userId && item.Protocol == protocol && item.BackendInstanceId == backend &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId && item.ItemId == itemId);
        if (currentState != null) acceptedEvents = acceptedEvents.Where(item => item.Id == currentState.LastEventId);
        var latest = await acceptedEvents.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var sameScope = latest != null && latest.IsRealProviderIntent == real && (member == null ? latest.SharedLinkId == null :
            latest.SharedLinkId == member.LinkId && latest.SharedLifecycleVersion == shared!.LifecycleVersion);
        if (latest != null && latest.Operation == request.Operation &&
            latest.SourceRevision.Equals(sourceRevision, StringComparison.Ordinal) && sameScope)
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FavoriteEventReceipt(latest.Id, latest.JobId, false, latest.State);
        }
        // A changed intent or provider scope starts a new own-profile lifecycle even
        // when the backend revision is unchanged. The current pointer also fences
        // a replacement principal without reading its private metadata.
        if (latest != null && latest.Operation != request.Operation ||
            key != null && currentState != null && (latest == null || !sameScope))
        {
            eventKey = HashKey(userId, protocol, backend, itemId, request.Operation,
                $"{scopedRevision}:after:{currentState?.LastEventId ?? latest!.Id:N}");
        }
        var existing = await context.Set<FavoriteEventRecord>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.EventKey == eventKey, cancellationToken);
        if (existing != null)
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FavoriteEventReceipt(existing.Id, existing.JobId, false, existing.State);
        }

        var eventId = Guid.CreateVersion7();
        IReadOnlyList<FavoriteStateRecord> linkedTargets = [];
        if (member != null && (changed || request.Operation == FavoriteOperation.Unfavorite))
        {
            linkedTargets = await FavoriteLinkScope.TargetStatesAsync(context, member.LinkId, key!, cancellationToken);
            await CancelLinkedWorkAsync(context, member.LinkId, linkedTargets, now, cancellationToken);
        }
        await CancelSupersededWorkAsync(context, userId, protocol, backend, itemId, now, cancellationToken, synthetic);
        IReadOnlyList<string> actionTypes = synthetic ? [VirtualLikedAction] : BuildActions(request.Operation, itemId);
        var job = await jobs.EnqueueInExistingTransactionAsync(
            context,
            new DurableJobEnqueueRequest<FavoriteJobPayload>(
                JobType,
                eventKey,
                new FavoriteJobPayload(eventId),
                userId,
                CorrelationId: execution.CorrelationId),
            cancellationToken);
        var record = new FavoriteEventRecord
        {
            Id = eventId,
            OwnerUserId = userId,
            Protocol = protocol,
            BackendInstanceId = backend,
            BackendPrincipalId = execution.VerifiedBackendPrincipalId,
            ItemId = itemId,
            Operation = request.Operation,
            IsRealProviderIntent = real,
            SharedLinkId = member?.LinkId,
            SharedLifecycleVersion = shared?.LifecycleVersion,
            SourceRevision = sourceRevision,
            EventKey = eventKey,
            CorrelationId = execution.CorrelationId,
            PolicySnapshotJson = JsonSerializer.Serialize(new
            {
                actions = actionTypes,
                policySource = "download-only"
            }),
            JobId = job.JobId,
            State = FavoriteEventState.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Set<FavoriteEventRecord>().Add(record);
        if (shared != null && (changed || context.Entry(shared).State == EntityState.Added)) shared.LastIntentEventId = eventId;
        if (currentState == null)
        {
            currentState = new FavoriteStateRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = userId,
                Protocol = protocol,
                BackendInstanceId = backend,
                ItemId = itemId
            };
            context.Add(currentState);
        }
        currentState.IsFavorite = request.Operation == FavoriteOperation.Favorite;
        if (display != null || key != null && latest == null) currentState.DisplayMetadataJson = display;
        currentState.LastEventId = eventId;
        currentState.UpdatedAt = now;
        currentState.Revision++;
        foreach (var actionType in actionTypes)
        {
            context.Set<FavoriteActionRecord>().Add(new FavoriteActionRecord
            {
                Id = Guid.CreateVersion7(),
                EventId = eventId,
                OwnerUserId = userId,
                ActionType = actionType,
                IdempotencyKey = $"{eventKey}:{actionType}",
                Reversible = actionType == VirtualLikedAction,
                State = FavoriteActionState.Pending,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        try
        {
            if (keptFiles != null && !synthetic)
            {
                if (member != null && request.Operation == FavoriteOperation.Unfavorite)
                    await keptFiles.StageLinkedFavoriteReleaseAsync(context, linkedTargets, record, cancellationToken);
                else await keptFiles.StageFavoriteInterestAsync(context, currentState, record, cancellationToken);
            }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FavoriteEventReceipt(eventId, job.JobId, true, FavoriteEventState.Pending);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<FavoriteEventStatus?> GetStatusAsync(
        Guid userId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await context.Set<FavoriteEventRecord>().AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == eventId && candidate.OwnerUserId == userId,
            cancellationToken);
        if (item == null) return null;
        var actions = await context.Set<FavoriteActionRecord>().AsNoTracking()
            .Where(action => action.EventId == eventId)
            .OrderBy(action => action.CreatedAt)
            .Select(action => new FavoriteActionStatus(action.ActionType, action.State, action.AttemptCount,
                action.LastErrorCode, action.LastErrorMessage))
            .ToListAsync(cancellationToken);
        return new FavoriteEventStatus(item.Id, item.JobId, item.Operation, item.State, item.Protocol,
            item.BackendInstanceId, item.ItemId, item.CreatedAt, item.CompletedAt,
            item.LastErrorCode, item.LastErrorMessage, actions);
    }

    private static IReadOnlyList<string> BuildActions(FavoriteOperation operation, string itemId) =>
        operation == FavoriteOperation.Favorite &&
        FavoriteDownloadActionExecutor.ParseExternalTrack(itemId) != null
            ? [VirtualLikedAction, "download"]
            : [VirtualLikedAction];

    private async Task CancelSupersededWorkAsync(AllstarrDbContext context, Guid userId,
        string protocol, string backend, string itemId, DateTimeOffset now, CancellationToken cancellationToken, bool syntheticOnly = false)
    {
        var priorEvents = await context.Set<FavoriteEventRecord>()
            .Where(item => item.OwnerUserId == userId && item.Protocol == protocol &&
                           item.BackendInstanceId == backend && item.ItemId == itemId &&
                           (item.State == FavoriteEventState.Pending || item.State == FavoriteEventState.Running ||
                            item.State == FavoriteEventState.Failed))
            .ToListAsync(cancellationToken);
        if (syntheticOnly) priorEvents = priorEvents.Where(item => !item.IsRealProviderIntent && item.SharedLinkId == null).ToList();
        await CancelEventsAsync(context, priorEvents, now, cancellationToken);
    }

    private static async Task CancelLinkedWorkAsync(AllstarrDbContext context, Guid linkId,
        IReadOnlyList<FavoriteStateRecord> targets, DateTimeOffset now, CancellationToken token)
    {
        var members = await context.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().Where(item => item.LinkId == linkId).ToArrayAsync(token);
        var events = new List<FavoriteEventRecord>();
        foreach (var target in targets)
        {
            var member = members.Single(item => item.UserId == target.OwnerUserId);
            var pending = await context.FavoriteEvents.Where(item => item.OwnerUserId == member.UserId && item.Protocol == member.Protocol &&
                item.BackendInstanceId == member.BackendInstanceId && item.BackendPrincipalId == member.BackendPrincipalId && item.ItemId == target.ItemId &&
                item.IsRealProviderIntent && (item.State == FavoriteEventState.Pending || item.State == FavoriteEventState.Running || item.State == FavoriteEventState.Failed)).ToArrayAsync(token);
            events.AddRange(pending.Where(item => item.SharedLinkId == linkId || item.SharedLinkId == null && FavoriteLinkScope.WithinBound(item, member)));
        }
        await CancelEventsAsync(context, events, now, token);
    }

    private static async Task CancelEventsAsync(AllstarrDbContext context, IReadOnlyList<FavoriteEventRecord> priorEvents,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (priorEvents.Count == 0) return;
        var eventIds = priorEvents.Select(item => item.Id).ToArray();
        var jobIds = priorEvents.Select(item => item.JobId).ToArray();
        var pendingJobs = await context.Jobs.Where(item => jobIds.Contains(item.Id) &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled ||
             item.State == DurableJobState.Running)).ToListAsync(cancellationToken);
        foreach (var job in pendingJobs)
        {
            job.CancellationRequestedAt ??= now;
            if (job.State != DurableJobState.Running)
            {
                job.State = DurableJobState.Cancelled;
                job.CompletedAt = now;
            }
            job.UpdatedAt = now;
            job.Revision++;
        }
        var cancelledJobIds = pendingJobs.Select(item => item.Id).ToHashSet();
        foreach (var prior in priorEvents.Where(item => cancelledJobIds.Contains(item.JobId)))
        {
            prior.State = FavoriteEventState.Cancelled;
            prior.CompletedAt = now;
            prior.UpdatedAt = now;
            prior.Revision++;
        }
        var actions = await context.Set<FavoriteActionRecord>()
            .Where(item => eventIds.Contains(item.EventId) && item.State != FavoriteActionState.Succeeded &&
                           item.State != FavoriteActionState.Cancelled).ToListAsync(cancellationToken);
        foreach (var action in actions.Where(item => priorEvents.Any(e => e.Id == item.EventId && cancelledJobIds.Contains(e.JobId))))
        {
            action.State = FavoriteActionState.Cancelled;
            action.CompletedAt = now;
            action.UpdatedAt = now;
            action.Revision++;
        }
    }

    private static string HashKey(Guid user, string protocol, string backend, string item,
        FavoriteOperation operation, string revision)
    {
        var value = string.Join('\n', user.ToString("N"), protocol, backend, item,
            operation.ToString(), revision);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Required(string? value, string name, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException($"{name} is required and must be at most {maxLength} characters.", name);
        return value;
    }

}

public sealed record FavoriteJobPayload(Guid EventId);

public sealed record FavoriteActionExecutionResult(bool Succeeded, bool Retryable = false,
    string? ErrorCode = null, string? SafeMessage = null)
{
    public static FavoriteActionExecutionResult Success() => new(true);
    public static FavoriteActionExecutionResult Retry(string code, string message) => new(false, true, code, message);
    public static FavoriteActionExecutionResult Failure(string code, string message) => new(false, false, code, message);
}

public interface IFavoriteActionExecutor
{
    string ActionType { get; }
    Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action, int executionAttempt, CancellationToken cancellationToken);
}
