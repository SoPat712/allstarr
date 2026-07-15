using allstarr.Core.Downloads;
using System.Security.Cryptography;
using allstarr.Core.Enrichment;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoriteEnrichActionExecutor(
    IDbContextFactory<AllstarrDbContext> factory,
    FavoriteTrackMetadataResolver metadata,
    IMetadataEnrichmentPlanner planner,
    DurableMetadataEnrichmentService durable,
    ManagedMetadataPlanApplicator applicator) : IFavoriteActionExecutor
{
    public string ActionType => "enrich";

    public async Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var artifact = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == favoriteEvent.TenantId && item.OwnerUserId == favoriteEvent.OwnerUserId &&
            item.LibraryScopeId == favoriteEvent.LibraryScopeId &&
            item.DurableJobId == favoriteEvent.JobId && item.State == ProviderDownloadArtifactState.Placed &&
            item.ManagedFileId != null, cancellationToken);
        if (artifact?.ManagedFileId is not { } managedFileId)
            return await FavoriteMatchActionExecutor.HasLocalMatchAsync(factory, favoriteEvent, cancellationToken)
                ? FavoriteActionExecutionResult.Success()
                : FavoriteActionExecutionResult.Failure("favorite_enrichment_managed_file_missing",
                    "The placed managed favorite file is unavailable for enrichment.");
        var managed = await db.Set<ManagedFileOwnershipEntity>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == managedFileId && item.TenantId == favoriteEvent.TenantId &&
            item.OwnerUserId == favoriteEvent.OwnerUserId && item.LibraryScopeId == favoriteEvent.LibraryScopeId &&
            item.IsManaged && item.RemovedAt == null, cancellationToken);
        if (managed == null)
            return FavoriteActionExecutionResult.Failure("favorite_enrichment_scope_denied",
                "The managed favorite file is outside the authorized scope.");
        var enrichment = await metadata.ResolveEnrichmentAsync(favoriteEvent, cancellationToken);
        if (enrichment == null)
            return FavoriteActionExecutionResult.Failure("favorite_enrichment_metadata_missing",
                "Track metadata required for managed enrichment is unavailable.");
        try
        {
            var plan = planner.CreatePlan(enrichment.Local, enrichment.MusicBrainz, enrichment.Providers);
            var savedPlan = await durable.SavePlanAsync(new DurableEnrichmentPlanRequest(
                favoriteEvent.TenantId, favoriteEvent.OwnerUserId, favoriteEvent.JobId, managedFileId, plan),
                cancellationToken);
            var application = await durable.BeginApplicationAsync(new DurableEnrichmentApplicationRequest(
                favoriteEvent.TenantId, favoriteEvent.OwnerUserId, favoriteEvent.JobId, managedFileId,
                savedPlan.Id, managed.ContentSha256), cancellationToken);
            if (application.State == MetadataEnrichmentApplicationState.Applied)
            {
                await VerifyAppliedArtifactAsync(managed.CanonicalPath, managed.ContentSha256, managed.Length,
                    cancellationToken);
                return FavoriteActionExecutionResult.Success();
            }
            var write = await applicator.ApplyAsync(new ManagedMetadataArtifact(managed.CanonicalPath,
                application.ArtifactContentSha256, IsAllstarrManaged: true, IsSourceLibraryFile: false)
            {
                TargetRootPath = managed.TargetRootPath,
                FileSystemDeviceId = managed.FileSystemDeviceId,
                FileSystemFileId = managed.FileSystemFileId,
                OperationFingerprint = plan.Fingerprint
            }, plan, cancellationToken);
            await using var writeLease = write.Lease;
            var trackedManaged = await db.Set<ManagedFileOwnershipEntity>().SingleAsync(item =>
                item.Id == managedFileId && item.TenantId == favoriteEvent.TenantId &&
                item.OwnerUserId == favoriteEvent.OwnerUserId &&
                item.LibraryScopeId == favoriteEvent.LibraryScopeId &&
                item.IsManaged && item.RemovedAt == null, cancellationToken);
            if (!trackedManaged.ContentSha256.Equals(managed.ContentSha256, StringComparison.OrdinalIgnoreCase) &&
                !trackedManaged.ContentSha256.Equals(write.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The managed file ownership record changed during enrichment.");
            trackedManaged.ContentSha256 = write.ContentSha256;
            trackedManaged.Length = write.Length;
            trackedManaged.FileSystemDeviceId = write.FileSystemDeviceId;
            trackedManaged.FileSystemFileId = write.FileSystemFileId;
            trackedManaged.FileSystemLinkCount = write.FileSystemLinkCount;
            trackedManaged.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            await durable.MarkAppliedAsync(favoriteEvent.TenantId, favoriteEvent.OwnerUserId,
                application.Id, cancellationToken);
            if (writeLease is not null) await writeLease.CommitAsync(cancellationToken);
            return FavoriteActionExecutionResult.Success();
        }
        catch (IOException)
        {
            return FavoriteActionExecutionResult.Retry("favorite_enrichment_io_failed",
                "Managed metadata enrichment temporarily failed.");
        }
        catch (InvalidOperationException)
        {
            return FavoriteActionExecutionResult.Failure("favorite_enrichment_invalid",
                "Managed metadata enrichment failed its safety checks.");
        }
        catch (ArgumentException)
        {
            return FavoriteActionExecutionResult.Failure("favorite_enrichment_metadata_invalid",
                "The available enrichment metadata failed validation.");
        }
    }

    private static async Task VerifyAppliedArtifactAsync(
        string path,
        string expectedContentSha256,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget != null || file.Length != expectedLength)
            throw new IOException("The applied managed metadata artifact no longer matches its ownership record.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        byte[] expected;
        try { expected = Convert.FromHexString(expectedContentSha256); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The managed file ownership checksum is invalid.", exception);
        }
        if (expected.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new IOException("The applied managed metadata artifact changed after enrichment.");
    }
}
