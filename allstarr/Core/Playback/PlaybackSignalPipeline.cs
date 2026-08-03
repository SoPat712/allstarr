using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public enum PlaybackTransition { Start, Progress, Stop, InferredStart, InferredStop, Submission }
public sealed record PlaybackSignalRequest(ProtocolExecutionContext ExecutionContext, PlaybackTransition Transition,
    string ItemId, string? DeviceId, string? PlaySessionId, long? PositionTicks, DateTimeOffset ObservedAt);
public sealed record PlaybackSignalPayload(IntelligenceScope Scope, PlaybackTransition Transition, string ItemId,
    string? DeviceId, string? PlaySessionId, long? PositionTicks, DateTimeOffset ObservedAt, string SignalKey,
    string? OccurrenceKey = null, string? ClientClass = null, string? DeviceClass = null);
public interface IPlaybackSignalPipeline { Task<bool> RecordAsync(PlaybackSignalRequest request, CancellationToken cancellationToken = default); }
public interface IScopedPlaybackScrobbleDelivery
{
    Task DeliverAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken);
}
public interface IPlaybackLyricsPrefetch
{
    Task PrefetchAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken);
}

public sealed class PlaybackSignalPipeline(
    DurableJobQueue jobs,
    IProtocolLibraryScopeResolver? libraryScopes = null) : IPlaybackSignalPipeline
{
    public const string JobType = "playback.signal.process";
    public async Task<bool> RecordAsync(PlaybackSignalRequest request, CancellationToken cancellationToken = default)
    {
        var execution = request.ExecutionContext;
        if (string.IsNullOrWhiteSpace(execution.LibraryScopeId) && libraryScopes != null)
            execution = await libraryScopes.ResolveAsync(execution, request.ItemId, cancellationToken);
        var actor = execution.RequireActor(); var owner = actor.EffectiveUserId ?? throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(execution.LibraryScopeId))
            throw new InvalidOperationException("Playback work requires an exact library scope.");
        if (string.IsNullOrWhiteSpace(request.ItemId) || request.ItemId.Length > 500 || request.ObservedAt == default ||
            request.DeviceId?.Length > 200 || request.PlaySessionId?.Length > 500 || request.PositionTicks is < 0)
            throw new ArgumentException("Playback signal is invalid.");
        var scope = new IntelligenceScope(actor.TenantId, owner, execution.Protocol.ToString().ToLowerInvariant(),
            execution.BackendInstanceId, execution.LibraryScopeId);
        var bucket = request.Transition == PlaybackTransition.Progress ? (request.PositionTicks ?? 0) / TimeSpan.TicksPerSecond / 10 : 0;
        var deviceId = request.DeviceId ?? execution.Client.DeviceId;
        var occurrenceKey = CreateOccurrenceKey(scope, request.ItemId, deviceId, request.PlaySessionId,
            request.PositionTicks, request.ObservedAt);
        var key = Hash($"{occurrenceKey}|{request.Transition}|{bucket}");
        var normalizedTicks = request.Transition == PlaybackTransition.Progress ? TimeSpan.FromSeconds(bucket * 10).Ticks : request.PositionTicks;
        var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaybackSignalPayload>(JobType, key,
            new(scope, request.Transition, request.ItemId, deviceId, request.PlaySessionId, normalizedTicks,
                request.ObservedAt, key, occurrenceKey, execution.Client.ClientId, execution.Client.DeviceName),
            scope.TenantId, scope.OwnerUserId, LibraryScopeId: scope.LibraryScopeId,
            CorrelationId: execution.CorrelationId), cancellationToken);
        return result.Created;
    }

    internal static string CreateOccurrenceKey(IntelligenceScope scope, string itemId, string? deviceId,
        string? playSessionId, long? positionTicks, DateTimeOffset observedAt)
    {
        var inferredStart = observedAt;
        if (positionTicks is > 0)
        {
            try { inferredStart -= TimeSpan.FromTicks(positionTicks.Value); }
            catch (ArgumentOutOfRangeException) { }
        }
        var occurrence = !string.IsNullOrWhiteSpace(playSessionId)
            ? $"session:{playSessionId}"
            : $"inferred:{inferredStart.ToUnixTimeSeconds() / 30}";
        return Hash($"{scope.TenantId:N}|{scope.OwnerUserId:N}|{scope.Protocol}|{scope.BackendInstanceId}|{scope.LibraryScopeId}|{deviceId}|{itemId}|{occurrence}");
    }

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class PlaybackSignalJobHandler(IRecommendationSignalWriter signals,
    IScopedPlaybackScrobbleDelivery scrobbles, IPlaybackLyricsPrefetch lyrics,
    IDbContextFactory<AllstarrDbContext> factory, IPlaybackTrackResolver? tracks = null,
    MusicBrainzListeningEnrichmentQueue? musicBrainz = null) : IDurableJobHandler
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
            var track = tracks == null ? null : await tracks.ResolveAsync(payload, cancellationToken);
            var state = await RecordOccurrenceAsync(
                payload, track, musicBrainz?.Enabled == true, cancellationToken);
            var signalType = SignalType(payload.Transition, state);
            if (signalType != null)
                await WriteSignalAsync(payload, execution.Claim.JobId, signalType, cancellationToken);
            if (payload.Transition is PlaybackTransition.Start or PlaybackTransition.InferredStart) await lyrics.PrefetchAsync(payload, cancellationToken);
            if (musicBrainz != null)
                await musicBrainz.EnqueueAsync(
                    payload.Scope,
                    payload.OccurrenceKey ?? payload.SignalKey,
                    execution.Claim.CorrelationId,
                    cancellationToken);
            await scrobbles.DeliverAsync(payload, cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return DurableJobCompletion.Cancelled(); }
        catch (ScopedPlaybackScrobbleDeliveryException ex) when (ex.Retryable)
        { return DurableJobCompletion.Retry(ex.Code, ex.Message, ex.RetryAfter); }
        catch (ScopedPlaybackScrobbleDeliveryException ex)
        { return DurableJobCompletion.Failure(ex.Code, ex.Message); }
        catch (UnauthorizedAccessException) { return DurableJobCompletion.Failure("playback_scrobble_unauthorized", "The scoped scrobble account is unauthorized."); }
        catch { return DurableJobCompletion.Retry("playback_signal_temporary_failure", "Playback side effects will retry."); }
    }

    private Task<bool> WriteSignalAsync(PlaybackSignalPayload payload, Guid jobId, string type, CancellationToken token) =>
        signals is IIdempotentRecommendationSignalWriter idempotent
            ? idempotent.WriteIdempotentAsync(payload.Scope, type, payload.ItemId, 1, payload.ObservedAt,
                PlaybackSignalPipeline.Hash($"{payload.OccurrenceKey ?? payload.SignalKey}|{type}"), jobId, token)
            : signals.WriteAsync(payload.Scope, type, payload.ItemId, 1, payload.ObservedAt, token);

    private static string? SignalType(PlaybackTransition transition, ListeningEventState state) => transition switch
    {
        PlaybackTransition.Start or PlaybackTransition.InferredStart => "play",
        PlaybackTransition.Submission => "complete",
        PlaybackTransition.Progress when state == ListeningEventState.Completed => "complete",
        PlaybackTransition.Stop or PlaybackTransition.InferredStop when state == ListeningEventState.Completed => "complete",
        PlaybackTransition.Stop or PlaybackTransition.InferredStop when state == ListeningEventState.Skipped => "skip",
        _ => null
    };

    private async Task<ListeningEventState> RecordOccurrenceAsync(PlaybackSignalPayload payload,
        PlaybackTrackSnapshot? track, bool enrichWithMusicBrainz, CancellationToken cancellationToken)
    {
        var occurrenceKey = payload.OccurrenceKey ?? payload.SignalKey;
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var record = await db.ListeningEvents.SingleOrDefaultAsync(item =>
                item.TenantId == payload.Scope.TenantId &&
                item.OwnerUserId == payload.Scope.OwnerUserId &&
                item.OccurrenceKey == occurrenceKey, cancellationToken);
            var added = record == null;
            record ??= new ListeningEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = payload.Scope.TenantId,
                OwnerUserId = payload.Scope.OwnerUserId,
                Protocol = payload.Scope.Protocol,
                BackendInstanceId = payload.Scope.BackendInstanceId,
                LibraryScopeId = payload.Scope.LibraryScopeId,
                OccurrenceKey = occurrenceKey,
                SourceKind = "protocol",
                TrackReference = payload.ItemId
            };
            Apply(record, payload, track, added, enrichWithMusicBrainz);
            if (added) db.ListeningEvents.Add(record);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return record.State;
            }
            catch (DbUpdateException) when (added && attempt == 0)
            {
                // A sibling event created the same occurrence; merge into that row once.
            }
        }
    }

    private static void Apply(ListeningEventRecord record, PlaybackSignalPayload payload,
        PlaybackTrackSnapshot? track, bool added, bool enrichWithMusicBrainz)
    {
        var next = Classify(payload, track);
        record.State = added || Rank(next) > Rank(record.State) ? next : record.State;
        if (record.StartedAt == null && payload.Transition != PlaybackTransition.Submission)
            record.StartedAt = InferStart(payload);
        if (record.State == ListeningEventState.Completed)
            record.ListenedAt ??= payload.Transition == PlaybackTransition.Submission
                ? payload.ObservedAt
                : record.StartedAt ?? payload.ObservedAt;
        record.UpdatedAt = record.UpdatedAt > payload.ObservedAt ? record.UpdatedAt : payload.ObservedAt;
        if (payload.PositionTicks is >= 0 && (!record.PositionTicks.HasValue || payload.PositionTicks > record.PositionTicks))
            record.PositionTicks = payload.PositionTicks;
        if (track?.DurationMilliseconds is > 0) record.DurationMilliseconds = track.DurationMilliseconds;
        record.ClientClass ??= Trim(payload.ClientClass, 200);
        record.DeviceClass ??= Trim(payload.DeviceClass, 200);
        record.Title ??= Trim(track?.Title, 500);
        record.Artist ??= Trim(track?.Artist, 500);
        record.Album ??= Trim(track?.Album, 500);
        record.AlbumArtist ??= Trim(track?.AlbumArtist, 500);
        record.RecordingMusicBrainzId ??= ValidMusicBrainzId(track?.RecordingMusicBrainzId);
        record.Isrc ??= MusicBrainzService.NormalizeIsrc(track?.Isrc);
        if (enrichWithMusicBrainz && record.MusicBrainzEnrichmentState == MusicBrainzEnrichmentState.NotRequested)
            record.MusicBrainzEnrichmentState = MusicBrainzEnrichmentState.Pending;
        record.TrackNumber ??= track?.TrackNumber is > 0 ? track.TrackNumber : null;
        record.LibraryTrackId ??= track?.LibraryTrackId;
        record.CanonicalRecordingId ??= track?.CanonicalRecordingId;
        record.ProviderId ??= Trim(track?.ProviderId, 100);
        record.ProviderAccountId ??= track?.ProviderAccountId;
        record.ProviderTrackIdentityId ??= track?.ProviderTrackIdentityId;
        record.ProviderTrackReference ??= Trim(track?.ProviderTrackReference, 500);
        record.Revision++;
    }

    internal static ListeningEventState Classify(PlaybackSignalPayload payload, PlaybackTrackSnapshot? track) =>
        payload.Transition switch
        {
            PlaybackTransition.Submission => ListeningEventState.Completed,
            PlaybackTransition.Progress or PlaybackTransition.Stop or PlaybackTransition.InferredStop
                when ScopedPlaybackScrobbleDelivery.EligibleForCompletedScrobble(track?.DurationMilliseconds, payload.PositionTicks)
                => ListeningEventState.Completed,
            PlaybackTransition.Stop or PlaybackTransition.InferredStop when track == null
                => ListeningEventState.Abandoned,
            PlaybackTransition.Stop or PlaybackTransition.InferredStop => ListeningEventState.Skipped,
            _ => ListeningEventState.Playing
        };

    private static DateTimeOffset InferStart(PlaybackSignalPayload payload)
    {
        if (payload.PositionTicks is not > 0) return payload.ObservedAt;
        try { return payload.ObservedAt - TimeSpan.FromTicks(payload.PositionTicks.Value); }
        catch (ArgumentOutOfRangeException) { return payload.ObservedAt; }
    }

    private static int Rank(ListeningEventState state) => state switch
    {
        ListeningEventState.Completed => 3,
        ListeningEventState.Skipped => 2,
        ListeningEventState.Abandoned => 1,
        _ => 0
    };

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, maxLength)];
    }

    private static string? ValidMusicBrainzId(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D") : null;

}
