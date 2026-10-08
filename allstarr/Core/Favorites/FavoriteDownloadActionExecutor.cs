using System.Text.Json;
using allstarr.Services.Local;
using allstarr.Core.Downloads;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoriteDownloadActionExecutor(
    ManagedTrackDownloadService managedDownloads,
    IDbContextFactory<AllstarrDbContext> factory) : IFavoriteActionExecutor
{
    public string ActionType => "download";

    public async Task<FavoriteActionExecutionResult> ExecuteAsync(
        FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action,
        CancellationToken cancellationToken)
    {
        var external = ParseExternalTrack(favoriteEvent.ItemId);
        if (external == null)
            return FavoriteActionExecutionResult.Failure(
                "favorite_download_external_id_required",
                "The favorite item has no provider track identity for download.");
        var libraryScopeId = favoriteEvent.LibraryScopeId;
        if (string.IsNullOrWhiteSpace(libraryScopeId))
            return FavoriteActionExecutionResult.Failure(
                "favorite_download_library_missing",
                "The favorite event has no authorized library scope for download.");
        if (await HasLocalMatchAsync(factory, favoriteEvent, cancellationToken))
            return FavoriteActionExecutionResult.Success();

        var result = await managedDownloads.ExecuteAsync(
            new ManagedTrackDownloadCommand(
                favoriteEvent.TenantId,
                favoriteEvent.OwnerUserId,
                libraryScopeId,
                favoriteEvent.JobId,
                external.Value.Provider,
                external.Value.Id,
                favoriteEvent.CorrelationId,
                action.IdempotencyKey,
                action.AttemptCount,
                "favorite-download"),
            cancellationToken);
        return MapResult(result);
    }

    private static FavoriteActionExecutionResult MapResult(ManagedTrackDownloadResult result)
    {
        if (result.Succeeded)
            return FavoriteActionExecutionResult.Success();

        var code = result.ErrorCode switch
        {
            "managed_download_external_id_required" => "favorite_download_external_id_required",
            "managed_download_library_missing" => "favorite_download_library_missing",
            "managed_download_provider_unavailable" => "favorite_download_provider_unavailable",
            "managed_download_route_denied" => "favorite_download_route_denied",
            "managed_download_route_unavailable" => "favorite_download_route_unavailable",
            "managed_download_unavailable" => "favorite_download_unavailable",
            "managed_download_temporary_failure" => "favorite_download_temporary_failure",
            "managed_download_failed" => "favorite_download_failed",
            "managed_download_artifact_io_failed" => "favorite_download_artifact_io_failed",
            "managed_download_artifact_invalid" => "favorite_download_artifact_invalid",
            "managed_download_route_exhausted" => "favorite_download_route_exhausted",
            _ => "favorite_download_failed"
        };
        var message = result.SafeMessage ?? "The managed download failed.";
        return result.Retryable
            ? FavoriteActionExecutionResult.Retry(code, message)
            : FavoriteActionExecutionResult.Failure(code, message);
    }

    internal static (string Provider, string Id)? ParseExternalTrack(string itemId)
    {
        var (isExternal, provider, type, externalId) = LocalLibraryService.ParseExternalResource(itemId);
        return isExternal && type == "song"
            ? (provider!.ToLowerInvariant(), externalId!)
            : null;
    }

    internal static bool ProviderIdMatches(string json, string provider, string externalId)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values != null && values.TryGetValue(provider, out var value) &&
                   value.Equals(externalId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static async Task<bool> HasLocalMatchAsync(IDbContextFactory<AllstarrDbContext> factory,
        FavoriteEventRecord favoriteEvent, CancellationToken cancellationToken)
    {
        var external = ParseExternalTrack(favoriteEvent.ItemId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.LibraryTracks.AsNoTracking().Where(item =>
            item.TenantId == favoriteEvent.TenantId && item.OwnerUserId == favoriteEvent.OwnerUserId &&
            item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
            item.LibraryScopeId == favoriteEvent.LibraryScopeId).ToListAsync(cancellationToken);
        return external == null
            ? candidates.Any(item => item.BackendItemId == favoriteEvent.ItemId)
            : candidates.Any(item => ProviderIdMatches(item.ProviderIdsJson, external.Value.Provider, external.Value.Id));
    }
}
