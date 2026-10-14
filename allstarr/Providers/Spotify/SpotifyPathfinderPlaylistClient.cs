using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Sources;
using allstarr.Services.Common;
using allstarr.Models.Settings;
using System.Net.Http.Json;

namespace allstarr.Core.Providers.Spotify;

/// <summary>
/// Spotify playlist transport backed by persisted web-player queries and public
/// profile reads. The typed playlist capability supplies scoped authentication
/// for each operation.
/// </summary>
public sealed class SpotifyPathfinderPlaylistClient
{
    internal const string LibraryOperation = "libraryV3";
    internal const string LibraryQueryHash = SpotifyApiSettings.DefaultLibraryQueryHash;
    internal const string PlaylistOperation = "fetchPlaylist";
    internal const string PlaylistQueryHash = SpotifyApiSettings.DefaultPlaylistQueryHash;
    internal static readonly PathfinderOperationDefinition LibraryQuery =
        new(LibraryOperation, LibraryQueryHash, 1);
    internal static readonly PathfinderOperationDefinition PlaylistQuery =
        new(PlaylistOperation, PlaylistQueryHash, 1);
    internal const string LikedSongsOperation = "fetchLibraryTracks";
    internal const string LikedSongsQueryHash = SpotifyApiSettings.DefaultLikedSongsQueryHash;
    internal static readonly PathfinderOperationDefinition LikedSongsQuery = new(LikedSongsOperation, LikedSongsQueryHash, 1);
    private const string ProviderId = SpotifyPlaylistCapabilityAdapter.StableProviderId;
    private readonly SpotifyApiSettings settings;
    private PathfinderOperationDefinition CurrentLibraryQuery => new(LibraryOperation, settings.LibraryQueryHash, 1);
    private PathfinderOperationDefinition CurrentPlaylistQuery => new(PlaylistOperation, settings.PlaylistQueryHash, 1);
    private PathfinderOperationDefinition CurrentLikedSongsQuery => new(LikedSongsOperation, settings.LikedSongsQueryHash, 1);
    private readonly HttpClient http;
    private readonly IApplicationCache? cache;
    private readonly ILogger<SpotifyPathfinderPlaylistClient>? logger;

    public SpotifyPathfinderPlaylistClient(
        HttpClient http,
        ILogger<SpotifyPathfinderPlaylistClient>? logger = null)
        : this(http, null, logger) { }

    public SpotifyPathfinderPlaylistClient(
        HttpClient http,
        IApplicationCache? cache,
        ILogger<SpotifyPathfinderPlaylistClient>? logger = null,
        SpotifyApiSettings? settings = null)
    {
        this.http = http;
        this.cache = cache;
        this.logger = logger;
        this.settings = settings ?? new();
    }

    private const int MaximumPublicProfilePlaylists = 1000;

