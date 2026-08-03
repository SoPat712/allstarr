using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Recommendations;

public sealed class AudioMuseRecommendationClient(
    IProviderRegistry providers,
    IScopedRecommendationAccountAccessor accounts,
    ILocalRecommendationCatalog catalog) : IAudioMuseRecommendationClient
{
    private const string ProviderId = "audiomuse-ai";
    public bool IsAvailable => providers.TryGetCapability<IProviderIntelligenceCapability>(
        ProviderId, ProviderCapabilityKind.Intelligence, out _);

    public async Task<bool> CheckHealthAsync(IntelligenceScope scope, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return false;
        var context = await ContextAsync(scope, "intelligence-health", cancellationToken);
        if (context == null || !providers.TryGetCapability<IProviderHealthProbeCapability>(
                ProviderId, ProviderCapabilityKind.Health, out var health)) return false;
        var result = await health!.ProbeAsync(context, new(ProviderCapabilityKind.Intelligence));
        return result.IsSuccess && result.Value?.Status == ProviderProbeStatus.Healthy;
    }

    public async Task<IReadOnlyList<RecommendationSourceItem>> RecommendAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Min(query.Limit, 200);
        var (capability, context) = await CapabilityAsync(query.Scope, "intelligence-recommend", cancellationToken);
        var outcome = await capability.RecommendAsync(context, query.SeedTrackKeys, limit);
        var excluded = query.SeedTrackKeys.Select(LocalRecommendationCatalog.NormalizeTrackKey)
            .ToHashSet(StringComparer.Ordinal);
        return await MapTracksAsync(query.Scope, context, Require(outcome, cancellationToken), excluded,
            limit, "audiomuse-intelligence",
            "AudioMuse-AI found this song from your selected listening profile.", cancellationToken);
    }

    public async Task<AudioMusePathResult> FindPathAsync(IntelligenceScope scope, string startTrackId,
        string endTrackId, int limit, CancellationToken cancellationToken)
    {
        var start = LocalRecommendationCatalog.NormalizeTrackKey(startTrackId);
        var end = LocalRecommendationCatalog.NormalizeTrackKey(endTrackId);
        var (capability, context) = await CapabilityAsync(scope, "intelligence-path", cancellationToken);
        var result = Require(await capability.FindPathAsync(context, start, end, limit), cancellationToken);
        var tracks = await MapTracksAsync(scope, context, result.Tracks,
            new HashSet<string>(StringComparer.Ordinal), limit, "audiomuse-path",
            "AudioMuse-AI placed this song in the selected path.", cancellationToken);
        if (tracks.Count < 2 || tracks[0].Identity?.BackendItemId != start ||
            tracks[^1].Identity?.BackendItemId != end ||
            tracks.Select(item => item.Identity!.BackendItemId).Distinct(StringComparer.Ordinal).Count() != tracks.Count)
            throw new InvalidOperationException("AudioMuse-AI returned an invalid song path.");
        return new(tracks, result.TotalDistance);
    }

    public async Task<IReadOnlyList<RecommendationSourceItem>> BlendAsync(IntelligenceScope scope,
        IReadOnlyList<string> positiveSeedTrackIds, IReadOnlyList<string> negativeSeedTrackIds,
        int limit, CancellationToken cancellationToken)
    {
        var positive = positiveSeedTrackIds.Select(LocalRecommendationCatalog.NormalizeTrackKey).ToArray();
        var negative = negativeSeedTrackIds.Select(LocalRecommendationCatalog.NormalizeTrackKey).ToArray();
        var (capability, context) = await CapabilityAsync(scope, "intelligence-blend", cancellationToken);
        var result = Require(await capability.BlendAsync(context, positive, negative, limit), cancellationToken);
        var excluded = positive.Concat(negative).ToHashSet(StringComparer.Ordinal);
        return await MapTracksAsync(scope, context, result, excluded, limit, "audiomuse-blend",
            "AudioMuse-AI matched your selected song choices.", cancellationToken);
    }

    public async Task<AudioMuseMapPage> GetMapAsync(IntelligenceScope scope, ProviderPageRequest page,
        CancellationToken cancellationToken)
    {
        var (capability, context) = await CapabilityAsync(scope, "intelligence-map", cancellationToken);
        var result = Require(await capability.GetMapAsync(context, page), cancellationToken);
        var local = await catalog.ResolveBackendItemsAsync(scope,
            result.Items.Select(item => item.TrackId).ToArray(), cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = result.Items.Where(item => seen.Add(item.TrackId) && local.ContainsKey(item.TrackId))
            .Select(item => new AudioMuseMapPoint(local[item.TrackId] with { ProviderId = ProviderId },
                item.X, item.Y, item.ClusterId)).ToArray();
        return new(items, result.Projection, result.NextCursor,
            result.IsPartial || items.Length != result.Items.Count, result.SnapshotVersion);
    }

    private async Task<(IProviderIntelligenceCapability Capability, ProviderExecutionContext Context)> CapabilityAsync(
        IntelligenceScope scope, string operation, CancellationToken cancellationToken)
    {
        if (!providers.TryGetCapability<IProviderIntelligenceCapability>(
                ProviderId, ProviderCapabilityKind.Intelligence, out var capability))
            throw new NotSupportedException("AudioMuse-AI extension is not installed.");
        var context = await ContextAsync(scope, operation, cancellationToken)
            ?? throw new NotSupportedException("AudioMuse-AI has no exact-scope account.");
        return (capability!, context);
    }

    private async Task<IReadOnlyList<RecommendationSourceItem>> MapTracksAsync(
        IntelligenceScope scope, ProviderExecutionContext context,
        IReadOnlyList<ProviderIntelligenceTrack> tracks, IReadOnlySet<string> excluded, int limit,
        string signalCode, string fallbackExplanation, CancellationToken cancellationToken)
    {
        var local = await catalog.ResolveBackendItemsAsync(scope,
            tracks.Select(item => item.TrackId).ToArray(), cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return tracks.Where(item => !excluded.Contains(item.TrackId) && seen.Add(item.TrackId) &&
                local.ContainsKey(item.TrackId)).Take(limit)
            .Select(item => new RecommendationSourceItem(item.TrackId, item.Score,
                [new(signalCode, item.Score, item.Explanation ?? fallbackExplanation)],
                local[item.TrackId] with { ProviderId = ProviderId }, context.Account!.AccountId,
                $"account:{context.Account.Revision}"))
            .ToArray();
    }

    private static T Require<T>(ProviderOutcome<T> outcome, CancellationToken cancellationToken)
    {
        if (outcome.IsSuccess) return outcome.Value!;
        throw outcome.Error?.Kind switch
        {
            ProviderErrorKind.AccountNeedsConfiguration or ProviderErrorKind.NotSupported or
                ProviderErrorKind.CapabilityUnavailable => new NotSupportedException(),
            ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.Unauthorized or
                ProviderErrorKind.Forbidden => new UnauthorizedAccessException(),
            ProviderErrorKind.Canceled => new OperationCanceledException(cancellationToken),
            _ => new InvalidOperationException("AudioMuse-AI discovery failed.")
        };
    }

    private async Task<ProviderExecutionContext?> ContextAsync(
        IntelligenceScope scope, string operation, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAccountAsync(scope, ProviderId, cancellationToken);
        if (account == null) return null;
        var actor = new ProviderActorContext(scope.TenantId, ProviderActorKind.User, scope.OwnerUserId,
            new(scope.Protocol, scope.BackendInstanceId, scope.OwnerUserId.ToString("D")));
        return new(actor, ProviderId, account, new(scope.TenantId, scope.LibraryScopeId),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, false, false, false, [ProviderId]),
            operation, Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken);
    }
}

