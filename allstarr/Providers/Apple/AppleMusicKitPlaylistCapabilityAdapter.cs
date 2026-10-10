using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;

namespace allstarr.Core.Providers.AppleMusicKit;

public sealed class AppleMusicKitPlaylistCapabilityAdapter : IProviderPlaylistCapability
{
    public const string StableProviderId = "apple-musickit";
    private static readonly Uri ApiOrigin = AppleMusicClient.ApiOrigin;
    private readonly HttpClient _http;
    private readonly AppleMusicClient _client;

    public AppleMusicKitPlaylistCapabilityAdapter(AppleMusicClient client, HttpClient artworkHttp)
    {
        _client = client;
        _http = artworkHttp;
    }

    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;
    public ProviderPlaylistMutationSupport MutationSupport { get; } = new(false, false);

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        ProviderExecutionContext context, ProviderUserPlaylistsRequest request) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        if (!TryOffset(request.Page.Cursor, out var offset)) return FailurePage();
        var limit = Math.Clamp(request.Page.Limit, 1, 25);
        var result = await SendAsync(credential, $"v1/me/library/playlists?limit={limit}&offset={offset}", ct);
        if (!result.Outcome.IsSuccess) return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(result.Outcome.Error!);
        try
        {
            using var document = JsonDocument.Parse(result.Body!);
            var items = Data(document.RootElement).Select(item => MapSummary(item, result.ETag)).ToArray();
            if (items.Length == 0 && HasNext(document.RootElement)) return FailurePage();
            var next = HasNext(document.RootElement) ? (offset + items.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(StableProviderId, items, next, next != null));
        }
        catch (JsonException) { return FailurePage(); }
    });

    public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        ProviderExecutionContext context, ProviderPlaylistTracksRequest request) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
        if (!TryOffset(request.Page.Cursor, out var offset))
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));
        var id = Uri.EscapeDataString(request.PlaylistId.Value);
        var metadata = await SendAsync(credential, $"{PlaylistRoot(request.PlaylistId.Value, storefront)}/{id}", ct);
        if (!metadata.Outcome.IsSuccess) return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(metadata.Outcome.Error!);
        try
        {
            using var metadataDocument = JsonDocument.Parse(metadata.Body!);
            var playlistElement = Data(metadataDocument.RootElement).FirstOrDefault();
            var summary = MapSummary(playlistElement, metadata.ETag);
            if (request.ExpectedRevision != null && request.ExpectedRevision != summary.SourceRevision)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));

            var limit = Math.Clamp(request.Page.Limit, 1, 25);
            var tracksResult = await SendAsync(credential,
                $"{PlaylistRoot(request.PlaylistId.Value, storefront)}/{id}/tracks?limit={limit}&offset={offset}", ct);
            if (!tracksResult.Outcome.IsSuccess) return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(tracksResult.Outcome.Error!);
            using var tracksDocument = JsonDocument.Parse(tracksResult.Body!);
            var tracks = new List<ProviderPlaylistTrack>();
            var sourcePosition = offset;
            foreach (var item in Data(tracksDocument.RootElement))
            {
                if (TryMapTrack(item, sourcePosition, out var track)) tracks.Add(track!);
                sourcePosition++;
            }
            if (HasNext(tracksDocument.RootElement) && sourcePosition == offset)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(ProviderError.CompatibilityContractChanged());
            var next = HasNext(tracksDocument.RootElement) ? sourcePosition.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(summary,
                new ProviderPage<ProviderPlaylistTrack>(StableProviderId, tracks, next, next != null, summary.SourceRevision)));
        }
        catch (JsonException)
        {
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    }, AppleMusicKitMetadataCapabilityAdapter.IsLibrary(request.PlaylistId.Value));

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
        ProviderExecutionContext context, ProviderPlaylistSearchRequest request) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Query) || !TryOffset(request.Page.Cursor, out var offset))
            return FailurePage();

        var limit = Math.Clamp(request.Page.Limit, 1, 25);
        var type = credential == null ? "playlists" : "library-playlists";
        var relative = (credential == null ? $"v1/catalog/{storefront}/search?term=" : "v1/me/library/search?term=") + Uri.EscapeDataString(request.Query.Trim()) +
                       "&types=" + type +
                       "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                       "&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = await SendAsync(credential, relative, ct);
        if (!result.Outcome.IsSuccess)
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(result.Outcome.Error!);

        try
        {
            using var document = JsonDocument.Parse(result.Body!);
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                !results.TryGetProperty(type, out var playlists))
                return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(
                    new(StableProviderId, [], null, false));
            var items = Data(playlists).Select(item => MapSummary(item, result.ETag)).ToArray();
            if (items.Length == 0 && HasNext(playlists)) return FailurePage();
            var next = HasNext(playlists)
                ? (offset + items.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(
                new(StableProviderId, items, next, next != null));
        }
        catch (JsonException)
        {
            return FailurePage();
        }
    }, context.Account != null);

    public Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProviderExecutionContext context, ProviderPlaylistArtworkRequest request) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        var resource = request.Artwork.ResourceId;
        if (resource == null || resource.ProviderId != StableProviderId || resource.ResourceKind != ProviderResourceKind.Playlist)
            return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
        var metadata = await SendAsync(credential,
            $"{PlaylistRoot(resource.Value, storefront)}/{Uri.EscapeDataString(resource.Value)}", ct);
        if (!metadata.Outcome.IsSuccess)
            return ProviderOutcome<ProviderPlaylistArtwork>.Failure(metadata.Outcome.Error!);
        Uri? imageUri = null;
        try
        {
            using var document = JsonDocument.Parse(metadata.Body!);
            var playlist = Data(document.RootElement).FirstOrDefault();
            var template = String(Object(playlist, "attributes"), "artwork") ??
                           String(Object(Object(playlist, "attributes"), "artwork"), "url");
            if (template != null)
            {
                var resolved = template.Replace("{w}", "1024", StringComparison.Ordinal)
                    .Replace("{h}", "1024", StringComparison.Ordinal);
                if (Uri.TryCreate(resolved, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps &&
                    IsAllowedArtworkHost(parsed.Host))
                    imageUri = parsed;
            }
        }
        catch (JsonException)
        {
            return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
        return imageUri == null
            ? ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.NotFound))
            : await DownloadArtworkAsync(imageUri, request.MaximumBytes, ct);
    }, AppleMusicKitMetadataCapabilityAdapter.IsLibrary(request.Artwork.ResourceId?.Value ?? ""));

    public Task<ProviderOutcome<ProviderPlaylistMutationReceipt>> MutatePlaylistAsync(
        ProviderExecutionContext context, ProviderPlaylistMutationRequest request) => Task.FromResult(
            ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(new(ProviderErrorKind.NotSupported)));

    public static ProviderRegistration CreateRegistration(AppleMusicKitPlaylistCapabilityAdapter adapter) => new(
        Descriptor([PlaylistDescriptor]), [adapter], ["apple-download", "applemusic"]);

    public static ProviderRegistration CreateRegistration(AppleMusicKitPlaylistCapabilityAdapter playlist,
        AppleMusicKitMetadataCapabilityAdapter metadata) => new(Descriptor([MetadataDescriptor, PlaylistDescriptor]), [metadata, playlist], ["apple-download", "applemusic"]);

    internal static ProviderDescriptor Descriptor(IEnumerable<ProviderCapabilityDescriptor> capabilities) => new(
        StableProviderId, "Apple Music", "Public catalog and personal Apple Music library with optional audio downloads.",
        ProviderOrigin.BuiltIn, "1", "apple-music-v3", capabilities,
        new ProviderPermissionDescriptor([ApiOrigin, new Uri("https://music.apple.com/")], false, ["musicUserToken"]),
        [new ProviderSettingDescriptor("musicUserToken", ProviderSettingValueKind.Secret,
            ProviderSettingScope.ProviderAccount, "Media User Token", true),
         new ProviderSettingDescriptor("storefront", ProviderSettingValueKind.Text,
            ProviderSettingScope.ProviderAccount, "Storefront", true, defaultJson: "\"us\"")]);

    internal static ProviderCapabilityDescriptor MetadataDescriptor => new(ProviderCapabilityKind.Metadata,
        ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Optional, "1",
        ["searchTracks", "getTrack", "lookupByIsrc", "searchAlbums", "getAlbum", "searchArtists", "getArtist", "getArtistAlbums", "getArtistTracks"],
        [ProviderAccountScope.Personal], supportsPublicRead: true);

    internal static ProviderCapabilityDescriptor PlaylistDescriptor => new(ProviderCapabilityKind.Playlist,
        ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Optional, "1",
        ["getUserPlaylists", "searchPlaylists", "getPlaylistTracks", "resolveArtwork"],
        [ProviderAccountScope.Personal], supportsPublicRead: true);

    private Task<ProviderOutcome<T>> ExecuteAsync<T>(ProviderExecutionContext context,
        Func<AppleMusicCredential?, string, CancellationToken, Task<ProviderOutcome<T>>> operation, bool personal = true) =>
        _client.ExecuteAsync(context, personal, operation);

    private Task<AppleMusicHttpResult> SendAsync(AppleMusicCredential? credential, string relative, CancellationToken ct) =>
        _client.SendAsync(credential, relative, ct);

    private static string PlaylistRoot(string id, string storefront) =>
        AppleMusicKitMetadataCapabilityAdapter.IsLibrary(id) ? "v1/me/library/playlists" :
            $"v1/catalog/{storefront}/playlists";

    private async Task<ProviderOutcome<ProviderPlaylistArtwork>> DownloadArtworkAsync(Uri uri, int maximumBytes, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsAllowedArtworkHost(finalUri.Host))
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
            if (!response.IsSuccessStatusCode) return ProviderOutcome<ProviderPlaylistArtwork>.Failure(Error(response, accountBound: false));
            var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (contentType is not ("image/jpeg" or "image/png" or "image/webp") ||
                response.Content.Headers.ContentLength > maximumBytes)
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(Math.Min(maximumBytes, 256 * 1024));
            var block = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(block, ct)) > 0)
            {
                if (buffer.Length + read > maximumBytes)
                    return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
                buffer.Write(block, 0, read);
            }
            return buffer.Length == 0
                ? ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.NotFound))
                : ProviderOutcome<ProviderPlaylistArtwork>.Success(new(buffer.ToArray(), contentType));
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.TransientFailure)); }
    }

    private static bool IsAllowedArtworkHost(string host) =>
        host.Equals("mzstatic.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase);

    private static ProviderPlaylistSummary MapSummary(JsonElement item, string? etag)
    {
        var id = Required(item, "id");
        var attributes = Object(item, "attributes");
        var resource = new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Playlist, id);
        var revision = String(attributes, "lastModifiedDate") ?? etag ?? $"unversioned:{ProviderPlaylistSnapshotCollector.HashResource(resource)}";
        int? count = null;
        var relationships = Object(item, "relationships");
        var tracks = Object(relationships, "tracks");
        if (tracks.ValueKind == JsonValueKind.Object && tracks.TryGetProperty("meta", out var meta) && meta.TryGetProperty("total", out var total) && total.TryGetInt32(out var parsed)) count = parsed;
        return new(resource, Required(attributes, "name"), new ProviderPlaylistOwner("selected-user"), revision,
            Description(attributes),
            AppleMusicKitMetadataCapabilityAdapter.Artwork(resource, attributes, revision) ?? new ProviderArtworkReference(resource, revision: revision), count, etag);
    }

    private static string? Description(JsonElement attributes)
    {
        var value = String(attributes, "description") ?? String(Object(attributes, "description"), "standard");
        if (string.IsNullOrWhiteSpace(value)) return null;
        var display = new string(value.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').Take(4000).ToArray());
        return string.IsNullOrWhiteSpace(display) ? null : display;
    }

    private static bool TryMapTrack(JsonElement item, int position, out ProviderPlaylistTrack? mapped)
    {
        mapped = null;
        var id = String(item, "id");
        var attributes = Object(item, "attributes");
        var title = String(attributes, "name");
        if (id == null || title == null) return false;
        var metadata = AppleMusicKitMetadataCapabilityAdapter.MapTrack(item, null);
        mapped = new(position, metadata.Id, metadata: metadata);
        return true;
    }

    private static ProviderError Error(HttpResponseMessage response, bool accountBound = true) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized when accountBound => new(ProviderErrorKind.AccountNeedsReauthentication),
        HttpStatusCode.Forbidden when accountBound => new(ProviderErrorKind.AccountNeedsReauthentication),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(ProviderErrorKind.PermanentFailure),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

    private static bool TryOffset(string? cursor, out int offset) => cursor == null
        ? (offset = 0) == 0
        : int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset) && offset >= 0;
    private static IEnumerable<JsonElement> Data(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [];
    private static bool HasNext(JsonElement root) => root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(next.GetString());
    private static JsonElement Object(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
    private static string? String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement root, string name) => String(root, name) ?? throw new JsonException($"Apple Music response omitted {name}.");
    private static ProviderOutcome<ProviderPage<ProviderPlaylistSummary>> FailurePage() => ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.PermanentFailure));
}
