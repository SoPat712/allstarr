using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed partial class FavoriteActionPipeline
{
    private async Task<string?> CaptureDisplayAsync(FavoriteMutationRequest request, string itemId, Guid userId,
        CancellationToken cancellationToken)
    {
        if (metadata == null || request.Operation != FavoriteOperation.Favorite ||
            FavoriteDisplaySnapshot.Resource(itemId) is not { } resource) return null;
        var execution = request.ExecutionContext;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (!await db.Users.AnyAsync(item => item.Id == userId && item.Enabled &&
                item.BackendType == execution.Principal!.BackendType && item.BackendInstanceId == execution.BackendInstanceId &&
                item.BackendPrincipalId == execution.VerifiedBackendPrincipalId, cancellationToken))
                throw new UnauthorizedAccessException("The favorite principal is no longer authorized.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, execution.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var deadline = clock.UtcNow.AddSeconds(2);
        var lookup = new ProtocolExecutionContext(execution.Protocol, execution.BackendInstanceId,
            execution.VerifiedBackendPrincipalId, execution.Principal, execution.CorrelationId,
            execution.Deadline < deadline ? execution.Deadline : deadline, timeout.Token, execution.Client)
        { Policy = execution.Policy, RequestedItemId = itemId };
        try
        {
            FavoriteDisplaySnapshot? display = null;
            switch (resource.ResourceKind)
            {
                case ProviderResourceKind.Track:
                    var song = await metadata.GetSongAsync(lookup, resource.ProviderId, resource.Value);
                    if (song?.Id == itemId)
                    {
                        var duration = song.PreciseDuration?.TotalMilliseconds ?? song.Duration * 1000d;
                        display = new(1, itemId, "song", song.Title,
                            Names(song.Artists is { Count: > 0 } ? song.Artists : [song.Artist]),
                            FavoriteDisplaySnapshot.Text(song.Album) ? song.Album : null,
                            duration is >= 0 and <= 86_400_000 ? (long)Math.Round(duration.Value) : null);
                    }
                    break;
                case ProviderResourceKind.Album:
                    var album = await metadata.GetAlbumAsync(lookup, resource.ProviderId, resource.Value);
                    if (album?.Id == itemId)
                        display = new(1, itemId, "album", album.Title, Names([album.Artist]), Year: album.Year,
                            Count: album.SongCount);
                    break;
                case ProviderResourceKind.Artist:
                    var artist = await metadata.GetArtistAsync(lookup, resource.ProviderId, resource.Value);
                    if (artist?.Id == itemId)
                        display = new(1, itemId, "artist", artist.Name, [], Count: artist.AlbumCount);
                    break;
                case ProviderResourceKind.Playlist:
                    var playlist = await metadata.GetPlaylistAsync(lookup, resource.ProviderId, resource.Value);
                    if (playlist?.Id == itemId)
                        display = new(1, itemId, "playlist", playlist.Name, Names([playlist.CuratorName ?? ""]),
                            DurationMilliseconds: (long)playlist.Duration * 1000, Count: playlist.TrackCount);
                    break;
            }
            if (display?.Valid() != true) return null;
            timeout.Token.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(display, FavoriteDisplaySnapshot.JsonOptions);
            return json.Length <= 32768 ? json : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !execution.CancellationToken.IsCancellationRequested)
        { return null; }
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
    }

    private static string[] Names(IEnumerable<string> names) => names.Where(FavoriteDisplaySnapshot.Text)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
}