public sealed class LastFmRecommendationClient(HttpClient http, IScopedRecommendationAccountAccessor accounts)
    : ILastFmRecommendationClient
{
    public bool IsConfigured => true;
    public async Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token) =>
        await accounts.HasAccountAsync(scope, "lastfm", token) ? new("lastfm", RecommendationProviderReadinessState.Ready) : new("lastfm", RecommendationProviderReadinessState.Unconfigured, "lastfm_scoped_account_missing");
    public Task<IReadOnlyList<RecommendationSourceItem>> GetSimilarTracksAsync(ScopedRecommendationQuery query, CancellationToken token) =>
        accounts.UseAsync(query.Scope, "lastfm", async (secret, ct) =>
        {
            var apiKey = Required(secret, "apiKey"); var username = Required(secret, "username");
            var uri = new Uri($"https://ws.audioscrobbler.com/2.0/?method=user.gettoptracks&user={Uri.EscapeDataString(username)}&api_key={Uri.EscapeDataString(apiKey)}&format=json&period=3month&limit=5");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
            response.EnsureSuccessStatusCode();
            await using var stream = await Bounded(response, ct); using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!document.RootElement.TryGetProperty("toptracks", out var top) || !top.TryGetProperty("track", out var tracks) || tracks.ValueKind != JsonValueKind.Array) return [];
            var seeds = tracks.EnumerateArray().Take(5).Select(track => (Title: Required(track, "name"),
                Artist: track.GetProperty("artist").GetProperty("name").GetString() ?? "unknown")).ToArray();
            var results = new Dictionary<string, RecommendationSourceItem>(StringComparer.Ordinal);
            foreach (var seed in seeds)
            {
                var similarUri = new Uri($"https://ws.audioscrobbler.com/2.0/?method=track.getsimilar&artist={Uri.EscapeDataString(seed.Artist)}&track={Uri.EscapeDataString(seed.Title)}&api_key={Uri.EscapeDataString(apiKey)}&format=json&limit={query.Limit}");
                using var similarResponse = await http.GetAsync(similarUri, HttpCompletionOption.ResponseHeadersRead, ct); similarResponse.EnsureSuccessStatusCode();
                await using var similarStream = await Bounded(similarResponse, ct); using var similarDocument = await JsonDocument.ParseAsync(similarStream, cancellationToken: ct);
                if (!similarDocument.RootElement.TryGetProperty("similartracks", out var similar) || !similar.TryGetProperty("track", out var similarTracks)) continue;
                foreach (var track in similarTracks.EnumerateArray())
                {
                    var title = Required(track, "name"); var artist = track.GetProperty("artist").GetProperty("name").GetString() ?? "unknown";
                    if (seeds.Any(value => value.Title.Equals(title, StringComparison.OrdinalIgnoreCase) && value.Artist.Equals(artist, StringComparison.OrdinalIgnoreCase))) continue;
                    var mbid = track.TryGetProperty("mbid", out var mbidNode) && !string.IsNullOrWhiteSpace(mbidNode.GetString()) ? mbidNode.GetString() : null;
                    var key = mbid == null ? $"lastfm-text:{Hash(artist, title)}" : $"musicbrainz:{mbid}";
                    var score = track.TryGetProperty("match", out var match) && double.TryParse(match.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 0, 1) : .5;
                    results.TryAdd(key, new(key, score, [new("lastfm-similar-to-top-track", .9, $"Last.fm relates this recording to a recent top track.")],
                        new("lastfm", MusicBrainzRecordingId: mbid, Title: title, Artist: artist)));
                    if (results.Count >= query.Limit) break;
                }
                if (results.Count >= query.Limit) break;
            }
            return (IReadOnlyList<RecommendationSourceItem>)results.Values.Take(query.Limit).ToArray();
        }, token);
    private static string Required(JsonElement value, string property) => value.TryGetProperty(property, out var item) && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString()! : throw new InvalidOperationException("Recommendation account data is incomplete.");
    private static async Task<Stream> Bounded(HttpResponseMessage response, CancellationToken token)
    { if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidOperationException(); return await response.Content.ReadAsStreamAsync(token); }
    private static string Hash(string artist, string title) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{artist.Trim().ToLowerInvariant()}\n{title.Trim().ToLowerInvariant()}")));
}

