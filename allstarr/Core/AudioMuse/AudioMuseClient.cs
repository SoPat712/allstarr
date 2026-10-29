using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Core.Secrets;

namespace allstarr.Core.AudioMuse;

internal sealed record AudioMuseServer(string Id, string Type, string Url);
internal sealed record AudioMuseCoverage(string ServerId, bool Enabled, int? Analyzed);
internal sealed record AudioMuseTrack(string Id);
internal sealed record AudioMuseScores(double? Energy, double? Relaxed, double? Danceable, double? Party);

public sealed class AudioMuseClient(IHttpClientFactory clients, EncryptedSecretStore secrets)
{
    internal const string HttpClientName = "AudioMuse";

    internal async Task<bool> HealthAsync(AudioMuseEndpoint endpoint, CancellationToken token) =>
        Text(await GetAsync(endpoint, "api/health", token), "status") == "ok";

    internal async Task<string?> VersionAsync(AudioMuseEndpoint endpoint, CancellationToken token) =>
        Text(await GetAsync(endpoint, "api/version", token), "app_version");

    internal async Task<AudioMuseServer[]> ServersAsync(AudioMuseEndpoint endpoint, CancellationToken token)
    {
        var root = await GetAsync(endpoint, "api/servers", token);
        return Property(root, "servers").EnumerateArray().Select(s => new AudioMuseServer(
            Text(s, "server_id") ?? "", Text(s, "server_type") ?? "", Text(Property(s, "creds"), "url") ?? "")).ToArray();
    }

    internal async Task<AudioMuseCoverage> CoverageAsync(AudioMuseEndpoint endpoint, string server, CancellationToken token)
    {
        var root = await GetAsync(endpoint, "api/models?server=" + Escape(server), token);
        var model = Property(Property(root, "models"), "musicnn");
        var count = Number(Property(model, "local"), "count");
        return new(Text(root, "server_id") ?? "", Property(model, "enabled").ValueKind == JsonValueKind.True,
            count is >= 0 and <= int.MaxValue ? (int)count.Value : null);
    }

    internal async Task<AudioMuseTrack[]> SearchAsync(AudioMuseEndpoint endpoint, string server, string query, CancellationToken token) =>
        Tracks(await GetAsync(endpoint, "api/search_tracks?search_query=" + Escape(query) + "&start=0&end=150&server=" + Escape(server), token));

    internal async Task<bool> TrackAsync(AudioMuseEndpoint endpoint, string server, string id, CancellationToken token) =>
        Text(await GetAsync(endpoint, "api/track?item_id=" + Escape(id) + "&server=" + Escape(server), token, "song_not_analyzed"), "item_id") == id && await AnalyzedAsync(endpoint, server, id, token);

    internal async Task<bool> AnalyzedAsync(AudioMuseEndpoint endpoint, string server, string id, CancellationToken token)
    {
        try
        {
            var root = await GetAsync(endpoint, "external/get_embedding?id=" + Escape(id) + "&server=" + Escape(server), token, "song_not_analyzed");
            return Text(root, "item_id") == id && Property(root, "embedding") is { ValueKind: JsonValueKind.Array } vector &&
                vector.GetArrayLength() == 200 && vector.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var number) && double.IsFinite(number));
        }
        catch (AudioMuseUnavailable error) when (error.Reason == "song_not_analyzed") { return false; }
    }

    internal async Task<AudioMuseTrack[]> SimilarAsync(AudioMuseEndpoint endpoint, string server, string? seed, string? mood, CancellationToken token)
    {
        try
        {
            return Tracks(await GetAsync(endpoint, "api/similar_tracks?" + (seed != null ? "item_id=" + Escape(seed) : "mood=" + Escape(mood!) + "&centroid_index=0") +
                "&n=150&eliminate_duplicates=false&server=" + Escape(server), token, "song_not_analyzed"));
        }
        catch (AudioMuseUnavailable error) when (error.Reason == "song_not_analyzed") { return []; }
    }

    internal async Task<bool> HasAnchorAsync(AudioMuseEndpoint endpoint, string mood, CancellationToken token)
    {
        var root = await GetAsync(endpoint, "api/mood_centroids", token);
        return Property(root, mood) is { ValueKind: JsonValueKind.Array } anchors &&
            anchors.EnumerateArray().Any(a => Number(a, "index") == 0);
    }

    internal async Task<AudioMuseScores> ScoresAsync(AudioMuseEndpoint endpoint, string server, string id, CancellationToken token)
    {
        var root = await GetAsync(endpoint, "external/get_score?id=" + Escape(id) + "&server=" + Escape(server), token, "song_not_analyzed");
        if (Text(root, "item_id") != id) throw new AudioMuseUnavailable("song_not_analyzed");
        var features = (Text(root, "other_features") ?? "").Split(',').Select(part => part.Split(':'))
            .Where(parts => parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            .GroupBy(parts => parts[0], StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => double.Parse(g.First()[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        if (features.Values.All(value => value == 0)) features.Clear();
        return new(Number(root, "energy"), Feature("relaxed"), Feature("danceable"), Feature("party"));
        double? Feature(string name) => features.TryGetValue(name, out var value) ? value : null;
    }

    private async Task<JsonElement> GetAsync(AudioMuseEndpoint endpoint, string relative, CancellationToken token, string notFound = "not_set_up")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseUri, relative));
        try
        {
            using var lease = await secrets.OpenAsync(endpoint.KeyReference, new(null, AudioMuseSettings.Purpose, AllowShared: true), token);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.ReadUtf8());
        }
        catch (Exception error) when (error is KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException or IOException or FormatException)
        {
            throw new AudioMuseUnavailable("not_set_up");
        }
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await response.Content.LoadIntoBufferAsync(1024 * 1024, token);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                try
                {
                    using var failure = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                    if (Number(failure.RootElement, "error_code") == 3002) throw new AudioMuseUnavailable("too_little_analyzed_music");
                }
                catch (JsonException) { }
            }
            throw new AudioMuseUnavailable(response.StatusCode switch
            {
                HttpStatusCode.NotFound => notFound,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest => "not_set_up",
                _ => "not_reachable"
            });
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        return document.RootElement.Clone();
    }

    private static AudioMuseTrack[] Tracks(JsonElement root) => root.EnumerateArray().Take(150)
        .Select(t => new AudioMuseTrack(Text(t, "item_id") ?? "")).Where(t => t.Id.Length > 0).ToArray();
    internal static JsonElement Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    internal static string? Text(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static double? Number(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static string Escape(string value) => Uri.EscapeDataString(value);
}
