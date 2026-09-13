using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Downloads;
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
    string? Album);

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

public sealed class PlaylistTrackRetentionQueue(
    DurableJobQueue jobs,
    DurablePlaylistProjectionReader projections,
    IDbContextFactory<AllstarrDbContext> factory) : IPlaylistTrackRetentionQueue
{
    public Task<int> EnqueueAsync(
        PlaylistLinkRecord link,
        PlaylistMaterializationPlan plan,
        string correlationId,
        CancellationToken cancellationToken) =>
        EnqueueAsync(
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

    public async Task<int> EnqueuePublishedAsync(
        PlaylistLinkRecord link,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (link.TrackRetention != PlaylistTrackRetention.KeepAll) return 0;
        var projection = await projections.ReadByLinkIdAsync(
            link.TenantId, link.OwnerUserId, link.Id, cancellationToken);
        if (projection == null) return 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var sourceEntries = await db.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.TenantId == link.TenantId &&
                           item.PlaylistSourceSnapshotId == projection.SnapshotId)
            .ToDictionaryAsync(item => item.SourcePosition, item => item.Id, cancellationToken);
        return await EnqueueAsync(
            link,
            projection.Entries.Select(entry => new RetentionCandidate(
                projection.SnapshotId,
                sourceEntries.GetValueOrDefault(entry.Position),
                entry.RouteKind.Equals("external", StringComparison.OrdinalIgnoreCase)
                    ? entry.RouteProviderId
                    : null,
                entry.RouteKind.Equals("external", StringComparison.OrdinalIgnoreCase)
                    ? entry.ExternalId
                    : null,
                entry.Title,
                entry.Artists.ToArray(),
                entry.Album)),
            correlationId,
            cancellationToken);
    }

    private async Task<int> EnqueueAsync(
        PlaylistLinkRecord link,
        IEnumerable<RetentionCandidate> candidates,
        string correlationId,
        CancellationToken cancellationToken)
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
        var queued = 0;
        foreach (var entry in tracks)
        {
            var providerId = entry.ProviderId!.Trim().ToLowerInvariant();
            var externalId = entry.ExternalId!;
            var retentionKey = Hash($"{link.Id:N}\u001f{providerId}\u001f{externalId}");
            var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistTrackRetentionJobPayload>(
                PlaylistTrackRetentionJobHandler.JobTypeName,
                $"playlist-retain:{retentionKey}",
                new(
                    link.Id,
                    entry.SourceSnapshotId,
                    entry.SourceEntryId,
                    providerId,
                    externalId,
                    retentionKey,
                    string.IsNullOrWhiteSpace(entry.Title) ? "Unknown track" : entry.Title,
                    entry.Artists ?? [],
                    entry.Album),
                link.TenantId,
                link.OwnerUserId,
                MaxAttempts: 12,
                MaxDeferrals: 100,
                LibraryScopeId: link.LibraryScopeId,
                CorrelationId: correlationId), cancellationToken);
            if (result.Created) queued++;
        }
        return queued;
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record RetentionCandidate(
        Guid SourceSnapshotId,
        Guid SourceEntryId,
        string? ProviderId,
        string? ExternalId,
        string? Title,
        string[]? Artists,
        string? Album);
}

