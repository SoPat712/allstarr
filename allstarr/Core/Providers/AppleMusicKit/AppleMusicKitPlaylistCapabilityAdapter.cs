using System.Net;
using System.Net.Http.Headers;
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
    public const string HttpClientName = "AppleMusicKitAccountBound";
    private static readonly Uri ApiOrigin = new("https://api.music.apple.com/");
    private readonly HttpClient _http;
    private readonly IProviderAccountSecretAccessor _secrets;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AppleMusicKitPlaylistCapabilityAdapter(IHttpClientFactory clients, IProviderAccountSecretAccessor secrets)
        : this(clients.CreateClient(HttpClientName), secrets) { }

    public AppleMusicKitPlaylistCapabilityAdapter(HttpClient http, IProviderAccountSecretAccessor secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        ProviderExecutionContext context, ProviderUserPlaylistsRequest request) => ExecuteAsync(context, async (credential, ct) =>
    {
        if (!TryOffset(request.Page.Cursor, out var offset)) return FailurePage();
        var result = await SendAsync(credential, $"v1/me/library/playlists?limit={request.Page.Limit}&offset={offset}", ct);
        if (!result.Outcome.IsSuccess) return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(result.Outcome.Error!);
        try
        {
            using var document = JsonDocument.Parse(result.Body!);
            var items = Data(document.RootElement).Select(item => MapSummary(item, result.ETag)).ToArray();
            var next = HasNext(document.RootElement) ? (offset + items.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(StableProviderId, items, next, next != null));
        }
        catch (JsonException) { return FailurePage(); }
    });

    public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        ProviderExecutionContext context, ProviderPlaylistTracksRequest request) => ExecuteAsync(context, async (credential, ct) =>
    {
        context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
        if (!TryOffset(request.Page.Cursor, out var offset))
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));
        var id = Uri.EscapeDataString(request.PlaylistId.Value);
        var metadata = await SendAsync(credential, $"v1/me/library/playlists/{id}", ct);
        if (!metadata.Outcome.IsSuccess) return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(metadata.Outcome.Error!);
        try
        {
            using var metadataDocument = JsonDocument.Parse(metadata.Body!);
            var playlistElement = Data(metadataDocument.RootElement).FirstOrDefault();
            var summary = MapSummary(playlistElement, metadata.ETag);
            if (request.ExpectedRevision != null && request.ExpectedRevision != summary.SourceRevision)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));

            var tracksResult = await SendAsync(credential,
                $"v1/me/library/playlists/{id}/tracks?limit={request.Page.Limit}&offset={offset}", ct);
            if (!tracksResult.Outcome.IsSuccess) return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(tracksResult.Outcome.Error!);
            using var tracksDocument = JsonDocument.Parse(tracksResult.Body!);
            var tracks = new List<ProviderPlaylistTrack>();
            var sourcePosition = offset;
            foreach (var item in Data(tracksDocument.RootElement))
            {
                if (TryMapTrack(item, sourcePosition, out var track)) tracks.Add(track!);
                sourcePosition++;
            }
            var next = HasNext(tracksDocument.RootElement) ? sourcePosition.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(summary,
                new ProviderPage<ProviderPlaylistTrack>(StableProviderId, tracks, next, next != null, summary.SourceRevision)));
        }
        catch (JsonException)
        {
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    });

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
        ProviderExecutionContext context, ProviderPlaylistSearchRequest request) =>
        Task.FromResult(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(
            new ProviderError(ProviderErrorKind.PermanentFailure)));

    public Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProviderExecutionContext context, ProviderPlaylistArtworkRequest request) => ExecuteAsync(context, async (credential, ct) =>
    {
        var resource = request.Artwork.ResourceId;
        if (resource == null || resource.ProviderId != StableProviderId || resource.ResourceKind != ProviderResourceKind.Playlist)
            return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
        var metadata = await SendAsync(credential,
            $"v1/me/library/playlists/{Uri.EscapeDataString(resource.Value)}", ct);
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
    });

    public static ProviderRegistration CreateRegistration(AppleMusicKitPlaylistCapabilityAdapter adapter) => new(
        new ProviderDescriptor(StableProviderId, "Apple MusicKit",
            "Account-bound Apple Music library playlist reads through a selected per-user Music User Token.",
            ProviderOrigin.BuiltIn, "1", "apple-musickit-library-playlist-v1",
            [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.Required, "1", ["getUserPlaylists", "getPlaylistTracks", "resolveArtwork"], [ProviderAccountScope.User])],
            new ProviderPermissionDescriptor([ApiOrigin], false, ["musickitcredentials"]),
            [new ProviderSettingDescriptor("musickitcredentials", ProviderSettingValueKind.Secret,
                ProviderSettingScope.ProviderAccount, "MusicKit developer token and Music User Token", true)]),
        [adapter]);

    public static ProviderRegistration CreateRegistration(
        AppleMusicKitPlaylistCapabilityAdapter playlist,
        AppleMusicKitMetadataCapabilityAdapter metadata) => new(
        new ProviderDescriptor(StableProviderId, "Apple MusicKit",
            "Account-bound Apple Music personal-library metadata and playlists through a selected per-user Music User Token.",
            ProviderOrigin.BuiltIn, "1", "apple-musickit-library-v2",
            [
                new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required, "1",
                    ["searchTracks", "getTrack", "searchAlbums", "getAlbum", "searchArtists", "getArtist"],
                    [ProviderAccountScope.User]),
                new ProviderCapabilityDescriptor(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required, "1", ["getUserPlaylists", "getPlaylistTracks", "resolveArtwork"],
                    [ProviderAccountScope.User])
            ],
            new ProviderPermissionDescriptor([ApiOrigin], false, ["musickitcredentials"]),
            [new ProviderSettingDescriptor("musickitcredentials", ProviderSettingValueKind.Secret,
                ProviderSettingScope.ProviderAccount, "MusicKit developer token and Music User Token", true)]),
        [metadata, playlist]);

    private async Task<ProviderOutcome<T>> ExecuteAsync<T>(ProviderExecutionContext context,
        Func<Credential, CancellationToken, Task<ProviderOutcome<T>>> operation)
    {
        var error = ValidateContext(context);
        if (error != null) return ProviderOutcome<T>.Failure(error);
        try
        {
            return await _secrets.UseAsync(context.Account!, async bytes =>
            {
                Credential? credential;
                try { credential = JsonSerializer.Deserialize<Credential>(bytes.Span); }
                catch (JsonException) { credential = null; }
                return credential is { IsValid: true }
                    ? await operation(credential, context.CancellationToken)
                    : ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
            }, context.CancellationToken);
        }
        catch (OperationCanceledException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Canceled)); }
        catch (KeyNotFoundException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration)); }
        catch { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.TransientFailure)); }
    }

    private async Task<HttpResult> SendAsync(Credential credential, string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiOrigin, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.DeveloperToken);
        request.Headers.TryAddWithoutValidation("Music-User-Token", credential.MusicUserToken);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new(ProviderOutcome<byte[]>.Failure(Error(response)), null, response.Headers.ETag?.Tag);
            return new(ProviderOutcome<byte[]>.Success([]), await response.Content.ReadAsByteArrayAsync(ct), response.Headers.ETag?.Tag);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return new(ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.TransientFailure)), null, null); }
    }

    private async Task<ProviderOutcome<ProviderPlaylistArtwork>> DownloadArtworkAsync(Uri uri, int maximumBytes, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsAllowedArtworkHost(finalUri.Host))
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
            if (!response.IsSuccessStatusCode) return ProviderOutcome<ProviderPlaylistArtwork>.Failure(Error(response));
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
        if (tracks.TryGetProperty("meta", out var meta) && meta.TryGetProperty("total", out var total) && total.TryGetInt32(out var parsed)) count = parsed;
        return new(resource, Required(attributes, "name"), new ProviderPlaylistOwner("selected-user"), revision,
            String(attributes, "description"), new ProviderArtworkReference(resource, revision: revision), count, etag);
    }

    private static bool TryMapTrack(JsonElement item, int position, out ProviderPlaylistTrack? mapped)
    {
        mapped = null;
        var id = String(item, "id");
        var attributes = Object(item, "attributes");
        var title = String(attributes, "name");
        if (id == null || title == null) return false;
        var trackId = new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Track, id);
        var artistName = String(attributes, "artistName");
        if (artistName == null) return false;
        var syntheticArtistId = "credit:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artistName))).ToLowerInvariant();
        var artists = new[] { new ProviderArtistCredit(artistName,
            new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Artist, syntheticArtistId)) };
        TimeSpan? duration = attributes.TryGetProperty("durationInMillis", out var durationValue) && durationValue.TryGetInt64(out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
        var albumTitle = String(attributes, "albumName");
        ProviderExternalResourceId? albumId = null;
        if (albumTitle != null)
        {
            var syntheticAlbumId = "title:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(albumTitle))).ToLowerInvariant();
            albumId = new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Album, syntheticAlbumId);
        }
        var isrc = String(attributes, "isrc");
        bool? explicitValue = String(attributes, "contentRating") switch { "explicit" => true, "clean" => false, _ => null };
        mapped = new(position, trackId, metadata: new ProviderTrackMetadata(trackId, title, artists, albumId: albumId, albumTitle: albumTitle,
            duration: duration, isrc: isrc, isExplicit: explicitValue));
        return true;
    }

    private static ProviderError? ValidateContext(ProviderExecutionContext context)
    {
        if (!context.ProviderId.Equals(StableProviderId, StringComparison.Ordinal)) return new(ProviderErrorKind.Forbidden);
        if (context.Account is not { Scope: ProviderAccountScope.User, SecretReferenceId: not null } account)
            return new(ProviderErrorKind.AccountNeedsConfiguration);
        if (account.TenantId != context.Actor.TenantId || account.OwnerUserId != context.Actor.UserId)
            return new(ProviderErrorKind.Forbidden);
        return null;
    }

    private static ProviderError Error(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
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
    private sealed record HttpResult(ProviderOutcome<byte[]> Outcome, byte[]? Body, string? ETag);
    public sealed record Credential(string DeveloperToken, string MusicUserToken)
    {
        public bool IsValid => !string.IsNullOrWhiteSpace(DeveloperToken) && !string.IsNullOrWhiteSpace(MusicUserToken);
    }
}
