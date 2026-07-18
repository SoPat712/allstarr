using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Secrets;

namespace allstarr.Core.Providers.Spotify;

public interface IProviderAccountSecretAccessor
{
    Task<T> UseAsync<T>(
        ProviderAccountContext account,
        Func<ReadOnlyMemory<byte>, Task<T>> operation,
        CancellationToken cancellationToken);
}

public sealed class EncryptedProviderAccountSecretAccessor(EncryptedSecretStore secretStore)
    : IProviderAccountSecretAccessor
{
    public async Task<T> UseAsync<T>(
        ProviderAccountContext account,
        Func<ReadOnlyMemory<byte>, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(operation);
        if (account.SecretReferenceId == null)
            throw new KeyNotFoundException("The selected provider account has no secret reference.");
        var access = account.Scope == Core.Storage.ProviderAccountScope.Global
            ? new SecretAccessContext(null, AllowGlobal: true)
            : new SecretAccessContext(account.TenantId);
        using var lease = await secretStore.OpenAsync(account.SecretReferenceId.Value, access, cancellationToken);
        return await operation(lease.Value);
    }
}

public sealed class SpotifyPlaylistCapabilityAdapter : IProviderPlaylistCapability
{
    public const string StableProviderId = "spotify";
    public const string HttpClientName = "SpotifyAccountBound";
    private static readonly Uri ApiOrigin = new("https://api.spotify.com/");
    private readonly HttpClient _http;
    private readonly IProviderAccountSecretAccessor _secrets;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SpotifyPlaylistCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets)
        : this(clients.CreateClient(HttpClientName), secrets) { }

    public SpotifyPlaylistCapabilityAdapter(HttpClient http, IProviderAccountSecretAccessor secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        ProviderExecutionContext context,
        ProviderUserPlaylistsRequest request) => ExecuteAsync(
        context,
        async (token, cancellationToken) =>
        {
            if (!TryOffset(request.Page.Cursor, out var offset)) return FailurePage();
            var uri = Api($"v1/me/playlists?limit={request.Page.Limit}&offset={offset}&fields=items(id,name,description,owner(id,display_name),images,snapshot_id,tracks(total)),next,total");
            var response = await SendApiAsync(token, HttpMethod.Get, uri, cancellationToken);
            if (!response.Outcome.IsSuccess) return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(response.Outcome.Error!);
            using var document = JsonDocument.Parse(response.Body!);
            return MapPlaylistPage(document.RootElement, request.Page, offset);
        });

    public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        ProviderExecutionContext context,
        ProviderPlaylistTracksRequest request) => ExecuteAsync(
        context,
        async (token, cancellationToken) =>
        {
            context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
            if (!TryOffset(request.Page.Cursor, out var offset))
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new ProviderError(ProviderErrorKind.PermanentFailure));
            var encodedId = Uri.EscapeDataString(request.PlaylistId.Value);
            var metadataResponse = await SendApiAsync(token, HttpMethod.Get,
                Api($"v1/playlists/{encodedId}?fields=id,name,description,owner(id,display_name),images,snapshot_id,tracks(total)"), cancellationToken);
            if (!metadataResponse.Outcome.IsSuccess)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(metadataResponse.Outcome.Error!);
            using var metadataDocument = JsonDocument.Parse(metadataResponse.Body!);
            var summary = MapSummary(metadataDocument.RootElement, metadataResponse.ETag);
            if (request.ExpectedRevision != null && request.ExpectedRevision != summary.SourceRevision)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new ProviderError(ProviderErrorKind.PermanentFailure));

            var fields = Uri.EscapeDataString("items(track(id,name,album(id,name),artists(id,name),duration_ms,explicit,external_ids)),next,total");
            var tracksResponse = await SendApiAsync(token, HttpMethod.Get,
                Api($"v1/playlists/{encodedId}/tracks?offset={offset}&limit={request.Page.Limit}&fields={fields}"), cancellationToken);
            if (!tracksResponse.Outcome.IsSuccess)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(tracksResponse.Outcome.Error!);
            using var tracksDocument = JsonDocument.Parse(tracksResponse.Body!);
            var root = tracksDocument.RootElement;
            var tracks = new List<ProviderPlaylistTrack>();
            var sourcePosition = offset;
            foreach (var item in Array(root, "items"))
            {
                if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object &&
                    TryMapTrack(track, sourcePosition, out var mapped))
                    tracks.Add(mapped!);
                sourcePosition++;
            }
            var nextCursor = HasNext(root) ? sourcePosition.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                summary,
                new ProviderPage<ProviderPlaylistTrack>(StableProviderId, tracks, nextCursor, nextCursor != null, summary.SourceRevision)));
        });

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
        ProviderExecutionContext context,
        ProviderPlaylistSearchRequest request) => ExecuteAsync(
        context,
        async (token, cancellationToken) =>
        {
            if (!TryOffset(request.Page.Cursor, out var offset)) return FailurePage();
            var uri = Api($"v1/search?type=playlist&q={Uri.EscapeDataString(request.Query)}&limit={request.Page.Limit}&offset={offset}");
            var response = await SendApiAsync(token, HttpMethod.Get, uri, cancellationToken);
            if (!response.Outcome.IsSuccess) return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(response.Outcome.Error!);
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement.TryGetProperty("playlists", out var playlists) ? playlists : default;
            return MapPlaylistPage(root, request.Page, offset);
        });

    public Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProviderExecutionContext context,
        ProviderPlaylistArtworkRequest request) => ExecuteAsync(
        context,
        async (token, cancellationToken) =>
        {
            var resource = request.Artwork.ResourceId;
            if (resource == null || resource.ProviderId != StableProviderId || resource.ResourceKind != ProviderResourceKind.Playlist)
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
            var encodedId = Uri.EscapeDataString(resource.Value);
            var metadata = await SendApiAsync(token, HttpMethod.Get,
                Api($"v1/playlists/{encodedId}?fields=id,images,snapshot_id"), cancellationToken);
            if (!metadata.Outcome.IsSuccess)
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(metadata.Outcome.Error!);
            Uri? imageUri = null;
            try
            {
                using var document = JsonDocument.Parse(metadata.Body!);
                imageUri = Array(document.RootElement, "images")
                    .Select(image => String(image, "url"))
                    .Where(value => Uri.TryCreate(value, UriKind.Absolute, out _))
                    .Select(value => new Uri(value!))
                    .FirstOrDefault(uri => uri.Scheme == Uri.UriSchemeHttps && IsAllowedArtworkHost(uri.Host));
            }
            catch (JsonException)
            {
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
            }
            return imageUri == null
                ? ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.NotFound))
                : await DownloadArtworkAsync(imageUri, request.MaximumBytes, cancellationToken);
        });

    public static ProviderRegistration CreateRegistration(SpotifyPlaylistCapabilityAdapter adapter) => new(
        new ProviderDescriptor(
            StableProviderId,
            "Spotify",
            "Account-bound Spotify playlist reads through the selected encrypted provider account.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "spotify-web-playlist-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Playlist,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    "1",
                    ["getUserPlaylists", "getPlaylistTracks", "searchPlaylists", "resolveArtwork"],
                    [Core.Storage.ProviderAccountScope.Global, Core.Storage.ProviderAccountScope.User, Core.Storage.ProviderAccountScope.Library]),
                ConfiguredLane(ProviderCapabilityKind.Lyrics),
                ConfiguredLane(ProviderCapabilityKind.Health)
            ],
            new ProviderPermissionDescriptor(
                [new Uri("https://open.spotify.com/"), new Uri("https://api.spotify.com/")],
                cache: false,
                secretSettingKeys: ["sessionCookie"]),
            settings:
            [
                new ProviderSettingDescriptor(
                    "sessionCookie",
                    ProviderSettingValueKind.Secret,
                    ProviderSettingScope.ProviderAccount,
                    "Spotify session cookie",
                    required: true)
            ]),
        [adapter]);

    private async Task<ProviderOutcome<T>> ExecuteAsync<T>(
        ProviderExecutionContext context,
        Func<string, CancellationToken, Task<ProviderOutcome<T>>> operation)
    {
        var contextError = ValidateContext(context);
        if (contextError != null) return ProviderOutcome<T>.Failure(contextError);
        try
        {
            return await _secrets.UseAsync(
                context.Account!,
                async secret =>
                {
                    var cookie = SpotifySessionCookie.Normalize(Encoding.UTF8.GetString(secret.Span));
                    if (string.IsNullOrWhiteSpace(cookie))
                        return ProviderOutcome<T>.Failure(new ProviderError(ProviderErrorKind.AccountNeedsConfiguration));
                    var token = await ExchangeCookieAsync(cookie, context.CancellationToken);
                    return token.Outcome.IsSuccess
                        ? await operation(token.Token!, context.CancellationToken)
                        : ProviderOutcome<T>.Failure(token.Outcome.Error!);
                },
                context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<T>.Failure(new ProviderError(ProviderErrorKind.Canceled));
        }
        catch (KeyNotFoundException)
        {
            return ProviderOutcome<T>.Failure(new ProviderError(ProviderErrorKind.AccountNeedsConfiguration));
        }
        catch
        {
            return ProviderOutcome<T>.Failure(new ProviderError(ProviderErrorKind.TransientFailure));
        }
    }

    private async Task<TokenResult> ExchangeCookieAsync(string cookie, CancellationToken cancellationToken)
    {
        var result = await SpotifyWebTokenExchange.ExchangeAsync(_http, SpotifySessionCookie.Normalize(cookie)!, cancellationToken);
        if (result.Success) return new(ProviderOutcome<byte[]>.Success([]), result.AccessToken);
        var kind = result.ReasonCode is "provider_unauthorized" or "anonymous_session"
            ? ProviderErrorKind.Unauthorized
            : ProviderErrorKind.TransientFailure;
        return new(ProviderOutcome<byte[]>.Failure(new ProviderError(kind)), null);
    }

    private async Task<HttpResult> SendApiAsync(string token, HttpMethod method, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, cancellationToken);
    }

    private async Task<HttpResult> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(ProviderOutcome<byte[]>.Failure(Error(response)), null, response.Headers.ETag?.Tag);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new(ProviderOutcome<byte[]>.Success(body), body, response.Headers.ETag?.Tag);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return new(ProviderOutcome<byte[]>.Failure(new ProviderError(ProviderErrorKind.TransientFailure)), null, null);
        }
    }

    private static ProviderError Error(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, RetryAfter(response)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

    private static TimeSpan RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero ? delta : TimeSpan.FromSeconds(30);

    private static ProviderOutcome<ProviderPage<ProviderPlaylistSummary>> MapPlaylistPage(
        JsonElement root,
        ProviderPageRequest request,
        int offset)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return FailurePage();
        var summaries = Array(root, "items").Where(item => item.ValueKind == JsonValueKind.Object).Select(item => MapSummary(item, null)).ToArray();
        var next = HasNext(root) ? (offset + summaries.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(StableProviderId, summaries, next, next != null));
    }

    private static ProviderOutcome<ProviderPage<ProviderPlaylistSummary>> FailurePage() =>
        ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new ProviderError(ProviderErrorKind.PermanentFailure));

    private static ProviderPlaylistSummary MapSummary(JsonElement value, string? etag)
    {
        var id = RequiredString(value, "id");
        var playlistId = new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Playlist, id);
        var ownerElement = value.TryGetProperty("owner", out var owner) ? owner : default;
        var ownerId = String(ownerElement, "id") ?? "unknown-owner";
        var revision = String(value, "snapshot_id") ?? etag ?? $"unversioned:{ProviderPlaylistSnapshotCollector.HashResource(playlistId)}";
        var artwork = new ProviderArtworkReference(playlistId, revision: revision);
        return new(
            playlistId,
            RequiredString(value, "name"),
            new ProviderPlaylistOwner(ownerId, String(ownerElement, "display_name")),
            revision,
            String(value, "description"),
            artwork,
            value.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("total", out var total) && total.TryGetInt32(out var count) ? count : null,
            etag);
    }

    private static bool TryMapTrack(JsonElement value, int position, out ProviderPlaylistTrack? mapped)
    {
        mapped = null;
        var id = String(value, "id");
        var title = String(value, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return false;
        var trackId = new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Track, id);
        var artists = Array(value, "artists")
            .Select(artist => (Name: String(artist, "name"), Id: String(artist, "id")))
            .Where(artist => !string.IsNullOrWhiteSpace(artist.Name) && !string.IsNullOrWhiteSpace(artist.Id))
            .Select(artist => new ProviderArtistCredit(artist.Name!, new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Artist, artist.Id!)))
            .ToArray();
        if (artists.Length == 0) return false;
        ProviderExternalResourceId? albumId = null;
        string? albumTitle = null;
        if (value.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object && String(album, "id") is { } albumValue)
        {
            albumId = new(StableProviderId, ProviderResourceKind.Album, albumValue);
            albumTitle = String(album, "name");
        }
        TimeSpan? duration = value.TryGetProperty("duration_ms", out var durationValue) && durationValue.TryGetInt64(out var durationMs)
            ? TimeSpan.FromMilliseconds(durationMs) : null;
        var isrc = value.TryGetProperty("external_ids", out var external) ? String(external, "isrc") : null;
        bool? explicitValue = value.TryGetProperty("explicit", out var explicitElement) && explicitElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? explicitElement.GetBoolean() : null;
        var metadata = new ProviderTrackMetadata(trackId, title, artists, albumId, albumTitle, duration, isrc, explicitValue);
        mapped = new(position, trackId, metadata: metadata);
        return true;
    }

    private static ProviderError? ValidateContext(ProviderExecutionContext context)
    {
        if (!context.ProviderId.Equals(StableProviderId, StringComparison.Ordinal)) return new(ProviderErrorKind.Forbidden);
        if (context.Account == null || context.Account.SecretReferenceId == null) return new(ProviderErrorKind.AccountNeedsConfiguration);
        return null;
    }

    private async Task<ProviderOutcome<ProviderPlaylistArtwork>> DownloadArtworkAsync(
        Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsAllowedArtworkHost(finalUri.Host))
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
            if (!response.IsSuccessStatusCode)
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(Error(response));
            var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (contentType is not ("image/jpeg" or "image/png" or "image/webp") ||
                response.Content.Headers.ContentLength > maximumBytes)
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream(Math.Min(maximumBytes, 256 * 1024));
            var block = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(block, cancellationToken)) > 0)
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
        host.Equals("i.scdn.co", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".spotifycdn.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryOffset(string? cursor, out int offset)
    {
        if (cursor == null) { offset = 0; return true; }
        return int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset) && offset >= 0;
    }

    private static Uri Api(string relative) => new(ApiOrigin, relative);
    private static bool HasNext(JsonElement root) => root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(next.GetString());
    private static IEnumerable<JsonElement> Array(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static string? String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredString(JsonElement root, string name) => String(root, name) ?? throw new JsonException($"Spotify response omitted {name}.");
    private static ProviderCapabilityDescriptor ConfiguredLane(ProviderCapabilityKind kind) => new(kind, ProviderCapabilitySupportState.ConfiguredOnly, ProviderAccountRequirement.Required, "legacy-seam-v1", allowedAccountScopes: [Core.Storage.ProviderAccountScope.Global, Core.Storage.ProviderAccountScope.User, Core.Storage.ProviderAccountScope.Library]);
    private sealed record HttpResult(ProviderOutcome<byte[]> Outcome, byte[]? Body, string? ETag);
    private sealed record TokenResult(ProviderOutcome<byte[]> Outcome, string? Token);
}
