using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed record FavoriteMutationRequest(
    ProtocolExecutionContext ExecutionContext,
    string ItemId,
    FavoriteOperation Operation,
    string SourceRevision,
    IReadOnlyCollection<string>? OptedInActions = null);

public sealed record FavoriteEventReceipt(Guid EventId, Guid JobId, bool Created, FavoriteEventState State);

public sealed class FavoriteActionPolicyOptions
{
    public bool AddToVirtualLiked { get; set; } = true;
    public bool MatchLocalLibrary { get; set; }
    public bool AutoDownload { get; set; }
    public bool EnrichMetadata { get; set; }
    public bool PlaceManagedFile { get; set; }
    public bool RefreshBackendLibrary { get; set; }
}

public sealed record EffectiveFavoriteActionPolicy(bool AddToVirtualLiked, bool MatchLocalLibrary,
    bool AutoDownload, bool EnrichMetadata, bool PlaceManagedFile, bool RefreshBackendLibrary,
    string Source, Guid? TargetCredentialReferenceId = null);


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
    Task<FavoriteEventStatus?> GetStatusAsync(Guid tenantId, Guid userId, Guid eventId, CancellationToken cancellationToken = default);
}

public sealed class FavoriteActionPipeline(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableJobQueue jobs,
    IPlatformClock clock,
    FavoriteActionPolicyOptions? policy = null,
    IDurableFavoriteActionPolicyResolver? policyResolver = null,
    IProtocolLibraryScopeResolver? libraryScopes = null,
    IScopedRecommendationAccountAccessor? recommendationAccounts = null) : IFavoriteActionPipeline
{
    public const string JobType = "favorite.process";
    public const string VirtualLikedAction = "virtual-liked";
    public const string LastFmAction = "lastfm";
    private static readonly string[] OrderedActionTypes =
        [VirtualLikedAction, "match", "download", "place", "enrich", "refresh", LastFmAction];
    private readonly IDurableFavoriteActionPolicyResolver _policyResolver = policyResolver ??
        new ConfiguredPolicyResolver(policy ?? new FavoriteActionPolicyOptions());

    public async Task<FavoriteEventReceipt> RecordAsync(
        FavoriteMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var execution = request.ExecutionContext;
        if (string.IsNullOrWhiteSpace(execution.LibraryScopeId) && libraryScopes != null)
            execution = await libraryScopes.ResolveAsync(execution, request.ItemId, cancellationToken);
        var actor = execution.RequireActor();
        var userId = actor.EffectiveUserId ?? throw new UnauthorizedAccessException("A canonical user is required.");
        var itemId = Required(request.ItemId, nameof(request.ItemId), 500);
        var sourceRevision = Required(request.SourceRevision, nameof(request.SourceRevision), 300);
        var protocol = execution.Protocol.ToString().ToLowerInvariant();
        var backend = execution.BackendInstanceId;
        var eventKey = HashKey(actor.TenantId, userId, protocol, backend, itemId, request.Operation, sourceRevision);
        var now = clock.UtcNow;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latest = await context.Set<FavoriteEventRecord>().AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == userId &&
                           item.Protocol == protocol && item.BackendInstanceId == backend && item.ItemId == itemId)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Select(item => new { item.Id, item.JobId, item.Operation, item.SourceRevision, item.State })
            .FirstOrDefaultAsync(cancellationToken);
        if (latest != null && latest.Operation == request.Operation &&
            latest.SourceRevision.Equals(sourceRevision, StringComparison.Ordinal))
        {
            return new FavoriteEventReceipt(latest.Id, latest.JobId, false, latest.State);
        }
        // A favorite after an unfavorite is a new lifecycle even when the backend supplies no revision.
        // Repeated notifications for the current lifecycle still collapse to one event.
        if (latest != null && latest.Operation != request.Operation)
        {
            eventKey = HashKey(actor.TenantId, userId, protocol, backend, itemId, request.Operation,
                $"{sourceRevision}:after:{latest.Id:N}");
        }
        var existing = await context.Set<FavoriteEventRecord>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.EventKey == eventKey, cancellationToken);
        if (existing != null)
        {
            return new FavoriteEventReceipt(existing.Id, existing.JobId, false, existing.State);
        }

        var effectivePolicy = await _policyResolver.ResolveAsync(actor.TenantId, userId, protocol, backend,
            execution.LibraryScopeId, cancellationToken);
        var includeLastFm = await HasLastFmAccountAsync(actor.TenantId, userId, execution, cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        existing = await context.Set<FavoriteEventRecord>()
            .SingleOrDefaultAsync(item => item.EventKey == eventKey, cancellationToken);
        if (existing != null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new FavoriteEventReceipt(existing.Id, existing.JobId, false, existing.State);
        }

        var eventId = Guid.CreateVersion7();
        if (request.Operation == FavoriteOperation.Unfavorite)
        {
            await CancelPendingFavoriteWorkAsync(context, actor.TenantId, userId, protocol, backend, itemId, now, cancellationToken);
        }

        var actionTypes = BuildActions(request, effectivePolicy, includeLastFm);
        var job = await jobs.EnqueueInExistingTransactionAsync(
            context,
            new DurableJobEnqueueRequest<FavoriteJobPayload>(
                JobType,
                eventKey,
                new FavoriteJobPayload(eventId),
                actor.TenantId,
                userId,
                CorrelationId: execution.CorrelationId),
            cancellationToken);
        var record = new FavoriteEventRecord
        {
            Id = eventId,
            TenantId = actor.TenantId,
            OwnerUserId = userId,
            Protocol = protocol,
            BackendInstanceId = backend,
            BackendPrincipalId = execution.VerifiedBackendPrincipalId,
            LibraryScopeId = execution.LibraryScopeId,
            ItemId = itemId,
            Operation = request.Operation,
            SourceRevision = sourceRevision,
            EventKey = eventKey,
            CorrelationId = execution.CorrelationId,
            PolicySnapshotJson = JsonSerializer.Serialize(new
            {
                actions = actionTypes,
                policySource = effectivePolicy.Source,
                targetCredentialReferenceId = effectivePolicy.TargetCredentialReferenceId
            }),
            TargetCredentialReferenceId = effectivePolicy.TargetCredentialReferenceId,
            JobId = job.JobId,
            State = FavoriteEventState.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Set<FavoriteEventRecord>().Add(record);
        foreach (var actionType in actionTypes)
        {
            context.Set<FavoriteActionRecord>().Add(new FavoriteActionRecord
            {
                Id = Guid.CreateVersion7(),
                EventId = eventId,
                TenantId = actor.TenantId,
                OwnerUserId = userId,
                ActionType = actionType,
                IdempotencyKey = $"{eventKey}:{actionType}",
                Reversible = actionType == VirtualLikedAction,
                State = FavoriteActionState.Pending,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        context.OutboxMessages.Add(jobs.CreateOutbox(actor.TenantId, "favorite.recorded", new
        {
            eventId,
            jobId = job.JobId,
            operation = request.Operation.ToString().ToLowerInvariant()
        }, now));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FavoriteEventReceipt(eventId, job.JobId, true, FavoriteEventState.Pending);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await using var retry = await contextFactory.CreateDbContextAsync(cancellationToken);
            existing = await retry.Set<FavoriteEventRecord>().AsNoTracking()
                .SingleAsync(item => item.EventKey == eventKey, cancellationToken);
            return new FavoriteEventReceipt(existing.Id, existing.JobId, false, existing.State);
        }
    }

    public async Task<FavoriteEventStatus?> GetStatusAsync(
        Guid tenantId,
        Guid userId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await context.Set<FavoriteEventRecord>().AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == eventId && candidate.TenantId == tenantId && candidate.OwnerUserId == userId,
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

    private async Task<bool> HasLastFmAccountAsync(Guid tenantId, Guid userId,
        ProtocolExecutionContext execution, CancellationToken cancellationToken)
    {
        if (recommendationAccounts == null || string.IsNullOrWhiteSpace(execution.LibraryScopeId))
            return false;
        var scope = new IntelligenceScope(tenantId, userId,
            execution.Protocol.ToString().ToLowerInvariant(), execution.BackendInstanceId,
            execution.LibraryScopeId);
        return await recommendationAccounts.HasAccountAsync(scope, "lastfm", cancellationToken);
    }

    private static IReadOnlyList<string> BuildActions(FavoriteMutationRequest request,
        EffectiveFavoriteActionPolicy policy, bool includeLastFm)
    {
        if (policy.PlaceManagedFile && !policy.AutoDownload || policy.EnrichMetadata && !policy.PlaceManagedFile)
            throw new InvalidOperationException("The effective favorite action policy has invalid download, placement, or enrichment dependencies.");
        var actions = new HashSet<string>(StringComparer.Ordinal);
        if (policy.AddToVirtualLiked) actions.Add(VirtualLikedAction);
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        if (policy.MatchLocalLibrary) enabled.Add("match");
        if (policy.AutoDownload) enabled.Add("download");
        if (policy.EnrichMetadata) enabled.Add("enrich");
        if (policy.PlaceManagedFile) enabled.Add("place");
        if (policy.RefreshBackendLibrary) enabled.Add("refresh");
        if (request.Operation == FavoriteOperation.Favorite && request.OptedInActions != null)
        {
            foreach (var value in request.OptedInActions)
            {
                var action = Required(value, nameof(request.OptedInActions), 100).ToLowerInvariant();
                if (!OrderedActionTypes.Contains(action, StringComparer.Ordinal))
                    throw new ArgumentException("The favorite action is unsupported.", nameof(request));
                if (action != VirtualLikedAction && !enabled.Contains(action))
                    throw new UnauthorizedAccessException("The requested favorite action is not enabled for this user and backend.");
                actions.Add(action);
            }
        }
        if (request.Operation == FavoriteOperation.Favorite)
        {
            actions.UnionWith(enabled);
        }
        if (includeLastFm) actions.Add(LastFmAction);
        return OrderedActionTypes.Where(actions.Contains).ToArray();
    }

    private async Task CancelPendingFavoriteWorkAsync(AllstarrDbContext context, Guid tenantId, Guid userId,
        string protocol, string backend, string itemId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var priorEvents = await context.Set<FavoriteEventRecord>()
            .Where(item => item.TenantId == tenantId && item.OwnerUserId == userId && item.Protocol == protocol &&
                           item.BackendInstanceId == backend && item.ItemId == itemId &&
                           item.Operation == FavoriteOperation.Favorite && item.State == FavoriteEventState.Pending)
            .ToListAsync(cancellationToken);
        if (priorEvents.Count == 0) return;
        var eventIds = priorEvents.Select(item => item.Id).ToArray();
        var jobIds = priorEvents.Select(item => item.JobId).ToArray();
        var pendingJobs = await context.Jobs.Where(item => jobIds.Contains(item.Id) &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled)).ToListAsync(cancellationToken);
        foreach (var job in pendingJobs)
        {
            job.State = DurableJobState.Cancelled;
            job.CancellationRequestedAt = now;
            job.CompletedAt = now;
            job.UpdatedAt = now;
            job.Revision++;
            context.OutboxMessages.Add(jobs.CreateOutbox(tenantId, "job.cancelled", new { jobId = job.Id, jobType = job.Type }, now));
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
            .Where(item => eventIds.Contains(item.EventId) && item.State == FavoriteActionState.Pending).ToListAsync(cancellationToken);
        foreach (var action in actions.Where(item => priorEvents.Any(e => e.Id == item.EventId && cancelledJobIds.Contains(e.JobId))))
        {
            action.State = FavoriteActionState.Cancelled;
            action.CompletedAt = now;
            action.UpdatedAt = now;
            action.Revision++;
        }
    }

    private static string HashKey(Guid tenant, Guid user, string protocol, string backend, string item,
        FavoriteOperation operation, string revision)
    {
        var value = string.Join('\n', tenant.ToString("N"), user.ToString("N"), protocol, backend, item,
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

    private sealed class ConfiguredPolicyResolver(FavoriteActionPolicyOptions options)
        : IDurableFavoriteActionPolicyResolver
    {
        public Task<EffectiveFavoriteActionPolicy> ResolveAsync(Guid tenantId, Guid ownerUserId, string protocol,
            string backendInstanceId, string? libraryScopeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveFavoriteActionPolicy(options.AddToVirtualLiked,
                options.MatchLocalLibrary, options.AutoDownload, options.EnrichMetadata,
                options.PlaceManagedFile, options.RefreshBackendLibrary, "configured-default"));
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
        FavoriteActionRecord action, CancellationToken cancellationToken);
}
