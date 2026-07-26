using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Services.Common;

namespace allstarr.Core.Playback;

public sealed record ScopedPlaybackTrack(string Title, string Artist, string? Album, long? DurationMilliseconds);
public interface IExactScopePlaybackScrobbleTarget
{
    string ProviderId { get; }
    Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken cancellationToken);
    Task DeliverAsync(IntelligenceScope scope, PlaybackTransition transition, ScopedPlaybackTrack track,
        long? positionTicks, DateTimeOffset observedAt, string signalKey, CancellationToken cancellationToken);
}

public sealed class ScopedPlaybackScrobbleDelivery(IDbContextFactory<AllstarrDbContext> factory,
    IEnumerable<IExactScopePlaybackScrobbleTarget> targets,
    IPlaybackDeliveryCheckpointStore checkpoints,
    IPlaybackTrackResolver? trackResolver = null,
    PlaybackDeliveryActivityStore? activity = null,
    ILogger<ScopedPlaybackScrobbleDelivery>? logger = null) : IScopedPlaybackScrobbleDelivery
{
    public async Task DeliverAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken)
    {
        var item = await (trackResolver ?? new PlaybackTrackResolver(factory)).ResolveAsync(payload, cancellationToken);
        if (item == null) return;
        var track = new ScopedPlaybackTrack(item.Title, item.Artist, item.Album, item.DurationMilliseconds);
        if (payload.Transition is PlaybackTransition.Progress or PlaybackTransition.Stop or PlaybackTransition.InferredStop &&
            !EligibleForCompletedScrobble(track.DurationMilliseconds, payload.PositionTicks)) return;
        var completion = payload.Transition is PlaybackTransition.Progress or PlaybackTransition.Stop or PlaybackTransition.InferredStop or PlaybackTransition.Submission;
        var checkpointKey = completion ? CompletedListenKey(payload) : payload.SignalKey;
        Exception? unauthorizedFailure = null;
        Exception? retryableFailure = null;
        var delivered = false;
        var completedTargets = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                if (!await target.IsConfiguredAsync(payload.Scope, cancellationToken)) continue;
                if (await checkpoints.IsCompletedAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId, checkpointKey, target.ProviderId, cancellationToken))
                {
                    delivered |= completion;
                    continue;
                }
                await target.DeliverAsync(payload.Scope, payload.Transition, track, payload.PositionTicks, payload.ObservedAt, checkpointKey, cancellationToken);
                await checkpoints.MarkCompletedAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId, checkpointKey, target.ProviderId, cancellationToken);
                delivered |= completion;
                if (completion) completedTargets.Add(target.ProviderId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                unauthorizedFailure ??= ex;
            }
            catch (Exception ex)
            {
                retryableFailure ??= ex;
            }
        }
        if (delivered)
        {
            activity?.MarkDelivered(payload.ItemId, payload.DeviceId);
        }
        if (completedTargets.Count > 0)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                var providerIds = completedTargets
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToArray();
                db.AuditEvents.Add(new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = payload.Scope.TenantId,
                    ActorUserId = payload.Scope.OwnerUserId,
                    Category = "scrobble",
                    Action = "delivered",
                    Outcome = "success",
                    CorrelationId = checkpointKey,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        providerIds,
                        providerCount = providerIds.Length,
                        track.Title,
                        track.Artist,
                        track.Album,
                        transition = payload.Transition.ToString(),
                        observedAt = payload.ObservedAt
                    }),
                    CreatedAt = now
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Completed scrobble delivery succeeded but its activity event could not be recorded");
            }
        }
        if (retryableFailure != null) ExceptionDispatchInfo.Capture(retryableFailure).Throw();
        if (unauthorizedFailure != null) ExceptionDispatchInfo.Capture(unauthorizedFailure).Throw();
    }

    private static string CompletedListenKey(PlaybackSignalPayload payload)
    {
        var inferredStart = payload.ObservedAt - TimeSpan.FromTicks(Math.Max(0, payload.PositionTicks ?? 0));
        var occurrence = payload.PlaySessionId ??
                         $"{payload.DeviceId}:{inferredStart.ToUnixTimeSeconds() / 30}";
        var identity = $"{payload.Scope.TenantId:N}|{payload.Scope.OwnerUserId:N}|{occurrence}|{payload.ItemId}|completed";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static bool EligibleForCompletedScrobble(long? durationMilliseconds, long? positionTicks)
    {
        if (!durationMilliseconds.HasValue) return false;
        var durationSeconds = durationMilliseconds.Value / 1000d;
        if (durationSeconds < 30 || positionTicks is null || positionTicks < 0) return false;
        var playedSeconds = positionTicks.Value / (double)TimeSpan.TicksPerSecond;
        return playedSeconds >= Math.Min(durationSeconds / 2d, 240d);
    }
}
