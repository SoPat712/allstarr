using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Jobs;

public sealed partial class DurableJobQueue
{
    internal async Task<DurableJobEnqueueResult> EnqueuePlaylistRematchInExistingTransactionAsync(
        AllstarrDbContext context,
        DurableJobEnqueueRequest<PlaylistRematchJobPayload> request,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Job staging requires the caller's transaction.");
        ValidateEnqueueRequest(request.Type, request.IdempotencyKey, request.Priority,
            request.MaxAttempts, request.MaxDeferrals);
        var saved = await _contextAuthorizer.AuthorizeEnqueueAsync(request.OwnerUserId,
            request.ProviderAccountId, request.Capability, request.CorrelationId, cancellationToken, request.Type);
        var scopeKey = CreateSavedScopeKey(saved);
        var candidates = await context.Jobs.Where(item =>
            item.ScopeKey == scopeKey && item.Type == request.Type && item.CancellationRequestedAt == null &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running ||
             item.State == DurableJobState.RetryScheduled)).OrderBy(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var active = candidates.FirstOrDefault(item => SamePlaylist(item.PayloadJson, request.Payload.PlaylistLinkId));
        if (active == null)
            return await EnqueueInExistingTransactionAsync(context,
                request with { IdempotencyKey = request.IdempotencyKey + Guid.CreateVersion7().ToString("N") }, cancellationToken);

        var payloadJson = _payloadPolicy.SerializeAndValidate(request.Payload);
        if (active.OwnerUserId != saved.OwnerUserId || active.ProviderAccountId != saved.ProviderAccountId ||
            active.ProviderCapability != saved.ProviderCapability || active.PolicySnapshotJson != saved.PolicySnapshotJson)
            throw new InvalidOperationException("The queued work has a different execution context.");
        if (active.PayloadJson != payloadJson)
        {
            active.PayloadJson = payloadJson;
            active.RequestFingerprint = CreateRequestFingerprint(payloadJson, active.Priority,
                active.MaxAttempts, active.MaxDeferrals, request.AvailableAt);
            active.UpdatedAt = _clock.UtcNow;
            active.Revision++;
        }
        return new(active.Id, false);
    }

    private static bool SamePlaylist(string payloadJson, Guid? playlistLinkId)
    {
        if (!playlistLinkId.HasValue) return false;
        try { return JsonSerializer.Deserialize<PlaylistRematchJobPayload>(payloadJson)?.PlaylistLinkId == playlistLinkId; }
        catch (JsonException) { return false; }
    }
}
