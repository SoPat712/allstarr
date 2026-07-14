using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Intelligence;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Recommendations;

public sealed class AudioMuseRecommendationClient(HttpClient http, IConfiguration configuration,
    IDbContextFactory<AllstarrDbContext>? factory = null, EncryptedSecretStore? secrets = null) : IAudioMuseRecommendationClient
{
    private string? ConfiguredUrl => configuration["Intelligence:AudioMuse:Url"];
    public bool IsAvailable => Uri.TryCreate(ConfiguredUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    public async Task<bool> CheckHealthAsync(IntelligenceScope scope, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return false;
        using var response = await http.GetAsync(new Uri(new Uri(ConfiguredUrl!.TrimEnd('/') + "/"), "api/health"), cancellationToken);
        return response.IsSuccessStatusCode;
    }
    public async Task<IReadOnlyList<RecommendationSourceItem>> RecommendAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken)
    {
        if (!IsAvailable) throw new NotSupportedException("AudioMuse-AI sidecar is not configured.");
        var endpoint = new Uri(new Uri(ConfiguredUrl!.TrimEnd('/') + "/"), "api/sonic_fingerprint/generate");
        var body = new Dictionary<string, object?> { ["n"] = query.Limit };
        SecretLease? credential = null;
        try
        {
            if (factory == null || secrets == null)
            {
                if (query.Scope.Protocol != "jellyfin") throw new NotSupportedException();
                body["jellyfin_user_identifier"] = query.Scope.OwnerUserId.ToString("D");
            }
            else
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                var identity = await db.BackendIdentities.AsNoTracking().SingleOrDefaultAsync(item =>
                    item.TenantId == query.Scope.TenantId && item.UserId == query.Scope.OwnerUserId &&
                    item.BackendType == query.Scope.Protocol && item.BackendInstanceId == query.Scope.BackendInstanceId,
                    cancellationToken) ?? throw new UnauthorizedAccessException();
                if (query.Scope.Protocol == "jellyfin")
                {
                    body["jellyfin_user_identifier"] = identity.PrincipalId;
                }
                else if (query.Scope.Protocol == "subsonic")
                {
                    var referenceId = await db.IntelligencePolicies.AsNoTracking().Where(item =>
                        item.TenantId == query.Scope.TenantId && item.OwnerUserId == query.Scope.OwnerUserId &&
                        item.Protocol == query.Scope.Protocol && item.BackendInstanceId == query.Scope.BackendInstanceId &&
                        item.LibraryScopeId == query.Scope.LibraryScopeId && item.Enabled)
                        .Select(item => item.TargetCredentialReferenceId).SingleOrDefaultAsync(cancellationToken);
                    if (!referenceId.HasValue) throw new UnauthorizedAccessException();
                    credential = await secrets.OpenAsync(referenceId.Value,
                        new SecretAccessContext(query.Scope.TenantId, AllowGlobal: false), cancellationToken);
                    using var secret = JsonDocument.Parse(credential.Value);
                    body["navidrome_user"] = Required(secret.RootElement, "username");
                    body["navidrome_password"] = Required(secret.RootElement, "password");
                }
                else throw new NotSupportedException();
            }
            using var response = await http.PostAsJsonAsync(endpoint, body, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented) throw new NotSupportedException();
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidOperationException();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new NotSupportedException("AudioMuse-AI sonic fingerprint contract is unavailable.");
            return document.RootElement.EnumerateArray().Take(query.Limit).Select(item =>
            {
                var backendId = Required(item, "item_id");
                var title = Required(item, "title");
                var artist = Required(item, "author");
                var album = item.TryGetProperty("album", out var albumNode) ? albumNode.GetString() : null;
                var distance = item.TryGetProperty("distance", out var distanceNode) && distanceNode.TryGetDouble(out var parsed)
                    ? Math.Max(0, parsed) : 1;
                var score = 1d / (1d + distance);
                return new RecommendationSourceItem($"backend:{backendId}", score,
                    [new("audiomuse-sonic-fingerprint", score, "AudioMuse-AI matched this track to the user's sonic listening fingerprint.")],
                    new("audiomuse-ai", Title: title, Artist: artist, Album: album, BackendItemId: backendId));
            }).ToArray();
        }
        finally { credential?.Dispose(); }
    }

    private static string Required(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString()! :
        throw new InvalidOperationException("AudioMuse-AI recommendation data is incomplete.");
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
    public bool IsConfigured => true;
    public async Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token) =>
        await accounts.HasAccountAsync(scope, "listenbrainz", token) ? new("listenbrainz", RecommendationProviderReadinessState.Ready) : new("listenbrainz", RecommendationProviderReadinessState.Unconfigured, "listenbrainz_scoped_account_missing");
    public Task<IReadOnlyList<RecommendationSourceItem>> GetRecommendationsAsync(ScopedRecommendationQuery query, CancellationToken token) =>
        accounts.UseAsync(query.Scope, "listenbrainz", async (secret, ct) =>
        {
            var username = Required(secret, "username"); var userToken = Required(secret, "token");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.listenbrainz.org/1/cf/recommendation/user/{Uri.EscapeDataString(username)}/recording?count={query.Limit}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", userToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidOperationException();
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!document.RootElement.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("mbids", out var mbids) || mbids.ValueKind != JsonValueKind.Array) return [];
            return (IReadOnlyList<RecommendationSourceItem>)mbids.EnumerateArray().Take(query.Limit).Select((node, index) =>
            {
                var mbid = node.ValueKind == JsonValueKind.String ? node.GetString() : node.TryGetProperty("recording_mbid", out var value) ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(mbid)) throw new InvalidOperationException("ListenBrainz recommendation identity is missing.");
                return new RecommendationSourceItem($"musicbrainz:{mbid}", Math.Max(.2, 1d - index / (double)Math.Max(1, query.Limit)),
                    [new("listenbrainz-collaborative-filtering", .85, "ListenBrainz collaborative filtering recommended this recording.")],
                    new("listenbrainz", MusicBrainzRecordingId: mbid));
            }).ToArray();
        }, token);
    private static string Required(JsonElement value, string property) => value.TryGetProperty(property, out var item) && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString()! : throw new InvalidOperationException("Recommendation account data is incomplete.");
}
