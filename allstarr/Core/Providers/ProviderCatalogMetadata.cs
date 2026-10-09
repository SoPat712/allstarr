using System.Globalization;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Core.Providers;

public abstract class ProviderCatalogMetadata(string providerId) : TrackParserBase, IProviderMetadataCapability
{
    public string ProviderId { get; } = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

    public abstract Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    public abstract Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    public abstract Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    public abstract Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    public abstract Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default);
    public abstract Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    public abstract Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    public abstract Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    public abstract Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    public abstract Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    public abstract Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    public abstract Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    public async Task<ProviderOutcome<ProviderArtworkReference>> GetPlaylistArtworkAsync(
        ProviderExecutionContext context,
        ProviderExternalResourceId playlistId)
    {
        var failure = ValidateContext(context);
        if (failure != null) return ProviderOutcome<ProviderArtworkReference>.Failure(failure);
        playlistId.RequireOwner(ProviderId, ProviderResourceKind.Playlist);
        try
        {
            var playlist = await GetPlaylistAsync(ProviderId, playlistId.Value, context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
            var artwork = PublicArtwork(playlist?.CoverUrl);
            return artwork == null
                ? ProviderOutcome<ProviderArtworkReference>.Failure(new(ProviderErrorKind.NotFound))
                : ProviderOutcome<ProviderArtworkReference>.Success(artwork);
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderArtworkReference>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderArtworkReference>.Failure(ProviderCatalogMetadata.HttpError(exception));
        }
        catch
        {
            return ProviderOutcome<ProviderArtworkReference>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => SearchSongsAsync(request.Query, request.Page.Limit, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context,
        ProviderTrackLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => GetSongAsync(ProviderId, request.Id.Value, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
        ProviderExecutionContext context,
        ProviderIsrcLookupRequest request) => ExecuteLookupAsync(
        context,
        expectedId: null,
        token => FindSongByIsrcAsync(request.Isrc, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => SearchAlbumsAsync(request.Query, request.Page.Limit, token),
        MapAlbum);

    public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
        ProviderExecutionContext context,
        ProviderAlbumLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => GetAlbumAsync(ProviderId, request.Id.Value, token),
        MapAlbum);

    public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => SearchArtistsAsync(request.Query, request.Page.Limit, token),
        MapArtist);

    public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
        ProviderExecutionContext context,
        ProviderArtistLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => GetArtistAsync(ProviderId, request.Id.Value, token),
        MapArtist);

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> GetArtistAlbumsAsync(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request) => ExecuteCollectionPageAsync(
        context,
        request,
        token => GetArtistAlbumsAsync(ProviderId, request.Id.Value, token),
        MapAlbum);

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetArtistTracksAsync(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request) => ExecuteCollectionPageAsync(
        context,
        request,
        token => GetArtistTracksAsync(ProviderId, request.Id.Value, token),
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
                ProviderId, MapValid(values, map), isPartial: values.Count >= page.Limit));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(ProviderCatalogMetadata.HttpError(exception));
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
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<TTarget>.Failure(ProviderCatalogMetadata.HttpError(exception));
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
            var pageValues = values.Skip(offset).Take(request.Page.Limit).ToArray();
            var items = MapValid(pageValues, map);
            var nextOffset = offset + pageValues.Length;
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
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(ProviderCatalogMetadata.HttpError(exception));
        }
        catch
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    private static IReadOnlyList<TTarget> MapValid<TLegacy, TTarget>(
        IEnumerable<TLegacy> values,
        Func<TLegacy, TTarget> map)
    {
        var mapped = new List<TTarget>();
        foreach (var value in values)
        {
            try { mapped.Add(map(value)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        return mapped;
    }

    internal static ProviderError HttpError(HttpRequestException exception) => exception.StatusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => new(ProviderErrorKind.Unauthorized),
        System.Net.HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        System.Net.HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)),
        >= System.Net.HttpStatusCode.InternalServerError or null => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

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

        var credits = names.Select((name, index) =>
        {
            var artistId = index < song.ArtistIds.Count && !string.IsNullOrWhiteSpace(song.ArtistIds[index])
                ? song.ArtistIds[index]
                : index == 0 ? song.ArtistId : null;
            return new ProviderArtistCredit(
                name.Trim(),
                string.IsNullOrWhiteSpace(artistId)
                    ? null
                    : ExternalId(ProviderResourceKind.Artist, artistId, null, "artist"));
        });
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
            explicitContentLyrics: song.ExplicitContentLyrics,
            bitrate: song.Bitrate);
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
