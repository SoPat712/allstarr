using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed class PlaybackScrobbleRepair(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs)
{
    public async Task<bool> ResolveAsync(Guid userId, Guid deliveryId, long revision, string action,
        long? accountRevision, CancellationToken token, Guid? destinationAccountId = null)
    {
        await using var lookup = await factory.CreateDbContextAsync(token);
        var saved = await lookup.PlaybackDeliveryCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId && x.OwnerUserId == userId, token);
        if (saved == null) return false;
        var gate = BoundPlaybackScrobbleDelivery.Gate(userId, saved.SignalKey, saved.TargetId);
        await gate.WaitAsync(token);
        try { return await ResolveCoreAsync(userId, deliveryId, revision, action, accountRevision, token, destinationAccountId); }
        finally { gate.Release(); }
    }

    private async Task<bool> ResolveCoreAsync(Guid userId, Guid deliveryId, long revision, string action,
        long? accountRevision, CancellationToken token, Guid? destinationAccountId)
    {
        if (action is not ("retry" or "confirm-delivered")) throw new ArgumentException("Choose retry or confirm-delivered.");
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var row = await db.PlaybackDeliveryCheckpoints.SingleOrDefaultAsync(x => x.Id == deliveryId && x.OwnerUserId == userId, token);
        if (row == null) return false;
        if (row.Revision != revision || row.State is ScopedPlaybackScrobbleOutcome.Delivered or ScopedPlaybackScrobbleOutcome.Ignored ||
            row.SignalPayloadJson == null)
            throw new InvalidOperationException("Refresh the delivery before resolving it.");
        var payload = JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson)!;
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.Enabled && x.BackendType == payload.Scope.Protocol &&
                x.BackendInstanceId == payload.Scope.BackendInstanceId, token)) throw new UnauthorizedAccessException();
        var running = await db.Jobs.AsNoTracking().Where(x => x.OwnerUserId == userId &&
            x.Type == PlaybackSignalPipeline.JobType && x.State == DurableJobState.Running).Select(x => x.PayloadJson).ToArrayAsync(token);
        if (running.Select(x => JsonSerializer.Deserialize<PlaybackSignalPayload>(x)).Any(x => x != null &&
                ScopedPlaybackScrobbleDelivery.CheckpointKey(x) == row.SignalKey))
            throw new InvalidOperationException("This listen is still being processed. Wait for it to finish before resolving it.");
        var ambiguous = row.ProviderCode is "ambiguous" or "destination-unbound" || row.AttemptStartedAt != null && !row.ReplaySafe;
        if (action == "confirm-delivered" && !ambiguous)
            throw new InvalidOperationException("Only an uncertain send can be confirmed as delivered.");
        if (action == "retry")
        {
            if (row.DestinationAccountId != null && destinationAccountId != null && destinationAccountId != row.DestinationAccountId)
                throw new InvalidOperationException("A retry must keep the saved destination account.");
            var selectedId = row.DestinationAccountId ?? destinationAccountId
                ?? throw new InvalidOperationException("Choose the intended destination account for this older listen.");
            var account = await db.ProviderAccounts.SingleOrDefaultAsync(x => x.Id == selectedId &&
                x.ProviderId == row.TargetId && x.Enabled && (x.OwnerUserId == userId || x.OwnerUserId == null), token)
                ?? throw new InvalidOperationException("Reconnect the saved destination account first.");
            if (account.Revision != accountRevision) throw new InvalidOperationException("Confirm the current destination account revision.");
            var original = row.DestinationJson == null ? null : JsonSerializer.Deserialize<ProviderAccountContext>(row.DestinationJson);
            if (original != null && original.OwnerUserId != account.OwnerUserId)
                throw new InvalidOperationException("The destination account ownership changed.");
            row.DestinationAccountId = account.Id;
            payload = payload with { DestinationTargets = [row.TargetId] };
            row.SignalPayloadJson = JsonSerializer.Serialize(payload);
            row.DestinationAccountRevision = account.Revision;
            row.DestinationJson = JsonSerializer.Serialize(new ProviderAccountContext(account.Id, account.ProviderId,
                account.Scope, account.Revision, account.Enabled, account.OwnerUserId, "scrobble-repair", account.SecretReferenceId));
            row.State = ScopedPlaybackScrobbleOutcome.Retrying;
            row.ProviderCode = "repair-queued";
            row.RetryAfter = null;
            row.AttemptStartedAt = null;
            row.RequiresReauthentication = false;
            row.SafeMessage = "An explicit retry was requested for the saved destination.";
            await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<PlaybackSignalPayload>(
                PlaybackSignalPipeline.JobType, $"scrobble-repair:{row.Id:N}:{row.Revision + 1}", payload,
                userId), token);
        }
        else
        {
            row.State = ScopedPlaybackScrobbleOutcome.Delivered;
            row.ProviderCode = "confirmed-delivered";
            row.AttemptStartedAt = null;
            row.SafeMessage = "The listener confirmed this listen in the destination history.";
        }
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = userId,
            Category = "scrobble",
            Action = "delivery-repaired",
            Outcome = "success",
            CreatedAt = row.UpdatedAt,
            DetailsJson = JsonSerializer.Serialize(new { deliveryId, action, ambiguous, destinationAccountId = row.DestinationAccountId })
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }
}
