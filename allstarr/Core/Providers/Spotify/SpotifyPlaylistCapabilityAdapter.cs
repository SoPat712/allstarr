using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Secrets;
using allstarr.Services.Common;

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
    private static readonly Uri WebApiOrigin = new("https://api.spotify.com/");
    private readonly HttpClient _http;
    private readonly IProviderAccountSecretAccessor _secrets;
    private readonly SpotifyPathfinderPlaylistClient _pathfinder;
    private readonly ILogger<SpotifyPathfinderPlaylistClient>? _logger;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SpotifyPlaylistCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets,
        IApplicationCache cache,
        ILogger<SpotifyPathfinderPlaylistClient> logger)
        : this(clients.CreateClient(HttpClientName), secrets, logger, cache) { }

    public SpotifyPlaylistCapabilityAdapter(
        HttpClient http,
        IProviderAccountSecretAccessor secrets,
        ILogger<SpotifyPathfinderPlaylistClient>? logger = null,
        IApplicationCache? cache = null)
    {
        _http = http;
        _secrets = secrets;
        _logger = logger;
        _pathfinder = new SpotifyPathfinderPlaylistClient(http, cache, logger);
    }

    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;
    public ProviderPlaylistMutationSupport MutationSupport { get; } = new(true, true);

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
            return _pathfinder.GetPlaylistTracksAsync(
                token,
                request,
                cancellationToken,
                AccountFingerprint(context.Account!.AccountId));
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
            var artwork = await _pathfinder.GetPlaylistArtworkUriAsync(token, request.Artwork, cancellationToken);
            return artwork.IsSuccess
                ? await DownloadArtworkAsync(_http, artwork.RequireValue(), request.MaximumBytes, cancellationToken)
                : ProviderOutcome<ProviderPlaylistArtwork>.Failure(artwork.Error!);
        });

    public Task<ProviderOutcome<ProviderPlaylistMutationReceipt>> MutatePlaylistAsync(
        ProviderExecutionContext context,
        ProviderPlaylistMutationRequest request) => ExecuteAsync(
            context,
            (token, cancellationToken) => MutateAsync(token, request, cancellationToken));

    public static ProviderRegistration CreateRegistration(
        SpotifyPlaylistCapabilityAdapter adapter,
        IProviderLyricsCapability? lyrics = null) => new(
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
                    ["getUserPlaylists", "getPlaylistTracks", "searchPlaylists", "resolveArtwork", "mutatePlaylist"],
                    [Core.Storage.ProviderAccountScope.Global, Core.Storage.ProviderAccountScope.User, Core.Storage.ProviderAccountScope.Library]),
                lyrics == null
                    ? ConfiguredLane(ProviderCapabilityKind.Lyrics)
                    : new ProviderCapabilityDescriptor(
                        ProviderCapabilityKind.Lyrics,
                        ProviderCapabilitySupportState.Supported,
                        ProviderAccountRequirement.None,
                        "1",
                        ["fetchLyrics"]),
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
        lyrics == null ? [adapter] : [adapter, lyrics]);

    private async Task<ProviderOutcome<ProviderPlaylistMutationReceipt>> MutateAsync(
        string token,
        ProviderPlaylistMutationRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ProviderId.Equals(StableProviderId, StringComparison.Ordinal))
            return ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(new(ProviderErrorKind.Forbidden));

        var warnings = request.Artwork == null
            ? Array.Empty<string>()
            : new[] { "Playlist artwork was not changed." };
        var playlistId = request.ExistingPlaylistId;
        ExistingPlaylist? existing = null;
        if (playlistId != null)
        {
            if (request.ConflictBehavior == ProviderPlaylistConflictBehavior.FailIfChanged &&
                request.ExpectedRevision == null)
                return ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(new(ProviderErrorKind.PermanentFailure));
            var read = await ReadPlaylistAsync(token, playlistId, request.ExpectedRevision, cancellationToken);
            if (!read.IsSuccess)
                return ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(read.Error!);
            existing = read.RequireValue();
            if (existing.TrackIds.SequenceEqual(request.OrderedTrackIds) &&
                existing.Summary.Name.Equals(request.Name, StringComparison.Ordinal) &&
                string.Equals(existing.Summary.Description, request.Description, StringComparison.Ordinal))
                return ProviderOutcome<ProviderPlaylistMutationReceipt>.Success(new(
                    playlistId,
                    existing.Summary.SourceRevision,
                    existing.TrackIds.Count,
                    applied: false,
                    warnings));
        }
        else
        {
            var created = await SendMutationAsync(
                token,
                HttpMethod.Post,
                "v1/me/playlists",
                new { name = request.Name, description = request.Description ?? string.Empty, @public = false },
                cancellationToken);
            if (!created.Outcome.IsSuccess)
                return ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(created.Outcome.Error!);
            if (!TryMutationResponse(created.Body, out var createdId, out var createdRevision) ||
                string.IsNullOrWhiteSpace(createdId))
                return ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(
                    ProviderError.CompatibilityContractChanged());
            playlistId = new(StableProviderId, ProviderResourceKind.Playlist, createdId);
            existing = new(
                new ProviderPlaylistSummary(
                    playlistId,
                    request.Name,
                    new ProviderPlaylistOwner("selected-user"),
                    createdRevision ?? "created",
                    request.Description,
                    trackCount: 0),
                []);
        }

        var revision = existing.Summary.SourceRevision;
        if (!existing.Summary.Name.Equals(request.Name, StringComparison.Ordinal) ||
            !string.Equals(existing.Summary.Description, request.Description, StringComparison.Ordinal))
        {
            var metadata = await SendMutationAsync(
                token,
                HttpMethod.Put,
                $"v1/playlists/{Uri.EscapeDataString(playlistId.Value)}",
                new { name = request.Name, description = request.Description ?? string.Empty },
                cancellationToken);
            if (!metadata.Outcome.IsSuccess)
                return ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(metadata.Outcome.Error!);
        }

        if (!existing.TrackIds.SequenceEqual(request.OrderedTrackIds))
        {
            var chunks = request.OrderedTrackIds
                .Select(item => $"spotify:track:{item.Value}")
                .Chunk(100)
                .ToArray();
            var first = await SendMutationAsync(
                token,
                HttpMethod.Put,
                $"v1/playlists/{Uri.EscapeDataString(playlistId.Value)}/items",
                new { uris = chunks.FirstOrDefault() ?? [] },
                cancellationToken);
            if (!first.Outcome.IsSuccess)
                return ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(first.Outcome.Error!);
            if (TryMutationResponse(first.Body, out _, out var firstRevision) && firstRevision != null)
                revision = firstRevision;
            foreach (var chunk in chunks.Skip(1))
            {
                var appended = await SendMutationAsync(
                    token,
                    HttpMethod.Post,
                    $"v1/playlists/{Uri.EscapeDataString(playlistId.Value)}/items",
                    new { uris = chunk },
                    cancellationToken);
                if (!appended.Outcome.IsSuccess)
                    return ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(appended.Outcome.Error!);
                if (TryMutationResponse(appended.Body, out _, out var appendedRevision) && appendedRevision != null)
                    revision = appendedRevision;
            }
        }

        return ProviderOutcome<ProviderPlaylistMutationReceipt>.Success(new(
            playlistId,
            revision,
            request.OrderedTrackIds.Count,
            applied: true,
            warnings));
    }

    private async Task<ProviderOutcome<ExistingPlaylist>> ReadPlaylistAsync(
        string token,
        ProviderExternalResourceId playlistId,
        string? expectedRevision,
        CancellationToken cancellationToken)
    {
        var tracks = new List<ProviderExternalResourceId>();
        string? cursor = null;
        ProviderPlaylistSummary? summary = null;
        do
        {
            var page = await _pathfinder.GetPlaylistTracksAsync(
                token,
                new ProviderPlaylistTracksRequest(playlistId, new ProviderPageRequest(200, cursor), expectedRevision),
                cancellationToken,
                accountFingerprint: null);
            if (!page.IsSuccess) return ProviderOutcome<ExistingPlaylist>.Failure(page.Error!);
            var value = page.RequireValue();
            summary ??= value.Playlist;
            expectedRevision ??= summary.SourceRevision;
            tracks.AddRange(value.Tracks.Items.Select(item => item.TrackId));
            cursor = value.Tracks.NextCursor;
        } while (cursor != null);
        return ProviderOutcome<ExistingPlaylist>.Success(new(summary!, tracks));
    }

    private async Task<MutationHttpResult> SendMutationAsync(
        string token,
        HttpMethod method,
        string relativePath,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(WebApiOrigin, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        SpotifyWebRequestProfile.Apply(request);
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(ProviderOutcome<byte[]>.Failure(Error(response)), null);
            return new(
                ProviderOutcome<byte[]>.Success([]),
                await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return new(ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.TransientFailure)), null);
        }
    }

    private static bool TryMutationResponse(byte[]? body, out string? playlistId, out string? revision)
    {
        playlistId = null;
        revision = null;
        if (body is not { Length: > 0 }) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            playlistId = document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            revision = document.RootElement.TryGetProperty("snapshot_id", out var snapshot) ? snapshot.GetString() : null;
            return playlistId != null || revision != null;
        }
        catch (JsonException) { return false; }
    }

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
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Spotify account-bound playlist operation {OperationId} failed before producing a typed provider outcome",
                context.OperationId);
            return ProviderOutcome<T>.Failure(new ProviderError(ProviderErrorKind.TransientFailure));
        }
    }

    private async Task<TokenResult> ExchangeCookieAsync(string cookie, CancellationToken cancellationToken)
    {
        var result = await SpotifyWebTokenExchange.ExchangeAsync(_http, SpotifySessionCookie.Normalize(cookie)!, cancellationToken);
        if (result.Success) return new(ProviderOutcome<byte[]>.Success([]), result.AccessToken);
        var kind = result.ReasonCode switch
        {
            "provider_unauthorized" or "anonymous_session" or "expired_session" =>
                ProviderErrorKind.Unauthorized,
            "provider_forbidden" => ProviderErrorKind.Forbidden,
            _ => ProviderErrorKind.TransientFailure
        };
        return new(ProviderOutcome<byte[]>.Failure(new ProviderError(kind)), null);
    }

    private static string AccountFingerprint(Guid accountId) =>
        Convert.ToHexString(SHA256.HashData(accountId.ToByteArray())).ToLowerInvariant()[..12];

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

    internal static async Task<ProviderOutcome<ProviderPlaylistArtwork>> DownloadArtworkAsync(
        HttpClient http, Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

    internal static bool IsAllowedArtworkHost(string host) =>
        host.Equals("i.scdn.co", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".spotifycdn.com", StringComparison.OrdinalIgnoreCase);

    private static ProviderCapabilityDescriptor ConfiguredLane(ProviderCapabilityKind kind) => new(kind, ProviderCapabilitySupportState.ConfiguredOnly, ProviderAccountRequirement.Required, "legacy-seam-v1", allowedAccountScopes: [Core.Storage.ProviderAccountScope.Global, Core.Storage.ProviderAccountScope.User, Core.Storage.ProviderAccountScope.Library]);
    private sealed record ExistingPlaylist(
        ProviderPlaylistSummary Summary,
        IReadOnlyList<ProviderExternalResourceId> TrackIds);
    private sealed record MutationHttpResult(ProviderOutcome<byte[]> Outcome, byte[]? Body);
    private sealed record TokenResult(ProviderOutcome<byte[]> Outcome, string? Token);
}
