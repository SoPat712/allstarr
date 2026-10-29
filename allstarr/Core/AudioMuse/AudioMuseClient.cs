using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Core.Secrets;

namespace allstarr.Core.AudioMuse;

internal sealed record AudioMuseServer(string Id, string Type, string Name, string Url, bool IsDefault);
internal sealed record AudioMuseCoverage(string ServerId, bool Enabled, int? Analyzed, int? Total)
{
    internal bool Complete => Analyzed.HasValue && Analyzed == Total;
}
internal sealed record AudioMuseTrack(string Id, string? Title = null, string? Author = null, string? Album = null,
    string? AlbumArtist = null, double? Distance = null, string? MoodVector = null, string? OtherFeatures = null,
    string? TopGenre = null, string? TopMood = null);
internal sealed record AudioMuseCentroid(int? ClusterId, int? Index, double? MoodScore, int? SongCount, string[] TopTags);
internal sealed record AudioMuseScores(double? Energy, double? Relaxed, double? Danceable, double? Party,
    double? Aggressive = null, double? Happy = null, double? Sad = null, double? Tempo = null,
    double? LyricDensity = null, IReadOnlyDictionary<string, double>? Tags = null);

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
            Text(s, "server_id") ?? "", Text(s, "server_type") ?? "", Text(s, "name") ?? "",
            Text(Property(s, "creds"), "url") ?? "", Text(s, "server_id") == Text(root, "default_id"))).ToArray();
    }

    internal async Task<AudioMuseCoverage> CoverageAsync(AudioMuseEndpoint endpoint, string server, CancellationToken token)
    {
        var root = await GetAsync(endpoint, "api/models?server=" + Escape(server), token);
        var model = Property(Property(root, "models"), "musicnn");
        var local = Property(model, "local");
        return new(Text(root, "server_id") ?? "", Property(model, "enabled").ValueKind == JsonValueKind.True,
            Count(local, "count"), Count(local, "total"));
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

    internal async Task<Dictionary<string, AudioMuseCentroid[]>> CentroidsAsync(AudioMuseEndpoint endpoint, CancellationToken token)
    {
        var root = await GetAsync(endpoint, "api/mood_centroids", token);
        return root.EnumerateObject().ToDictionary(mood => mood.Name, mood => mood.Value.EnumerateArray()
            .Select(c => new AudioMuseCentroid(Count(c, "cluster_id"), Count(c, "index"), Number(c, "mood_score"), Count(c, "n_songs"),
                Property(c, "top_tags") is { ValueKind: JsonValueKind.Array } tags
                    ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray() : []))
            .ToArray(), StringComparer.Ordinal);
    }

    internal async Task<bool> HasAnchorAsync(AudioMuseEndpoint endpoint, string mood, CancellationToken token) =>
        (await CentroidsAsync(endpoint, token)).TryGetValue(mood, out var anchors) && anchors.Any(a => a.Index == 0);

    internal async Task<AudioMuseScores> ScoresAsync(AudioMuseEndpoint endpoint, string server, string id, CancellationToken token)
    {
        var root = await GetAsync(endpoint, "external/get_score?id=" + Escape(id) + "&server=" + Escape(server), token, "song_not_analyzed");
        if (Text(root, "item_id") != id) throw new AudioMuseUnavailable("song_not_analyzed");
        return ParseScores(root);
    }

    internal static AudioMuseScores ParseScores(JsonElement root)
    {
        static Dictionary<string, double> Vector(string? text) => (text ?? "").Split(',').Select(part => part.Split(':'))
            .Where(parts => parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1)
            .GroupBy(parts => parts[0].Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => double.Parse(g.First()[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var features = Vector(Text(root, "other_features"));
        if (features.Values.All(value => value == 0)) features.Clear();
        return new(Unit("energy"), Feature("relaxed"), Feature("danceable"), Feature("party"),
            Feature("aggressive"), Feature("happy"), Feature("sad"), Number(root, "tempo") ?? Number(root, "bpm"),
            Unit("lyric_density"), Vector(Text(root, "mood_vector")));
        double? Feature(string name) => features.TryGetValue(name, out var value) ? value : null;
        double? Unit(string name) => Number(root, name) is >= 0 and <= 1 ? Number(root, name) : null;
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
        .Select(t => new AudioMuseTrack(Text(t, "item_id") ?? "", Text(t, "title"), Text(t, "author"), Text(t, "album"),
            Text(t, "album_artist"), Number(t, "distance"), Text(t, "mood_vector"), Text(t, "other_features"),
            Text(t, "top_genre"), Text(t, "top_mood"))).Where(t => t.Id.Length > 0).ToArray();
    internal static JsonElement Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    internal static string? Text(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static double? Number(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static int? Count(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var count) && count >= 0 ? count : null;
    private static string Escape(string value) => Uri.EscapeDataString(value);
}
