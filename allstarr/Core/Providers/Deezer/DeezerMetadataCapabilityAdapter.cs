using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services;

namespace allstarr.Core.Providers.Deezer;

/// <summary>
/// Keeps the current Deezer HTTP implementation in place while exposing it through the capability core.
/// Protocol controllers continue to use the legacy service until their adapter phase.
/// </summary>
public sealed class DeezerMetadataCapabilityAdapter : IProviderMetadataCapability
{
    public const string StableProviderId = "deezer";

    private readonly IConcreteMetadataService _legacy;

    public DeezerMetadataCapabilityAdapter(IConcreteMetadataService legacy)
    {
        _legacy = legacy;
    }

    public string ProviderId => StableProviderId;

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => _legacy.SearchSongsAsync(request.Query, request.Page.Limit, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context,
        ProviderTrackLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => _legacy.GetSongAsync(StableProviderId, request.Id.Value, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
        ProviderExecutionContext context,
        ProviderIsrcLookupRequest request) => ExecuteLookupAsync(
        context,
        expectedId: null,
        token => _legacy.FindSongByIsrcAsync(request.Isrc, token),
        MapTrack);

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => _legacy.SearchAlbumsAsync(request.Query, request.Page.Limit, token),
        MapAlbum);

    public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
        ProviderExecutionContext context,
        ProviderAlbumLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => _legacy.GetAlbumAsync(StableProviderId, request.Id.Value, token),
        MapAlbum);

    public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) => ExecutePageAsync(
        context,
        request.Page,
        token => _legacy.SearchArtistsAsync(request.Query, request.Page.Limit, token),
        MapArtist);

    public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
        ProviderExecutionContext context,
        ProviderArtistLookupRequest request) => ExecuteLookupAsync(
        context,
        request.Id,
        token => _legacy.GetArtistAsync(StableProviderId, request.Id.Value, token),
        MapArtist);

    public static ProviderRegistration CreateRegistration(
        DeezerMetadataCapabilityAdapter adapter,
        IProviderDownloadCapability? download = null,
        IProviderStreamingCapability? streaming = null) => new(
        new ProviderDescriptor(
            StableProviderId,
            "Deezer",
            "Public Deezer metadata through the existing Allstarr provider implementation.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "legacy-metadata-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Metadata,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks:
                    [
                        "searchTracks",
                        "getTrack",
                        "lookupByIsrc",
                        "searchAlbums",
                        "getAlbum",
                        "searchArtists",
                        "getArtist"
                    ]),
                streaming == null
                    ? LegacyLane(ProviderCapabilityKind.Streaming)
                    : new ProviderCapabilityDescriptor(
                        ProviderCapabilityKind.Streaming,
                        ProviderCapabilitySupportState.Supported,
                        ProviderAccountRequirement.Required,
                        compatibilityVersion: "1",
                        hooks: ["getStreamLease", "probeStream"],
                        allowedAccountScopes:
                        [
                            ProviderAccountScope.Global,
                            ProviderAccountScope.User,
                            ProviderAccountScope.Library
                        ]),
                download == null
                    ? LegacyLane(ProviderCapabilityKind.Download)
                    : new ProviderCapabilityDescriptor(
                        ProviderCapabilityKind.Download,
                        ProviderCapabilitySupportState.Supported,
                        ProviderAccountRequirement.Required,
                        compatibilityVersion: "1",
                        hooks: ["checkAvailability", "download"],
                        allowedAccountScopes:
                        [
                            ProviderAccountScope.Global,
                            ProviderAccountScope.User,
                            ProviderAccountScope.Library
                        ]),
                LegacyLane(ProviderCapabilityKind.Playlist),
                LegacyLane(ProviderCapabilityKind.Health)
            ],
            permissions: new ProviderPermissionDescriptor(
                networkOrigins:
                [
                    new Uri("https://api.deezer.com/"),
                    new Uri("https://media.deezer.com/"),
                    new Uri("https://www.deezer.com/")
                ],
                cache: true)),
        Implementations(adapter, download, streaming));

    private static IProviderCapability[] Implementations(
        IProviderMetadataCapability metadata,
        IProviderDownloadCapability? download,
        IProviderStreamingCapability? streaming)
    {
        var values = new List<IProviderCapability> { metadata };
        if (download != null) values.Add(download);
        if (streaming != null) values.Add(streaming);
        return values.ToArray();
    }

    private static ProviderCapabilityDescriptor LegacyLane(
        ProviderCapabilityKind capability) => new(
        capability,
        ProviderCapabilitySupportState.ConfiguredOnly,
        ProviderAccountRequirement.Required,
        compatibilityVersion: "legacy-seam-v1",
        allowedAccountScopes:
        [
            ProviderAccountScope.Global,
            ProviderAccountScope.User,
            ProviderAccountScope.Library
        ]);

    private async Task<ProviderOutcome<ProviderPage<TTarget>>> ExecutePageAsync<TLegacy, TTarget>(
        ProviderExecutionContext context,
        ProviderPageRequest page,
        Func<CancellationToken, Task<List<TLegacy>>> fetch,
        Func<TLegacy, TTarget> map)
        where TTarget : class
    {
        var contextFailure = ValidateContext(context);
        if (contextFailure != null)
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(contextFailure);
        }

        if (page.Cursor != null)
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(
                new ProviderError(ProviderErrorKind.NotSupported));
        }

        try
        {
            var values = await fetch(context.CancellationToken);
            var mapped = values.Select(map).ToArray();
            return ProviderOutcome<ProviderPage<TTarget>>.Success(new ProviderPage<TTarget>(
                StableProviderId,
                mapped,
                isPartial: values.Count >= page.Limit));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(
                new ProviderError(ProviderErrorKind.Canceled));
        }
        catch
        {
            return ProviderOutcome<ProviderPage<TTarget>>.Failure(
                new ProviderError(ProviderErrorKind.TransientFailure));
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
        {
            return ProviderOutcome<TTarget>.Failure(contextFailure);
        }

        if (expectedId != null &&
            !expectedId.ProviderId.Equals(StableProviderId, StringComparison.Ordinal))
        {
            return ProviderOutcome<TTarget>.Failure(new ProviderError(ProviderErrorKind.Forbidden));
        }

        try
        {
            var value = await fetch(context.CancellationToken);
            return value == null
                ? ProviderOutcome<TTarget>.Failure(new ProviderError(ProviderErrorKind.NotFound))
                : ProviderOutcome<TTarget>.Success(map(value));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<TTarget>.Failure(new ProviderError(ProviderErrorKind.Canceled));
        }
        catch
        {
            return ProviderOutcome<TTarget>.Failure(
                new ProviderError(ProviderErrorKind.TransientFailure));
        }
    }

    private static ProviderError? ValidateContext(ProviderExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.ProviderId.Equals(StableProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(StableProviderId))
        {
            return new ProviderError(ProviderErrorKind.Forbidden);
        }

        if (context.CancellationToken.IsCancellationRequested)
        {
            return new ProviderError(ProviderErrorKind.Canceled);
        }

        return context.IsExpired(DateTimeOffset.UtcNow)
            ? new ProviderError(ProviderErrorKind.CapabilityUnavailable)
            : null;
    }

    private static ProviderTrackMetadata MapTrack(Song song)
    {
        var trackId = ExternalId(
            ProviderResourceKind.Track,
            song.ExternalId,
            song.Id,
            "track");
        var names = song.Artists.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (names.Length == 0 && !string.IsNullOrWhiteSpace(song.Artist))
        {
            names = [song.Artist];
        }

        if (names.Length == 0)
        {
            throw new InvalidOperationException("The legacy Deezer track has no artist credit.");
        }

        var credits = names.Select((name, index) => new ProviderArtistCredit(
            name.Trim(),
            index < song.ArtistIds.Count && !string.IsNullOrWhiteSpace(song.ArtistIds[index])
                ? ExternalId(
                    ProviderResourceKind.Artist,
                    song.ArtistIds[index],
                    fallback: null,
                    "artist")
                : null));
        var albumId = string.IsNullOrWhiteSpace(song.AlbumId)
            ? null
            : ExternalId(ProviderResourceKind.Album, song.AlbumId, fallback: null, "album");
        return new ProviderTrackMetadata(
            trackId,
            song.Title,
            credits,
            albumId,
            string.IsNullOrWhiteSpace(song.Album) ? null : song.Album,
            song.Duration is > 0 ? TimeSpan.FromSeconds(song.Duration.Value) : null,
            string.IsNullOrWhiteSpace(song.Isrc) ? null : song.Isrc,
            ExplicitState(song.ExplicitContentLyrics),
            PublicArtwork(song.CoverArtUrlLarge ?? song.CoverArtUrl));
    }

    private static ProviderAlbumMetadata MapAlbum(Album album)
    {
        if (string.IsNullOrWhiteSpace(album.Artist))
        {
            throw new InvalidOperationException("The legacy Deezer album has no artist credit.");
        }

        var artistId = string.IsNullOrWhiteSpace(album.ArtistId)
            ? null
            : ExternalId(ProviderResourceKind.Artist, album.ArtistId, fallback: null, "artist");
        return new ProviderAlbumMetadata(
            ExternalId(ProviderResourceKind.Album, album.ExternalId, album.Id, "album"),
            album.Title,
            [new ProviderArtistCredit(album.Artist, artistId)],
            album.SongCount,
            PublicArtwork(album.CoverArtUrl));
    }

    private static ProviderArtistMetadata MapArtist(Artist artist) => new(
        ExternalId(ProviderResourceKind.Artist, artist.ExternalId, artist.Id, "artist"),
        artist.Name,
        PublicArtwork(artist.ImageUrl));

    private static ProviderExternalResourceId ExternalId(
        ProviderResourceKind kind,
        string? preferred,
        string? fallback,
        string label)
    {
        var value = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"The legacy Deezer {label} has no provider ID.");
        }

        var resourceLabel = kind switch
        {
            ProviderResourceKind.Track => "song",
            ProviderResourceKind.Album => "album",
            ProviderResourceKind.Artist => "artist",
            ProviderResourceKind.Playlist => "playlist",
            _ => string.Empty
        };
        var typedCompatibilityPrefix = $"ext-{StableProviderId}-{resourceLabel}-";
        var compatibilityPrefix = $"ext-{StableProviderId}-";
        if (!string.IsNullOrEmpty(resourceLabel) &&
            value.StartsWith(typedCompatibilityPrefix, StringComparison.Ordinal))
        {
            value = value[typedCompatibilityPrefix.Length..];
        }
        else if (value.StartsWith(compatibilityPrefix, StringComparison.Ordinal))
        {
            value = value[compatibilityPrefix.Length..];
        }

        return new ProviderExternalResourceId(StableProviderId, kind, value);
    }

    private static ProviderArtworkReference? PublicArtwork(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? new ProviderArtworkReference(publicUri: uri)
            : null;
    }

    private static bool? ExplicitState(int? value) => value switch
    {
        1 => true,
        0 or 3 => false,
        _ => null
    };
}
