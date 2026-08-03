using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Services.Common;

namespace allstarr.Core.Playback;

public enum PlaybackScrobbleDeliveryKind { NowPlaying, Completed }
public enum ScopedPlaybackScrobbleOutcome { Delivered, Ignored, Retrying, PermanentFailure }
public sealed record ScopedPlaybackScrobbleResult(
    ScopedPlaybackScrobbleOutcome Outcome,
    string? ProviderCode = null,
    string? SafeMessage = null,
    string DetailsJson = "{}",
    TimeSpan? RetryAfter = null,
    bool RequiresReauthentication = false)
{
    public static ScopedPlaybackScrobbleResult Delivered(string detailsJson = "{}") =>
        new(ScopedPlaybackScrobbleOutcome.Delivered, DetailsJson: detailsJson);
    public static ScopedPlaybackScrobbleResult Ignored(string? providerCode, string? safeMessage, string detailsJson) =>
        new(ScopedPlaybackScrobbleOutcome.Ignored, providerCode, safeMessage, detailsJson);
    public static ScopedPlaybackScrobbleResult Retrying(string? providerCode, string safeMessage,
        TimeSpan? retryAfter = null, string detailsJson = "{}") =>
        new(ScopedPlaybackScrobbleOutcome.Retrying, providerCode, safeMessage, detailsJson, retryAfter);
    public static ScopedPlaybackScrobbleResult Permanent(string? providerCode, string safeMessage,
        bool requiresReauthentication = false, string detailsJson = "{}") =>
        new(ScopedPlaybackScrobbleOutcome.PermanentFailure, providerCode, safeMessage, detailsJson,
            RequiresReauthentication: requiresReauthentication);
}
public sealed class ScopedPlaybackScrobbleDeliveryException(
    string code, string safeMessage, bool retryable, TimeSpan? retryAfter = null) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
public sealed record ScopedPlaybackTrack(string Title, string Artist, string? Album, long? DurationMilliseconds,
    string? AlbumArtist = null, string? RecordingMusicBrainzId = null, int? TrackNumber = null,
    bool ChosenByUser = true, string? ClientClass = null, string? DeviceClass = null);
