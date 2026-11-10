using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryRecordingLinkPayload(Guid UserId)
{
    public Guid[]? RecordingIds { get; init; }
    public string? BackendLibraryId { get; init; }
    public bool RemoveKeptCopy { get; init; }
    public DateTimeOffset? ConfirmBy { get; init; }
}

public sealed class LibraryRecordingLinkQueue(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    IBackendLibraryAccessResolver? libraryAccess = null)
{
    public const string JobType = "recording.library-link";
    internal const string CompletionAction = "library-recording-link.complete";

    public async Task<int> EnqueuePendingAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var users = await db.Users.AsNoTracking().Where(user => user.Enabled &&
            (libraryAccess != null || db.LibraryTracks.Any(track => track.OwnerUserId == user.Id)) &&
            !db.AuditEvents.Any(item => item.ActorUserId == user.Id && item.Action == CompletionAction))
            .Select(user => user.Id).ToArrayAsync(cancellationToken);
        var queued = 0;
        foreach (var user in users)
        {
            if (libraryAccess != null && !await LibraryTrackAccess.Query(db,
                    await libraryAccess.ResolveUserAsync(user, cancellationToken)).AnyAsync(cancellationToken)) continue;
            var receipt = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<LibraryRecordingLinkPayload>(
                JobType, "existing-library", new(user), user, Priority: -100,
                CorrelationId: "library-recording-link"), cancellationToken);
            if (receipt.Created) queued++;
        }
        return queued;
    }
}

