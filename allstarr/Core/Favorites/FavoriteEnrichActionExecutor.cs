using allstarr.Core.Downloads;
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
            item.SourceJobId == favoriteEvent.JobId && item.IsManaged && item.RemovedAt == null, cancellationToken);
        if (managed == null)
            return FavoriteActionExecutionResult.Failure("favorite_enrichment_scope_denied",
                "The managed favorite file is outside the authorized scope.");
        var track = await metadata.ResolveAsync(favoriteEvent, cancellationToken);
        if (track == null)
            return FavoriteActionExecutionResult.Failure("favorite_enrichment_metadata_missing",
                "Track metadata required for managed enrichment is unavailable.");
        var plan = planner.CreatePlan(new LocalMetadataSnapshot(
            new MetadataField(track.Title), new MetadataField(track.Artist),
            new MetadataField(track.Album), new MetadataField(track.AlbumArtist),
            new MetadataField(track.Genre), new MetadataField(track.Year?.ToString()),
            new MetadataField(track.Track?.ToString())), null);
        try
        {
            var savedPlan = await durable.SavePlanAsync(new DurableEnrichmentPlanRequest(
                favoriteEvent.TenantId, favoriteEvent.OwnerUserId, favoriteEvent.JobId, managedFileId, plan),
                cancellationToken);
            var application = await durable.BeginApplicationAsync(new DurableEnrichmentApplicationRequest(
                favoriteEvent.TenantId, favoriteEvent.OwnerUserId, favoriteEvent.JobId, managedFileId,
                savedPlan.Id, artifact.ContentSha256), cancellationToken);
            if (application.State == MetadataEnrichmentApplicationState.Applied)
                return FavoriteActionExecutionResult.Success();
            await applicator.ApplyAsync(new ManagedMetadataArtifact(managed.CanonicalPath,
                artifact.ContentSha256, IsAllstarrManaged: true, IsSourceLibraryFile: false), plan, cancellationToken);
            await durable.MarkAppliedAsync(favoriteEvent.TenantId, favoriteEvent.OwnerUserId,
                application.Id, cancellationToken);
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
    }
}
