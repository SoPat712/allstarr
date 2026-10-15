using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record PlaylistTrackRetentionJobPayload(
    Guid PlaylistLinkId,
    Guid SourceSnapshotId,
    Guid SourceEntryId,
    string ProviderId,
    string ExternalId,
    string RetentionKey,
    string Title,
    string[] Artists,
    string? Album)
{
    public Guid? TargetCanonicalRecordingId { get; init; }
}

public interface IPlaylistTrackRetentionQueue
{
    Task<int> EnqueueAsync(
        PlaylistLinkRecord link,
        PlaylistMaterializationPlan plan,
        string correlationId,
        CancellationToken cancellationToken);

    Task<int> EnqueuePublishedAsync(
        PlaylistLinkRecord link,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed partial class PlaylistTrackRetentionQueue(
    DurableJobQueue jobs,
    DurablePlaylistProjectionReader projections,
    IDbContextFactory<AllstarrDbContext> factory,
    ITrackIdentityService identities) : IPlaylistTrackRetentionQueue
{
    public async Task<int> EnqueueAsync(
        PlaylistLinkRecord link,
        PlaylistMaterializationPlan plan,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var saved = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == link.Id && item.OwnerUserId == link.OwnerUserId, cancellationToken);
        if (saved != null)
            return await EnqueuePublishedAsync(saved, correlationId, cancellationToken);
        return await EnqueueAsync(
            link,
            plan.Entries.Select(entry => new RetentionCandidate(
                plan.SourceSnapshotId,
                entry.SourceEntryId,
                entry.ResolvedRoute?.Kind == TrackRouteKind.External
                    ? entry.ResolvedRoute.ProviderId
                    : null,
                entry.ResolvedRoute?.Kind == TrackRouteKind.External
                    ? entry.ResolvedRoute.ExternalId
                    : null,
                entry.SourceMetadata?.Title,
                entry.SourceMetadata?.Artists?.ToArray(),
                entry.SourceMetadata?.Album)),
            correlationId,
            cancellationToken);
    }

    public async Task<int> EnqueuePublishedAsync(
        PlaylistLinkRecord link,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var saved = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == link.Id && item.OwnerUserId == link.OwnerUserId, cancellationToken);
        if (saved?.TrackRetention != PlaylistTrackRetention.KeepAll) return 0;
        var access = await projections.ResolveRetentionAccessAsync(saved.OwnerUserId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        saved = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == link.Id && item.OwnerUserId == link.OwnerUserId, cancellationToken);
        if (saved?.TrackRetention != PlaylistTrackRetention.KeepAll) return 0;
        link = saved;
        var projection = await projections.ReadForRetentionAsync(db, link.OwnerUserId, link.Id, access, cancellationToken);
        if (projection == null) return 0;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == link.OwnerUserId && item.Enabled,
            cancellationToken);
        if (user == null || user.BackendType != link.TargetProtocol || user.BackendInstanceId != link.TargetBackendInstanceId)
            throw new UnauthorizedAccessException("The playlist owner is no longer available.");
        var actor = new ProviderActorContext(ProviderActorKind.User, user.Id,
            new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
        var entries = projection.Entries.Where(item => item.RouteKind == "external" && item.RouteProviderId != null).ToArray();
        var targets = await PlaylistRetentionTargets.ResolveAsync(db, identities, actor, entries, cancellationToken);
        var linkText = link.Id.ToString("D");
        var active = await db.Jobs.AsNoTracking().Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName &&
            item.OwnerUserId == user.Id && item.PayloadJson.Contains(linkText) &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled || item.State == DurableJobState.Running))
            .ToArrayAsync(cancellationToken);
        var compatible = await PlaylistRetentionContinuity.CompatibleAsync(db, identities, link, user, projection,
            targets, active, cancellationToken);
        var queued = await EnqueueProjectionInTransactionAsync(db, link, projection, correlationId, cancellationToken,
            targets, compatible.Values.ToHashSet(StringComparer.Ordinal));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return queued;
    }

    private async Task<int> EnqueueAsync(
        PlaylistLinkRecord link,
        IEnumerable<RetentionCandidate> candidates,
        string correlationId,
        CancellationToken cancellationToken,
        AllstarrDbContext? transactionContext = null)
    {
        if (link.TrackRetention != PlaylistTrackRetention.KeepAll) return 0;
        var tracks = candidates
            .Where(item => item.SourceEntryId != Guid.Empty &&
                           !string.IsNullOrWhiteSpace(item.ProviderId) &&
                           !string.IsNullOrWhiteSpace(item.ExternalId))
            .GroupBy(item => $"{item.ProviderId!.Trim().ToLowerInvariant()}\u001f{item.ExternalId}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var requests = new List<DurableJobEnqueueRequest<PlaylistTrackRetentionJobPayload>>(tracks.Length);
        foreach (var entry in tracks)
        {
            var providerId = entry.ProviderId!.Trim().ToLowerInvariant();
            var externalId = entry.ExternalId!;
            var retentionKey = RetentionKey(link.Id, providerId, externalId);
            var request = new DurableJobEnqueueRequest<PlaylistTrackRetentionJobPayload>(
                PlaylistTrackRetentionJobHandler.JobTypeName,
                $"playlist-retain:{retentionKey}:{entry.TargetCanonicalRecordingId?.ToString("N") ?? "unbound"}:{entry.SourceEntryId:N}:{entry.SourceSnapshotId:N}:{link.Revision}",
                new(
                    link.Id,
                    entry.SourceSnapshotId,
                    entry.SourceEntryId,
                    providerId,
                    externalId,
                    retentionKey,
                    string.IsNullOrWhiteSpace(entry.Title) ? "Unknown track" : entry.Title,
                    entry.Artists ?? [],
                    entry.Album)
                { TargetCanonicalRecordingId = entry.TargetCanonicalRecordingId },
                link.OwnerUserId,
                MaxAttempts: 12,
                MaxDeferrals: 100,
                CorrelationId: correlationId);
            requests.Add(request);
        }
        if (transactionContext != null)
        {
            var current = await ResolveCancelledLifecyclesAsync(transactionContext, requests, cancellationToken);
            var staged = await jobs.EnqueueManyInExistingTransactionAsync(transactionContext, current, cancellationToken);
            await jobs.PersistEnqueuedJobsAsync(transactionContext, staged, cancellationToken);
            return staged.Count(item => item.Created);
        }
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var currentRequests = await ResolveCancelledLifecyclesAsync(db, requests, cancellationToken);
        var results = await jobs.EnqueueManyInExistingTransactionAsync(db, currentRequests, cancellationToken);
        await jobs.PersistEnqueuedJobsAsync(db, results, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return results.Count(item => item.Created);
    }

    internal async Task<int> EnqueueProjectionInTransactionAsync(AllstarrDbContext db, PlaylistLinkRecord link,
        DurablePlaylistProjection projection, string correlationId, CancellationToken cancellationToken,
        IReadOnlyDictionary<int, Guid?>? targets = null, IReadOnlySet<string>? retained = null)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Playlist retention requires the publication transaction.");
        var entries = await db.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.PlaylistSourceSnapshotId == projection.SnapshotId)
            .ToDictionaryAsync(item => item.SourcePosition, item => item.Id, cancellationToken);
        return await EnqueueAsync(link, projection.Entries.Where(item => item.RouteProviderId == null ||
                retained?.Contains(RetentionKey(link.Id, item.RouteProviderId, item.ExternalId)) != true)
            .Select(item => new RetentionCandidate(
            projection.SnapshotId, entries.GetValueOrDefault(item.Position),
            item.RouteKind == "external" ? item.RouteProviderId : null,
            item.RouteKind == "external" ? item.ExternalId : null,
            item.Title, item.Artists.ToArray(), item.Album, targets?.GetValueOrDefault(item.Position))),
            correlationId, cancellationToken, db);
    }

    internal static string RetentionKey(Guid linkId, string providerId, string externalId) =>
        Hash($"{linkId:N}\u001f{providerId.Trim().ToLowerInvariant()}\u001f{externalId}");

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record RetentionCandidate(
        Guid SourceSnapshotId,
        Guid SourceEntryId,
        string? ProviderId,
        string? ExternalId,
        string? Title,
        string[]? Artists,
        string? Album,
        Guid? TargetCanonicalRecordingId = null);
}

