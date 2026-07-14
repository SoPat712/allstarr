using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;

namespace allstarr.Core.Playback;

public enum PlaybackTransition { Start, Progress, Stop, InferredStart, InferredStop }
public sealed record PlaybackSignalRequest(ProtocolExecutionContext ExecutionContext, PlaybackTransition Transition,
    string ItemId, string? DeviceId, string? PlaySessionId, long? PositionTicks, DateTimeOffset ObservedAt);
public sealed record PlaybackSignalPayload(IntelligenceScope Scope, PlaybackTransition Transition, string ItemId,
    string? DeviceId, string? PlaySessionId, long? PositionTicks, DateTimeOffset ObservedAt, string SignalKey);
public interface IPlaybackSignalPipeline { Task<bool> RecordAsync(PlaybackSignalRequest request, CancellationToken cancellationToken = default); }
public interface IScopedPlaybackScrobbleDelivery
{
    Task DeliverAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken);
}
public interface IPlaybackLyricsPrefetch
{
    Task PrefetchAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken);
}

public sealed class PlaybackSignalPipeline(DurableJobQueue jobs) : IPlaybackSignalPipeline
{
    public const string JobType = "playback.signal.process";
    public async Task<bool> RecordAsync(PlaybackSignalRequest request, CancellationToken cancellationToken = default)
    {
        var actor = request.ExecutionContext.RequireActor(); var owner = actor.EffectiveUserId ?? throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(request.ItemId) || request.ItemId.Length > 500 || request.ObservedAt == default) throw new ArgumentException("Playback signal is invalid.");
        var scope = new IntelligenceScope(actor.TenantId, owner, request.ExecutionContext.Protocol.ToString().ToLowerInvariant(),
            request.ExecutionContext.BackendInstanceId, request.ExecutionContext.LibraryScopeId ?? "default");
        var bucket = request.Transition == PlaybackTransition.Progress ? (request.PositionTicks ?? 0) / TimeSpan.TicksPerSecond / 10 : 0;
        var identity = $"{scope.TenantId:N}|{scope.OwnerUserId:N}|{scope.Protocol}|{scope.BackendInstanceId}|{scope.LibraryScopeId}|{request.DeviceId}|{request.PlaySessionId}|{request.ItemId}|{request.Transition}|{bucket}";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var normalizedTicks = request.Transition == PlaybackTransition.Progress ? TimeSpan.FromSeconds(bucket * 10).Ticks : request.PositionTicks;
        var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaybackSignalPayload>(JobType, key,
            new(scope, request.Transition, request.ItemId, request.DeviceId, request.PlaySessionId, normalizedTicks, request.ObservedAt, key),
            scope.TenantId, scope.OwnerUserId, LibraryScopeId: scope.LibraryScopeId,
            CorrelationId: request.ExecutionContext.CorrelationId), cancellationToken);
        return result.Created;
    }
}

public sealed class PlaybackSignalJobHandler(IRecommendationSignalWriter signals,
    IScopedPlaybackScrobbleDelivery scrobbles, IPlaybackLyricsPrefetch lyrics) : IDurableJobHandler
{
    public string JobType => PlaybackSignalPipeline.JobType;
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken cancellationToken)
    {
        var payload = execution.Claim.Payload.Deserialize<PlaybackSignalPayload>();
        if (payload == null || execution.Claim.TenantId != payload.Scope.TenantId || execution.Claim.OwnerUserId != payload.Scope.OwnerUserId ||
            execution.Claim.LibraryScopeId != payload.Scope.LibraryScopeId)
            return DurableJobCompletion.Failure("playback_signal_scope_invalid", "The playback signal scope is invalid.");
        try
        {
            if (payload.Transition is PlaybackTransition.Start or PlaybackTransition.InferredStart)
                await WriteSignalAsync(payload, execution.Claim.JobId, "play", cancellationToken);
            else if (payload.Transition is PlaybackTransition.Stop or PlaybackTransition.InferredStop)
                await WriteSignalAsync(payload, execution.Claim.JobId, "complete", cancellationToken);
            if (payload.Transition is PlaybackTransition.Start or PlaybackTransition.InferredStart) await lyrics.PrefetchAsync(payload, cancellationToken);
            await scrobbles.DeliverAsync(payload, cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return DurableJobCompletion.Cancelled(); }
        catch (UnauthorizedAccessException) { return DurableJobCompletion.Failure("playback_scrobble_unauthorized", "The scoped scrobble account is unauthorized."); }
        catch { return DurableJobCompletion.Retry("playback_signal_temporary_failure", "Playback side effects will retry."); }
    }

    private Task<bool> WriteSignalAsync(PlaybackSignalPayload payload, Guid jobId, string type, CancellationToken token) =>
        signals is IIdempotentRecommendationSignalWriter idempotent
            ? idempotent.WriteIdempotentAsync(payload.Scope, type, payload.ItemId, 1, payload.ObservedAt, payload.SignalKey, jobId, token)
            : signals.WriteAsync(payload.Scope, type, payload.ItemId, 1, payload.ObservedAt, token);
}
