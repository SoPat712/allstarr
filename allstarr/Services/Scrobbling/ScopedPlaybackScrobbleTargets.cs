using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Services.Common;

namespace allstarr.Services.Scrobbling;

public sealed class LastFmScopedPlaybackScrobbleTarget(HttpClient http, IScopedRecommendationAccountAccessor accounts)
    : IExactScopePlaybackScrobbleTarget
{
    public string ProviderId => "lastfm";
    public Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken token) => accounts.HasAccountAsync(scope, ProviderId, token);
    public Task<ScopedPlaybackScrobbleResult> DeliverAsync(IntelligenceScope scope, PlaybackTransition transition, ScopedPlaybackTrack track,
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
            await response.Content.LoadIntoBufferAsync(64 * 1024, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return Classify(response, body, method == "track.scrobble");
        }, token);
    private static ScopedPlaybackScrobbleResult Classify(HttpResponseMessage response, string body, bool scrobble)
    {
        var retryAfter = RetryAfter(response);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var first = body.AsSpan().TrimStart()[0];
                if (first == '{') return ParseJson(response.StatusCode, body, scrobble, retryAfter);
                if (first == '<') return ParseXml(response.StatusCode, body, scrobble, retryAfter);
            }
            catch (JsonException) { return Retry("invalid-response", response.StatusCode, retryAfter); }
            catch (XmlException) { return Retry("invalid-response", response.StatusCode, retryAfter); }
            catch (InvalidOperationException) { return Retry("invalid-response", response.StatusCode, retryAfter); }
        }
        return HttpResult(response.StatusCode, retryAfter);
    }

    private static ScopedPlaybackScrobbleResult ParseJson(HttpStatusCode status, string body, bool scrobble, TimeSpan? retryAfter)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            return ApiError(Int(error), Text(root, "message"), status, retryAfter);
        if (status is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            return HttpResult(status, retryAfter);
        var containerName = scrobble ? "scrobbles" : "nowplaying";
        if (!root.TryGetProperty(containerName, out var container)) return HttpResult(status, retryAfter);
        if (!scrobble) return ScopedPlaybackScrobbleResult.Delivered(Details(status, corrections: JsonCorrections(container)));

        var accepted = 0;
        var ignored = 0;
        if (container.TryGetProperty("@attr", out var attributes))
        {
            accepted = IntProperty(attributes, "accepted");
            ignored = IntProperty(attributes, "ignored");
        }
        if (!container.TryGetProperty("scrobble", out var item)) return Retry("invalid-response", status, retryAfter);
        if (item.ValueKind == JsonValueKind.Array) item = item.EnumerateArray().FirstOrDefault();
        var ignoredNode = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("ignoredMessage", out var node)
            ? node
            : default;
        var ignoredCode = ignoredNode.ValueKind == JsonValueKind.Object ? Text(ignoredNode, "code") : null;
        var ignoredMessage = ignoredNode.ValueKind == JsonValueKind.Object ? Text(ignoredNode, "#text") : null;
        var details = Details(status, accepted, ignored, ignoredCode, ignoredMessage, JsonCorrections(item));
        if (ignored > 0 || ignoredCode is not (null or "0"))
            return ScopedPlaybackScrobbleResult.Ignored(ignoredCode,
                SafeOperationalText.Sanitize(ignoredMessage, 500), details);
        return accepted > 0
            ? ScopedPlaybackScrobbleResult.Delivered(details)
            : Retry("invalid-response", status, retryAfter, details);
    }

    private static ScopedPlaybackScrobbleResult ParseXml(HttpStatusCode status, string body, bool scrobble, TimeSpan? retryAfter)
    {
        using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = XDocument.Load(reader).Root;
        if (root == null) return Retry("invalid-response", status, retryAfter);
        var error = root.Element("error");
        if (root.Attribute("status")?.Value == "failed" || error != null)
            return ApiError(ParseInt(error?.Attribute("code")?.Value), error?.Value, status, retryAfter);
        if (status is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            return HttpResult(status, retryAfter);
        var container = root.Descendants(scrobble ? "scrobbles" : "nowplaying").FirstOrDefault();
        if (container == null) return HttpResult(status, retryAfter);
        if (!scrobble) return ScopedPlaybackScrobbleResult.Delivered(Details(status, corrections: XmlCorrections(container)));

        var accepted = ParseInt(container.Attribute("accepted")?.Value);
        var ignored = ParseInt(container.Attribute("ignored")?.Value);
        var item = container.Descendants("scrobble").FirstOrDefault();
        if (item == null) return Retry("invalid-response", status, retryAfter);
        var ignoredNode = item.Element("ignoredMessage") ?? item.Element("ignoredmessage");
        var ignoredCode = ignoredNode?.Attribute("code")?.Value;
        var ignoredMessage = ignoredNode?.Value;
        var details = Details(status, accepted, ignored, ignoredCode, ignoredMessage, XmlCorrections(item));
        if (ignored > 0 || ignoredCode is not (null or "0"))
            return ScopedPlaybackScrobbleResult.Ignored(ignoredCode,
                SafeOperationalText.Sanitize(ignoredMessage, 500), details);
        return accepted > 0
            ? ScopedPlaybackScrobbleResult.Delivered(details)
            : Retry("invalid-response", status, retryAfter, details);
    }

    private static ScopedPlaybackScrobbleResult ApiError(int code, string? providerMessage,
        HttpStatusCode status, TimeSpan? retryAfter)
    {
        var details = Details(status, apiErrorCode: code, providerMessage: providerMessage);
        if (code is 8 or 11 or 16 or 29)
            return ScopedPlaybackScrobbleResult.Retrying(code.ToString(CultureInfo.InvariantCulture),
                "Last.fm could not accept the listen yet.", retryAfter ?? (code == 29 ? TimeSpan.FromSeconds(30) : null), details);
        if (code is 4 or 9)
            return ScopedPlaybackScrobbleResult.Permanent(code.ToString(CultureInfo.InvariantCulture),
                "Reconnect the selected Last.fm account and replace its expired or revoked credentials.", true, details);
        if (code is 10 or 13 or 26)
            return ScopedPlaybackScrobbleResult.Permanent(code.ToString(CultureInfo.InvariantCulture),
                "The selected Last.fm account needs configuration.", detailsJson: details);
        return ScopedPlaybackScrobbleResult.Permanent(code.ToString(CultureInfo.InvariantCulture),
            code == 6 ? "Last.fm rejected the listen metadata." : "Last.fm rejected the listen.", detailsJson: details);
    }

    private static ScopedPlaybackScrobbleResult HttpResult(HttpStatusCode status, TimeSpan? retryAfter) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ScopedPlaybackScrobbleResult.Permanent(
            ((int)status).ToString(CultureInfo.InvariantCulture),
            "Reconnect the selected Last.fm account and replace its expired or revoked credentials.", true,
            Details(status)),
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError =>
            Retry("http-" + (int)status, status,
                retryAfter ?? (status == HttpStatusCode.TooManyRequests ? TimeSpan.FromSeconds(30) : null)),
        >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => Retry("invalid-response", status, retryAfter),
        _ => ScopedPlaybackScrobbleResult.Permanent(((int)status).ToString(CultureInfo.InvariantCulture),
            "Last.fm rejected the request.", detailsJson: Details(status))
    };

    private static ScopedPlaybackScrobbleResult Retry(string code, HttpStatusCode status,
        TimeSpan? retryAfter, string? details = null) => ScopedPlaybackScrobbleResult.Retrying(code,
        "Last.fm could not accept the listen yet.", retryAfter, details ?? Details(status));

    private static string Details(HttpStatusCode status, int? accepted = null, int? ignored = null,
        string? ignoredCode = null, string? ignoredMessage = null, object? corrections = null,
        int? apiErrorCode = null, string? providerMessage = null)
    {
        var values = new Dictionary<string, object> { ["httpStatus"] = (int)status };
        if (accepted.HasValue) values["accepted"] = accepted.Value;
        if (ignored.HasValue) values["ignored"] = ignored.Value;
        if (!string.IsNullOrWhiteSpace(ignoredCode)) values["ignoredCode"] = ignoredCode;
        if (SafeOperationalText.Sanitize(ignoredMessage, 500) is { } safeIgnored) values["ignoredMessage"] = safeIgnored;
        if (corrections != null) values["corrections"] = corrections;
        if (apiErrorCode.HasValue) values["apiErrorCode"] = apiErrorCode.Value;
        if (SafeOperationalText.Sanitize(providerMessage, 500) is { } safeMessage) values["providerMessage"] = safeMessage;
        return JsonSerializer.Serialize(values);
    }

    private static Dictionary<string, object>? JsonCorrections(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object) return null;
        var corrections = new Dictionary<string, object>();
        foreach (var name in new[] { "artist", "track", "album", "albumArtist" })
            if (container.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object &&
                Text(field, "corrected") == "1" && SafeOperationalText.Sanitize(Text(field, "#text"), 500) is { } value)
                corrections[name] = new { corrected = true, value };
        return corrections.Count == 0 ? null : corrections;
    }

    private static Dictionary<string, object>? XmlCorrections(XElement container)
    {
        var corrections = new Dictionary<string, object>();
        foreach (var name in new[] { "artist", "track", "album", "albumArtist" })
        {
            var field = container.Element(name);
            if (field?.Attribute("corrected")?.Value == "1" && SafeOperationalText.Sanitize(field.Value, 500) is { } value)
                corrections[name] = new { corrected = true, value };
        }
        return corrections.Count == 0 ? null : corrections;
    }

    private static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var node)
            ? node.ValueKind == JsonValueKind.String ? node.GetString() : node.ToString()
            : null;
    private static int Int(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        ? number : ParseInt(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString());
    private static int IntProperty(JsonElement value, string property) =>
        value.TryGetProperty(property, out var node) ? Int(node) : 0;
    private static int ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retry?.Date is { } date) return date <= DateTimeOffset.UtcNow ? TimeSpan.Zero : date - DateTimeOffset.UtcNow;
        return null;
    }
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
    public Task<ScopedPlaybackScrobbleResult> DeliverAsync(IntelligenceScope scope, PlaybackTransition transition, ScopedPlaybackTrack track,
        long? positionTicks, DateTimeOffset observedAt, string signalKey, CancellationToken token) =>
        accounts.UseAsync(scope, ProviderId, async (secret, ct) =>
        {
            var playing = transition is PlaybackTransition.Start or PlaybackTransition.InferredStart;
            var baseUri = ListenBrainzServiceEndpoint.FromSecret(secret);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                ListenBrainzServiceEndpoint.Route(baseUri, "submit-listens"));
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
            return response.StatusCode switch
            {
                >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices =>
                    ScopedPlaybackScrobbleResult.Delivered(JsonSerializer.Serialize(new { httpStatus = (int)response.StatusCode })),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ScopedPlaybackScrobbleResult.Permanent(
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                    "Reconnect the selected ListenBrainz account and replace its expired or revoked credentials.", true,
                    JsonSerializer.Serialize(new { httpStatus = (int)response.StatusCode })),
                HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError =>
                    ScopedPlaybackScrobbleResult.Retrying(
                        ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                        "ListenBrainz could not accept the listen yet.", RetryAfter(response),
                        JsonSerializer.Serialize(new { httpStatus = (int)response.StatusCode })),
                _ => ScopedPlaybackScrobbleResult.Permanent(
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                    "ListenBrainz rejected the listen.",
                    detailsJson: JsonSerializer.Serialize(new { httpStatus = (int)response.StatusCode }))
            };
        }, token);
    private static string Required(JsonElement value, string property) => value.TryGetProperty(property, out var node) && !string.IsNullOrWhiteSpace(node.GetString()) ? node.GetString()! : throw new InvalidOperationException("Scoped scrobble account is incomplete.");
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retry?.Date is { } date) return date <= DateTimeOffset.UtcNow ? TimeSpan.Zero : date - DateTimeOffset.UtcNow;
        return response.StatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromSeconds(30) : null;
    }
}
