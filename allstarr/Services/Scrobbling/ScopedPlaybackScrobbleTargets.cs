using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Globalization;
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
            Add(values, "album", track.Album);
            if (!string.Equals(track.AlbumArtist, track.Artist, StringComparison.Ordinal)) Add(values, "albumArtist", track.AlbumArtist);
            if (track.DurationMilliseconds is > 0) values["duration"] = (track.DurationMilliseconds.Value / 1000).ToString(CultureInfo.InvariantCulture);
            if (Guid.TryParseExact(track.RecordingMusicBrainzId, "D", out var mbid) && mbid != Guid.Empty) values["mbid"] = mbid.ToString("D");
            if (track.TrackNumber is > 0) values["trackNumber"] = track.TrackNumber.Value.ToString(CultureInfo.InvariantCulture);
            if (method == "track.scrobble")
            {
                values["chosenByUser"] = track.ChosenByUser ? "1" : "0";
                values["timestamp"] = observedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            }
            values["api_sig"] = Sign(values, Required(secret, "sharedSecret")); values["format"] = "json";
            using var response = await http.PostAsync("https://ws.audioscrobbler.com/2.0/", new FormUrlEncodedContent(values), ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
            response.EnsureSuccessStatusCode(); return true;
        }, token);
    private static void Add(IDictionary<string, string> values, string key, string? value)
    { if (!string.IsNullOrWhiteSpace(value)) values[key] = value; }
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
            var additionalInfo = new Dictionary<string, object>
            {
                ["submission_client"] = "Allstarr",
                ["submission_client_version"] = AppVersion.Version
            };
            if (track.DurationMilliseconds is > 0) additionalInfo["duration_ms"] = track.DurationMilliseconds.Value;
            if (Guid.TryParseExact(track.RecordingMusicBrainzId, "D", out var mbid) && mbid != Guid.Empty)
                additionalInfo["recording_mbid"] = mbid.ToString("D");
            if (track.TrackNumber is > 0) additionalInfo["tracknumber"] = track.TrackNumber.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(track.ClientClass)) additionalInfo["media_player"] = track.ClientClass;
            var metadata = new Dictionary<string, object>
            {
                ["artist_name"] = track.Artist,
                ["track_name"] = track.Title,
                ["additional_info"] = additionalInfo
            };
            if (!string.IsNullOrWhiteSpace(track.Album)) metadata["release_name"] = track.Album;
            var listen = new Dictionary<string, object> { ["track_metadata"] = metadata };
            if (!playing) listen["listened_at"] = observedAt.ToUnixTimeSeconds();
            request.Content = JsonContent.Create(new Dictionary<string, object>
            {
                ["listen_type"] = playing ? "playing_now" : "single",
                ["payload"] = new[] { listen }
            });
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
            response.EnsureSuccessStatusCode(); return true;
        }, token);
    private static string Required(JsonElement value, string property) => value.TryGetProperty(property, out var node) && !string.IsNullOrWhiteSpace(node.GetString()) ? node.GetString()! : throw new InvalidOperationException("Scoped scrobble account is incomplete.");
}
