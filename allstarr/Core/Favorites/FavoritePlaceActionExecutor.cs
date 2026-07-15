using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoritePlacementOptions
{
    public Guid RootId { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string PathTemplate { get; set; } = "{albumArtist}/{album}/{track} - {title}{extension}";
}

public sealed class FavoritePlaceActionExecutor(
    IDbContextFactory<Core.Storage.AllstarrDbContext> factory,
    ProviderDownloadArtifactResolver artifacts,
    IServiceScopeFactory scopes,
    FavoriteTrackMetadataResolver metadata,
    FavoritePlacementOptions options) : IFavoriteActionExecutor
{
    public string ActionType => "place";
    public async Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action, CancellationToken cancellationToken)
    {
        if (await FavoriteMatchActionExecutor.HasLocalMatchAsync(factory, favoriteEvent, cancellationToken))
            return FavoriteActionExecutionResult.Success();
        if (options.RootId == Guid.Empty || string.IsNullOrWhiteSpace(options.RootPath))
            return FavoriteActionExecutionResult.Failure("favorite_placement_not_configured",
                "The managed favorite placement root is not configured.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == favoriteEvent.TenantId && item.OwnerUserId == favoriteEvent.OwnerUserId &&
            item.LibraryScopeId == favoriteEvent.LibraryScopeId &&
            item.DurableJobId == favoriteEvent.JobId, cancellationToken);
        if (record == null)
            return FavoriteActionExecutionResult.Failure("favorite_download_artifact_missing",
                "The verified favorite download artifact is unavailable.");
        if (record.State == ProviderDownloadArtifactState.Placed && record.ManagedFileId.HasValue)
            return FavoriteActionExecutionResult.Success();
        var artifact = await artifacts.FindByJobAsync(favoriteEvent.TenantId, favoriteEvent.JobId,
            record.ProviderId, cancellationToken);
        if (artifact == null || artifact.OwnerUserId != favoriteEvent.OwnerUserId ||
            artifact.LibraryScopeId != favoriteEvent.LibraryScopeId ||
            artifact.State != ProviderDownloadArtifactState.Verified)
            return FavoriteActionExecutionResult.Failure("favorite_download_artifact_invalid",
                "The favorite download artifact is outside the authorized scope.");
        var track = await metadata.ResolveAsync(favoriteEvent, cancellationToken);
        if (track == null)
            return FavoriteActionExecutionResult.Failure("favorite_placement_metadata_missing",
                "Track metadata required for safe managed placement is unavailable.");
        track = track with { Extension = NormalizeExtension(track.Extension, artifact.SourcePath) };
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var placement = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
            var result = await placement.PlaceAsync(new ManagedFilePlacementRequest(
                new ManagedFileRoot(options.RootId, ScopedRoot(options.RootPath, favoriteEvent.TenantId,
                        favoriteEvent.OwnerUserId), favoriteEvent.TenantId,
                    favoriteEvent.OwnerUserId, favoriteEvent.LibraryScopeId),
                artifact.SourcePath, options.PathTemplate, track, favoriteEvent.JobId,
                ManagedFileScopeKey.Create(favoriteEvent.TenantId, favoriteEvent.OwnerUserId,
                    options.RootId, favoriteEvent.LibraryScopeId),
                SourceIsAllstarrManaged: true,
                SourceIsImmutable: false,
                ExpectedContentSha256: artifact.ContentSha256,
                ExpectedLength: artifact.Length)
            {
                ReferenceKey = action.IdempotencyKey,
                DestinationIsImmutable = false
            }, cancellationToken);
            await artifacts.MarkPlacedAsync(artifact.Id, result.File.Id, cancellationToken);
            return FavoriteActionExecutionResult.Success();
        }
        catch (IOException)
        {
            return FavoriteActionExecutionResult.Retry("favorite_placement_io_failed",
                "The managed favorite placement temporarily failed.");
        }
        catch (UnauthorizedAccessException)
        {
            return FavoriteActionExecutionResult.Failure("favorite_placement_path_denied",
                "The managed favorite placement path is not authorized.");
        }
    }

    private static string NormalizeExtension(string configured, string sourcePath)
    {
        var extension = string.IsNullOrWhiteSpace(configured) ? Path.GetExtension(sourcePath) : configured;
        if (string.IsNullOrWhiteSpace(extension)) return ".bin";
        return extension.StartsWith('.') ? extension : $".{extension}";
    }

    private static string ScopedRoot(string configuredRoot, Guid tenantId, Guid ownerUserId)
    {
        var root = Path.GetFullPath(configuredRoot);
        return Path.Combine(root, tenantId.ToString("N"), ownerUserId.ToString("N"));
    }

}
