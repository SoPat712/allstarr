using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services;

namespace allstarr.Core.Providers;

public abstract class ConcreteMetadataCapabilityAdapter(
    string providerId,
    IConcreteMetadataService legacy) : IProviderMetadataCapability
{
    public string ProviderId { get; } = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => legacy.SearchSongsAsync(request.Query, request.Page.Limit, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context,
        ProviderTrackLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => legacy.GetSongAsync(ProviderId, request.Id.Value, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
        ProviderExecutionContext context,
        ProviderIsrcLookupRequest request) => ExecuteLookupAsync(
        context,
        expectedId: null,
        token => legacy.FindSongByIsrcAsync(request.Isrc, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => legacy.SearchAlbumsAsync(request.Query, request.Page.Limit, token),
        MapAlbum);

    public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
        ProviderExecutionContext context,
        ProviderAlbumLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => legacy.GetAlbumAsync(ProviderId, request.Id.Value, token),
        MapAlbum);

    public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => legacy.SearchArtistsAsync(request.Query, request.Page.Limit, token),
        MapArtist);

    public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
        ProviderExecutionContext context,
        ProviderArtistLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => legacy.GetArtistAsync(ProviderId, request.Id.Value, token),
        MapArtist);

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> GetArtistAlbumsAsync(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request) => ExecuteCollectionPageAsync(
        context,
        request,
        token => legacy.GetArtistAlbumsAsync(ProviderId, request.Id.Value, token),
        MapAlbum);

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetArtistTracksAsync(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request) => ExecuteCollectionPageAsync(
        context,
        request,
        token => legacy.GetArtistTracksAsync(ProviderId, request.Id.Value, token),
        MapTrack);

    private async Task<ProviderOutcome<ProviderPage<TTarget>>> ExecutePageAsync<TLegacy, TTarget>(
        ProviderExecutionContext context,
        ProviderPageRequest page,
        Func<CancellationToken, Task<List<TLegacy>>> fetch,
        Func<TLegacy, TTarget> map)
        where TTarget : class
    {
        var contextFailure = ValidateContext(context);
        if (contextFailure != null)
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(contextFailure);
        if (page.Cursor != null)
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.NotSupported));

        try
        {
            var values = await fetch(context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
            return ProviderOutcome<ProviderPage<TTarget>>.Success(new(
                ProviderId, values.Select(map), isPartial: values.Count >= page.Limit));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    private async Task<ProviderOutcome<TTarget>> ExecuteLookupAsync<TLegacy, TTarget>(
        ProviderExecutionContext context,
        ProviderExternalResourceId? expectedId,
        Func<CancellationToken, Task<TLegacy?>> fetch,
        Func<TLegacy, TTarget> map)
        where TLegacy : class
        where TTarget : class
    {
        var contextFailure = ValidateContext(context);
        if (contextFailure != null)
            return ProviderOutcome<TTarget>.Failure(contextFailure);
        if (expectedId != null && !expectedId.ProviderId.Equals(ProviderId, StringComparison.Ordinal))
            return ProviderOutcome<TTarget>.Failure(new(ProviderErrorKind.Forbidden));

        try
        {
            var value = await fetch(context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
            return value == null
                ? ProviderOutcome<TTarget>.Failure(new(ProviderErrorKind.NotFound))
                : ProviderOutcome<TTarget>.Success(map(value));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<TTarget>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch
        {
            return ProviderOutcome<TTarget>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    private async Task<ProviderOutcome<ProviderPage<TTarget>>> ExecuteCollectionPageAsync<TLegacy, TTarget>(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request,
        Func<CancellationToken, Task<List<TLegacy>>> fetch,
        Func<TLegacy, TTarget> map)
        where TTarget : class
    {
        var contextFailure = ValidateContext(context);
        if (contextFailure != null)
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(contextFailure);
        if (!request.Id.ProviderId.Equals(ProviderId, StringComparison.Ordinal))
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.Forbidden));
        if (request.ExpectedSnapshotVersion != null)
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.NotSupported));
        if (!int.TryParse(request.Page.Cursor ?? "0", NumberStyles.None, CultureInfo.InvariantCulture,
                out var offset))
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.NotSupported));

        try
        {
            var values = await fetch(context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
            var items = values.Skip(offset).Take(request.Page.Limit).Select(map).ToArray();
            var nextOffset = offset + items.Length;
            var nextCursor = nextOffset < values.Count
                ? nextOffset.ToString(CultureInfo.InvariantCulture)
                : null;
            return ProviderOutcome<ProviderPage<TTarget>>.Success(new(
                ProviderId, items, nextCursor, nextCursor != null));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    internal ProviderError? ValidateContext(ProviderExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.ProviderId.Equals(ProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(ProviderId))
            return new(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested)
            return new(ProviderErrorKind.Canceled);
        return context.IsExpired(DateTimeOffset.UtcNow)
            ? new(ProviderErrorKind.CapabilityUnavailable)
            : null;
    }

    internal ProviderTrackMetadata MapTrack(Song song)
    {
        var trackId = ExternalId(ProviderResourceKind.Track, song.ExternalId, song.Id, "track");
        var names = song.Artists.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (names.Length == 0 && !string.IsNullOrWhiteSpace(song.Artist)) names = [song.Artist];
        if (names.Length == 0)
            throw new InvalidOperationException($"The {ProviderId} track has no artist credit.");

        var credits = names.Select((name, index) => new ProviderArtistCredit(
            name.Trim(),
            index < song.ArtistIds.Count && !string.IsNullOrWhiteSpace(song.ArtistIds[index])
                ? ExternalId(ProviderResourceKind.Artist, song.ArtistIds[index], null, "artist")
                : null));
        var albumId = string.IsNullOrWhiteSpace(song.AlbumId)
            ? null
            : ExternalId(ProviderResourceKind.Album, song.AlbumId, null, "album");
        return new(
            trackId,
            song.Title,
            credits,
            albumId,
            string.IsNullOrWhiteSpace(song.Album) ? null : song.Album,
            song.Duration is > 0 ? TimeSpan.FromSeconds(song.Duration.Value) : null,
            string.IsNullOrWhiteSpace(song.Isrc) ? null : song.Isrc,
            ExplicitState(song.ExplicitContentLyrics),
            PublicArtwork(song.CoverArtUrlLarge ?? song.CoverArtUrl),
            trackNumber: song.Track,
            discNumber: song.DiscNumber,
            totalTracks: song.TotalTracks,
            year: song.Year,
            genre: song.Genre,
            bpm: song.Bpm,
            spotifyId: song.SpotifyId,
            releaseDate: song.ReleaseDate,
            albumArtist: song.AlbumArtist,
            composer: song.Composer,
            label: song.Label,
            copyright: song.Copyright,
            contributors: song.Contributors,
            explicitContentLyrics: song.ExplicitContentLyrics);
    }

    private ProviderAlbumMetadata MapAlbum(Album album)
    {
        if (string.IsNullOrWhiteSpace(album.Artist))
            throw new InvalidOperationException($"The {ProviderId} album has no artist credit.");
        var artistId = string.IsNullOrWhiteSpace(album.ArtistId)
            ? null
            : ExternalId(ProviderResourceKind.Artist, album.ArtistId, null, "artist");
        return new(
            ExternalId(ProviderResourceKind.Album, album.ExternalId, album.Id, "album"),
            album.Title,
            [new ProviderArtistCredit(album.Artist, artistId)],
            album.SongCount,
            PublicArtwork(album.CoverArtUrl),
            year: album.Year,
            genre: album.Genre,
            tracks: album.Songs.Select(MapTrack));
    }

    private ProviderArtistMetadata MapArtist(Artist artist) => new(
        ExternalId(ProviderResourceKind.Artist, artist.ExternalId, artist.Id, "artist"),
        artist.Name,
        PublicArtwork(artist.ImageUrl));

    internal ProviderExternalResourceId ExternalId(
        ProviderResourceKind kind,
        string? preferred,
        string? fallback,
        string label)
    {
        var value = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"The {ProviderId} {label} has no provider ID.");

        var resourceLabel = kind switch
        {
            ProviderResourceKind.Track => "song",
            ProviderResourceKind.Album => "album",
            ProviderResourceKind.Artist => "artist",
            ProviderResourceKind.Playlist => "playlist",
            _ => string.Empty
        };
        var typedPrefix = $"ext-{ProviderId}-{resourceLabel}-";
        var prefix = $"ext-{ProviderId}-";
        if (!string.IsNullOrEmpty(resourceLabel) && value.StartsWith(typedPrefix, StringComparison.Ordinal))
            value = value[typedPrefix.Length..];
        else if (value.StartsWith(prefix, StringComparison.Ordinal))
            value = value[prefix.Length..];
        return new(ProviderId, kind, value);
    }

    internal static ProviderArtworkReference? PublicArtwork(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? new(publicUri: uri)
            : null;

    private static bool? ExplicitState(int? value) => value switch
    {
        1 => true,
        0 or 3 => false,
        _ => null
    };
}

public class ConcretePlaylistCapabilityAdapter(
    string providerId,
    IConcreteMetadataService legacy,
    ConcreteMetadataCapabilityAdapter metadata) : IProviderPlaylistCapability
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
            var playlists = await legacy.SearchPlaylistsAsync(
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
            var playlistTask = legacy.GetPlaylistAsync(
                ProviderId, request.PlaylistId.Value, context.CancellationToken);
            var tracksTask = legacy.GetPlaylistTracksAsync(
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
            ConcreteMetadataCapabilityAdapter.PublicArtwork(playlist.CoverUrl),
            playlist.TrackCount,
            durationSeconds: playlist.Duration,
            createdDate: playlist.CreatedDate);
    }

    // ponytail: concrete readers expose no checksum/ETag; use the full playlist summary until they do.
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
