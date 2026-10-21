using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public interface IBoundPlaybackScrobbleTarget : IExactScopePlaybackScrobbleTarget
{
    Task<ProviderAccountContext?> FindDestinationAsync(IntelligenceScope scope, CancellationToken token);
    Task<ScopedPlaybackScrobbleResult> DeliverBoundAsync(IntelligenceScope scope, ProviderAccountContext account,
        PlaybackTransition transition, ScopedPlaybackTrack track, long? positionTicks, DateTimeOffset observedAt,
        string signalKey, CancellationToken token);
    Task<bool> CanReplayAsync(IntelligenceScope scope, ProviderAccountContext account, CancellationToken token) => Task.FromResult(false);
}

public enum PlaybackScrobbleVerification { Unavailable, Found, Absent }

public interface IVerifiablePlaybackScrobbleTarget : IBoundPlaybackScrobbleTarget
{
    Task<PlaybackScrobbleVerification> VerifyAsync(IntelligenceScope scope, ProviderAccountContext account,
        PlaybackScrobbleSubmission submission, CancellationToken token);
}

public sealed record PlaybackScrobbleSubmission(PlaybackTransition Transition, ScopedPlaybackTrack Track,
    long? PositionTicks, DateTimeOffset OccurredAt);

// Delivery state belongs to the playback checkpoint; the playback job owns execution and backoff.
public sealed class BoundPlaybackScrobbleDelivery(IDbContextFactory<AllstarrDbContext> factory,
    IPlaybackDeliveryCheckpointStore checkpoints)
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<ScopedPlaybackScrobbleResult?> DeliverAsync(IBoundPlaybackScrobbleTarget target,
        PlaybackSignalPayload payload, ScopedPlaybackTrack track, DateTimeOffset occurredAt, CancellationToken token)
    {
        var gate = Gate(payload.Scope.OwnerUserId, ScopedPlaybackScrobbleDelivery.CheckpointKey(payload), target.ProviderId);
        await gate.WaitAsync(token);
        try
        {
            try { return await DeliverCoreAsync(target, payload, track, occurredAt, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                await using var db = await factory.CreateDbContextAsync(token);
                var row = await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.OwnerUserId == payload.Scope.OwnerUserId && x.SignalKey == ScopedPlaybackScrobbleDelivery.CheckpointKey(payload) &&
                    x.TargetId == target.ProviderId, token);
                var uncertain = row != null && IsUncertain(row);
                var result = uncertain
                    ? ScopedPlaybackScrobbleResult.Permanent("ambiguous",
                        "Delivery could not be confirmed after its attempt began. Check the destination history before retrying.")
                    : ScopedPlaybackScrobbleResult.Retrying("transport-failure", "The saved destination could not be processed yet.");
                await checkpoints.RecordAsync(payload.Scope.OwnerUserId, payload.OccurrenceKey ?? payload.SignalKey,
                    ScopedPlaybackScrobbleDelivery.CheckpointKey(payload),
                    payload.Transition is PlaybackTransition.Start or PlaybackTransition.InferredStart
                        ? PlaybackScrobbleDeliveryKind.NowPlaying : PlaybackScrobbleDeliveryKind.Completed,
                    target.ProviderId, result, token);
                return result;
            }
        }
        finally { gate.Release(); }
    }

    internal static bool IsUncertain(PlaybackDeliveryCheckpointEntity row) =>
        !row.ReplaySafe && (row.AttemptStartedAt != null || row.ProviderCode is "ambiguous" or "verification-pending");

    internal static SemaphoreSlim Gate(Guid owner, string key, string target) =>
        Gates[(int)((uint)HashCode.Combine(owner, key, target) % Gates.Length)];

    private async Task<ScopedPlaybackScrobbleResult?> DeliverCoreAsync(IBoundPlaybackScrobbleTarget target,
        PlaybackSignalPayload payload, ScopedPlaybackTrack track, DateTimeOffset occurredAt, CancellationToken token)
    {
        var key = ScopedPlaybackScrobbleDelivery.CheckpointKey(payload);
        var occurrence = payload.OccurrenceKey ?? payload.SignalKey;
        var kind = payload.Transition is PlaybackTransition.Start or PlaybackTransition.InferredStart
            ? PlaybackScrobbleDeliveryKind.NowPlaying : PlaybackScrobbleDeliveryKind.Completed;
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.PlaybackDeliveryCheckpoints.SingleOrDefaultAsync(x => x.OwnerUserId == payload.Scope.OwnerUserId &&
            x.SignalKey == key && x.TargetId == target.ProviderId, token);
        if (row?.State is ScopedPlaybackScrobbleOutcome.Delivered or ScopedPlaybackScrobbleOutcome.Ignored)
            return new(row.State, row.ProviderCode, row.SafeMessage, row.DetailsJson);
        if (row?.State == ScopedPlaybackScrobbleOutcome.PermanentFailure &&
            (row.DestinationJson != null || row.ProviderCode == "destination-unbound") &&
            row.ProviderCode != "listen-incomplete" &&
            !(row.ProviderCode == "ambiguous" && target is IVerifiablePlaybackScrobbleTarget))
            return new(row.State, row.ProviderCode, row.SafeMessage, row.DetailsJson, RequiresReauthentication: row.RequiresReauthentication);
        if (row == null && payload.DestinationTargets == null && await target.FindDestinationAsync(payload.Scope, token) == null)
            return null;
        if (kind == PlaybackScrobbleDeliveryKind.Completed && (payload.DestinationTargets == null || row != null && row.DestinationJson == null))
        {
            row ??= new PlaybackDeliveryCheckpointEntity
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = payload.Scope.OwnerUserId,
                OccurrenceKey = occurrence,
                SignalKey = key,
                TargetId = target.ProviderId,
                Kind = kind
            };
            if (db.Entry(row).State == EntityState.Detached) db.Add(row);
            row.SubmissionJson ??= JsonSerializer.Serialize(new PlaybackScrobbleSubmission(payload.Transition, track, payload.PositionTicks, occurredAt));
            row.SignalPayloadJson ??= JsonSerializer.Serialize(payload with { DestinationTargets = [target.ProviderId] });
            row.State = ScopedPlaybackScrobbleOutcome.PermanentFailure;
            row.ProviderCode = "destination-unbound";
            row.SafeMessage = "This older listen has no saved destination or confirmed outcome. Choose the intended account and check its history before retrying.";
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.Revision++;
            await db.SaveChangesAsync(token);
            return ScopedPlaybackScrobbleResult.Permanent(row.ProviderCode, row.SafeMessage);
        }
        if (row?.DestinationJson == null)
        {
            var destination = await target.FindDestinationAsync(payload.Scope, token);
            if (destination == null) return null;
            row ??= New(payload, key, kind, target.ProviderId, destination);
            row.DestinationJson = JsonSerializer.Serialize(destination);
            row.DestinationAccountId = destination.AccountId;
            row.DestinationAccountRevision = destination.Revision;
            if (db.Entry(row).State == EntityState.Detached) db.Add(row);
        }
        var account = JsonSerializer.Deserialize<ProviderAccountContext>(row.DestinationJson!)!;
        row.SubmissionJson ??= JsonSerializer.Serialize(new PlaybackScrobbleSubmission(payload.Transition, track, payload.PositionTicks, occurredAt));
        row.SignalPayloadJson ??= JsonSerializer.Serialize(payload);
        var submission = JsonSerializer.Deserialize<PlaybackScrobbleSubmission>(row.SubmissionJson)!;
        var replay = kind == PlaybackScrobbleDeliveryKind.NowPlaying;
        if (!replay && IsUncertain(row))
        {
            var verification = await VerifyUncertainAsync(target, payload, account, submission, row, token);
            if (verification != null)
            {
                await checkpoints.RecordAsync(payload.Scope.OwnerUserId, occurrence, key, kind, target.ProviderId, verification, token);
                return verification;
            }
            // The single automatic resend is reserved durably before crossing the transport boundary.
            row.AutomaticResendCount++;
        }
        try
        {
            replay |= await target.CanReplayAsync(payload.Scope, account, token);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        {
            var unavailable = ScopedPlaybackScrobbleResult.Permanent("destination-changed",
                "Reconnect the saved destination account, then explicitly retry this listen with its current revision.", true);
            await db.SaveChangesAsync(token);
            await checkpoints.RecordAsync(payload.Scope.OwnerUserId, occurrence, key, kind, target.ProviderId, unavailable, token);
            return unavailable;
        }
        if (row.RetryAfter is { } next && next > DateTimeOffset.UtcNow)
            return ScopedPlaybackScrobbleResult.Retrying(row.ProviderCode, row.SafeMessage ?? "Delivery is waiting for its retry time.", next - DateTimeOffset.UtcNow);
        row.ReplaySafe = replay;
        row.AttemptStartedAt = DateTimeOffset.UtcNow;
        row.SendCount++;
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        ScopedPlaybackScrobbleResult result;
        try
        {
            result = await target.DeliverBoundAsync(payload.Scope, account, submission.Transition, submission.Track,
                submission.PositionTicks, submission.OccurredAt, key, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException)
        {
            result = ScopedPlaybackScrobbleResult.Permanent("destination-changed", "The saved destination account needs reauthentication or revision repair.", true);
        }
        catch (Exception)
        {
            result = replay
                ? ScopedPlaybackScrobbleResult.Retrying("transport-failure", "Delivery could not be confirmed and will retry with the original listen.")
                : ScopedPlaybackScrobbleResult.Permanent("ambiguous", "The destination may already have accepted this listen. Check its history before explicitly retrying.");
        }
        if (result.Outcome == ScopedPlaybackScrobbleOutcome.Retrying)
        {
            if (!replay && result.ProviderCode is not ("29" or "429" or "http-429" or "8" or "11" or "16"))
                result = ScopedPlaybackScrobbleResult.Permanent("ambiguous", "The destination did not confirm whether it accepted this listen. Check its history before explicitly retrying.");
            else
                result = result with { RetryAfter = result.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(21600, 30 * Math.Pow(2, Math.Min(row.SendCount - 1, 10)))) };
        }
        if (result.ProviderCode == "ambiguous" && target is IVerifiablePlaybackScrobbleTarget)
            result = await VerifyUncertainAsync(target, payload, account, submission, row, token)
                ?? ScopedPlaybackScrobbleResult.Retrying("verification-pending", "The listen will be checked again before any resend.", TimeSpan.FromMinutes(2));
        // Never turn a failed local acknowledgement save into a blind resend.
        await checkpoints.RecordAsync(payload.Scope.OwnerUserId, occurrence, key, kind, target.ProviderId, result, token);
        return result;
    }

    private static async Task<ScopedPlaybackScrobbleResult?> VerifyUncertainAsync(IBoundPlaybackScrobbleTarget target,
        PlaybackSignalPayload payload, ProviderAccountContext account, PlaybackScrobbleSubmission submission,
        PlaybackDeliveryCheckpointEntity row, CancellationToken token)
    {
        var ambiguous = ScopedPlaybackScrobbleResult.Permanent("ambiguous",
            "The destination may already have accepted this listen. Check its history before explicitly retrying.");
        if (target is not IVerifiablePlaybackScrobbleTarget verifier) return ambiguous;
        PlaybackScrobbleVerification verification;
        try { verification = await verifier.VerifyAsync(payload.Scope, account, submission, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return ambiguous; }
        if (verification == PlaybackScrobbleVerification.Found) return ScopedPlaybackScrobbleResult.Delivered();
        if (verification != PlaybackScrobbleVerification.Absent) return ambiguous;
        var settledAt = (row.AttemptStartedAt ?? row.UpdatedAt).AddMinutes(2);
        if (settledAt > DateTimeOffset.UtcNow)
            return ScopedPlaybackScrobbleResult.Retrying("verification-pending",
                "Waiting for Last.fm history to settle before checking this listen again.", settledAt - DateTimeOffset.UtcNow);
        return row.AutomaticResendCount == 0 ? null : ambiguous;
    }

    internal static PlaybackDeliveryCheckpointEntity New(PlaybackSignalPayload payload, string key,
        PlaybackScrobbleDeliveryKind kind, string targetId, ProviderAccountContext account) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = payload.Scope.OwnerUserId,
            OccurrenceKey = payload.OccurrenceKey ?? payload.SignalKey,
            SignalKey = key,
            TargetId = targetId,
            Kind = kind,
            State = ScopedPlaybackScrobbleOutcome.Retrying,
            ProviderCode = "pending",
            DestinationAccountId = account.AccountId,
            DestinationAccountRevision = account.Revision,
            DestinationJson = JsonSerializer.Serialize(account),
            SignalPayloadJson = JsonSerializer.Serialize(payload),
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