public sealed class PlaylistTrackRetentionJobHandler(
    IDbContextFactory<AllstarrDbContext> factory,
    ManagedTrackDownloadService downloads,
    RecordingKeptFiles kept) : IDurableJobHandler
{
    public const string JobTypeName = "playlist.retain-track";
    public string JobType => JobTypeName;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        PlaylistTrackRetentionJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<PlaylistTrackRetentionJobPayload>(); }
        catch (JsonException) { payload = null; }
        if (!Valid(payload) || context.Claim.OwnerUserId is not { } ownerUserId)
            return DurableJobCompletion.Failure("playlist_retention_payload_invalid",
                "The playlist retention payload is invalid.");
        var request = payload!;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == request.PlaylistLinkId && item.OwnerUserId == ownerUserId,
            cancellationToken);
        if (link == null || link.TrackRetention != PlaylistTrackRetention.KeepAll)
            return DurableJobCompletion.Success();
        var external = await (from entry in db.PlaylistSourceEntries.AsNoTracking()
                              join snapshot in db.PlaylistSourceSnapshots.AsNoTracking()
                                  on entry.PlaylistSourceSnapshotId equals snapshot.Id
                              join metadata in db.ExternalMetadataSnapshots.AsNoTracking()
                                  on entry.ExternalMetadataSnapshotId equals metadata.Id
                              join owner in db.Users.AsNoTracking() on ownerUserId equals owner.Id
                              where owner.Enabled && owner.BackendType == link.TargetProtocol &&
                                    owner.BackendInstanceId == link.TargetBackendInstanceId &&
                                    metadata.BackendPrincipalId == owner.BackendPrincipalId &&
                                    entry.Id == request.SourceEntryId &&
                                    snapshot.Id == request.SourceSnapshotId &&
                                    snapshot.PlaylistLinkId == link.Id &&
                                    snapshot.OwnerUserId == ownerUserId &&
                                    snapshot.ProviderAccountId == link.ProviderAccountId &&
                                    metadata.OwnerUserId == ownerUserId &&
                                    metadata.ProviderAccountId == link.ProviderAccountId &&
                                    metadata.BackendInstanceId == link.TargetBackendInstanceId &&
                                    metadata.Protocol == link.TargetProtocol
                              select metadata)
            .SingleOrDefaultAsync(cancellationToken);
        if (external == null)
            return DurableJobCompletion.Failure("playlist_retention_source_unavailable",
                "The imported track is no longer available in this playlist snapshot.");

        try
        {
            if (await kept.RetainPlaylistAsync(context.Claim, cancellationToken) != null)
                return DurableJobCompletion.Success();
        }
        catch (UnauthorizedAccessException) { return DurableJobCompletion.Cancelled(); }

        await context.ReportProgressAsync(new(
            "playlist.retain.download",
            $"Downloading {request.Title} for permanent storage.",
            Provider: request.ProviderId,
            Track: request.Title), cancellationToken);
        var download = await downloads.ExecuteAsync(new ManagedTrackDownloadCommand(
            ownerUserId,
            context.Claim.JobId,
            request.ProviderId,
            request.ExternalId,
            context.Claim.CorrelationId,
            request.RetentionKey,
            context.Claim.AttemptNumber,
            "recording-keep"), cancellationToken);
        if (!download.Succeeded)
        {
            var code = download.ErrorCode?.Replace(
                "managed_download",
                "playlist_retention_download",
                StringComparison.Ordinal) ?? "playlist_retention_download_failed";
            var message = download.SafeMessage ?? "The playlist track download failed.";
            return download.Retryable
                ? DurableJobCompletion.Retry(code, message)
                : DurableJobCompletion.Failure(code, message);
        }

        var artifact = download.Artifact;
        if (artifact is not { State: ProviderDownloadArtifactState.Verified } ||
            artifact.OwnerUserId != ownerUserId || artifact.DurableJobId != context.Claim.JobId)
            return DurableJobCompletion.Failure("playlist_retention_artifact_invalid",
                "The downloaded track is outside the playlist owner or job scope.");

        await context.ReportProgressAsync(new(
            "playlist.retain.place",
            $"Keeping {request.Title} in permanent storage.",
            Provider: artifact.ProviderId,
            Track: request.Title), cancellationToken);
        try
        {
            await kept.PublishPlaylistAsync(context.Claim, artifact, cancellationToken);
            await context.ReportProgressAsync(new(
                "playlist.retain.complete",
                $"Kept {request.Title} in permanent storage.",
                Completed: 1,
                Total: 1,
                Provider: artifact.ProviderId,
                Track: request.Title), cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (IOException)
        {
            return DurableJobCompletion.Retry("playlist_retention_placement_io_failed",
                "Permanent track placement temporarily failed.");
        }
        catch (UnauthorizedAccessException) { return DurableJobCompletion.Cancelled(); }
        catch (ArgumentException)
        {
            return DurableJobCompletion.Failure("playlist_retention_metadata_invalid",
                "The song information could not be saved. Refresh the playlist and try again.");
        }
    }

    internal static bool Valid(PlaylistTrackRetentionJobPayload? payload) =>
        payload != null &&
        payload.PlaylistLinkId != Guid.Empty &&
        payload.SourceSnapshotId != Guid.Empty &&
        payload.SourceEntryId != Guid.Empty &&
        payload.TargetCanonicalRecordingId != Guid.Empty &&
        payload.ProviderId is { Length: > 0 and <= 100 } &&
        payload.ExternalId is { Length: > 0 and <= 500 } &&
        payload.RetentionKey is { Length: 64 } &&
        payload.Title is { Length: > 0 and <= 500 } &&
        payload.Artists is { Length: <= 100 } &&
        payload.Artists.All(value => value is { Length: <= 500 });
}
