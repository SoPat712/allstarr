using System.Text.Json;
using allstarr.Services.Local;
using allstarr.Core.Downloads;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Core.Matching;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Capabilities;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoriteDownloadActionExecutor(
    ManagedTrackDownloadService managedDownloads,
    IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraryAccess,
    RecordingKeptFiles? keptFiles = null,
    ITrackIdentityService? identities = null,
    IProtocolProviderGateway? providerGateway = null,
    IPlatformClock? clock = null) : IFavoriteActionExecutor
{
    public string ActionType => "download";

    public async Task<FavoriteActionExecutionResult> ExecuteAsync(
        FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action,
        int executionAttempt,
        CancellationToken cancellationToken)
    {
        var external = ParseExternalTrack(favoriteEvent.ItemId);
        if (external == null)
            return FavoriteActionExecutionResult.Failure(
                "favorite_download_external_id_required",
                "The favorite item has no provider track identity for download.");
        var knownRecording = identities == null ? null : await identities.ResolveRecordingAsync(
            new ProviderActorContext(ProviderActorKind.User, favoriteEvent.OwnerUserId,
                new(favoriteEvent.Protocol, favoriteEvent.BackendInstanceId, favoriteEvent.BackendPrincipalId)),
            favoriteEvent.ItemId, cancellationToken);
        if (await HasLocalMatchAsync(factory, libraryAccess, favoriteEvent, cancellationToken, knownRecording?.Id))
            return FavoriteActionExecutionResult.Success();
        if (keptFiles != null && await keptFiles.RetainFavoriteAsync(
                new(favoriteEvent.OwnerUserId, favoriteEvent.JobId, executionAttempt), cancellationToken) != null)
            return FavoriteActionExecutionResult.Success();

        var result = await managedDownloads.ExecuteAsync(
            new ManagedTrackDownloadCommand(
                favoriteEvent.OwnerUserId,
                favoriteEvent.JobId,
                external.Value.Provider,
                external.Value.Id,
                favoriteEvent.CorrelationId,
                action.IdempotencyKey,
                executionAttempt,
                "recording-keep"),
            cancellationToken);
        if (!result.Succeeded) return MapResult(result);
        if (keptFiles == null || identities == null || providerGateway == null ||
            result.Artifact is not { State: ProviderDownloadArtifactState.Verified } artifact)
            return FavoriteActionExecutionResult.Failure("favorite_keep_unavailable",
                "The download could not be attached to a kept recording.");
        try
        {
            var protocol = new ProtocolExecutionContext(
                favoriteEvent.Protocol == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
                favoriteEvent.BackendInstanceId, favoriteEvent.BackendPrincipalId,
                new(favoriteEvent.OwnerUserId, favoriteEvent.Protocol, favoriteEvent.BackendInstanceId,
                    favoriteEvent.BackendPrincipalId, "Listener", false), favoriteEvent.CorrelationId,
                (clock?.UtcNow ?? DateTimeOffset.UtcNow).AddMinutes(5), cancellationToken);
            var recording = await identities.ResolveRecordingAsync(protocol.RequireActor(),
                favoriteEvent.ItemId, cancellationToken);
            if (recording == null)
                return FavoriteActionExecutionResult.Retry("favorite_keep_identity_unavailable",
                    "The song's recording identity is not ready yet.");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var state = await db.Set<FavoriteStateRecord>().AsNoTracking().SingleOrDefaultAsync(item =>
                item.OwnerUserId == favoriteEvent.OwnerUserId && item.Protocol == favoriteEvent.Protocol &&
                item.BackendInstanceId == favoriteEvent.BackendInstanceId && item.ItemId == favoriteEvent.ItemId &&
                item.LastEventId == favoriteEvent.Id && item.IsFavorite, cancellationToken);
            if (state == null) return Superseded();
            var song = await providerGateway.GetSongAsync(protocol, external.Value.Provider, external.Value.Id);
            if (song == null)
                return FavoriteActionExecutionResult.Retry("favorite_keep_metadata_unavailable",
                    "The song information is unavailable right now. The kept copy will be retried.");
            var metadataRecording = await identities.ResolveRecordingAsync(protocol.RequireActor(),
                song.CanonicalRecordingId?.ToString("D") ?? song.Id, cancellationToken);
            if (metadataRecording?.Id != recording.Id)
                return FavoriteActionExecutionResult.Failure("favorite_keep_metadata_mismatch",
                    "The song information no longer matches this recording. Refresh the song and try again.");
            var title = song.Title;
            var artists = song.Artists is { Count: > 0 } ? song.Artists.ToArray() :
                new[] { string.IsNullOrWhiteSpace(song.Artist) ? "Unknown artist" : song.Artist };
            var duration = song.PreciseDuration?.TotalMilliseconds ?? song.Duration * 1000d;
            var metadata = new KeptRecordingMetadata(title, artists, song.Album,
                duration is { } milliseconds ? checked((long)Math.Round(milliseconds)) : null,
                song.ExplicitContentLyrics);
            await keptFiles.PublishAsync(new(recording.Id, favoriteEvent.OwnerUserId, favoriteEvent.JobId,
                artifact.Id, artifact.ProviderId, $"heart:{state.Id:N}", new(title, artists[0], song.Album))
            { AttemptNumber = executionAttempt, Metadata = metadata }, cancellationToken);
            return FavoriteActionExecutionResult.Success();
        }
        catch (UnauthorizedAccessException) { return Superseded(); }
        catch (IOException)
        {
            return FavoriteActionExecutionResult.Retry("favorite_keep_io_failed",
                "The kept copy could not be saved yet. It will be retried.");
        }
        catch (ArgumentException)
        {
            return FavoriteActionExecutionResult.Failure("favorite_keep_metadata_invalid",
                "The song's display information could not be saved.");
        }
    }

    private static FavoriteActionExecutionResult Superseded() => FavoriteActionExecutionResult.Failure(
        "favorite_action_superseded", "A newer favorite request replaced this download.");

    private static FavoriteActionExecutionResult MapResult(ManagedTrackDownloadResult result)
    {
        if (result.Succeeded)
            return FavoriteActionExecutionResult.Success();

        var code = result.ErrorCode switch
        {
            "managed_download_external_id_required" => "favorite_download_external_id_required",
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
        IBackendLibraryAccessResolver libraryAccess, FavoriteEventRecord favoriteEvent, CancellationToken cancellationToken,
        Guid? canonicalRecordingId = null)
    {
        var external = ParseExternalTrack(favoriteEvent.ItemId);
        if (canonicalRecordingId == null && external is { Provider: "allstarr" } &&
            Guid.TryParse(external.Value.Id, out var canonicalId)) canonicalRecordingId = canonicalId;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var access = await libraryAccess.ResolveUserAsync(favoriteEvent.OwnerUserId, cancellationToken);
        var candidates = await LibraryTrackAccess.Query(db, access).Where(item =>
            item.Protocol == favoriteEvent.Protocol &&
            item.BackendInstanceId == favoriteEvent.BackendInstanceId).ToListAsync(cancellationToken);
        return external == null
            ? candidates.Any(item => item.BackendItemId == favoriteEvent.ItemId)
            : candidates.Any(item => canonicalRecordingId != null && item.CanonicalRecordingId == canonicalRecordingId ||
                ProviderIdMatches(item.ProviderIdsJson, external.Value.Provider, external.Value.Id));
    }
}
