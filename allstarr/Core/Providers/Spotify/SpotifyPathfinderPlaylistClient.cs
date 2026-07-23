using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Sources;

namespace allstarr.Core.Providers.Spotify;

/// <summary>
/// Account-bound Spotify playlist transport backed by the web player's persisted
/// Pathfinder queries. Authentication is deliberately supplied by the caller so this
/// transport can be shared by provider-core and compatibility callers.
/// </summary>
public sealed class SpotifyPathfinderPlaylistClient(
    HttpClient http,
    ILogger<SpotifyPathfinderPlaylistClient>? logger = null)
{
    internal const string LibraryOperation = "libraryV3";
    internal const string LibraryQueryHash = "50650f72ea32a99b5b46240bee22fea83024eec302478a9a75cfd05a0814ba99";
    internal const string PlaylistOperation = "fetchPlaylist";
    internal const string PlaylistQueryHash = "19ff1327c29e99c208c86d7a9d8f1929cfdf3d3202a0ff4253c821f1901aa94d";
    private const string ProviderId = SpotifyPlaylistCapabilityAdapter.StableProviderId;
    private static readonly Uri Endpoint = new("https://api-partner.spotify.com/pathfinder/v1/query");
    private readonly ConcurrentDictionary<string, ArtworkCacheEntry> _artwork = new(StringComparer.Ordinal);

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
            features = new[] { "LIKED_SONGS", "YOUR_EPISODES" },
            offset,
            limit = page.Limit
        };
        var response = await QueryAsync(token, LibraryOperation, LibraryQueryHash, variables, cancellationToken);
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
                    new ProviderError(ProviderErrorKind.CapabilityUnavailable));
            }

            var rawItems = Array(library, "items");
            var summaries = new List<ProviderPlaylistSummary>(rawItems.Count);
            foreach (var entry in rawItems)
            {
                if (!TryPlaylistEntry(entry, out var wrapper, out var playlist, out var playlistId))
                    continue;
                if (MapSummary(playlist, playlistId) is { } summary)
                    summaries.Add(summary);
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
        CancellationToken cancellationToken)
    {
        if (!TryOffset(request.Page.Cursor, out var offset))
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                new ProviderError(ProviderErrorKind.PermanentFailure));

        var variables = new
        {
            uri = $"spotify:playlist:{request.PlaylistId.Value}",
            offset,
            limit = request.Page.Limit
        };
        var response = await QueryAsync(
            token,
            PlaylistOperation,
            PlaylistQueryHash,
            variables,
            cancellationToken);
        if (!response.Outcome.IsSuccess)
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(response.Outcome.Error!);

        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            if (GraphQlFailure(document.RootElement, PlaylistOperation) is { } failure)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(failure);
            if (!TryPath(document.RootElement, out var playlist, "data", "playlistV2"))
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                    new ProviderError(ProviderErrorKind.CapabilityUnavailable));

            var summary = MapSummary(playlist, request.PlaylistId.Value);
            if (summary == null)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                    new ProviderError(ProviderErrorKind.CapabilityUnavailable));
            if (request.ExpectedRevision != null &&
                !request.ExpectedRevision.Equals(summary.SourceRevision, StringComparison.Ordinal))
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                    new ProviderError(ProviderErrorKind.PermanentFailure));

            var content = TryPath(playlist, out var contentValue, "content")
                ? contentValue
                : default;
            var rawItems = Array(content, "items");
            var tracks = new List<ProviderPlaylistTrack>(rawItems.Count);
            for (var index = 0; index < rawItems.Count; index++)
            {
                if (MapTrack(rawItems[index], offset + index) is { } track)
                    tracks.Add(track);
            }

            var consumed = rawItems.Count;
            var total = Integer(content, "totalCount") ?? summary.TrackCount;
            var nextOffset = offset + consumed;
            var hasNext = consumed > 0 && (total == null
                ? consumed >= request.Page.Limit
                : nextOffset < total);
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
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable));
        }
    }

    public async Task<ProviderOutcome<Uri>> GetPlaylistArtworkUriAsync(
        string token,
        ProviderArtworkReference artwork,
        CancellationToken cancellationToken)
    {
        var playlistId = artwork.ResourceId;
        if (playlistId == null)
            return ProviderOutcome<Uri>.Failure(new ProviderError(ProviderErrorKind.NotFound));
        if (TryCachedArtwork(playlistId.Value, artwork.Revision, out var cached))
            return ProviderOutcome<Uri>.Success(cached);

        var variables = new { uri = $"spotify:playlist:{playlistId.Value}", offset = 0, limit = 1 };
        var response = await QueryAsync(
            token,
            PlaylistOperation,
            PlaylistQueryHash,
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
            CacheArtwork(playlistId.Value, artwork.Revision, resolvedArtwork);
            return ProviderOutcome<Uri>.Success(resolvedArtwork);
        }
        catch (JsonException)
        {
            return ProviderOutcome<Uri>.Failure(new ProviderError(ProviderErrorKind.CapabilityUnavailable));
        }
    }

    private async Task<PathfinderResponse> QueryAsync(
        string token,
        string operation,
        string hash,
        object variables,
        CancellationToken cancellationToken)
    {
        var extensions = JsonSerializer.Serialize(new
        {
            persistedQuery = new { version = 1, sha256Hash = hash }
        });
        var parameters = new Dictionary<string, string>
        {
            ["operationName"] = operation,
            ["variables"] = JsonSerializer.Serialize(variables),
            ["extensions"] = extensions
        };
        var query = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Endpoint}?{query}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(ProviderOutcome<byte[]>.Failure(HttpFailure(response)), null);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new(ProviderOutcome<byte[]>.Success(body), body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return new(
                ProviderOutcome<byte[]>.Failure(new ProviderError(ProviderErrorKind.TransientFailure)),
                null);
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
        var diagnostics = errors
            .Select(error => String(error, "message"))
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message!.Length > 240 ? message[..240] : message)
            .Take(3)
            .ToArray();
        logger?.LogWarning(
            "Spotify Pathfinder operation {Operation} returned GraphQL errors: {Diagnostics}",
            operation,
            diagnostics.Length == 0 ? "No message supplied" : string.Join(" | ", diagnostics));
        var staleHash = errors.Any(error =>
            String(error, "message")?.Contains("persisted", StringComparison.OrdinalIgnoreCase) == true ||
            (TryPath(error, out var extensions, "extensions") &&
             String(extensions, "code")?.Contains("persisted", StringComparison.OrdinalIgnoreCase) == true));
        return new ProviderError(staleHash
            ? ProviderErrorKind.CapabilityUnavailable
            : ProviderErrorKind.PermanentFailure);
    }

    private ProviderPlaylistSummary? MapSummary(JsonElement value, string id)
    {
        var name = Text(value, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;
        var resource = new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Playlist, id);
        var ownerData = TryPath(value, out var owner, "ownerV2", "data") ? owner : default;
        var ownerId = String(ownerData, "username") ?? String(ownerData, "uri") ?? "unknown-owner";
        var ownerName = String(ownerData, "name") ?? String(ownerData, "username");
        var trackCount = Integer(value, "totalCount") ??
                         (TryPath(value, out var content, "content")
                             ? Integer(content, "totalCount")
                             : null) ??
                         AttributeInteger(value, "core:item_count");
        var revision = String(value, "revisionId") ??
                       $"pathfinder:{ProviderPlaylistSnapshotCollector.HashResource(resource)}:{trackCount ?? -1}";
        var summary = new ProviderPlaylistSummary(
            resource,
            name,
            new ProviderPlaylistOwner(ownerId, ownerName),
            revision,
            Text(value, "description"),
            new ProviderArtworkReference(resource, revision: revision),
            trackCount);
        if (ArtworkUri(value) is { } artwork)
            CacheArtwork(id, revision, artwork);
        return summary;
    }

    private bool TryCachedArtwork(string playlistId, string? revision, out Uri artwork)
    {
        var key = ArtworkKey(playlistId, revision);
        if (_artwork.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            artwork = entry.Uri;
            return true;
        }
        _artwork.TryRemove(key, out _);
        artwork = null!;
        return false;
    }

    private void CacheArtwork(string playlistId, string? revision, Uri artwork)
    {
        _artwork[ArtworkKey(playlistId, revision)] =
            new ArtworkCacheEntry(artwork, DateTimeOffset.UtcNow.AddMinutes(30));
    }

    private static string ArtworkKey(string playlistId, string? revision) =>
        $"{playlistId}\n{revision ?? ""}";

    private static string PropertyNames(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
            ? string.Join(", ", value.EnumerateObject().Select(property => property.Name).Take(12))
            : "none";

    private static ProviderPlaylistTrack? MapTrack(JsonElement item, int position)
    {
        if (!TryPath(item, out var data, "itemV2", "data"))
            return null;
        var id = SpotifyId(String(data, "uri"), "track");
        var title = String(data, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

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
            return null;

        ProviderExternalResourceId? albumId = null;
        string? albumTitle = null;
        if (TryPath(data, out var album, "albumOfTrack"))
        {
            albumTitle = String(album, "name");
            var albumValue = SpotifyId(String(album, "uri"), "album");
            if (albumValue != null)
                albumId = new(ProviderId, ProviderResourceKind.Album, albumValue);
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
            isExplicit: explicitValue);
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
            ? String(value, "text")
            : null;
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

    private sealed record PathfinderResponse(ProviderOutcome<byte[]> Outcome, byte[]? Body);
    private sealed record ArtworkCacheEntry(Uri Uri, DateTimeOffset ExpiresAt);
}
