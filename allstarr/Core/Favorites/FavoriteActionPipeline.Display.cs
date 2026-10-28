using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed partial class FavoriteActionPipeline
{
    internal async Task<string?> CaptureDisplayAsync(FavoriteMutationRequest request, string itemId, Guid userId,
        CancellationToken cancellationToken)
    {
        if (request.Operation != FavoriteOperation.Favorite ||
            FavoriteDisplaySnapshot.Resource(itemId) is not { } resource) return null;
        var execution = request.ExecutionContext;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (!await db.Users.AnyAsync(item => item.Id == userId && item.Enabled &&
                item.BackendType == execution.Principal!.BackendType && item.BackendInstanceId == execution.BackendInstanceId &&
                item.BackendPrincipalId == execution.VerifiedBackendPrincipalId, cancellationToken))
                throw new UnauthorizedAccessException("The favorite principal is no longer authorized.");
        }
        if (resource.ResourceKind == ProviderResourceKind.Track && keptFiles != null)
        {
            var kept = await keptFiles.FindSongAsync(execution.RequireActor(), itemId, cancellationToken);
            if (kept != null && SerializeDisplay(SongDisplay(itemId, kept)) is { } saved) return saved;
        }
        if (metadata == null) return null;
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
                    var song = await metadata.GetSongAsync(lookup, resource.ProviderId, resource.Value).WaitAsync(timeout.Token);
                    if (song != null && await SameSongAsync(lookup, resource, itemId, song.Id, timeout.Token))
                        display = SongDisplay(itemId, song);
                    break;
                case ProviderResourceKind.Album:
                    var album = await metadata.GetAlbumAsync(lookup, resource.ProviderId, resource.Value).WaitAsync(timeout.Token);
                    if (album != null && SameResource(resource, album.Id))
                        display = new(1, itemId, "album", album.Title, Names([album.Artist]), Year: album.Year,
                            Count: album.SongCount)
                        {
                            ArtistIds = Names([album.ArtistId ?? ""]),
                            AlbumArtistIds = Names([album.ArtistId ?? ""]),
                            Genres = Names([album.Genre ?? ""]),
                            IsExplicit = album.IsExplicit,
                            Upc = album.Upc,
                            Edition = album.Edition,
                            ProviderIds = new(album.ProviderIds, StringComparer.Ordinal)
                        };
                    break;
                case ProviderResourceKind.Artist:
                    var artist = await metadata.GetArtistAsync(lookup, resource.ProviderId, resource.Value).WaitAsync(timeout.Token);
                    if (artist != null && SameResource(resource, artist.Id))
                        display = new(1, itemId, "artist", artist.Name, [], Count: artist.AlbumCount);
                    break;
                case ProviderResourceKind.Playlist:
                    var playlist = await metadata.GetPlaylistAsync(lookup, resource.ProviderId, resource.Value).WaitAsync(timeout.Token);
                    if (playlist != null && SameResource(resource, playlist.Id))
                        display = new(1, itemId, "playlist", playlist.Name, Names([playlist.CuratorName ?? ""]),
                            DurationMilliseconds: (long)playlist.Duration * 1000, Count: playlist.TrackCount);
                    break;
            }
            if (display?.Valid() != true) return null;
            timeout.Token.ThrowIfCancellationRequested();
            return SerializeDisplay(display);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !execution.CancellationToken.IsCancellationRequested)
        { return null; }
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
    }

    private async Task<bool> SameSongAsync(ProtocolExecutionContext execution, ProviderExternalResourceId resource,
        string itemId, string returnedId, CancellationToken cancellationToken)
    {
        if (SameResource(resource, returnedId)) return true;
        if (keptFiles == null) return false;
        var actor = execution.RequireActor();
        var requested = await keptFiles.ResolveKeptRecordingIdAsync(actor, itemId, cancellationToken);
        return requested != null && requested == await keptFiles.ResolveKeptRecordingIdAsync(actor, returnedId, cancellationToken);
    }

    private static bool SameResource(ProviderExternalResourceId resource, string itemId) =>
        FavoriteDisplaySnapshot.Resource(itemId) is { } returned && returned.ProviderId == resource.ProviderId &&
        returned.ResourceKind == resource.ResourceKind && returned.Value == resource.Value;

    private static FavoriteDisplaySnapshot SongDisplay(string itemId, Song song)
    {
        var duration = song.PreciseDuration?.TotalMilliseconds ?? song.Duration * 1000d;
        return new(1, itemId, "song", song.Title,
            Names(song.Artists is { Count: > 0 } ? song.Artists : [song.Artist]),
            FavoriteDisplaySnapshot.Text(song.Album) ? song.Album : null,
            duration is >= 0 and <= 86_400_000 ? (long)Math.Round(duration.Value) : null, song.Year)
        {
            ArtistIds = Names(song.ArtistIds.Prepend(song.ArtistId ?? "")),
            AlbumArtists = Names(song.AlbumArtists.Count > 0 ? song.AlbumArtists.Select(artist => artist.Name) : [song.AlbumArtist ?? song.Artist]),
            AlbumArtistIds = Names(song.AlbumArtists.Count > 0 ? song.AlbumArtists.Select(artist => artist.Id) : string.IsNullOrWhiteSpace(song.AlbumArtist) ||
                string.Equals(song.AlbumArtist, song.Artist, StringComparison.OrdinalIgnoreCase)
                ? [song.ArtistId ?? ""]
                : song.Artists.Select((name, index) => string.Equals(name, song.AlbumArtist, StringComparison.OrdinalIgnoreCase) &&
                    index < song.ArtistIds.Count ? song.ArtistIds[index] : "")),
            AlbumId = FavoriteDisplaySnapshot.Text(song.AlbumId) ? song.AlbumId : null,
            Genres = Names([song.Genre ?? ""]),
            IsExplicit = song.ExplicitContentLyrics switch { 1 => true, 0 or 3 => false, _ => null },
            Upc = song.Upc
        };
    }

    private static string? SerializeDisplay(FavoriteDisplaySnapshot display)
    {
        if (!display.Valid()) return null;
        var json = JsonSerializer.Serialize(display, FavoriteDisplaySnapshot.JsonOptions);
        return json.Length <= 32768 ? json : null;
    }

    private static string[] Names(IEnumerable<string> names) => names.Where(FavoriteDisplaySnapshot.Text)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
}