public interface IExactScopePlaybackScrobbleTarget
{
    string ProviderId { get; }
    Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken cancellationToken);
    Task<ScopedPlaybackScrobbleResult> DeliverAsync(IntelligenceScope scope, PlaybackTransition transition, ScopedPlaybackTrack track,
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
        var timer = Stopwatch.StartNew();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var occurrenceKey = payload.OccurrenceKey ?? payload.SignalKey;
        var occurrence = await db.ListeningEvents.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == payload.Scope.TenantId && item.OwnerUserId == payload.Scope.OwnerUserId &&
            item.Protocol == payload.Scope.Protocol && item.BackendInstanceId == payload.Scope.BackendInstanceId &&
            item.LibraryScopeId == payload.Scope.LibraryScopeId &&
            item.OccurrenceKey == occurrenceKey, cancellationToken);
        var item = occurrence == null || string.IsNullOrWhiteSpace(occurrence.Title) || string.IsNullOrWhiteSpace(occurrence.Artist)
            ? await (trackResolver ?? new PlaybackTrackResolver(factory)).ResolveAsync(payload, cancellationToken)
            : null;
        var title = occurrence?.Title ?? item?.Title;
        var artist = occurrence?.Artist ?? item?.Artist;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return;
        var track = new ScopedPlaybackTrack(title, artist, occurrence?.Album ?? item?.Album,
            occurrence?.DurationMilliseconds ?? item?.DurationMilliseconds,
            occurrence?.AlbumArtist ?? item?.AlbumArtist,
            occurrence?.RecordingMusicBrainzId ?? item?.RecordingMusicBrainzId,
            occurrence?.TrackNumber ?? item?.TrackNumber,
            occurrence?.ChosenByUser ?? true,
            occurrence?.ClientClass ?? payload.ClientClass,
            occurrence?.DeviceClass ?? payload.DeviceClass);
        if (payload.Transition is PlaybackTransition.Progress or PlaybackTransition.Stop or PlaybackTransition.InferredStop &&
            !EligibleForCompletedScrobble(track.DurationMilliseconds, payload.PositionTicks)) return;
        var kind = payload.Transition is PlaybackTransition.Start or PlaybackTransition.InferredStart
            ? PlaybackScrobbleDeliveryKind.NowPlaying
            : PlaybackScrobbleDeliveryKind.Completed;
        var completion = kind == PlaybackScrobbleDeliveryKind.Completed;
        var checkpointKey = CheckpointKey(payload);
        var occurredAt = occurrence?.ListenedAt ?? occurrence?.StartedAt ?? payload.ObservedAt;
        ScopedPlaybackScrobbleDeliveryException? permanentFailure = null;
        ScopedPlaybackScrobbleDeliveryException? retryableFailure = null;
        var delivered = false;
        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deliveredCount = 0;
        var ignoredCount = 0;
        var retryingCount = 0;
        var failedCount = 0;
        var checkpointedCount = 0;
        foreach (var target in targets)
        {
            try
            {
                if (!await target.IsConfiguredAsync(payload.Scope, cancellationToken)) continue;
                providerIds.Add(target.ProviderId);
                if (await checkpoints.IsCompletedAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId, checkpointKey, target.ProviderId, cancellationToken))
                {
                    delivered |= completion;
                    checkpointedCount++;
                    continue;
                }
                var result = await target.DeliverAsync(payload.Scope, payload.Transition, track, payload.PositionTicks,
                    occurredAt, checkpointKey, cancellationToken);
                await checkpoints.RecordAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId,
                    occurrenceKey, checkpointKey, kind, target.ProviderId, result, cancellationToken);
                switch (result.Outcome)
                {
                    case ScopedPlaybackScrobbleOutcome.Delivered:
                        delivered |= completion;
                        deliveredCount++;
                        break;
                    case ScopedPlaybackScrobbleOutcome.Ignored:
                        delivered |= completion;
                        ignoredCount++;
                        break;
                    case ScopedPlaybackScrobbleOutcome.Retrying:
                        retryingCount++;
                        retryableFailure ??= new("playback_scrobble_retrying",
                            result.SafeMessage ?? "The scoped scrobble target asked Allstarr to retry.", true,
                            result.RetryAfter);
                        break;
                    case ScopedPlaybackScrobbleOutcome.PermanentFailure:
                        failedCount++;
                        permanentFailure ??= new(result.RequiresReauthentication
                                ? "playback_scrobble_unauthorized"
                                : "playback_scrobble_rejected",
                            result.SafeMessage ?? "The scoped scrobble target rejected this listen.", false);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                providerIds.Add(target.ProviderId);
                failedCount++;
                var result = ScopedPlaybackScrobbleResult.Permanent("unauthorized",
                    "Reconnect the selected scrobble account and replace its expired or revoked credentials.", true);
                await checkpoints.RecordAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId,
                    occurrenceKey, checkpointKey, kind, target.ProviderId, result, cancellationToken);
                permanentFailure ??= new("playback_scrobble_unauthorized", result.SafeMessage!, false);
            }
            catch (InvalidOperationException)
            {
                providerIds.Add(target.ProviderId);
                failedCount++;
                var result = ScopedPlaybackScrobbleResult.Permanent("account-incomplete",
                    "The selected scrobble account needs configuration.");
                await checkpoints.RecordAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId,
                    occurrenceKey, checkpointKey, kind, target.ProviderId, result, cancellationToken);
                permanentFailure ??= new("playback_scrobble_account_incomplete", result.SafeMessage!, false);
            }
            catch (Exception)
            {
                providerIds.Add(target.ProviderId);
                retryingCount++;
                var result = ScopedPlaybackScrobbleResult.Retrying("transport-failure",
                    "The scoped scrobble target could not be reached.");
                await checkpoints.RecordAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId,
                    occurrenceKey, checkpointKey, kind, target.ProviderId, result, cancellationToken);
                retryableFailure ??= new("playback_scrobble_retrying", result.SafeMessage!, true);
            }
        }
        if (delivered)
        {
            activity?.MarkDelivered(payload.ItemId, payload.DeviceId);
        }
        if (providerIds.Count > 0)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var recordedProviderIds = providerIds
                    .OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToArray();
                var providerNames = recordedProviderIds.Select(providerId => providerId.ToLowerInvariant() switch
                {
                    "lastfm" => "Last.fm",
                    "listenbrainz" => "ListenBrainz",
                    _ => providerId
                }).ToArray();
                var reasonCode = retryableFailure?.Code ?? permanentFailure?.Code ??
                    (ignoredCount > 0 && deliveredCount == 0
                        ? "playback_scrobble_ignored"
                        : checkpointedCount > 0 && deliveredCount == 0
                            ? "playback_scrobble_checkpointed"
                            : completion ? "playback_scrobble_delivered" : "playback_now_playing_delivered");
                var outcome = retryableFailure != null
                    ? "retrying"
                    : permanentFailure != null
                        ? deliveredCount + ignoredCount + checkpointedCount > 0 ? "partial-failure" : "failed"
                        : "success";
                var destination = string.Join(", ", providerNames);
                var message = outcome switch
                {
                    "success" => $"Allstarr sent the listening update to {destination}.",
                    "retrying" => $"Allstarr could not send the listening update to {destination} yet and will retry.",
                    "partial-failure" => $"Allstarr could not send the listening update to every selected service ({destination}).",
                    _ => $"Allstarr could not send the listening update to {destination}."
                };
                db.AuditEvents.Add(new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = payload.Scope.TenantId,
                    ActorUserId = payload.Scope.OwnerUserId,
                    Category = "scrobble",
                    Action = completion ? "delivered" : "now-playing",
                    Outcome = outcome,
                    CorrelationId = checkpointKey,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        providerIds = recordedProviderIds,
                        providerNames,
                        providerCount = providerIds.Count,
                        attemptedCount = deliveredCount + ignoredCount + retryingCount + failedCount,
                        deliveredCount,
                        ignoredCount,
                        retryingCount,
                        failedCount,
                        checkpointedCount,
                        durationMilliseconds = timer.ElapsedMilliseconds,
                        reasonCode,
                        transition = payload.Transition.ToString(),
                        message
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
        if (permanentFailure != null) ExceptionDispatchInfo.Capture(permanentFailure).Throw();
    }

    internal static string CheckpointKey(PlaybackSignalPayload payload) =>
        payload.Transition is PlaybackTransition.Start or PlaybackTransition.InferredStart
            ? payload.SignalKey
            : CompletedListenKey(payload);

    private static string CompletedListenKey(PlaybackSignalPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.OccurrenceKey))
            return PlaybackSignalPipeline.Hash($"{payload.OccurrenceKey}|completed");
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
