using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed class PlaybackScrobbleRepair(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    IPlaybackTrackResolver? tracks = null)
{
    internal static readonly TimeSpan ReservationLifetime = TimeSpan.FromHours(1);
    internal static readonly TimeSpan MetadataRecoveryLifetime = TimeSpan.FromDays(7);
    internal const string MissingListenReason = "This delivery has no saved completed listen, so it cannot be resent or confirmed. You can dismiss it.";
    private Guid? recoveryBefore;

    public async Task ReconcileStartupAsync(CancellationToken token)
    {
        await using (var db = await factory.CreateDbContextAsync(token))
            await db.PlaybackDeliveryCheckpoints.Where(x => x.Kind == PlaybackScrobbleDeliveryKind.NowPlaying &&
                    x.State == ScopedPlaybackScrobbleOutcome.PermanentFailure)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, ScopedPlaybackScrobbleOutcome.Ignored)
                    .SetProperty(x => x.ProviderCode, "now-playing-expired")
                    .SetProperty(x => x.SafeMessage, "The now-playing notice expired. Completed listens are sent separately.")
                    .SetProperty(x => x.RetryAfter, (DateTimeOffset?)null)
                    .SetProperty(x => x.AttemptStartedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.RequiresReauthentication, false)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.Revision, x => x.Revision + 1), token);
        do { await RecoverPendingAsync(token); }
        while (recoveryBefore != null);
    }

    public async Task RecoverPendingAsync(CancellationToken token, Guid? owner = null, string? occurrence = null)
    {
        await using var lookup = await factory.CreateDbContextAsync(token);
        var query = lookup.PlaybackDeliveryCheckpoints.AsNoTracking();
        if (owner != null) query = query.Where(x => x.OwnerUserId == owner);
        if (occurrence != null) query = query.Where(x => x.OccurrenceKey == occurrence);
        // Page the primary key before filtering state: completed history must not cause an unbounded scan.
        var page = query.OrderByDescending(x => x.Id).Select(x => new
        {
            x.Id,
            x.OwnerUserId,
            x.SignalKey,
            x.TargetId,
            Recoverable = x.State == ScopedPlaybackScrobbleOutcome.Retrying && (x.DestinationJson != null || x.Kind == PlaybackScrobbleDeliveryKind.NowPlaying ||
                x.Kind == PlaybackScrobbleDeliveryKind.Completed && x.SendCount == 0 && x.AttemptStartedAt == null &&
                x.SubmissionJson == null && x.SignalPayloadJson == null)
        });
        var recent = await page.Take(100).ToArrayAsync(token);
        var history = owner == null && occurrence == null && recoveryBefore is { } before
            ? await page.Where(x => x.Id.CompareTo(before) < 0).Take(100).ToArrayAsync(token) : recent;
        if (owner == null && occurrence == null) recoveryBefore = history.Length == 100 ? history[^1].Id : null;
        foreach (var candidate in recent.Concat(history).DistinctBy(x => x.Id).Where(x => x.Recoverable))
        {
            var gate = BoundPlaybackScrobbleDelivery.Gate(candidate.OwnerUserId, candidate.SignalKey, candidate.TargetId);
            await gate.WaitAsync(token);
            try { await RecoverPendingCoreAsync(candidate.Id, token); }
            finally { gate.Release(); }
        }
    }

    private async Task RecoverPendingCoreAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Id == id, token);
        if (row.State != ScopedPlaybackScrobbleOutcome.Retrying) return;
        var savedPayload = row.SignalPayloadJson == null ? null : JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson);
        if (await IgnoreSyntheticAsync(db, row, savedPayload, token))
        {
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return;
        }
        if (row.Kind == PlaybackScrobbleDeliveryKind.Completed && row.ProviderCode == "pending" &&
            row.SendCount == 0 && row.AttemptStartedAt == null && row.SubmissionJson == null && row.SignalPayloadJson == null &&
            row.UpdatedAt < DateTimeOffset.UtcNow - ReservationLifetime)
        {
            row.State = ScopedPlaybackScrobbleOutcome.Ignored;
            row.ProviderCode = "listen-not-completed";
            row.SafeMessage = null;
            row.Revision++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return;
        }
        var active = await db.Jobs.AsNoTracking().Where(x => x.Type == PlaybackSignalPipeline.JobType &&
            x.ScopeKey == DurableJobQueue.CreateUserScopeKey(row.OwnerUserId) && x.OwnerUserId == row.OwnerUserId &&
            (x.State == DurableJobState.Pending || x.State == DurableJobState.Running ||
                x.State == DurableJobState.RetryScheduled)).Select(x => x.PayloadJson).ToArrayAsync(token);
        if (active.Select(x => JsonSerializer.Deserialize<PlaybackSignalPayload>(x)).Any(x => x != null &&
                (x.DestinationTargets == null || x.DestinationTargets.Contains(row.TargetId)) &&
                ScopedPlaybackScrobbleDelivery.CheckpointKey(x) == row.SignalKey)) return;
        if (row.Kind == PlaybackScrobbleDeliveryKind.NowPlaying)
        {
            row.State = ScopedPlaybackScrobbleOutcome.Ignored;
            row.ProviderCode = "now-playing-expired";
            row.SafeMessage = "The now-playing notice expired. Completed listens are sent separately.";
            row.RetryAfter = null;
            row.AttemptStartedAt = null;
            row.RequiresReauthentication = false;
            row.Revision++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return;
        }
        var restored = await RestorePayloadAsync(db, row, token);
        var payload = restored.Payload;
        if (await IgnoreSyntheticAsync(db, row, payload, token))
        {
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return;
        }
        var enabled = payload != null && await db.Users.AnyAsync(x => x.Id == row.OwnerUserId && x.Enabled &&
            x.BackendType == payload.Scope.Protocol && x.BackendInstanceId == payload.Scope.BackendInstanceId, token);
        if (BoundPlaybackScrobbleDelivery.IsUncertain(row))
        {
            row.State = ScopedPlaybackScrobbleOutcome.PermanentFailure;
            row.ProviderCode = "ambiguous";
            row.SafeMessage = "Delivery was interrupted after its send attempt began. Check the destination history before resolving it.";
        }
        else if (payload == null && restored.MetadataUnavailable)
        {
            var observedAt = row.SignalPayloadJson == null ? row.UpdatedAt :
                JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson)?.ObservedAt ?? row.UpdatedAt;
            var expired = DateTimeOffset.UtcNow - observedAt >= MetadataRecoveryLifetime;
            row.State = expired ? ScopedPlaybackScrobbleOutcome.PermanentFailure : ScopedPlaybackScrobbleOutcome.Retrying;
            row.ProviderCode = expired ? "song-unidentified" : "metadata-unavailable";
            row.SafeMessage = expired ? "We couldn't identify this song after seven days. This listen cannot be sent. You can dismiss it."
                : "Track details are temporarily unavailable. The saved listen will be checked again for up to seven days.";
            row.RetryAfter = null;
        }
        else if (payload == null)
        {
            var reservation = row.Kind == PlaybackScrobbleDeliveryKind.Completed && row.SendCount == 0 &&
                row.AttemptStartedAt == null && row.SubmissionJson == null;
            var ended = row.UpdatedAt < DateTimeOffset.UtcNow - ReservationLifetime || row.SignalPayloadJson != null &&
                JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson)?.Transition is PlaybackTransition.Stop or PlaybackTransition.InferredStop;
            row.State = reservation ? ScopedPlaybackScrobbleOutcome.Ignored : ScopedPlaybackScrobbleOutcome.PermanentFailure;
            row.ProviderCode = reservation ? ended ? "listen-not-completed" : "listen-incomplete" : "payload-unavailable";
            row.SafeMessage = reservation ? null : "No completed playback observation is available for this delivery.";
        }
        else if (!enabled || row.SendCount > 0 || row.ProviderCode == "recovery-queued")
        {
            row.State = ScopedPlaybackScrobbleOutcome.PermanentFailure;
            row.ProviderCode = "delivery-not-attempted";
            row.SafeMessage = "The delivery job ended without completing this destination. Check the saved account and explicitly retry this listen.";
        }
        else
        {
            row.ProviderCode = "recovery-queued";
            row.SafeMessage = "The unsent listen was queued again for its saved destination.";
            row.RetryAfter = null;
            row.SignalPayloadJson = JsonSerializer.Serialize(payload with { DestinationTargets = [row.TargetId] });
            await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<PlaybackSignalPayload>(
                PlaybackSignalPipeline.JobType, $"scrobble-recover:{row.Id:N}:{row.Revision + 1}",
                payload with { DestinationTargets = [row.TargetId] }, row.OwnerUserId), token);
        }
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private sealed record RestoredPlayback(PlaybackSignalPayload? Payload, bool MetadataUnavailable = false);

    private static async Task<bool> IgnoreSyntheticAsync(AllstarrDbContext db, PlaybackDeliveryCheckpointEntity row,
        PlaybackSignalPayload? payload, CancellationToken token)
    {
        if (payload == null || payload.Scope.OwnerUserId != row.OwnerUserId ||
            !await SyntheticActivityPolicy.IsExcludedAsync(db, payload.Scope, payload.ItemId, token)) return false;
        row.State = ScopedPlaybackScrobbleOutcome.Ignored;
        row.ProviderCode = "synthetic-listener";
        row.SafeMessage = null;
        row.RetryAfter = null;
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private async Task<RestoredPlayback> RestorePayloadAsync(AllstarrDbContext db,
        PlaybackDeliveryCheckpointEntity row, CancellationToken token)
    {
        var metadataUnavailable = false;
        if (row.SignalPayloadJson != null)
        {
            var saved = JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson);
            if (saved == null || saved.Scope.OwnerUserId != row.OwnerUserId ||
                ScopedPlaybackScrobbleDelivery.CheckpointKey(saved) != row.SignalKey) return new(null, metadataUnavailable);
            if (await SyntheticActivityPolicy.IsExcludedAsync(db, saved.Scope, saved.ItemId, token)) return new(saved);
            if (row.Kind == PlaybackScrobbleDeliveryKind.NowPlaying || row.SubmissionJson != null) return new(saved);
            var metadata = saved.SubmittedTrack;
            if ((metadata?.DurationMilliseconds is not > 0 || string.IsNullOrWhiteSpace(metadata?.Title) || string.IsNullOrWhiteSpace(metadata?.Artist)) && tracks != null)
            {
                try { metadata = await tracks.ResolveAsync(saved, token) ?? metadata; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { }
            }
            metadataUnavailable |= saved.Transition != PlaybackTransition.Submission && metadata?.DurationMilliseconds is not > 0 ||
                string.IsNullOrWhiteSpace(metadata?.Title) || string.IsNullOrWhiteSpace(metadata?.Artist);
            if (PlaybackSignalJobHandler.Classify(saved, metadata) == ListeningEventState.Completed && metadata != null &&
                !string.IsNullOrWhiteSpace(metadata.Title) && !string.IsNullOrWhiteSpace(metadata.Artist))
            {
                saved = saved with { SubmittedTrack = metadata };
                row.SignalPayloadJson = JsonSerializer.Serialize(saved);
                var occurredAt = saved.ObservedAt;
                if (saved.Transition != PlaybackTransition.Submission && saved.PositionTicks is > 0)
                    occurredAt -= TimeSpan.FromTicks(saved.PositionTicks.Value);
                row.SubmissionJson ??= JsonSerializer.Serialize(new PlaybackScrobbleSubmission(saved.Transition,
                    new(metadata.Title, metadata.Artist, metadata.Album, metadata.DurationMilliseconds,
                        metadata.AlbumArtist, metadata.RecordingMusicBrainzId, metadata.TrackNumber,
                        ClientClass: saved.ClientClass, DeviceClass: saved.DeviceClass), saved.PositionTicks, occurredAt));
                return new(saved);
            }
        }
        if (row.DestinationJson == null || row.Kind != PlaybackScrobbleDeliveryKind.Completed) return new(null, metadataUnavailable);
        var listen = await db.ListeningEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerUserId == row.OwnerUserId &&
            x.OccurrenceKey == row.OccurrenceKey && x.State == ListeningEventState.Completed, token);
        if (listen == null || string.IsNullOrWhiteSpace(listen.Title) || string.IsNullOrWhiteSpace(listen.Artist) ||
            listen.ListenedAt == null)
        {
            var savedJobs = await db.Jobs.AsNoTracking().Where(x => x.Type == PlaybackSignalPipeline.JobType &&
                x.ScopeKey == DurableJobQueue.CreateUserScopeKey(row.OwnerUserId) && x.OwnerUserId == row.OwnerUserId &&
                x.PayloadJson.Contains(row.OccurrenceKey ?? row.SignalKey))
                .OrderByDescending(x => x.Id).Select(x => x.PayloadJson).ToArrayAsync(token);
            foreach (var json in savedJobs)
            {
                var saved = JsonSerializer.Deserialize<PlaybackSignalPayload>(json);
                if (saved == null || saved.Scope.OwnerUserId != row.OwnerUserId || !saved.RelayExternally ||
                    saved.DestinationTargets?.Contains(row.TargetId) != true ||
                    ScopedPlaybackScrobbleDelivery.CheckpointKey(saved) != row.SignalKey) continue;
                row.SignalPayloadJson ??= JsonSerializer.Serialize(saved);
                if (await SyntheticActivityPolicy.IsExcludedAsync(db, saved.Scope, saved.ItemId, token))
                    return new(saved);
                var metadata = saved.SubmittedTrack;
                if ((metadata?.DurationMilliseconds is not > 0 || string.IsNullOrWhiteSpace(metadata?.Title) || string.IsNullOrWhiteSpace(metadata?.Artist)) && tracks != null)
                {
                    try { metadata = await tracks.ResolveAsync(saved, token) ?? metadata; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { }
                }
                metadataUnavailable |= saved.Transition != PlaybackTransition.Submission && metadata?.DurationMilliseconds is not > 0 ||
                    string.IsNullOrWhiteSpace(metadata?.Title) || string.IsNullOrWhiteSpace(metadata?.Artist);
                if (PlaybackSignalJobHandler.Classify(saved, metadata) != ListeningEventState.Completed ||
                    metadata == null || string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Artist)) continue;
                var occurredAt = saved.ObservedAt;
                if (saved.Transition != PlaybackTransition.Submission && saved.PositionTicks is > 0)
                    occurredAt -= TimeSpan.FromTicks(saved.PositionTicks.Value);
                row.SubmissionJson ??= JsonSerializer.Serialize(new PlaybackScrobbleSubmission(saved.Transition,
                    new(metadata.Title, metadata.Artist, metadata.Album, metadata.DurationMilliseconds,
                        metadata.AlbumArtist, metadata.RecordingMusicBrainzId, metadata.TrackNumber,
                        ClientClass: saved.ClientClass, DeviceClass: saved.DeviceClass), saved.PositionTicks, occurredAt));
                row.SignalPayloadJson = JsonSerializer.Serialize(saved with { SubmittedTrack = metadata, DestinationTargets = [row.TargetId] });
                return new(saved);
            }
            return new(null, metadataUnavailable);
        }
        var track = new ScopedPlaybackTrack(listen.Title, listen.Artist, listen.Album, listen.DurationMilliseconds,
            listen.AlbumArtist, listen.RecordingMusicBrainzId, listen.TrackNumber, listen.ChosenByUser,
            listen.ClientClass, listen.DeviceClass);
        var payload = new PlaybackSignalPayload(new(row.OwnerUserId, listen.Protocol, listen.BackendInstanceId),
            PlaybackTransition.Submission, listen.TrackReference, null, null, listen.PositionTicks, listen.ListenedAt.Value,
            row.SignalKey, listen.OccurrenceKey, listen.ClientClass, listen.DeviceClass,
            new(listen.LibraryTrackId, listen.CanonicalRecordingId, listen.TrackReference, listen.Title, listen.Artist,
                listen.Album, listen.DurationMilliseconds, listen.AlbumArtist, listen.RecordingMusicBrainzId, listen.TrackNumber),
            SourceKind: listen.SourceKind, DestinationTargets: [row.TargetId]);
        if (ScopedPlaybackScrobbleDelivery.CheckpointKey(payload) != row.SignalKey) return new(null, metadataUnavailable);
        row.SignalPayloadJson = JsonSerializer.Serialize(payload);
        row.SubmissionJson ??= JsonSerializer.Serialize(new PlaybackScrobbleSubmission(PlaybackTransition.Submission,
            track, listen.PositionTicks, listen.ListenedAt.Value));
        return new(payload);
    }

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

    internal static bool HasSavedCompletedListen(PlaybackDeliveryCheckpointEntity row)
    {
        if (row.Kind != PlaybackScrobbleDeliveryKind.Completed || row.SignalPayloadJson == null || row.SubmissionJson == null) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson);
            var submission = JsonSerializer.Deserialize<PlaybackScrobbleSubmission>(row.SubmissionJson);
            return payload != null && payload.Scope.OwnerUserId == row.OwnerUserId &&
                payload.Transition is not (PlaybackTransition.Start or PlaybackTransition.InferredStart) &&
                ScopedPlaybackScrobbleDelivery.CheckpointKey(payload) == row.SignalKey && submission != null &&
                !string.IsNullOrWhiteSpace(submission.Track.Title) && !string.IsNullOrWhiteSpace(submission.Track.Artist) &&
                (submission.Transition == PlaybackTransition.Submission ||
                    submission.Transition is PlaybackTransition.Progress or PlaybackTransition.Stop or PlaybackTransition.InferredStop &&
                    ScopedPlaybackScrobbleDelivery.EligibleForCompletedScrobble(submission.Track.DurationMilliseconds, submission.PositionTicks));
        }
        catch (JsonException) { return false; }
    }

    internal static bool CanConfirm(PlaybackDeliveryCheckpointEntity row) =>
        row.ProviderCode is "ambiguous" or "verification-pending" or "destination-unbound" || BoundPlaybackScrobbleDelivery.IsUncertain(row);

    private async Task<bool> ResolveCoreAsync(Guid userId, Guid deliveryId, long revision, string action,
        long? accountRevision, CancellationToken token, Guid? destinationAccountId)
    {
        if (action is not ("retry" or "confirm-delivered" or "dismiss")) throw new ArgumentException("Choose retry, confirm-delivered or dismiss.");
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var row = await db.PlaybackDeliveryCheckpoints.SingleOrDefaultAsync(x => x.Id == deliveryId && x.OwnerUserId == userId, token);
        if (row == null) return false;
        if (row.Revision != revision || row.State is ScopedPlaybackScrobbleOutcome.Delivered or ScopedPlaybackScrobbleOutcome.Ignored)
            throw new InvalidOperationException("Refresh the delivery before resolving it.");
        if (row.Kind != PlaybackScrobbleDeliveryKind.Completed)
            throw new InvalidOperationException("Now-playing notices expire and cannot be resent or confirmed.");
        if (action == "dismiss")
        {
            if (row.State != ScopedPlaybackScrobbleOutcome.PermanentFailure || HasSavedCompletedListen(row))
                throw new InvalidOperationException("Only a delivery without a saved completed listen can be dismissed.");
            row.State = ScopedPlaybackScrobbleOutcome.Ignored;
            row.ProviderCode = "dismissed";
            row.SafeMessage = "The listener dismissed this delivery because no saved completed listen was available.";
            row.RetryAfter = null;
            row.AttemptStartedAt = null;
            row.RequiresReauthentication = false;
            row.Revision++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return true;
        }
        var savedPayload = row.SignalPayloadJson == null ? null : JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson);
        if (savedPayload != null && savedPayload.Scope.OwnerUserId == userId &&
            await SyntheticActivityPolicy.IsExcludedAsync(db, savedPayload.Scope, savedPayload.ItemId, token))
            throw new InvalidOperationException("Synthetic listener activity cannot be delivered.");
        var payload = (await RestorePayloadAsync(db, row, token)).Payload
            ?? throw new InvalidOperationException(MissingListenReason);
        if (!HasSavedCompletedListen(row)) throw new InvalidOperationException(MissingListenReason);
        if (await SyntheticActivityPolicy.IsExcludedAsync(db, payload.Scope, payload.ItemId, token))
            throw new InvalidOperationException("Synthetic listener activity cannot be delivered.");
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.Enabled && x.BackendType == payload.Scope.Protocol &&
                x.BackendInstanceId == payload.Scope.BackendInstanceId, token)) throw new UnauthorizedAccessException();
        var running = await db.Jobs.AsNoTracking().Where(x => x.OwnerUserId == userId &&
            x.Type == PlaybackSignalPipeline.JobType && x.State == DurableJobState.Running).Select(x => x.PayloadJson).ToArrayAsync(token);
        if (running.Select(x => JsonSerializer.Deserialize<PlaybackSignalPayload>(x)).Any(x => x != null &&
                ScopedPlaybackScrobbleDelivery.CheckpointKey(x) == row.SignalKey))
            throw new InvalidOperationException("This listen is still being processed. Wait for it to finish before resolving it.");
        var ambiguous = CanConfirm(row);
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