public sealed class PlaylistTrackRetentionJobHandler(
    IDbContextFactory<AllstarrDbContext> factory,
    ManagedTrackDownloadService downloads,
    ProviderDownloadArtifactResolver artifacts,
    IServiceScopeFactory scopes,
    ManagedTrackPlacementOptions placement) : IDurableJobHandler
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
        if (!Valid(payload) || context.Claim.TenantId is not { } tenantId ||
            context.Claim.OwnerUserId is not { } ownerUserId ||
            string.IsNullOrWhiteSpace(context.Claim.LibraryScopeId))
            return DurableJobCompletion.Failure("playlist_retention_payload_invalid",
                "The playlist retention payload is invalid.");
        var request = payload!;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == request.PlaylistLinkId && item.TenantId == tenantId &&
            item.OwnerUserId == ownerUserId && item.LibraryScopeId == context.Claim.LibraryScopeId,
            cancellationToken);
        if (link == null || link.TrackRetention != PlaylistTrackRetention.KeepAll)
            return DurableJobCompletion.Success();
        var external = await (from entry in db.PlaylistSourceEntries.AsNoTracking()
                              join snapshot in db.PlaylistSourceSnapshots.AsNoTracking()
                                  on entry.PlaylistSourceSnapshotId equals snapshot.Id
                              join metadata in db.ExternalMetadataSnapshots.AsNoTracking()
                                  on entry.ExternalMetadataSnapshotId equals metadata.Id
                              where entry.Id == request.SourceEntryId &&
                                    entry.TenantId == tenantId &&
                                    snapshot.Id == request.SourceSnapshotId &&
                                    snapshot.PlaylistLinkId == link.Id &&
                                    snapshot.TenantId == tenantId &&
                                    snapshot.OwnerUserId == ownerUserId &&
                                    snapshot.ProviderAccountId == link.ProviderAccountId &&
                                    metadata.TenantId == tenantId &&
                                    metadata.OwnerUserId == ownerUserId &&
                                    metadata.ProviderAccountId == link.ProviderAccountId &&
                                    metadata.LibraryScopeId == link.LibraryScopeId &&
                                    metadata.BackendInstanceId == link.TargetBackendInstanceId &&
                                    metadata.Protocol == link.TargetProtocol
                              select metadata)
            .SingleOrDefaultAsync(cancellationToken);
        if (external == null)
            return DurableJobCompletion.Failure("playlist_retention_source_unavailable",
                "The imported track is no longer available in this playlist snapshot.");

        await context.ReportProgressAsync(new(
            "playlist.retain.download",
            $"Downloading {request.Title} for permanent storage.",
            Provider: request.ProviderId,
            Track: request.Title), cancellationToken);
        var download = await downloads.ExecuteAsync(new ManagedTrackDownloadCommand(
            tenantId,
            ownerUserId,
            link.LibraryScopeId,
            context.Claim.JobId,
            request.ProviderId,
            request.ExternalId,
            context.Claim.CorrelationId,
            request.RetentionKey,
            context.Claim.AttemptNumber,
            "playlist-retention",
            request.RetentionKey), cancellationToken);
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
        if (artifact is { State: ProviderDownloadArtifactState.Placed, ManagedFileId: not null })
            return DurableJobCompletion.Success();
        if (artifact is not { State: ProviderDownloadArtifactState.Verified } ||
            artifact.TenantId != tenantId ||
            artifact.OwnerUserId != ownerUserId ||
            artifact.LibraryScopeId != link.LibraryScopeId ||
            artifact.DurableJobId != context.Claim.JobId)
            return DurableJobCompletion.Failure("playlist_retention_artifact_invalid",
                "The downloaded track is outside the playlist owner or library scope.");
        if (placement.RootId == Guid.Empty || string.IsNullOrWhiteSpace(placement.RootPath))
            return DurableJobCompletion.Failure("playlist_retention_placement_not_configured",
                "The permanent music storage root is not configured.");

        await context.ReportProgressAsync(new(
            "playlist.retain.place",
            $"Keeping {request.Title} in permanent storage.",
            Provider: artifact.ProviderId,
            Track: request.Title), cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
            var extension = Path.GetExtension(artifact.SourcePath);
            var result = await service.PlaceAsync(new ManagedFilePlacementRequest(
                new ManagedFileRoot(
                    placement.RootId,
                    Path.Combine(Path.GetFullPath(placement.RootPath), tenantId.ToString("N"), ownerUserId.ToString("N")),
                    tenantId,
                    ownerUserId,
                    link.LibraryScopeId),
                artifact.SourcePath,
                placement.PathTemplate,
                new ManagedTrackPathValues(
                    request.Title,
                    request.Artists.FirstOrDefault() ?? "Unknown artist",
                    request.Album,
                    Extension: string.IsNullOrWhiteSpace(extension) ? ".bin" : extension),
                context.Claim.JobId,
                ManagedFileScopeKey.Create(tenantId, ownerUserId, placement.RootId, link.LibraryScopeId),
                SourceIsAllstarrManaged: true,
                SourceIsImmutable: false,
                ExpectedContentSha256: artifact.ContentSha256,
                ExpectedLength: artifact.Length)
            {
                ReferenceKey = request.RetentionKey,
                DestinationIsImmutable = false
            }, cancellationToken);
            await artifacts.MarkPlacedAsync(artifact.Id, result.File.Id, cancellationToken);
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
        catch (UnauthorizedAccessException)
        {
            return DurableJobCompletion.Failure("playlist_retention_placement_denied",
                "The permanent music storage path is not authorized.");
        }
    }

    private static bool Valid(PlaylistTrackRetentionJobPayload? payload) =>
        payload != null &&
        payload.PlaylistLinkId != Guid.Empty &&
        payload.SourceSnapshotId != Guid.Empty &&
        payload.SourceEntryId != Guid.Empty &&
        payload.ProviderId is { Length: > 0 and <= 100 } &&
        payload.ExternalId is { Length: > 0 and <= 500 } &&
        payload.RetentionKey is { Length: 64 } &&
        payload.Title is { Length: > 0 and <= 500 } &&
        payload.Artists is { Length: <= 100 } &&
        payload.Artists.All(value => value is { Length: <= 500 });
}
