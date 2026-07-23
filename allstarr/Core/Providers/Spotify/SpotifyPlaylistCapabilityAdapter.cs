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
    private readonly HttpClient _http;
    private readonly IProviderAccountSecretAccessor _secrets;
    private readonly SpotifyPathfinderPlaylistClient _pathfinder;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SpotifyPlaylistCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets)
        : this(clients.CreateClient(HttpClientName), secrets) { }

    public SpotifyPlaylistCapabilityAdapter(HttpClient http, IProviderAccountSecretAccessor secrets)
    {
        _http = http;
        _secrets = secrets;
        _pathfinder = new SpotifyPathfinderPlaylistClient(http);
    }

    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        ProviderExecutionContext context,
        ProviderUserPlaylistsRequest request) => ExecuteAsync(
        context,
        (token, cancellationToken) =>
            _pathfinder.GetUserPlaylistsAsync(token, request.Page, null, cancellationToken));

    public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        ProviderExecutionContext context,
        ProviderPlaylistTracksRequest request) => ExecuteAsync(
        context,
        (token, cancellationToken) =>
        {
            context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
            return _pathfinder.GetPlaylistTracksAsync(token, request, cancellationToken);
        });

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
        ProviderExecutionContext context,
        ProviderPlaylistSearchRequest request) => ExecuteAsync(
        context,
        (token, cancellationToken) =>
            _pathfinder.GetUserPlaylistsAsync(token, request.Page, request.Query, cancellationToken));

    public Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProviderExecutionContext context,
        ProviderPlaylistArtworkRequest request) => ExecuteAsync(
        context,
        async (token, cancellationToken) =>
        {
            var resource = request.Artwork.ResourceId;
            if (resource == null || resource.ProviderId != StableProviderId || resource.ResourceKind != ProviderResourceKind.Playlist)
                return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.PermanentFailure));
            var artwork = await _pathfinder.GetPlaylistArtworkUriAsync(token, resource, cancellationToken);
            return artwork.IsSuccess
                ? await DownloadArtworkAsync(artwork.RequireValue(), request.MaximumBytes, cancellationToken)
                : ProviderOutcome<ProviderPlaylistArtwork>.Failure(artwork.Error!);
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

    private static ProviderError Error(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, RetryAfter(response)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            return delta;
        if (retryAfter?.Date is { } date)
            return date <= DateTimeOffset.UtcNow ? TimeSpan.Zero : date - DateTimeOffset.UtcNow;
        return TimeSpan.FromSeconds(30);
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

    private static ProviderCapabilityDescriptor ConfiguredLane(ProviderCapabilityKind kind) => new(kind, ProviderCapabilitySupportState.ConfiguredOnly, ProviderAccountRequirement.Required, "legacy-seam-v1", allowedAccountScopes: [Core.Storage.ProviderAccountScope.Global, Core.Storage.ProviderAccountScope.User, Core.Storage.ProviderAccountScope.Library]);
    private sealed record TokenResult(ProviderOutcome<byte[]> Outcome, string? Token);
}
