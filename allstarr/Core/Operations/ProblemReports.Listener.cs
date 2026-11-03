using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Operations;

public sealed partial class ProblemReportService
{
    public async Task<ProblemReportReceipt> CreateListenerAsync(ProtocolExecutionContext execution,
        CreateClientReportRequest request, CancellationToken token)
    {
        if (request == null) throw ProblemReportException.Invalid();
        var kind = request.Kind?.Trim().ToLowerInvariant() switch
        {
            "wrong-song" => "wrong-match",
            "wrong-lyrics" => "wrong-lyrics",
            "wont-play" or "bad-quality" => "playback",
            "wrong-info" => "metadata",
            "other" => "other",
            _ => throw ProblemReportException.Invalid()
        };
        var normalized = NormalizeRequest(new(Guid.CreateVersion7(), kind, request.Note,
            request.ItemId == null ? null : new(ItemId: request.ItemId)));
        ProblemReportContext? captured = null;
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            var actor = await RequireListenerActorAsync(db, execution, token);
            if (request.PlaybackRequestId is { } requestId)
            {
                var trace = await ListenerPlaybackAsync(db, execution, actor, requestId, normalized.Selector?.ItemId, token);
                captured = PlaybackReportContext(trace, actor.Id, actor.DisplayName);
            }
            else if (execution.Client.DeviceId is { } device)
            {
                var now = clock.UtcNow;
                var state = playbackSources.SelectMany(source => source.GetActivePlaybackStates(TimeSpan.FromMinutes(5)))
                    .Where(state => state.UserId == actor.Id && state.DeviceId == device &&
                        state.BackendUserId == execution.VerifiedBackendPrincipalId &&
                        state.LastActivity.ToUniversalTime() >= now.AddMinutes(-5).UtcDateTime &&
                        state.LastActivity.ToUniversalTime() <= now.UtcDateTime &&
                        (normalized.Selector?.ItemId == null || SameItem(state.ItemId, normalized.Selector.ItemId)))
                    .OrderByDescending(state => state.LastActivity).FirstOrDefault();
                if (state != null)
                    normalized = normalized with { Selector = new(ItemId: state.ItemId, Playback: new(actor.Id, device)) };
            }
        }
        return await CreateCoreAsync((db, cancellation) => RequireListenerActorAsync(db, execution, cancellation),
            normalized, captured, token);
    }

    private async Task<UserRecord> RequireListenerActorAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        CancellationToken token)
    {
        var owner = execution.RequireActor().EffectiveUserId;
        var backend = execution.Protocol.ToString().ToLowerInvariant();
        if (owner == null || configuration?["Backend:Type"] is { } selectedBackend &&
            !selectedBackend.Trim().Equals(backend, StringComparison.OrdinalIgnoreCase)) throw ProblemReportException.Unauthorized();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner && user.Enabled &&
            user.BackendType == backend && user.BackendInstanceId == execution.BackendInstanceId &&
            user.BackendPrincipalId == execution.VerifiedBackendPrincipalId, token) ?? throw ProblemReportException.Unauthorized();
        actor.IsAdmin = false;
        return actor;
    }

    private async Task<PlayTraceDetail> ListenerPlaybackAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        UserRecord actor, Guid id, string? itemId, CancellationToken token)
    {
        if (execution.Client.DeviceId is not { } device) throw ProblemReportException.Missing();
        var row = await PlaybackStartTimingStore.RecentEvents(db, clock.UtcNow)
            .SingleOrDefaultAsync(row => row.Id == id && row.ActorUserId == actor.Id, token) ?? throw ProblemReportException.Missing();
        if (row.DetailsJson.Length > 131072) throw ProblemReportException.Missing();
        PlaybackStartSample? sample;
        try { sample = JsonSerializer.Deserialize<PlaybackStartSample>(row.DetailsJson); }
        catch (JsonException) { throw ProblemReportException.Missing(); }
        if (sample?.Trace is not { } trace || sample.Id != row.Id || trace.OwnerUserId != actor.Id ||
            trace.Request.Protocol != execution.Protocol.ToString() || sample.DeviceId != device ||
            itemId != null && (trace.Request.ItemId == null || !SameItem(itemId, trace.Request.ItemId)))
            throw ProblemReportException.Missing();
        if (trace.Resolution.ExternalSnapshotId is { } snapshotId)
        {
            var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item => item.Id == snapshotId, token);
            if (snapshot != null && (snapshot.OwnerUserId != actor.Id || snapshot.Protocol != actor.BackendType ||
                snapshot.BackendInstanceId != actor.BackendInstanceId || snapshot.BackendPrincipalId != actor.BackendPrincipalId))
                throw ProblemReportException.Missing();
        }
        if (trace.Request.ItemId is { } playedItem && playbackDeliveries?.StreamFor(actor.Id, device, playedItem) is { } stream &&
            stream.PlaybackRequestId == id && !stream.Matches(actor.BackendType, actor.BackendInstanceId))
            throw ProblemReportException.Missing();
        return new(row.Id, row.CreatedAt, sample.Outcome, trace.Request, trace.Resolution, trace.Received, trace.Delivery, [],
            new(sample.ElapsedMs, sample.HeadersMs, sample.FirstByteMs, sample.Phases, sample.OmittedPhases), actor.Id);
    }
}