    public async Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetPublicUserPlaylistsAsync(
        string token, string userId, ProviderPageRequest page, CancellationToken cancellationToken)
    {
        if (!SpotifyUserReference.TryNormalize(userId, out userId) || !TryOffset(page.Cursor, out var offset) ||
            offset >= MaximumPublicProfilePlaylists)
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.PermanentFailure));
        var cacheKey = CacheKeyBuilder.BuildPublicUserPlaylistDiscoveryKey(ProviderId, userId);
        var profile = cache == null ? null : await cache.GetAsync<PublicProfileCacheEntry>(cacheKey);
        if (profile == null)
        {
            var response = await PublicProfileAsync(token, userId, 1, cancellationToken);
            if (!response.IsSuccess) return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(response.Error!);
            try
            {
                using var initial = JsonDocument.Parse(response.RequireValue());
                if (!initial.RootElement.TryGetProperty("total_public_playlists_count", out var countValue) ||
                    !countValue.TryGetInt32(out var count) || count < 0)
                    return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(ProviderError.CompatibilityContractChanged());
                if (count > 1)
                {
                    response = await PublicProfileAsync(token, userId, Math.Min(count, MaximumPublicProfilePlaylists), cancellationToken);
                    if (!response.IsSuccess) return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(response.Error!);
                }
                using var document = JsonDocument.Parse(response.RequireValue());
                if (!document.RootElement.TryGetProperty("public_playlists", out var values) ||
                    values.ValueKind != JsonValueKind.Array || values.GetArrayLength() < Math.Min(count, MaximumPublicProfilePlaylists) ||
                    values.GetArrayLength() > MaximumPublicProfilePlaylists)
                    return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(ProviderError.CompatibilityContractChanged());
                var items = new List<ProviderPlaylistSummary>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in values.EnumerateArray())
                {
                    if (!SpotifyPlaylistReference.TryNormalize(String(value, "uri")!, out var playlistId) ||
                        playlistId == SpotifyPlaylistReference.LikedSongsId ||
                        !SpotifyUserReference.TryNormalize(String(value, "owner_uri")!, out var ownerId) ||
                        ContractText(String(value, "name"), 500) is not { } name)
                        return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(ProviderError.CompatibilityContractChanged());
                    if (!seen.Add(playlistId)) continue;
                    var resource = new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Playlist, playlistId);
                    var revision = ContractText(String(value, "revisionId"), 300) ??
                                   $"public-profile:{ProviderPlaylistSnapshotCollector.HashResource(resource)}";
                    items.Add(new(resource, name, new(ownerId), revision));
                }
                profile = new(items.ToArray(), count > MaximumPublicProfilePlaylists);
                if (cache != null) await cache.SetAsync(cacheKey, profile, TimeSpan.FromMinutes(2));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(ProviderError.CompatibilityContractChanged());
            }
        }
        var nextOffset = offset + page.Limit;
        var nextCursor = nextOffset < profile.Items.Length ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(ProviderId,
            profile.Items.Skip(offset).Take(page.Limit), nextCursor, nextCursor != null || profile.Truncated));
    }

    private async Task<ProviderOutcome<byte[]>> PublicProfileAsync(
        string token, string userId, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://spclient.wg.spotify.com/user-profile-view/v3/profile/{Uri.EscapeDataString(userId)}?playlist_limit={limit}&artist_limit=0&episode_limit=0&market=from_token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        SpotifyWebRequestProfile.Apply(request);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return ProviderOutcome<byte[]>.Failure(HttpFailure(response));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (body.Length + read > 4 * 1024 * 1024)
                    return ProviderOutcome<byte[]>.Failure(ProviderError.CompatibilityContractChanged());
                body.Write(buffer, 0, read);
            }
            return ProviderOutcome<byte[]>.Success(body.ToArray());
        }
        catch (HttpRequestException) { return ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.TransientFailure)); }
    }

    private sealed record PublicProfileCacheEntry(ProviderPlaylistSummary[] Items, bool Truncated);

    public async Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        string token,
        ProviderPageRequest page,
        string? query,
        CancellationToken cancellationToken)
    {
        if (!TryOffset(page.Cursor, out var offset))
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(
                new ProviderError(ProviderErrorKind.PermanentFailure));

        var variables = new
        {
            filters = new[] { "Playlists" },
            order = (string?)null,
            textFilter = query?.Trim() ?? "",
            features = new[] { "LIKED_SONGS", "YOUR_EPISODES_V2", "PRERELEASES", "EVENTS" },
            offset,
            limit = page.Limit,
            flatten = true,
            expandedFolders = System.Array.Empty<string>(),
            folderUri = (string?)null,
            includeFoldersWhenFlattening = true,
            withCuration = true
        };
        var response = await QueryAsync(token, CurrentLibraryQuery, variables, cancellationToken);
        if (!response.Outcome.IsSuccess)
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(response.Outcome.Error!);

        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            if (GraphQlFailure(document.RootElement, LibraryOperation) is { } failure)
                return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(failure);
            if (!TryPath(document.RootElement, out var library, "data", "me", "libraryV3"))
            {
                logger?.LogWarning(
                    "Spotify Pathfinder operation {Operation} returned an unexpected {RootKind} envelope. Root fields: {RootFields}. Data fields: {DataFields}",
                    LibraryOperation,
                    document.RootElement.ValueKind,
                    PropertyNames(document.RootElement),
                    TryPath(document.RootElement, out var data, "data")
                        ? PropertyNames(data)
                        : "none");
                return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(
                    ProviderError.CompatibilityContractChanged());
            }

            var rawItems = Array(library, "items");
            var playlistEntries = new List<LibraryPlaylistEntry>(rawItems.Count);
            foreach (var rawItem in rawItems)
                CollectPlaylistEntries(rawItem, playlistEntries);

            var summaries = new List<ProviderPlaylistSummary>(playlistEntries.Count);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < playlistEntries.Count; index++)
            {
                var entry = playlistEntries[index];
                var playlist = entry.Playlist;
                var playlistId = entry.Id;
                if (!seenIds.Add(playlistId))
                    continue;
                try
                {
                    if (MapSummary(playlist, playlistId, entry.Wrapper, entry.Entry) is { } summary)
                    {
                        summaries.Add(summary);
                        if (ArtworkUri(playlist) is { } artwork)
                            await CacheArtworkAsync(playlistId, summary.SourceRevision, artwork);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or
                                                 InvalidOperationException or
                                                 OverflowException)
                {
                    logger?.LogWarning(
                        exception,
                        "Spotify Pathfinder operation {Operation} skipped incompatible playlist item {ItemIndex}",
                        LibraryOperation,
                        index);
                }
            }

            var consumed = rawItems.Count;
            var total = Integer(library, "totalCount");
            var nextOffset = offset + consumed;
            var hasNext = consumed > 0 && (total == null ? consumed >= page.Limit : nextOffset < total);
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(
                ProviderId,
                summaries,
                hasNext ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                hasNext));
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(
                exception,
                "Spotify Pathfinder operation {Operation} returned {ByteCount} bytes that were not valid JSON",
                LibraryOperation,
                response.Body?.Length ?? 0);
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable));
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                         FormatException or
                                         ArgumentException or
                                         OverflowException)
        {
            logger?.LogWarning(
                exception,
                "Spotify Pathfinder operation {Operation} returned a structurally incompatible playlist envelope",
                LibraryOperation);
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable));
        }
    }

    public async Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        string token,
        ProviderPlaylistTracksRequest request,
        CancellationToken cancellationToken,
        string? accountFingerprint = null)
    {
        if (!TryOffset(request.Page.Cursor, out var offset))
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                new ProviderError(ProviderErrorKind.PermanentFailure));

        if (!SpotifyPlaylistReference.TryNormalize(request.PlaylistId.Value, out var playlistId))
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));
        request = new(new(ProviderId, ProviderResourceKind.Playlist, playlistId), request.Page, request.ExpectedRevision);
        var likedSongs = playlistId == SpotifyPlaylistReference.LikedSongsId;
        var operation = likedSongs ? CurrentLikedSongsQuery : CurrentPlaylistQuery;
        object variables = likedSongs
            ? new { offset, limit = request.Page.Limit }
            : new { uri = $"spotify:playlist:{playlistId}", offset, limit = request.Page.Limit };
        var response = await QueryAsync(token, operation, variables, cancellationToken);
        if (!response.Outcome.IsSuccess)
        {
            LogPlaylistPage(
                request, accountFingerprint, offset, response,
                response.Outcome.Error!.Code);
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(response.Outcome.Error!);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            if (GraphQlFailure(document.RootElement, operation.OperationName) is { } failure)
            {
                LogPlaylistPage(
                    request, accountFingerprint, offset, response,
                    failure.Code);
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(failure);
            }
            JsonElement content;
            ProviderPlaylistSummary? summary;
            if (likedSongs)
            {
                if (!TryPath(document.RootElement, out content, "data", "me", "library", "tracks") ||
                    Integer(content, "totalCount") is not { } totalCount)
                    return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(ProviderError.CompatibilityContractChanged());
                var revision = ContractText(String(content, "revisionId"), 300) ?? $"library-tracks:{totalCount}";
                summary = new(request.PlaylistId, "Liked Songs", new ProviderPlaylistOwner("selected-account"), revision, trackCount: totalCount);
            }
            else
            {
                if (!TryPath(document.RootElement, out var playlist, "data", "playlistV2") ||
                    !TryPath(playlist, out content, "content"))
                    return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(ProviderError.CompatibilityContractChanged());
                summary = MapSummary(playlist, playlistId);
                if (summary != null && ArtworkUri(playlist) is { } artwork)
                    await CacheArtworkAsync(playlistId, summary.SourceRevision, artwork);
            }
            if (summary == null)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(ProviderError.CompatibilityContractChanged());
            if (request.ExpectedRevision != null && request.ExpectedRevision != summary.SourceRevision)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.PermanentFailure));
            var rawItems = Array(content, "items");
            var tracks = new List<ProviderPlaylistTrack>(rawItems.Count);
            for (var index = 0; index < rawItems.Count; index++)
            {
                var position = offset + index;
                if (MapTrack(rawItems[index], position, out var reasonCode) is not { } track)
                {
                    LogPlaylistPage(
                        request,
                        accountFingerprint,
                        offset,
                        response,
                        reasonCode,
                        Integer(content, "totalCount") ?? summary.TrackCount,
                        rawItems.Count,
                        tracks.Count,
                        position);
                    return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                        ProviderError.CompatibilityContractChanged());
                }
                tracks.Add(track);
            }

            var consumed = rawItems.Count;
            var total = Integer(content, "totalCount") ?? summary.TrackCount;
            var nextOffset = offset + consumed;
            var hasNext = consumed > 0 && (total == null
                ? consumed >= request.Page.Limit
                : nextOffset < total);
            logger?.LogInformation(
                "Spotify Pathfinder page completed. Operation: {Operation}. QueryVersion: {QueryVersion}. QueryHash: {QueryHash}. Account: {Account}. Playlist: {Playlist}. Offset: {Offset}. Limit: {Limit}. HttpOutcome: {HttpOutcome}. ElapsedMilliseconds: {ElapsedMilliseconds}. DeclaredCount: {DeclaredCount}. RawCount: {RawCount}. MappedCount: {MappedCount}. NextOffset: {NextOffset}. ResponseByteCount: {ResponseByteCount}",
                operation.OperationName,
                operation.Version,
                operation.Sha256Hash[..12],
                accountFingerprint ?? "unavailable",
                ProviderPlaylistSnapshotCollector.HashResource(request.PlaylistId)[..12],
                offset,
                request.Page.Limit,
                response.HttpStatusCode,
                response.ElapsedMilliseconds,
                total,
                rawItems.Count,
                tracks.Count,
                nextOffset,
                response.Body!.Length);
            return ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                summary,
                new ProviderPage<ProviderPlaylistTrack>(
                    ProviderId,
                    tracks,
                    hasNext ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    hasNext,
                    summary.SourceRevision)));
        }
        catch (JsonException)
        {
            LogPlaylistPage(
                request, accountFingerprint, offset, response,
                "invalid_json");
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable));
        }
    }

    private void LogPlaylistPage(
        ProviderPlaylistTracksRequest request,
        string? accountFingerprint,
        int offset,
        PathfinderResponse response,
        string reasonCode,
        int? declaredCount = null,
        int rawCount = 0,
        int mappedCount = 0,
        int? failingSourcePosition = null)
    {
        var operation = request.PlaylistId.Value == SpotifyPlaylistReference.LikedSongsId ? CurrentLikedSongsQuery : CurrentPlaylistQuery;
        logger?.LogWarning(
            "Spotify Pathfinder page completed. Operation: {Operation}. QueryVersion: {QueryVersion}. QueryHash: {QueryHash}. Account: {Account}. Playlist: {Playlist}. Offset: {Offset}. Limit: {Limit}. HttpOutcome: {HttpOutcome}. ElapsedMilliseconds: {ElapsedMilliseconds}. DeclaredCount: {DeclaredCount}. RawCount: {RawCount}. MappedCount: {MappedCount}. NextOffset: {NextOffset}. ReasonCode: {ReasonCode}. FailingSourcePosition: {SourcePosition}. ResponseByteCount: {ResponseByteCount}",
            operation.OperationName,
            operation.Version,
            operation.Sha256Hash[..Math.Min(12, operation.Sha256Hash.Length)],
            accountFingerprint ?? "unavailable",
            ProviderPlaylistSnapshotCollector.HashResource(request.PlaylistId)[..12],
            offset,
            request.Page.Limit,
            response.HttpStatusCode,
            response.ElapsedMilliseconds,
            declaredCount,
            rawCount,
            mappedCount,
            offset + rawCount,
            reasonCode,
            failingSourcePosition,
            response.Body?.Length ?? 0);
    }

    public async Task<ProviderOutcome<Uri>> GetPlaylistArtworkUriAsync(
        string token,
        ProviderArtworkReference artwork,
        CancellationToken cancellationToken)
    {
        var playlistId = artwork.ResourceId;
        if (playlistId == null || playlistId.Value == SpotifyPlaylistReference.LikedSongsId)
            return ProviderOutcome<Uri>.Failure(new ProviderError(ProviderErrorKind.NotFound));
        if (await TryCachedArtworkAsync(playlistId.Value, artwork.Revision) is { } cached)
            return ProviderOutcome<Uri>.Success(cached);

        var variables = new { uri = $"spotify:playlist:{playlistId.Value}", offset = 0, limit = 1 };
        var response = await QueryAsync(
            token,
            CurrentPlaylistQuery,
            variables,
            cancellationToken);
        if (!response.Outcome.IsSuccess)
            return ProviderOutcome<Uri>.Failure(response.Outcome.Error!);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            if (GraphQlFailure(document.RootElement, PlaylistOperation) is { } failure)
                return ProviderOutcome<Uri>.Failure(failure);
            if (!TryPath(document.RootElement, out var playlist, "data", "playlistV2") ||
                ArtworkUri(playlist) is not { } resolvedArtwork)
                return ProviderOutcome<Uri>.Failure(new ProviderError(ProviderErrorKind.NotFound));
            await CacheArtworkAsync(playlistId.Value, artwork.Revision, resolvedArtwork);
            return ProviderOutcome<Uri>.Success(resolvedArtwork);
        }
        catch (JsonException)
        {
            return ProviderOutcome<Uri>.Failure(new ProviderError(ProviderErrorKind.CapabilityUnavailable));
        }
    }

    private async Task<PathfinderResponse> QueryAsync(
        string token,
        PathfinderOperationDefinition operation,
        object variables,
        CancellationToken cancellationToken)
    {
        if (settings.PathfinderEndpoint is not (SpotifyApiSettings.DefaultPathfinderEndpoint or "https://api-partner.spotify.com/pathfinder/v2/query") ||
            operation.Sha256Hash.Length != 64 || !operation.Sha256Hash.All(Uri.IsHexDigit))
            return new(ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration)), null, null, 0);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var extensions = JsonSerializer.Serialize(new
        {
            persistedQuery = new { version = operation.Version, sha256Hash = operation.Sha256Hash }
        });
        var parameters = new Dictionary<string, string>
        {
            ["operationName"] = operation.OperationName,
            ["variables"] = JsonSerializer.Serialize(variables),
            ["extensions"] = extensions
        };
        var query = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var post = settings.PathfinderEndpoint.EndsWith("/v2/query", StringComparison.Ordinal);
        using var request = post
            ? new HttpRequestMessage(HttpMethod.Post, settings.PathfinderEndpoint)
            {
                Content = JsonContent.Create(new { operationName = operation.OperationName, variables, extensions = new { persistedQuery = new { version = operation.Version, sha256Hash = operation.Sha256Hash } } })
            }
            : new HttpRequestMessage(HttpMethod.Get, new Uri($"{settings.PathfinderEndpoint}?{query}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        SpotifyWebRequestProfile.Apply(request);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(
                    ProviderOutcome<byte[]>.Failure(HttpFailure(response)),
                    null,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new(
                ProviderOutcome<byte[]>.Success(body),
                body,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return new(
                ProviderOutcome<byte[]>.Failure(new ProviderError(ProviderErrorKind.TransientFailure)),
                null,
                null,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static ProviderError HttpFailure(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, RetryAfter(response)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

    private ProviderError? GraphQlFailure(JsonElement root, string operation)
    {
        var errors = Array(root, "errors");
        if (errors.Count == 0)
            return null;
        var staleHash = errors.Any(error =>
            String(error, "message")?.Contains("persisted", StringComparison.OrdinalIgnoreCase) == true ||
            (TryPath(error, out var extensions, "extensions") &&
             String(extensions, "code")?.Contains("persisted", StringComparison.OrdinalIgnoreCase) == true));
        logger?.LogWarning(
            "Spotify Pathfinder operation {Operation} returned {ErrorCount} GraphQL errors. ReasonCode: {ReasonCode}",
            operation,
            errors.Count,
            staleHash ? "persisted_query_changed" : "graphql_error");
        return staleHash
            ? ProviderError.CompatibilityContractChanged()
            : new ProviderError(ProviderErrorKind.PermanentFailure);
    }

    private ProviderPlaylistSummary? MapSummary(
        JsonElement value,
        string id,
        JsonElement wrapper = default,
        JsonElement entry = default)
    {
        var name = ContractText(Text(value, "name"), 500);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;
        var resource = new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Playlist, id);
        var ownerData = TryPath(value, out var owner, "ownerV2", "data") ? owner : default;
        var ownerId = ContractText(
            String(ownerData, "username") ?? String(ownerData, "uri"),
            500) ?? "unknown-owner";
        var ownerName = ContractText(
            String(ownerData, "name") ?? String(ownerData, "username"),
            300);
        var trackCount = PlaylistItemCount(value) ??
                         PlaylistItemCount(wrapper) ??
                         PlaylistItemCount(entry);
        var revision = ContractText(String(value, "revisionId"), 300) ??
                       $"pathfinder:{ProviderPlaylistSnapshotCollector.HashResource(resource)}:{trackCount ?? -1}";
        var summary = new ProviderPlaylistSummary(
            resource,
            name,
            new ProviderPlaylistOwner(ownerId, ownerName),
            revision,
            ContractText(Text(value, "description"), 4000),
            new ProviderArtworkReference(resource, revision: revision),
            trackCount);
        return summary;
    }

    private async Task<Uri?> TryCachedArtworkAsync(string playlistId, string? revision)
    {
        if (cache == null) return null;
        var entry = await cache.GetAsync<ArtworkCacheEntry>(
            CacheKeyBuilder.BuildProviderPlaylistArtworkDescriptorKey(ProviderId, playlistId, revision));
        return entry != null &&
               Uri.TryCreate(entry.Uri, UriKind.Absolute, out var artwork) &&
               artwork.Scheme == Uri.UriSchemeHttps
            ? artwork
            : null;
    }

    private async Task CacheArtworkAsync(string playlistId, string? revision, Uri artwork)
    {
        if (cache == null) return;
        await cache.SetAsync(
            CacheKeyBuilder.BuildProviderPlaylistArtworkDescriptorKey(ProviderId, playlistId, revision),
            new ArtworkCacheEntry(artwork.AbsoluteUri),
            TimeSpan.FromMinutes(30));
    }

    private static string PropertyNames(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
            ? string.Join(", ", value.EnumerateObject().Select(property => property.Name).Take(12))
            : "none";

    private static ProviderPlaylistTrack? MapTrack(
        JsonElement item,
        int position,
        out string reasonCode)
    {
        if (!TryPath(item, out var data, "itemV2", "data"))
        {
            reasonCode = "missing_item_data";
            return null;
        }
        var id = SpotifyId(String(data, "uri"), "track");
        var title = String(data, "name");
        if (string.IsNullOrWhiteSpace(id))
        {
            reasonCode = "unsupported_item_identity";
            return null;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            reasonCode = "missing_title";
            return null;
        }

        var artists = new List<ProviderArtistCredit>();
        if (TryPath(data, out var artistItems, "artists", "items") &&
            artistItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistItems.EnumerateArray())
            {
                var name = TryPath(artist, out var profile, "profile")
                    ? String(profile, "name")
                    : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var artistId = SpotifyId(String(artist, "uri"), "artist");
                artists.Add(new(
                    name,
                    artistId == null
                        ? null
                        : new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Artist, artistId)));
            }
        }
        if (artists.Count == 0)
        {
            reasonCode = "missing_named_artist";
            return null;
        }

        ProviderExternalResourceId? albumId = null;
        string? albumTitle = null;
        Uri? artwork = null;
        if (TryPath(data, out var album, "albumOfTrack"))
        {
            albumTitle = String(album, "name");
            var albumValue = SpotifyId(String(album, "uri"), "album");
            if (albumValue != null)
                albumId = new(ProviderId, ProviderResourceKind.Album, albumValue);
            artwork = ArtworkUri(album);
        }

        var durationMs = TryPath(data, out var duration, "trackDuration")
            ? Integer(duration, "totalMilliseconds")
            : null;
        var explicitValue = TryPath(data, out var rating, "contentRating")
            ? String(rating, "label")?.Equals("EXPLICIT", StringComparison.OrdinalIgnoreCase)
            : null;
        var trackId = new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Track, id);
        var metadata = new ProviderTrackMetadata(
            trackId,
            title,
            artists,
            albumId,
            albumTitle,
            durationMs is null ? null : TimeSpan.FromMilliseconds(durationMs.Value),
            isExplicit: explicitValue,
            artwork: artwork == null ? null : new ProviderArtworkReference(publicUri: artwork));
        reasonCode = "ok";
        return new ProviderPlaylistTrack(position, trackId, metadata: metadata);
    }

    private static bool TryPlaylistEntry(
        JsonElement entry,
        out JsonElement wrapper,
        out JsonElement playlist,
        out string id)
    {
        wrapper = TryPath(entry, out var wrapped, "item") ? wrapped : entry;
        playlist = TryPath(wrapper, out var data, "data") ? data : wrapper;
        if (playlist.ValueKind != JsonValueKind.Object)
        {
            id = "";
            return false;
        }
        return TryPlaylistId(entry, wrapper, playlist, out id);
    }

    private static void CollectPlaylistEntries(
        JsonElement value,
        List<LibraryPlaylistEntry> entries,
        int depth = 0)
    {
        const int maximumDepth = 16;
        if (depth > maximumDepth)
            return;

        if (TryPlaylistEntry(value, out var wrapper, out var playlist, out var playlistId))
        {
            entries.Add(new(playlist, wrapper, value, playlistId));
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                CollectPlaylistEntries(item, entries, depth + 1);
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
            return;

        foreach (var childName in LibraryContainerProperties)
        {
            if (value.TryGetProperty(childName, out var child))
                CollectPlaylistEntries(child, entries, depth + 1);
        }
    }

    private static readonly string[] LibraryContainerProperties =
    [
        "item",
        "data",
        "items",
        "children",
        "entries",
        "content"
    ];

    private static bool TryPlaylistId(
        JsonElement entry,
        JsonElement wrapper,
        JsonElement playlist,
        out string id)
    {
        id = SpotifyId(
            String(entry, "uri") ??
            String(entry, "_uri") ??
            String(wrapper, "uri") ??
            String(wrapper, "_uri") ??
            String(playlist, "uri"),
            "playlist") ?? "";
        return id.Length > 0;
    }

    private static Uri? ArtworkUri(JsonElement value)
    {
        JsonElement sources;
        if (TryPath(value, out var imageItems, "images", "items") &&
            imageItems.ValueKind == JsonValueKind.Array &&
            imageItems.GetArrayLength() > 0 &&
            TryPath(imageItems[0], out sources, "sources"))
        {
            return LargestArtwork(sources);
        }
        if (TryPath(value, out sources, "coverArt", "sources"))
            return LargestArtwork(sources);
        if (TryPath(value, out sources, "visuals", "avatarImage", "sources"))
            return LargestArtwork(sources);
        return null;
    }

    private static Uri? LargestArtwork(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array)
            return null;
        return sources.EnumerateArray()
            .Select(source => new
            {
                Url = String(source, "url"),
                Width = Integer(source, "width") ?? 0
            })
            .Where(source => Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) &&
                             uri.Scheme == Uri.UriSchemeHttps &&
                             IsAllowedArtworkHost(uri.Host))
            .OrderByDescending(source => source.Width)
            .Select(source => new Uri(source.Url!))
            .FirstOrDefault();
    }

    private static bool IsAllowedArtworkHost(string host) =>
        host.Equals("i.scdn.co", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".spotifycdn.com", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            return delta;
        if (retryAfter?.Date is { } date)
            return date <= DateTimeOffset.UtcNow ? TimeSpan.Zero : date - DateTimeOffset.UtcNow;
        return TimeSpan.FromSeconds(30);
    }

    private static string? SpotifyId(string? uri, string kind)
    {
        var prefix = $"spotify:{kind}:";
        return uri?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? uri[prefix.Length..]
            : null;
    }

    private static int? AttributeInteger(JsonElement value, string key)
    {
        foreach (var attribute in Array(value, "attributes"))
        {
            if (!key.Equals(String(attribute, "key"), StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(String(attribute, "value"), out var number))
                return number;
        }
        return null;
    }

    private static int? PlaylistItemCount(JsonElement value) =>
        Integer(value, "totalCount") ??
        (TryPath(value, out var content, "content")
            ? Integer(content, "totalCount")
            : null) ??
        AttributeInteger(value, "core:item_count");

    private static bool TryOffset(string? cursor, out int offset)
    {
        if (cursor == null)
        {
            offset = 0;
            return true;
        }
        return int.TryParse(
                   cursor,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out offset) &&
               offset >= 0;
    }

    private static bool TryPath(JsonElement root, out JsonElement value, params string[] names)
    {
        value = root;
        foreach (var name in names)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(name, out value))
                return false;
        }
        return true;
    }

    private static List<JsonElement> Array(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : [];

    private static string? String(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return value.ValueKind == JsonValueKind.Object
            ? String(value, "text") ??
              String(value, "transformedLabel") ??
              String(value, "label")
            : null;
    }

    private static string? ContractText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (normalized.Length == 0)
            return null;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static int? Integer(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(
                   value.GetString(),
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }

    private sealed record PathfinderResponse(
        ProviderOutcome<byte[]> Outcome,
        byte[]? Body,
        int? HttpStatusCode,
        long ElapsedMilliseconds);
    private sealed record ArtworkCacheEntry(string Uri);
    private sealed record LibraryPlaylistEntry(
        JsonElement Playlist,
        JsonElement Wrapper,
        JsonElement Entry,
        string Id);
    internal sealed record PathfinderOperationDefinition(
        string OperationName,
        string Sha256Hash,
        int Version);
}
