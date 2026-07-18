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
    public const string HttpClientName = "AppleMusicKitMetadataAccountBound";
    private static readonly Uri ApiOrigin = new("https://api.music.apple.com/");
    private readonly HttpClient _http;
    private readonly IProviderAccountSecretAccessor _secrets;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AppleMusicKitMetadataCapabilityAdapter(IHttpClientFactory clients, IProviderAccountSecretAccessor secrets)
        : this(clients.CreateClient(HttpClientName), secrets) { }

    public AppleMusicKitMetadataCapabilityAdapter(HttpClient http, IProviderAccountSecretAccessor secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public string ProviderId => AppleMusicKitPlaylistCapabilityAdapter.StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) => SearchAsync(
            context, request, "library-songs", MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context, ProviderTrackLookupRequest request) => LookupAsync(
            context, request.Id, ProviderResourceKind.Track, "library/songs", request.ExpectedSnapshotVersion, MapTrack);

    public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
        ProviderExecutionContext context, ProviderIsrcLookupRequest request)
    {
        var error = ValidateContext(context);
        return Task.FromResult(error == null
            ? ProviderOutcome<ProviderTrackMetadata>.Failure(new(ProviderErrorKind.NotSupported))
            : ProviderOutcome<ProviderTrackMetadata>.Failure(error));
    }

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) => SearchAsync(
            context, request, "library-albums", MapAlbum);

    public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
        ProviderExecutionContext context, ProviderAlbumLookupRequest request) => LookupAsync(
            context, request.Id, ProviderResourceKind.Album, "library/albums", request.ExpectedSnapshotVersion, MapAlbum);

    public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) => SearchAsync(
            context, request, "library-artists", MapArtist);

    public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
        ProviderExecutionContext context, ProviderArtistLookupRequest request) => LookupAsync(
            context, request.Id, ProviderResourceKind.Artist, "library/artists", request.ExpectedSnapshotVersion, MapArtist);

    private async Task<ProviderOutcome<ProviderPage<T>>> SearchAsync<T>(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request,
        string resourceType,
        Func<JsonElement, string?, T> map) where T : class
    {
        if (!TryOffset(request.Page.Cursor, out var offset))
            return ProviderOutcome<ProviderPage<T>>.Failure(new(ProviderErrorKind.PermanentFailure));

        return await ExecuteAsync(context, async (credential, ct) =>
        {
            var relative = "v1/me/library/search?term=" + Uri.EscapeDataString(request.Query) +
                           "&types=" + resourceType +
                           "&limit=" + request.Page.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                           "&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var result = await SendAsync(credential, relative, ct);
            if (!result.Outcome.IsSuccess)
                return ProviderOutcome<ProviderPage<T>>.Failure(result.Outcome.Error!);

            try
            {
                using var document = JsonDocument.Parse(result.Body!);
                var container = SearchContainer(document.RootElement, resourceType);
                var items = Data(container).Select(item => map(item, result.ETag)).ToArray();
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

        return await ExecuteAsync(context, async (credential, ct) =>
        {
            var result = await SendAsync(credential,
                $"v1/me/{resourcePath}/{Uri.EscapeDataString(id.Value)}", ct);
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
        });
    }

    private async Task<ProviderOutcome<T>> ExecuteAsync<T>(
        ProviderExecutionContext context,
        Func<AppleMusicKitPlaylistCapabilityAdapter.Credential, CancellationToken, Task<ProviderOutcome<T>>> operation)
    {
        var error = ValidateContext(context);
        if (error != null) return ProviderOutcome<T>.Failure(error);
        try
        {
            return await _secrets.UseAsync(context.Account!, async bytes =>
            {
                AppleMusicKitPlaylistCapabilityAdapter.Credential? credential;
                try
                {
                    credential = JsonSerializer.Deserialize<AppleMusicKitPlaylistCapabilityAdapter.Credential>(bytes.Span);
                }
                catch (JsonException)
                {
                    credential = null;
                }
                return credential is { IsValid: true }
                    ? await operation(credential, context.CancellationToken)
                    : ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
            }, context.CancellationToken);
        }
        catch (OperationCanceledException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Canceled)); }
        catch (KeyNotFoundException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration)); }
        catch { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.TransientFailure)); }
    }

    private async Task<HttpResult> SendAsync(
        AppleMusicKitPlaylistCapabilityAdapter.Credential credential,
        string relative,
        CancellationToken ct)
    {
        var uri = new Uri(ApiOrigin, relative);
        if (!IsAppleApiOrigin(uri))
            return new(ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.Forbidden)), null, null);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.DeveloperToken);
        request.Headers.TryAddWithoutValidation("Music-User-Token", credential.MusicUserToken);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsAppleApiOrigin(finalUri))
                return new(ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.Forbidden)), null, null);
            if (!response.IsSuccessStatusCode)
                return new(ProviderOutcome<byte[]>.Failure(Error(response)), null, response.Headers.ETag?.Tag);
            return new(ProviderOutcome<byte[]>.Success([]),
                await response.Content.ReadAsByteArrayAsync(ct), response.Headers.ETag?.Tag);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return new(ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.TransientFailure)), null, null);
        }
    }

    private static ProviderTrackMetadata MapTrack(JsonElement item, string? revision)
    {
        var id = Resource(item, ProviderResourceKind.Track);
        var attributes = RequiredObject(item, "attributes");
        var artistName = Required(attributes, "artistName");
        ProviderExternalResourceId? albumId = null;
        var albumResourceId = String(attributes, "albumId");
        if (albumResourceId != null)
            albumId = new(AppleMusicKitPlaylistCapabilityAdapter.StableProviderId, ProviderResourceKind.Album, albumResourceId);
        TimeSpan? duration = attributes.TryGetProperty("durationInMillis", out var durationValue) &&
                             durationValue.TryGetInt64(out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
        var artwork = Artwork(id, attributes, revision);
        return new(id, Required(attributes, "name"), [ArtistCredit(artistName)], albumId,
            String(attributes, "albumName"), duration, String(attributes, "isrc"),
            String(attributes, "contentRating") switch { "explicit" => true, "clean" => false, _ => null },
            artwork, revision);
    }

    private static ProviderAlbumMetadata MapAlbum(JsonElement item, string? revision)
    {
        var id = Resource(item, ProviderResourceKind.Album);
        var attributes = RequiredObject(item, "attributes");
        int? trackCount = attributes.TryGetProperty("trackCount", out var count) && count.TryGetInt32(out var parsed)
            ? parsed
            : null;
        return new(id, Required(attributes, "name"), [ArtistCredit(Required(attributes, "artistName"))],
            trackCount, Artwork(id, attributes, revision), revision);
    }

    private static ProviderArtistMetadata MapArtist(JsonElement item, string? revision)
    {
        var id = Resource(item, ProviderResourceKind.Artist);
        var attributes = RequiredObject(item, "attributes");
        return new(id, Required(attributes, "name"), Artwork(id, attributes, revision), revision);
    }

    private static ProviderArtworkReference? Artwork(
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

    private static ProviderArtistCredit ArtistCredit(string name)
    {
        var syntheticId = "credit:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        return new(name, new(AppleMusicKitPlaylistCapabilityAdapter.StableProviderId,
            ProviderResourceKind.Artist, syntheticId));
    }

    private static ProviderError? ValidateContext(ProviderExecutionContext context)
    {
        if (!context.ProviderId.Equals(AppleMusicKitPlaylistCapabilityAdapter.StableProviderId, StringComparison.Ordinal))
            return new(ProviderErrorKind.Forbidden);
        if (context.Account is not { Scope: ProviderAccountScope.User, SecretReferenceId: not null } account)
            return new(ProviderErrorKind.AccountNeedsConfiguration);
        if (!account.ProviderId.Equals(AppleMusicKitPlaylistCapabilityAdapter.StableProviderId, StringComparison.Ordinal) ||
            account.TenantId != context.Actor.TenantId || account.OwnerUserId != context.Actor.EffectiveUserId)
            return new(ProviderErrorKind.Forbidden);
        return null;
    }

    private static bool IsAppleApiOrigin(Uri uri) => uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals(ApiOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsAppleArtworkHost(string host) =>
        host.Equals("mzstatic.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase);

    private static ProviderError Error(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited,
            response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

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

    private sealed record HttpResult(ProviderOutcome<byte[]> Outcome, byte[]? Body, string? ETag);
}