public sealed partial class LibraryRecordingLinkJobHandler(ITrackIdentityService identities,
    IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock, RecordingKeptFiles? kept = null,
    IBackendLibraryAccessResolver? libraryAccess = null, BackendLibraryCatalogScannerResolver? scanners = null,
    allstarr.Core.Playlists.IBackendPlaylistTargetResolver? targets = null,
    allstarr.Core.Enrichment.LibraryPromotionService? promotions = null) : IDurableJobHandler
{
    public string JobType => LibraryRecordingLinkQueue.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken cancellationToken)
    {
        LibraryRecordingLinkPayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<LibraryRecordingLinkPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload?.UserId is not { } user || user == Guid.Empty || execution.Claim.OwnerUserId != user)
            return DurableJobCompletion.Failure("library_recording_link_invalid", "The saved library linking request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (payload.RecordingIds is { Length: > 0 } recordings)
        {
            if (recordings.Length > 256 || string.IsNullOrWhiteSpace(payload.BackendLibraryId))
                return DurableJobCompletion.Failure("library_recording_link_invalid", "The release linking request is invalid.");
            var promotionActor = new ProviderActorContext(ProviderActorKind.SystemJob, null,
                durableJobId: execution.Claim.JobId, actingForUserId: user);
            var listener = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user && item.Enabled, cancellationToken);
            var confirmBy = payload.ConfirmBy ?? (await db.Jobs.AsNoTracking().SingleAsync(
                item => item.Id == execution.Claim.JobId, cancellationToken)).CreatedAt.AddMinutes(10);
            DurableJobCompletion Pending(string reason) => clock.UtcNow >= confirmBy
                ? DurableJobCompletion.Failure("library_recording_confirmation_expired",
                    "The library song or its listener handover was not confirmed after ten minutes. Check the music folder, library scan and heart access in Jellyfin, then retry.")
                : DurableJobCompletion.Defer("library_recording_scan_pending", reason, TimeSpan.FromSeconds(30));
            if (scanners != null)
            {
                try
                {
                    await scanners.Resolve(ProtocolKind.Jellyfin).ScanAsync(
                        LibraryIndexExecution.Create(listener, null, execution.Claim.CorrelationId, clock, cancellationToken),
                        new(payload.BackendLibraryId), cancellationToken);
                }
                catch (HttpRequestException) { return Pending("Waiting for Jellyfin's refreshed music library to become available."); }
            }
            var accessible = libraryAccess == null ? db.LibraryTracks.Where(item => item.OwnerUserId == user) :
                LibraryTrackAccess.Query(db, await libraryAccess.ResolveUserAsync(user, cancellationToken));
            var pending = false;
            var favorites = new Dictionary<Guid, Dictionary<string, Guid?>>();
            foreach (var recordingId in recordings.Distinct())
            {
                var recording = await identities.ResolveRecordingAsync(promotionActor, recordingId.ToString("D"), cancellationToken);
                if (recording == null) return DurableJobCompletion.Failure("library_recording_missing", "A promoted recording is no longer available.");
                await identities.LinkLibraryRecordingsAsync(promotionActor, execution.Claim.CorrelationId,
                    recording.Id, cancellationToken: cancellationToken);
                recording = await identities.ResolveRecordingAsync(promotionActor, recordingId.ToString("D"), cancellationToken) ?? recording;
                var linked = await accessible.AsNoTracking().Where(item =>
                    item.BackendInstanceId == listener.BackendInstanceId && item.BackendLibraryId == payload.BackendLibraryId &&
                    item.CanonicalRecordingId == recording.Id).OrderBy(item => item.BackendItemId).Select(item => item.Id).FirstOrDefaultAsync(cancellationToken);
                if (linked == Guid.Empty) { pending = true; continue; }
                var details = JsonSerializer.Serialize(new { RecordingId = recordingId, CanonicalRecordingId = recording.Id });
                if (!await db.AuditEvents.AnyAsync(item => item.ActorUserId == user && item.Action == "promotion.library-linked" &&
                        item.CorrelationId == execution.Claim.CorrelationId && item.DetailsJson == details, cancellationToken))
                {
                    db.AuditEvents.Add(new()
                    {
                        Id = Guid.CreateVersion7(),
                        ActorUserId = user,
                        Category = "library-promotion",
                        Action = "promotion.library-linked",
                        Outcome = "succeeded",
                        DetailsJson = details,
                        SubjectType = "library-track",
                        SubjectId = linked.ToString("N"),
                        CorrelationId = execution.Claim.CorrelationId,
                        CreatedAt = clock.UtcNow
                    });
                    await db.SaveChangesAsync(cancellationToken);
                    if (promotions != null) await promotions.PublishLinkedAdditionsAsync(user, recordingId, cancellationToken);
                }
                if (!await HandoverAsync(recording.Id, listener, payload.BackendLibraryId, execution, payload.RemoveKeptCopy, favorites, cancellationToken))
                { pending = true; continue; }
                if (!await db.AuditEvents.AnyAsync(item => item.ActorUserId == user && item.Action == "promotion.handover-confirmed" &&
                        item.CorrelationId == execution.Claim.CorrelationId && item.DetailsJson == details, cancellationToken))
                {
                    db.AuditEvents.Add(new()
                    {
                        Id = Guid.CreateVersion7(),
                        ActorUserId = user,
                        Category = "library-promotion",
                        Action = "promotion.handover-confirmed",
                        Outcome = "succeeded",
                        DetailsJson = details,
                        SubjectType = "library-track",
                        SubjectId = linked.ToString("N"),
                        CorrelationId = execution.Claim.CorrelationId,
                        CreatedAt = clock.UtcNow
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            return pending ? Pending("Waiting for Jellyfin to confirm the library song and accept its listener handover.") : DurableJobCompletion.Success();
        }
        if (await db.AuditEvents.AnyAsync(item => item.ActorUserId == user &&
                item.Action == LibraryRecordingLinkQueue.CompletionAction, cancellationToken))
            return DurableJobCompletion.Success();
        var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null,
            durableJobId: execution.Claim.JobId, actingForUserId: user);
        var result = await identities.LinkLibraryRecordingsAsync(actor, execution.Claim.CorrelationId,
            cancellationToken: cancellationToken);
        await execution.ReportProgressAsync(new("completed",
            $"{result.Scanned} scanned, {result.Linked} linked, {result.Conflicts} conflicts.",
            result.Scanned, result.Scanned), cancellationToken);
        db.AuditEvents.Add(new()
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = user,
            Category = "library-recording-link",
            Action = LibraryRecordingLinkQueue.CompletionAction,
            Outcome = "completed",
            CorrelationId = execution.Claim.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(result),
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return DurableJobCompletion.Success();
    }
}

public sealed class LibraryRecordingLinkDiscoveryService(LibraryRecordingLinkQueue queue, DurableStorageState storage,
    ILogger<LibraryRecordingLinkDiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            try
            {
                await queue.EnqueuePendingAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Library recording linking discovery will retry ({ErrorType})", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