public sealed class ListenBrainzRecommendationClient(HttpClient http, IScopedRecommendationAccountAccessor accounts)
    : IListenBrainzRecommendationClient
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumPlaylistPages = 10;
    public bool IsConfigured => true;
    public async Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token) =>
        await accounts.HasAccountAsync(scope, "listenbrainz", token) ? new("listenbrainz", RecommendationProviderReadinessState.Ready) : new("listenbrainz", RecommendationProviderReadinessState.Unconfigured, "listenbrainz_scoped_account_missing");
    public Task<IReadOnlyList<RecommendationSourceItem>> GetRecommendationsAsync(ScopedRecommendationQuery query,
        ListenBrainzDiscoveryKind kind, CancellationToken token) =>
        accounts.UseAsync(query.Scope, "listenbrainz", async (secret, ct) =>
        {
            var username = Required(secret, "username"); var userToken = Required(secret, "token");
            return kind switch
            {
                ListenBrainzDiscoveryKind.CollaborativeFiltering => await CollaborativeAsync(query, username, userToken, ct),
                ListenBrainzDiscoveryKind.WeeklyExploration => await PlaylistAsync(query, username, userToken,
                    "weekly-exploration", "listenbrainz-weekly-exploration",
                    "ListenBrainz included this track in your latest Weekly Exploration playlist.", ct),
                ListenBrainzDiscoveryKind.WeeklyJams => await PlaylistAsync(query, username, userToken,
                    "weekly-jams", "listenbrainz-weekly-jams",
                    "ListenBrainz included this track in your latest Weekly Jams playlist.", ct),
                ListenBrainzDiscoveryKind.TopRecordings => await TopRecordingsAsync(query, username, userToken, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }, token);

    private async Task<IReadOnlyList<RecommendationSourceItem>> CollaborativeAsync(
        ScopedRecommendationQuery query, string username, string token, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
            $"cf/recommendation/user/{Uri.EscapeDataString(username)}/recording?count={query.Limit}", token,
            cancellationToken);
        if (!document.RootElement.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("mbids", out var mbids) || mbids.ValueKind != JsonValueKind.Array) return [];
        return mbids.EnumerateArray().Take(query.Limit).Select((node, index) =>
        {
            var raw = node.ValueKind == JsonValueKind.String ? node.GetString() :
                node.ValueKind == JsonValueKind.Object && node.TryGetProperty("recording_mbid", out var value)
                    ? value.GetString() : null;
            var mbid = Mbid(raw);
            return new RecommendationSourceItem($"musicbrainz:{mbid}", Score(index, query.Limit),
                [new("listenbrainz-collaborative-filtering", .85,
                    "ListenBrainz collaborative filtering recommended this recording.")],
                new("listenbrainz", MusicBrainzRecordingId: mbid));
        }).ToArray();
    }

    private async Task<IReadOnlyList<RecommendationSourceItem>> TopRecordingsAsync(
        ScopedRecommendationQuery query, string username, string token, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
            $"stats/user/{Uri.EscapeDataString(username)}/recordings?count={query.Limit}&range=month", token,
            cancellationToken);
        if (!document.RootElement.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
            return [];
        return recordings.EnumerateArray().Take(query.Limit).Select((item, index) =>
        {
            var title = Text(item, "track_name", true)!;
            var artist = Text(item, "artist_name", true)!;
            var album = Text(item, "release_name", false);
            var mbid = OptionalMbid(Text(item, "recording_mbid", false));
            var key = mbid == null ? $"listenbrainz-text:{Hash(artist, title, album)}" : $"musicbrainz:{mbid}";
            return new RecommendationSourceItem(key, Score(index, query.Limit),
                [new("listenbrainz-top-recordings", .9,
                    "This was one of your most-played ListenBrainz tracks this month.")],
                new("listenbrainz", MusicBrainzRecordingId: mbid, Title: title, Artist: artist, Album: album));
        }).ToArray();
    }

    private async Task<IReadOnlyList<RecommendationSourceItem>> PlaylistAsync(
        ScopedRecommendationQuery query, string username, string token, string playlistType,
        string signalCode, string explanation, CancellationToken cancellationToken)
    {
        var playlistId = await LatestPlaylistAsync(username, token, playlistType, cancellationToken);
        using var document = await GetAsync($"playlist/{playlistId}", token, cancellationToken);
        if (!document.RootElement.TryGetProperty("playlist", out var playlist) ||
            !playlist.TryGetProperty("track", out var tracks) || tracks.ValueKind != JsonValueKind.Array) return [];
        return tracks.EnumerateArray().Take(query.Limit).Select((item, index) =>
        {
            var title = Text(item, "title", true)!;
            var artist = Text(item, "creator", true)!;
            var album = Text(item, "album", false);
            var mbid = RecordingMbid(item);
            var key = mbid == null ? $"listenbrainz-text:{Hash(artist, title, album)}" : $"musicbrainz:{mbid}";
            return new RecommendationSourceItem(key, Score(index, query.Limit),
                [new(signalCode, .9, explanation)],
                new("listenbrainz", MusicBrainzRecordingId: mbid, Title: title, Artist: artist, Album: album));
        }).ToArray();
    }

    private async Task<string> LatestPlaylistAsync(
        string username, string token, string playlistType, CancellationToken cancellationToken)
    {
        var offset = 0;
        DateTimeOffset? newest = null;
        string? selected = null;
        var complete = false;
        for (var page = 0; page < MaximumPlaylistPages; page++)
        {
            using var document = await GetAsync(
                $"user/{Uri.EscapeDataString(username)}/playlists/createdfor?offset={offset}", token,
                cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("playlists", out var playlists) || playlists.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("ListenBrainz playlist data is malformed.");
            foreach (var item in playlists.EnumerateArray())
            {
                if (!TryPlaylist(item, playlistType, out var id, out var createdAt)) continue;
                if (newest == null || createdAt > newest) { newest = createdAt; selected = id; }
            }
            var count = Integer(root, "count");
            var responseOffset = Integer(root, "offset");
            var total = Integer(root, "playlist_count");
            if (count == 0 || responseOffset + count >= total) { complete = true; break; }
            var next = responseOffset + count;
            if (next <= offset) throw new InvalidOperationException("ListenBrainz playlist paging is malformed.");
            offset = next;
        }
        if (!complete) throw new InvalidOperationException("ListenBrainz playlist paging exceeded its safe limit.");
        return selected ?? throw new InvalidOperationException("ListenBrainz has not generated that playlist yet.");
    }

    private static bool TryPlaylist(JsonElement item, string playlistType, out string id,
        out DateTimeOffset createdAt)
    {
        id = ""; createdAt = default;
        if (!item.TryGetProperty("playlist", out var playlist) || playlist.ValueKind != JsonValueKind.Object ||
            !playlist.TryGetProperty("extension", out var extension) ||
            !extension.TryGetProperty("https://musicbrainz.org/doc/jspf#playlist", out var jspf) ||
            !jspf.TryGetProperty("additional_metadata", out var metadata) ||
            !metadata.TryGetProperty("algorithm_metadata", out var algorithm) ||
            Text(algorithm, "source_patch", false) != playlistType) return false;
        var identifier = Text(playlist, "identifier", true)!;
        if (!DateTimeOffset.TryParse(Text(playlist, "date", true), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out createdAt))
            throw new InvalidOperationException("ListenBrainz playlist date is malformed.");
        id = Mbid(identifier.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
        return true;
    }

    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.listenbrainz.org/1/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidOperationException();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidOperationException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
    }

    private static string? RecordingMbid(JsonElement item)
    {
        if (!item.TryGetProperty("identifier", out var identifiers) || identifiers.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var identifier in identifiers.EnumerateArray())
        {
            if (identifier.ValueKind != JsonValueKind.String) continue;
            var candidate = OptionalMbid(identifier.GetString()?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
            if (candidate != null) return candidate;
        }
        return null;
    }

    private static string Mbid(string? value) => OptionalMbid(value) ??
        throw new InvalidOperationException("ListenBrainz recording identity is malformed.");
    private static string? OptionalMbid(string? value) => Guid.TryParse(value, out var parsed)
        ? parsed.ToString("D") : null;
    private static int Integer(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var parsed) && parsed >= 0
            ? parsed : throw new InvalidOperationException("ListenBrainz paging is malformed.");
    private static string? Text(JsonElement value, string name, bool required)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            if (!required) return null;
            throw new InvalidOperationException("ListenBrainz recommendation data is malformed.");
        }
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("ListenBrainz recommendation data is malformed.");
        var text = property.GetString()?.Trim();
        if (text is { Length: > 0 and <= 500 } && !text.Any(char.IsControl)) return text;
        if (!required && string.IsNullOrEmpty(text)) return null;
        throw new InvalidOperationException("ListenBrainz recommendation data is malformed.");
    }
    private static double Score(int index, int limit) => Math.Max(.2, 1d - index / (double)Math.Max(1, limit));
    private static string Hash(string artist, string title, string? album) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{artist.Trim().ToLowerInvariant()}\n{title.Trim().ToLowerInvariant()}\n{album?.Trim().ToLowerInvariant()}")));
    private static string Required(JsonElement value, string property) => value.TryGetProperty(property, out var item) && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString()! : throw new InvalidOperationException("Recommendation account data is incomplete.");
}
