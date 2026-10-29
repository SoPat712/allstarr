using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.ListenBrainz;

public sealed class ListenBrainzPlaylistCapabilityAdapter(HttpClient http, IProviderAccountSecretAccessor secrets)
    : IProviderPlaylistCapability
{
    public const string StableProviderId = "listenbrainz";
    public const string HttpClientName = "ListenBrainzPlaylists";
    private const string ExtensionKey = "https://musicbrainz.org/doc/jspf#playlist";
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int ListCount = 100;
    private static readonly Uri PublicBaseUri = new("https://api.listenbrainz.org/1/");
    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;

    public static bool TryNormalize(string? reference, out string normalized)
    {
        normalized = "";
        var value = reference?.Trim();
        if (value == null) return false;
        if (value.StartsWith("createdfor:", StringComparison.Ordinal) && SafeKind(value[11..]))
        { normalized = value; return true; }
        if (Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty)
        { normalized = id.ToString("D"); return true; }
        if (!TryOwnedUuid(value, "listenbrainz.org", "/playlist/", out normalized)) return false;
        return true;
    }

    public Task<ProviderOutcome<ProviderExternalResourceId>> FollowNewestAsync(
        ProviderExecutionContext context, ProviderExternalResourceId playlistId) => ExecuteAsync(context, false,
        async (baseUri, token, ct) =>
        {
            RequirePlaylist(playlistId, out var id);
            var username = await UsernameAsync(baseUri, token!, ct);
            if (id.StartsWith("createdfor:", StringComparison.Ordinal)) return Resource(id);
            var playlist = Playlist(await GetAsync(baseUri, "playlist/" + id, token, ct));
            if (PlaylistId(playlist) != id) throw new InvalidOperationException();
            var kind = SourcePatch(playlist);
            if (!SafeKind(kind) || Text(Extension(playlist), "created_for") != username)
                throw new ProviderFailure(new(ProviderErrorKind.Forbidden));
            return new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Playlist, "createdfor:" + kind);
        });

    public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        ProviderExecutionContext context, ProviderPlaylistTracksRequest request) => ExecuteAsync(context, true,
        async (baseUri, token, ct) =>
        {
            RequirePlaylist(request.PlaylistId, out var id);
            var offset = Offset(request.Page.Cursor);
            JsonElement playlist;
            var generated = id.StartsWith("createdfor:", StringComparison.Ordinal);
            if (generated)
            {
                if (token == null) throw new ProviderFailure(new(ProviderErrorKind.Forbidden));
                var username = await UsernameAsync(baseUri, token, ct);
                var kind = id[11..];
                var candidates = await ListAsync(baseUri, username, token, true, ct);
                var selected = candidates.Where(item => SourcePatch(item) == kind &&
                        Text(Extension(item), "created_for") == username && Date(item).HasValue)
                    .OrderByDescending(item => Date(item)).FirstOrDefault();
                if (selected.ValueKind == JsonValueKind.Undefined)
                    return TrackPage(new(request.PlaylistId, "ListenBrainz " + kind, new(username),
                        Hash(id + ":not-generated"), "This playlist is not generated yet.", trackCount: 0), [], request, offset);
                id = PlaylistId(selected);
                try { playlist = Playlist(await GetAsync(baseUri, "playlist/" + id, token, ct)); }
                catch (ProviderFailure failure) when (failure.Error.Kind == ProviderErrorKind.NotFound)
                { return TrackPage(Summary(selected, request.PlaylistId, "The generated playlist is unavailable.", 0), [], request, offset); }
                if (SourcePatch(playlist) != kind || Text(Extension(playlist), "created_for") != username)
                    throw new InvalidOperationException();
            }
            else playlist = Playlist(await GetAsync(baseUri, "playlist/" + id, token, ct));
            if (PlaylistId(playlist) != id) throw new InvalidOperationException();
            if (!playlist.TryGetProperty("track", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException();
            var mapped = tracks.EnumerateArray().Select((track, position) => MapTrack(track, id, position)).ToArray();
            return TrackPage(Summary(playlist, request.PlaylistId,
                mapped.Length == 0 ? "This playlist is empty." : null, mapped.Length), mapped, request, offset);
        });

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        ProviderExecutionContext context, ProviderUserPlaylistsRequest request) => DiscoverAsync(context, request.Page, null, request.SourceUserId);

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
        ProviderExecutionContext context, ProviderPlaylistSearchRequest request) => DiscoverAsync(context, request.Page, request.Query, null);

    private Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> DiscoverAsync(
        ProviderExecutionContext context, ProviderPageRequest page, string? search, string? sourceUserId) => ExecuteAsync(context, false,
        async (baseUri, token, ct) =>
        {
            if (sourceUserId != null) throw new ProviderFailure(new(ProviderErrorKind.Forbidden));
            var offset = Offset(page.Cursor);
            var username = await UsernameAsync(baseUri, token!, ct);
            var own = await ListAsync(baseUri, username, token!, false, ct);
            var created = await ListAsync(baseUri, username, token!, true, ct);
            var summaries = own.Concat(created.Where(item => Text(Extension(item), "created_for") == username))
                .DistinctBy(PlaylistId).Select(item => Summary(item, Resource(PlaylistId(item))))
                .Where(item => search == null || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (item.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray();
            var items = summaries.Skip(offset).Take(page.Limit).ToArray();
            var next = offset + items.Length < summaries.Length ? (offset + items.Length).ToString(CultureInfo.InvariantCulture) : null;
            return new ProviderPage<ProviderPlaylistSummary>(StableProviderId, items, next, next != null);
        });

    private async Task<List<JsonElement>> ListAsync(Uri baseUri, string username, string token, bool createdFor, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        var offset = 0;
        for (var page = 0; page < 10; page++)
        {
            var root = await GetAsync(baseUri, $"user/{Uri.EscapeDataString(username)}/playlists{(createdFor ? "/createdfor" : "")}?offset={offset}&count={ListCount}", token, ct);
            if (!root.TryGetProperty("playlists", out var items) || items.ValueKind != JsonValueKind.Array ||
                Integer(root, "offset") != offset || Integer(root, "count") != ListCount || items.GetArrayLength() > ListCount)
                throw new InvalidOperationException();
            var total = Integer(root, "playlist_count");
            if ((long)offset + items.GetArrayLength() > total) throw new InvalidOperationException();
            foreach (var item in items.EnumerateArray())
            {
                var playlist = Playlist(item);
                _ = PlaylistId(playlist);
                result.Add(playlist);
            }
            if ((long)offset + ListCount >= total) return result;
            if (items.GetArrayLength() == 0) throw new InvalidOperationException();
            offset += ListCount;
        }
        throw new InvalidOperationException();
    }

    private async Task<string> UsernameAsync(Uri baseUri, string token, CancellationToken ct)
    {
        var root = await GetAsync(baseUri, "validate-token", token, ct);
        if (!root.TryGetProperty("valid", out var valid) || valid.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException();
        if (!valid.GetBoolean()) throw new ProviderFailure(new(ProviderErrorKind.AccountNeedsReauthentication));
        var username = Text(root, "user_name");
        if (string.IsNullOrWhiteSpace(username) || username.Length > 500) throw new InvalidOperationException();
        return username;
    }

    private static ProviderPlaylistTrackPage TrackPage(ProviderPlaylistSummary summary, ProviderPlaylistTrack[] tracks,
        ProviderPlaylistTracksRequest request, int offset)
    {
        if (request.ExpectedRevision != null && request.ExpectedRevision != summary.SourceRevision)
            throw new InvalidOperationException();
        var items = tracks.Skip(offset).Take(request.Page.Limit).ToArray();
        var next = offset + items.Length < tracks.Length ? (offset + items.Length).ToString(CultureInfo.InvariantCulture) : null;
        return new(summary, new(StableProviderId, items, next, next != null, summary.SourceRevision));
    }

    private static ProviderPlaylistSummary Summary(JsonElement playlist, ProviderExternalResourceId requested,
        string? reason = null, int? trackCount = null)
    {
        var id = PlaylistId(playlist);
        var creator = Clip(Text(playlist, "creator") ?? Text(Extension(playlist), "creator") ?? "Unknown", 500)!;
        var description = string.Join("\n", new[] { reason, "Creator: " + creator,
            Text(playlist, "date") is { } date ? "Created: " + date : null,
            SourcePatch(playlist) is { } kind ? "Algorithm: " + kind : null, Text(playlist, "annotation") }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new(requested, Clip(Text(playlist, "title") ?? "ListenBrainz playlist", 500)!, new(creator),
            Hash(id + playlist.GetRawText() + reason), Clip(description, 4000), trackCount: trackCount,
            sourceETag: id, createdDate: Date(playlist)?.UtcDateTime);
    }

    private static ProviderPlaylistTrack MapTrack(JsonElement track, string playlistId, int position)
    {
        if (track.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        string? recording = null;
        if (track.TryGetProperty("identifier", out var identifiers))
        {
            if (identifiers.ValueKind != JsonValueKind.Array) throw new InvalidOperationException();
            foreach (var identifier in identifiers.EnumerateArray())
                if (identifier.ValueKind == JsonValueKind.String &&
                    TryOwnedUuid(identifier.GetString(), "musicbrainz.org", "/recording/", out var mbid))
                { recording = mbid; break; }
        }
        var id = new ProviderExternalResourceId(StableProviderId, ProviderResourceKind.Track, recording ?? $"{playlistId}:{position}");
        TimeSpan? duration = track.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var milliseconds) && milliseconds > 0 && milliseconds <= int.MaxValue
            ? TimeSpan.FromMilliseconds(milliseconds) : null;
        var metadata = new ProviderTrackMetadata(id, Clip(Text(track, "title") ?? "Unknown", 500)!,
            [new(Clip(Text(track, "creator") ?? "Unknown", 300)!)], albumTitle: Clip(Text(track, "album"), 500), duration: duration,
            providerIds: recording == null ? null : new Dictionary<string, string> { ["musicbrainzrecording"] = recording });
        return new(position, id, metadata: metadata);
    }

    private async Task<ProviderOutcome<T>> ExecuteAsync<T>(ProviderExecutionContext context, bool allowPublic,
        Func<Uri, string?, CancellationToken, Task<T>> operation)
    {
        if (context.ProviderId != StableProviderId || context.Account?.Scope == ProviderAccountScope.Shared ||
            context.Account == null && (!allowPublic || context.Actor.Kind != ProviderActorKind.PublicRead))
            return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Forbidden));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(context.Remaining(DateTimeOffset.UtcNow));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var value = context.Account == null ? await operation(PublicBaseUri, null, deadline.Token) :
                await secrets.UseAsync(context.Account, async bytes =>
                {
                    using var secret = JsonDocument.Parse(bytes);
                    if (secret.RootElement.ValueKind != JsonValueKind.Object) throw new ProviderFailure(new(ProviderErrorKind.AccountNeedsConfiguration));
                    var token = secret.RootElement.EnumerateObject().FirstOrDefault(item => item.Name.Equals("token", StringComparison.OrdinalIgnoreCase)).Value;
                    if (token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()))
                        throw new ProviderFailure(new(ProviderErrorKind.AccountNeedsConfiguration));
                    return await operation(ListenBrainzServiceEndpoint.FromSecret(secret.RootElement), token.GetString(), deadline.Token);
                }, deadline.Token);
            return ProviderOutcome<T>.Success(value);
        }
        catch (ProviderFailure failure) { return ProviderOutcome<T>.Failure(failure.Error); }
        catch (OperationCanceledException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Canceled)); }
        catch (HttpRequestException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.TransientFailure)); }
        catch (KeyNotFoundException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration)); }
        catch (UnauthorizedAccessException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Forbidden)); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.PermanentFailure)); }
    }

    private async Task<JsonElement> GetAsync(Uri baseUri, string path, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ListenBrainzServiceEndpoint.Route(baseUri, path));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var kind = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => ProviderErrorKind.AccountNeedsReauthentication,
                HttpStatusCode.Forbidden => ProviderErrorKind.Forbidden,
                HttpStatusCode.NotFound => ProviderErrorKind.NotFound,
                HttpStatusCode.TooManyRequests => ProviderErrorKind.RateLimited,
                _ => (int)response.StatusCode >= 500 ? ProviderErrorKind.TransientFailure : ProviderErrorKind.PermanentFailure
            };
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
            throw new ProviderFailure(new(kind, kind == ProviderErrorKind.RateLimited ? (retry < TimeSpan.Zero ? TimeSpan.Zero : retry) : null));
        }
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidOperationException();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidOperationException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 32 }, ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        return document.RootElement.Clone();
    }

    private static JsonElement Playlist(JsonElement root) => Object(root, "playlist", required: true);
    private static JsonElement Extension(JsonElement playlist) => Object(Object(playlist, "extension"), ExtensionKey);
    private static string? SourcePatch(JsonElement playlist) => Text(Object(Object(Extension(playlist), "additional_metadata"), "algorithm_metadata"), "source_patch");
    private static DateTimeOffset? Date(JsonElement playlist) => DateTimeOffset.TryParse(Text(playlist, "date"), CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    private static string PlaylistId(JsonElement playlist)
    {
        if (!TryNormalize(Text(playlist, "identifier"), out var id) || id.StartsWith("createdfor:", StringComparison.Ordinal)) throw new InvalidOperationException();
        return id;
    }
    private static void RequirePlaylist(ProviderExternalResourceId resource, out string id)
    {
        if (resource.ProviderId != StableProviderId || resource.ResourceKind != ProviderResourceKind.Playlist)
            throw new ProviderFailure(new(ProviderErrorKind.Forbidden));
        if (!TryNormalize(resource.Value, out id)) throw new InvalidOperationException();
    }
    private static JsonElement Object(JsonElement root, string key, bool required = false)
    {
        if (root.ValueKind == JsonValueKind.Undefined && !required) return default;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        if (!root.TryGetProperty(key, out var result)) return required ? throw new InvalidOperationException() : default;
        return result.ValueKind == JsonValueKind.Object ? result : throw new InvalidOperationException();
    }
    private static string? Text(JsonElement root, string key)
    {
        if (root.ValueKind == JsonValueKind.Undefined) return null;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException();
        return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
    }
    private static int Integer(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.TryGetInt32(out var result) && result >= 0
        ? result : throw new InvalidOperationException();
    private static int Offset(string? cursor) => cursor == null ? 0 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
        ? value : throw new InvalidOperationException();
    private static bool SafeKind(string? kind) => kind is { Length: > 0 and <= 100 } && kind[0] != '-' && kind[^1] != '-' &&
        !kind.Contains("--", StringComparison.Ordinal) && kind.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    private static bool TryOwnedUuid(string? value, string host, string prefix, out string id)
    {
        id = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(uri.AbsolutePath[prefix.Length..], "D", out var parsed) || parsed == Guid.Empty) return false;
        id = parsed.ToString("D"); return true;
    }
    private static ProviderExternalResourceId Resource(string id) => new(StableProviderId, ProviderResourceKind.Playlist, id);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string? Clip(string? value, int length) => value?.Length > length ? value[..length] : value;
    private sealed class ProviderFailure(ProviderError error) : Exception { public ProviderError Error { get; } = error; }

    public static ProviderRegistration CreateRegistration(ListenBrainzPlaylistCapabilityAdapter adapter) => new(
        new ProviderDescriptor(StableProviderId, "ListenBrainz", "Public playlists and playlists created for the connected listener.",
            ProviderOrigin.BuiltIn, sdkVersion: "1", compatibilityVersion: "listenbrainz-jspf-v1",
            capabilities: [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.Optional, "1", ["getUserPlaylists", "getPlaylistTracks", "searchPlaylists"],
                [ProviderAccountScope.Personal, ProviderAccountScope.Shared], supportsPublicRead: true)],
            new ProviderPermissionDescriptor([new Uri("https://api.listenbrainz.org/")], secretSettingKeys: ["token"]),
            settings: [new("token", ProviderSettingValueKind.Secret, ProviderSettingScope.ProviderAccount, "User token", required: true),
                new("baseUrl", ProviderSettingValueKind.Text, ProviderSettingScope.ProviderAccount, "API base URL")]), [adapter]);
}
