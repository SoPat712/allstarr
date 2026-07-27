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
    IScopedRecommendationAccountAccessor accounts) : IAudioMuseRecommendationClient
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
        if (!providers.TryGetCapability<IProviderIntelligenceCapability>(
                ProviderId, ProviderCapabilityKind.Intelligence, out var capability))
            throw new NotSupportedException("AudioMuse-AI extension is not installed.");
        var context = await ContextAsync(query.Scope, "intelligence-recommend", cancellationToken)
            ?? throw new NotSupportedException("AudioMuse-AI has no exact-scope account.");
        var outcome = await capability!.RecommendAsync(context, query.SeedTrackKeys, query.Limit);
        if (!outcome.IsSuccess)
        {
            switch (outcome.Error?.Kind)
            {
                case ProviderErrorKind.AccountNeedsConfiguration:
                case ProviderErrorKind.NotSupported:
                case ProviderErrorKind.CapabilityUnavailable:
                    throw new NotSupportedException();
                case ProviderErrorKind.Forbidden:
                    throw new UnauthorizedAccessException();
                default:
                    throw new InvalidOperationException("AudioMuse-AI recommendation failed.");
            }
        }
        return outcome.Value!.Select(item => new RecommendationSourceItem(
            $"provider:{ProviderId}:{item.TrackId}", item.Score,
            [new("audiomuse-intelligence", item.Score,
                item.Explanation ?? "AudioMuse-AI identified this track from the scoped listening profile.")],
            new(ProviderId, Title: item.Title, Artist: item.Artist, Album: item.Album,
                BackendItemId: item.TrackId), context.Account!.AccountId,
            $"account:{context.Account.Revision}")).ToArray();
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
