using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;

namespace allstarr.Core.Providers;

public class CatalogPlaylistCapability(
    string providerId,
    ProviderCatalogMetadata metadata) : IProviderPlaylistCapability
{
    public string ProviderId { get; } = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        ProviderExecutionContext context,
        ProviderUserPlaylistsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = Validate(context);
        return Task.FromResult(failure == null
            ? ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.NotSupported))
            : ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(failure));
    }

    public async Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
        ProviderExecutionContext context,
        ProviderPlaylistSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = Validate(context);
        if (failure != null) return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(failure);
        if (request.Page.Cursor != null)
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.NotSupported));

        try
        {
            var playlists = await metadata.SearchPlaylistsAsync(
                request.Query, request.Page.Limit, context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(
                ProviderId,
                playlists.Select(playlist => MapPlaylist(playlist, Revision(playlist))),
                isPartial: playlists.Count >= request.Page.Limit));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(ProviderCatalogMetadata.HttpError(exception));
        }
        catch
        {
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    public async Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        ProviderExecutionContext context,
        ProviderPlaylistTracksRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = Validate(context);
        if (failure != null) return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(failure);
        if (!request.PlaylistId.ProviderId.Equals(ProviderId, StringComparison.Ordinal))
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.Forbidden));
        if (!int.TryParse(request.Page.Cursor ?? "0", NumberStyles.None, CultureInfo.InvariantCulture,
                out var offset))
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.NotSupported));

        try
        {
            var playlistTask = metadata.GetPlaylistAsync(
                ProviderId, request.PlaylistId.Value, context.CancellationToken);
            var tracksTask = metadata.GetPlaylistTracksAsync(
                ProviderId, request.PlaylistId.Value, context.CancellationToken);
            await Task.WhenAll(playlistTask, tracksTask);
            context.CancellationToken.ThrowIfCancellationRequested();
            var playlist = await playlistTask;
            if (playlist == null)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.NotFound));
            var tracks = await tracksTask;
            var mapped = tracks.Select(metadata.MapTrack).ToArray();
            var revision = Revision(playlist);
            if (request.ExpectedRevision != null && request.ExpectedRevision != revision)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));

            var items = mapped.Skip(offset).Take(request.Page.Limit)
                .Select((track, index) => new ProviderPlaylistTrack(offset + index, track.Id, metadata: track))
                .ToArray();
            var nextOffset = offset + items.Length;
            var nextCursor = nextOffset < mapped.Length
                ? nextOffset.ToString(CultureInfo.InvariantCulture)
                : null;
            return ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                MapPlaylist(playlist, revision),
                new ProviderPage<ProviderPlaylistTrack>(
                    ProviderId, items, nextCursor, nextCursor != null, revision)));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(ProviderCatalogMetadata.HttpError(exception));
        }
        catch
        {
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    private ProviderError? Validate(ProviderExecutionContext context)
    {
        var failure = metadata.ValidateContext(context);
        return failure ?? (context.Account == null
            ? new ProviderError(ProviderErrorKind.AccountNeedsConfiguration)
            : null);
    }

    private ProviderPlaylistSummary MapPlaylist(ExternalPlaylist playlist, string revision)
    {
        var owner = string.IsNullOrWhiteSpace(playlist.CuratorName)
            ? $"{ProviderId}:unknown"
            : playlist.CuratorName.Trim();
        return new(
            metadata.ExternalId(ProviderResourceKind.Playlist, playlist.ExternalId, playlist.Id, "playlist"),
            playlist.Name,
            new ProviderPlaylistOwner(owner, playlist.CuratorName),
            revision,
            playlist.Description,
            ProviderCatalogMetadata.PublicArtwork(playlist.CoverUrl),
            playlist.TrackCount,
            durationSeconds: playlist.Duration,
            createdDate: playlist.CreatedDate);
    }

    private static string Revision(ExternalPlaylist playlist) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            playlist.ExternalId,
            playlist.Name,
            playlist.Description,
            playlist.CuratorName,
            playlist.TrackCount,
            playlist.Duration,
            playlist.CoverUrl,
            playlist.CreatedDate
        })));
}
