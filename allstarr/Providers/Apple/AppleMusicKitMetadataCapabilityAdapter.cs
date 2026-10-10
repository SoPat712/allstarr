using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;

namespace allstarr.Core.Providers.AppleMusicKit;

public sealed class AppleMusicKitMetadataCapabilityAdapter : IProviderMetadataCapability
{
    private readonly AppleMusicClient _client;

    public AppleMusicKitMetadataCapabilityAdapter(AppleMusicClient client) => _client = client;

    public string ProviderId => AppleMusicKitPlaylistCapabilityAdapter.StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) => SearchAsync(
            context, request, "songs", MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context, ProviderTrackLookupRequest request) => LookupAsync(
            context, request.Id, ProviderResourceKind.Track, "songs", request.ExpectedSnapshotVersion, MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
        ProviderExecutionContext context, ProviderIsrcLookupRequest request) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        var result = await SendAsync(credential, $"v1/catalog/{storefront}/songs?filter[isrc]=" + Uri.EscapeDataString(request.Isrc), ct);
        if (!result.Outcome.IsSuccess) return ProviderOutcome<ProviderTrackMetadata>.Failure(result.Outcome.Error!);
        using var document = JsonDocument.Parse(result.Body!);
        var item = Data(document.RootElement).FirstOrDefault();
        return item.ValueKind == JsonValueKind.Undefined
            ? ProviderOutcome<ProviderTrackMetadata>.Failure(new(ProviderErrorKind.NotFound))
            : ProviderOutcome<ProviderTrackMetadata>.Success(MapTrack(item, result.ETag));
    });

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) => SearchAsync(
            context, request, "albums", MapAlbum);

    public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
        ProviderExecutionContext context, ProviderAlbumLookupRequest request) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        context.RequireResourceOwner(request.Id, ProviderResourceKind.Album);
        var root = IsLibrary(request.Id.Value) ? "v1/me/library" : $"v1/catalog/{storefront}";
        var path = $"{root}/albums/{Uri.EscapeDataString(request.Id.Value)}";
        var result = await SendAsync(credential, path, ct);
        if (!result.Outcome.IsSuccess) return ProviderOutcome<ProviderAlbumMetadata>.Failure(result.Outcome.Error!);
        if (request.ExpectedSnapshotVersion != null && request.ExpectedSnapshotVersion != result.ETag)
            return ProviderOutcome<ProviderAlbumMetadata>.Failure(new(ProviderErrorKind.PermanentFailure));
        using var document = JsonDocument.Parse(result.Body!);
        var item = Data(document.RootElement).FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined)
            return ProviderOutcome<ProviderAlbumMetadata>.Failure(new(ProviderErrorKind.NotFound));
        var album = MapAlbum(item, result.ETag);
        var tracks = new List<ProviderTrackMetadata>();
        for (var page = 0; page < 200; page++)
        {
            var response = await SendAsync(credential, $"{path}/tracks?limit=100&offset={tracks.Count}", ct);
            if (!response.Outcome.IsSuccess) return ProviderOutcome<ProviderAlbumMetadata>.Failure(response.Outcome.Error!);
            using var trackDocument = JsonDocument.Parse(response.Body!);
            var entries = Data(trackDocument.RootElement).ToArray();
            tracks.AddRange(entries.Select(track => MapTrack(track, response.ETag)));
            if (!HasNext(trackDocument.RootElement))
                return ProviderOutcome<ProviderAlbumMetadata>.Success(new(album.Id, album.Title, album.Artists,
                    album.TrackCount, album.Artwork, album.SnapshotVersion, tracks: tracks));
            if (entries.Length == 0) break;
        }
        return ProviderOutcome<ProviderAlbumMetadata>.Failure(ProviderError.CompatibilityContractChanged());
    }, IsLibrary(request.Id.Value));

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> GetArtistAlbumsAsync(
        ProviderExecutionContext context, ProviderArtistItemsRequest request) => ArtistItemsAsync(context, request, "albums", MapAlbum);

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetArtistTracksAsync(
        ProviderExecutionContext context, ProviderArtistItemsRequest request) => ArtistItemsAsync(context, request, "view/top-songs", MapTrack);

    private Task<ProviderOutcome<ProviderPage<T>>> ArtistItemsAsync<T>(ProviderExecutionContext context,
        ProviderArtistItemsRequest request, string relationship, Func<JsonElement, string?, T> map) where T : class =>
        ExecuteAsync(context, async (credential, storefront, ct) =>
        {
            context.RequireResourceOwner(request.Id, ProviderResourceKind.Artist);
            if (!TryOffset(request.Page.Cursor, out var offset))
                return ProviderOutcome<ProviderPage<T>>.Failure(new(ProviderErrorKind.PermanentFailure));
            var root = IsLibrary(request.Id.Value) ? "v1/me/library" : $"v1/catalog/{storefront}";
            var result = await SendAsync(credential,
                $"{root}/artists/{Uri.EscapeDataString(request.Id.Value)}/{relationship}?limit={Math.Clamp(request.Page.Limit, 1, 100)}&offset={offset}", ct);
            if (!result.Outcome.IsSuccess) return ProviderOutcome<ProviderPage<T>>.Failure(result.Outcome.Error!);
            if (request.ExpectedSnapshotVersion != null && request.ExpectedSnapshotVersion != result.ETag)
                return ProviderOutcome<ProviderPage<T>>.Failure(new(ProviderErrorKind.PermanentFailure));
            using var document = JsonDocument.Parse(result.Body!);
            var entries = Data(document.RootElement).Select(item => map(item, result.ETag)).ToArray();
            if (HasNext(document.RootElement) && entries.Length == 0)
                return ProviderOutcome<ProviderPage<T>>.Failure(ProviderError.CompatibilityContractChanged());
            var next = HasNext(document.RootElement) ? (offset + entries.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return ProviderOutcome<ProviderPage<T>>.Success(new(ProviderId, entries, next, next != null, result.ETag));
        }, IsLibrary(request.Id.Value));

    public Task<ProviderOutcome<ProviderArtworkReference>> GetPlaylistArtworkAsync(
        ProviderExecutionContext context, ProviderExternalResourceId playlistId) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        context.RequireResourceOwner(playlistId, ProviderResourceKind.Playlist);
        var root = IsLibrary(playlistId.Value) ? "v1/me/library" : $"v1/catalog/{storefront}";
        var response = await SendAsync(credential, $"{root}/playlists/{Uri.EscapeDataString(playlistId.Value)}", ct);
        if (!response.Outcome.IsSuccess) return ProviderOutcome<ProviderArtworkReference>.Failure(response.Outcome.Error!);
        using var document = JsonDocument.Parse(response.Body!);
        var item = Data(document.RootElement).FirstOrDefault();
        var artwork = item.ValueKind == JsonValueKind.Object ? Artwork(playlistId, RequiredObject(item, "attributes"), response.ETag) : null;
        return artwork?.PublicUri == null ? ProviderOutcome<ProviderArtworkReference>.Failure(new(ProviderErrorKind.NotFound)) :
            ProviderOutcome<ProviderArtworkReference>.Success(artwork);
    }, IsLibrary(playlistId.Value));

    public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) => SearchAsync(
            context, request, "artists", MapArtist);

    public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
        ProviderExecutionContext context, ProviderArtistLookupRequest request) => LookupAsync(
            context, request.Id, ProviderResourceKind.Artist, "artists", request.ExpectedSnapshotVersion, MapArtist);

    private async Task<ProviderOutcome<ProviderPage<T>>> SearchAsync<T>(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request,
        string resourceType,
        Func<JsonElement, string?, T> map) where T : class
    {
        if (!TryOffset(request.Page.Cursor, out var offset))
            return ProviderOutcome<ProviderPage<T>>.Failure(new(ProviderErrorKind.PermanentFailure));

        return await ExecuteAsync(context, async (credential, storefront, ct) =>
        {
            var relative = $"v1/catalog/{storefront}/search?term=" + Uri.EscapeDataString(request.Query) +
                           "&types=" + resourceType +
                           "&limit=" + Math.Clamp(request.Page.Limit, 1, 25).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                           "&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var result = await SendAsync(credential, relative, ct);
            if (!result.Outcome.IsSuccess)
                return ProviderOutcome<ProviderPage<T>>.Failure(result.Outcome.Error!);

            try
            {
                using var document = JsonDocument.Parse(result.Body!);
                var container = SearchContainer(document.RootElement, resourceType);
                var items = Data(container).Select(item => map(item, result.ETag)).ToArray();
                if (HasNext(container) && items.Length == 0)
                    return ProviderOutcome<ProviderPage<T>>.Failure(ProviderError.CompatibilityContractChanged());
                var next = HasNext(container)
                    ? (offset + items.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                return ProviderOutcome<ProviderPage<T>>.Success(
                    new(ProviderId, items, next, next != null, result.ETag));
            }
            catch (JsonException)
            {
                return ProviderOutcome<ProviderPage<T>>.Failure(new(ProviderErrorKind.PermanentFailure));
            }
        });
    }

    private async Task<ProviderOutcome<T>> LookupAsync<T>(
        ProviderExecutionContext context,
        ProviderExternalResourceId id,
        ProviderResourceKind resourceKind,
        string resourcePath,
        string? expectedSnapshotVersion,
        Func<JsonElement, string?, T> map) where T : class
    {
        try { context.RequireResourceOwner(id, resourceKind); }
        catch (ArgumentException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Forbidden)); }

        return await ExecuteAsync(context, async (credential, storefront, ct) =>
        {
            var result = await SendAsync(credential,
                $"{(IsLibrary(id.Value) ? "v1/me/library" : $"v1/catalog/{storefront}")}/{resourcePath}/{Uri.EscapeDataString(id.Value)}", ct);
            if (!result.Outcome.IsSuccess) return ProviderOutcome<T>.Failure(result.Outcome.Error!);
            if (expectedSnapshotVersion != null &&
                !string.Equals(expectedSnapshotVersion, result.ETag, StringComparison.Ordinal))
                return ProviderOutcome<T>.Failure(new(ProviderErrorKind.PermanentFailure));
            try
            {
                using var document = JsonDocument.Parse(result.Body!);
                var item = Data(document.RootElement).SingleOrDefault();
                if (item.ValueKind == JsonValueKind.Undefined)
                    return ProviderOutcome<T>.Failure(new(ProviderErrorKind.NotFound));
                return ProviderOutcome<T>.Success(map(item, result.ETag));
            }
            catch (JsonException)
            {
                return ProviderOutcome<T>.Failure(new(ProviderErrorKind.PermanentFailure));
            }
            catch (InvalidOperationException)
            {
                return ProviderOutcome<T>.Failure(new(ProviderErrorKind.PermanentFailure));
            }
        }, IsLibrary(id.Value));
    }

    private Task<ProviderOutcome<T>> ExecuteAsync<T>(ProviderExecutionContext context,
        Func<AppleMusicCredential?, string, CancellationToken, Task<ProviderOutcome<T>>> operation, bool personal = false) =>
        _client.ExecuteAsync(context, personal, operation);

    private Task<AppleMusicHttpResult> SendAsync(AppleMusicCredential? credential, string relative, CancellationToken ct) =>
        _client.SendAsync(credential, relative, ct);

    internal static bool IsLibrary(string id) => id.StartsWith("i.", StringComparison.Ordinal) ||
        id.StartsWith("l.", StringComparison.Ordinal) || id.StartsWith("r.", StringComparison.Ordinal) ||
        id.StartsWith("p.", StringComparison.Ordinal);

    internal static ProviderTrackMetadata MapTrack(JsonElement item, string? revision)
    {
        var id = Resource(item, ProviderResourceKind.Track);
        var attributes = RequiredObject(item, "attributes");
        var artistName = Required(attributes, "artistName");
        ProviderExternalResourceId? albumId = null;
        var albumResourceId = RelationshipId(item, "albums") ?? String(attributes, "albumId");
        if (albumResourceId != null)
            albumId = new(AppleMusicKitPlaylistCapabilityAdapter.StableProviderId, ProviderResourceKind.Album, albumResourceId);
        TimeSpan? duration = attributes.TryGetProperty("durationInMillis", out var durationValue) &&
                             durationValue.TryGetInt64(out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
        var artwork = Artwork(id, attributes, revision);
        return new(id, Required(attributes, "name"), [ArtistCredit(artistName, RelationshipId(item, "artists"))], albumId,
            String(attributes, "albumName"), duration, String(attributes, "isrc"),
            String(attributes, "contentRating") switch { "explicit" => true, "clean" => false, _ => null },
            artwork, revision, trackNumber: Number(attributes, "trackNumber"), discNumber: Number(attributes, "discNumber"),
            releaseDate: String(attributes, "releaseDate"));
    }

    private static ProviderAlbumMetadata MapAlbum(JsonElement item, string? revision)
    {
        var id = Resource(item, ProviderResourceKind.Album);
        var attributes = RequiredObject(item, "attributes");
        int? trackCount = attributes.TryGetProperty("trackCount", out var count) && count.TryGetInt32(out var parsed)
            ? parsed
            : null;
        return new(id, Required(attributes, "name"), [ArtistCredit(Required(attributes, "artistName"), RelationshipId(item, "artists"))],
            trackCount, Artwork(id, attributes, revision), revision);
    }

    private static ProviderArtistMetadata MapArtist(JsonElement item, string? revision)
    {
        var id = Resource(item, ProviderResourceKind.Artist);
        var attributes = RequiredObject(item, "attributes");
        return new(id, Required(attributes, "name"), Artwork(id, attributes, revision), revision);
    }

    internal static ProviderArtworkReference? Artwork(
        ProviderExternalResourceId resource, JsonElement attributes, string? revision)
    {
        if (!attributes.TryGetProperty("artwork", out var artwork) || artwork.ValueKind != JsonValueKind.Object)
            return null;

        var template = String(artwork, "url");
        if (template == null) return null;

        var resolved = template.Replace("{w}", "1024", StringComparison.Ordinal)
            .Replace("{h}", "1024", StringComparison.Ordinal);
        return Uri.TryCreate(resolved, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               IsAppleArtworkHost(uri.Host) && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo)
            ? new ProviderArtworkReference(resource, uri, revision)
            : new ProviderArtworkReference(resource, revision: revision);
    }

    private static ProviderExternalResourceId Resource(JsonElement item, ProviderResourceKind kind) =>
        new(AppleMusicKitPlaylistCapabilityAdapter.StableProviderId, kind, Required(item, "id"));

    private static ProviderArtistCredit ArtistCredit(string name, string? id = null)
    {
        var syntheticId = "credit:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        return new(name, new(AppleMusicKitPlaylistCapabilityAdapter.StableProviderId,
            ProviderResourceKind.Artist, id ?? syntheticId));
    }

    private static bool IsAppleArtworkHost(string host) =>
        host.Equals("mzstatic.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase);

    private static int? Number(JsonElement value, string name) => value.TryGetProperty(name, out var number) &&
        number.TryGetInt32(out var parsed) ? parsed : null;

    private static string? RelationshipId(JsonElement item, string name) =>
        item.TryGetProperty("relationships", out var relationships) && relationships.TryGetProperty(name, out var relationship)
            ? Data(relationship).Select(value => String(value, "id")).FirstOrDefault() : null;

    private static bool TryOffset(string? cursor, out int offset) => cursor == null
        ? (offset = 0) == 0
        : int.TryParse(cursor, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out offset) && offset >= 0;
    private static JsonElement SearchContainer(JsonElement root, string resourceType)
    {
        var results = RequiredObject(root, "results");
        if (!results.TryGetProperty(resourceType, out var container) || container.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Apple Music response omitted {resourceType}.");
        return container;
    }
    private static IEnumerable<JsonElement> Data(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToArray()
            : throw new JsonException("Apple Music response omitted data.");
    private static bool HasNext(JsonElement root) => root.TryGetProperty("next", out var next) &&
        next.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(next.GetString());
    private static JsonElement RequiredObject(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new JsonException($"Apple Music response omitted {name}.");
    private static string? String(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    private static string Required(JsonElement root, string name) =>
        String(root, name) ?? throw new JsonException($"Apple Music response omitted {name}.");


}
