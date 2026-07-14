using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;

namespace allstarr.Services.Scrobbling;

public sealed class LastFmScopedPlaybackScrobbleTarget(HttpClient http, IScopedRecommendationAccountAccessor accounts)
    : IExactScopePlaybackScrobbleTarget
{
    public string ProviderId => "lastfm";
    public Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken token) => accounts.HasAccountAsync(scope, ProviderId, token);
    public Task DeliverAsync(IntelligenceScope scope, PlaybackTransition transition, ScopedPlaybackTrack track,
        long? positionTicks, DateTimeOffset observedAt, string signalKey, CancellationToken token) =>
        accounts.UseAsync(scope, ProviderId, async (secret, ct) =>
        {
            var method = transition is PlaybackTransition.Start or PlaybackTransition.InferredStart ? "track.updateNowPlaying" : "track.scrobble";
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["api_key"] = Required(secret, "apiKey"),
                ["artist"] = track.Artist,
                ["method"] = method,
                ["sk"] = Required(secret, "sessionKey"),
                ["track"] = track.Title
            };
            if (method == "track.scrobble") values["timestamp"] = observedAt.ToUnixTimeSeconds().ToString();
            values["api_sig"] = Sign(values, Required(secret, "sharedSecret")); values["format"] = "json";
            using var response = await http.PostAsync("https://ws.audioscrobbler.com/2.0/", new FormUrlEncodedContent(values), ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
            response.EnsureSuccessStatusCode(); return true;
        }, token);
    private static string Required(JsonElement value, string property) => value.TryGetProperty(property, out var node) && !string.IsNullOrWhiteSpace(node.GetString()) ? node.GetString()! : throw new InvalidOperationException("Scoped scrobble account is incomplete.");
    private static string Sign(IEnumerable<KeyValuePair<string, string>> values, string secret)
    { var text = string.Concat(values.Where(x => x.Key != "format" && x.Key != "callback").Select(x => x.Key + x.Value)) + secret; return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text))); }
}

public sealed class ListenBrainzScopedPlaybackScrobbleTarget(HttpClient http, IScopedRecommendationAccountAccessor accounts)
    : IExactScopePlaybackScrobbleTarget
{
    public string ProviderId => "listenbrainz";
    public Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken token) => accounts.HasAccountAsync(scope, ProviderId, token);
    public Task DeliverAsync(IntelligenceScope scope, PlaybackTransition transition, ScopedPlaybackTrack track,
        long? positionTicks, DateTimeOffset observedAt, string signalKey, CancellationToken token) =>
        accounts.UseAsync(scope, ProviderId, async (secret, ct) =>
        {
            var playing = transition is PlaybackTransition.Start or PlaybackTransition.InferredStart;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.listenbrainz.org/1/submit-listens");
            request.Headers.Authorization = new("Token", Required(secret, "token"));
            request.Content = JsonContent.Create(new
            {
                listen_type = playing ? "playing_now" : "single",
                payload = new[] { new
            { listened_at = playing ? (long?)null : observedAt.ToUnixTimeSeconds(), track_metadata = new { artist_name = track.Artist,
                track_name = track.Title, release_name = track.Album, additional_info = new { duration_ms = track.DurationMilliseconds, submission_client = "Allstarr", submission_client_version = AppVersion.Version } } } }
            });
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
            response.EnsureSuccessStatusCode(); return true;
        }, token);
    private static string Required(JsonElement value, string property) => value.TryGetProperty(property, out var node) && !string.IsNullOrWhiteSpace(node.GetString()) ? node.GetString()! : throw new InvalidOperationException("Scoped scrobble account is incomplete.");
}
