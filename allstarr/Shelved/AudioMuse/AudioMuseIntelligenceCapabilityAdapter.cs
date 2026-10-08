using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Core.Providers.AudioMuse;

public sealed class AudioMuseIntelligenceCapabilityAdapter : IProviderIntelligenceCapability
{
    public const string StableProviderId = "audiomuse-ai";
    public const string HttpClientName = "AudioMuseAccountBound";
    private readonly AudioMuseEndpointClient _endpoint;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AudioMuseIntelligenceCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets)
        : this(new AudioMuseEndpointClient(clients.CreateClient(HttpClientName), secrets)) { }

    internal AudioMuseIntelligenceCapabilityAdapter(AudioMuseEndpointClient endpoint) =>
        _endpoint = endpoint;

    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Intelligence;

    public Task<ProviderOutcome<ProviderAnalysisProgress>> StartAnalysisAsync(
        ProviderExecutionContext context,
        bool rebuild = false) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        var payload = new Dictionary<string, object?> { ["rebuild"] = rebuild };
        AddServer(payload, credential.Server);
        var response = await _endpoint.SendAsync(
            credential, HttpMethod.Post, "api/analysis/start", payload, cancellationToken);
        return response.IsSuccess
            ? ParseAnalysis(response.Value!, ProviderAnalysisState.Queued)
            : ProviderOutcome<ProviderAnalysisProgress>.Failure(response.Error!);
    });

    public Task<ProviderOutcome<ProviderAnalysisProgress>> GetAnalysisProgressAsync(
        ProviderExecutionContext context,
        string jobId) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return ProviderOutcome<ProviderAnalysisProgress>.Failure(new(ProviderErrorKind.PermanentFailure));
        var relative = WithServer(
            $"api/status/{Uri.EscapeDataString(jobId.Trim())}", credential.Server);
        var response = await _endpoint.SendAsync(
            credential, HttpMethod.Get, relative, null, cancellationToken);
        return response.IsSuccess
            ? ParseAnalysis(response.Value!, ProviderAnalysisState.Running)
            : ProviderOutcome<ProviderAnalysisProgress>.Failure(response.Error!);
    });

    public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>> GetClustersAsync(
        ProviderExecutionContext context,
        int limit = 50) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        var response = await _endpoint.SendAsync(
            credential, HttpMethod.Get, "api/playlists", null, cancellationToken);
        if (!response.IsSuccess)
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>.Failure(response.Error!);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            var clusters = new List<ProviderIntelligenceCluster>();
            if (!document.RootElement.TryGetProperty("servers", out var servers) ||
                servers.ValueKind != JsonValueKind.Array)
                return ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>.Success([]);

            foreach (var server in servers.EnumerateArray())
            {
                if (!MatchesServer(server, credential.Server) ||
                    !server.TryGetProperty("playlists", out var playlists) ||
                    playlists.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var playlist in playlists.EnumerateObject())
                {
                    var tracks = ReadTracks(playlist.Value, limit);
                    if (tracks.Count == 0) continue;
                    clusters.Add(new(SafeId(playlist.Name), playlist.Name, tracks));
                    if (clusters.Count >= Math.Clamp(limit, 1, 200)) break;
                }
                if (clusters.Count >= Math.Clamp(limit, 1, 200)) break;
            }
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>.Success(clusters);
        }
        catch (JsonException)
        {
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>.Failure(
                new(ProviderErrorKind.PermanentFailure));
        }
    });

    public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> RecommendAsync(
        ProviderExecutionContext context,
        IReadOnlyList<string> seedTrackIds,
        int limit) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        if (seedTrackIds.Count == 0)
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success([]);
        var boundedLimit = Math.Clamp(limit, 1, 200);
        var byId = new Dictionary<string, ProviderIntelligenceTrack>(StringComparer.Ordinal);
        foreach (var seed in seedTrackIds.Where(value => !string.IsNullOrWhiteSpace(value)).Take(10))
        {
            var relative = $"api/similar_tracks?item_id={Uri.EscapeDataString(seed)}&n={boundedLimit.ToString(CultureInfo.InvariantCulture)}";
            relative = AppendServer(relative, credential.Server);
            var response = await _endpoint.SendAsync(
                credential, HttpMethod.Get, relative, null, cancellationToken);
            if (!response.IsSuccess)
            {
                if (response.Error!.Kind == ProviderErrorKind.NotFound) continue;
                return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Failure(response.Error);
            }
            try
            {
                using var document = JsonDocument.Parse(response.Value!);
                foreach (var track in ReadTracks(document.RootElement, boundedLimit))
                {
                    if (!byId.TryGetValue(track.TrackId, out var current) || track.Score > current.Score)
                        byId[track.TrackId] = track;
                }
            }
            catch (JsonException)
            {
                return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Failure(
                    new(ProviderErrorKind.PermanentFailure));
            }
        }
        return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success(
            byId.Values.OrderByDescending(item => item.Score).Take(boundedLimit).ToArray());
    });

    public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> SearchAsync(
        ProviderExecutionContext context,
        string query,
        bool includeLyrics,
        int limit) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(query))
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success([]);
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query.Trim(),
            ["limit"] = Math.Clamp(limit, 1, 200)
        };
        AddServer(payload, credential.Server);
        var path = includeLyrics ? "api/lyrics/search/text" : "api/clap/search";
        var response = await _endpoint.SendAsync(
            credential, HttpMethod.Post, path, payload, cancellationToken);
        if (!response.IsSuccess)
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Failure(response.Error!);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            var root = document.RootElement.TryGetProperty("results", out var results)
                ? results
                : document.RootElement;
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success(
                ReadTracks(root, Math.Clamp(limit, 1, 200)));
        }
        catch (JsonException)
        {
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Failure(
                new(ProviderErrorKind.PermanentFailure));
        }
    });

    public Task<ProviderOutcome<ProviderIntelligencePath>> FindPathAsync(
        ProviderExecutionContext context,
        string startTrackId,
        string endTrackId,
        int limit) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        var relative = "api/find_path?start_song_id=" + Uri.EscapeDataString(startTrackId) +
                       "&end_song_id=" + Uri.EscapeDataString(endTrackId) +
                       "&max_steps=" + Math.Clamp(limit, 2, 200).ToString(CultureInfo.InvariantCulture);
        relative = AppendServer(relative, credential.Server);
        var response = await _endpoint.SendAsync(
            credential, HttpMethod.Get, relative, null, cancellationToken);
        if (!response.IsSuccess)
            return ProviderOutcome<ProviderIntelligencePath>.Failure(response.Error!);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            var path = document.RootElement.TryGetProperty("path", out var pathValue)
                ? ReadTracks(pathValue, Math.Clamp(limit, 2, 200))
                : [];
            var distance = Number(document.RootElement, "total_distance") ?? 0;
            return ProviderOutcome<ProviderIntelligencePath>.Success(new(path, distance));
        }
        catch (JsonException)
        {
            return ProviderOutcome<ProviderIntelligencePath>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    });

    public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> BlendAsync(
        ProviderExecutionContext context,
        IReadOnlyList<string> positiveSeedTrackIds,
        IReadOnlyList<string> negativeSeedTrackIds,
        int limit) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        if (positiveSeedTrackIds.Count == 0)
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Failure(
                new(ProviderErrorKind.PermanentFailure));
        var items = positiveSeedTrackIds.Select(id => new { op = "ADD", type = "song", id })
            .Concat(negativeSeedTrackIds.Select(id => new { op = "SUBTRACT", type = "song", id }))
            .ToArray();
        var payload = new Dictionary<string, object?>
        {
            ["items"] = items,
            ["n"] = Math.Clamp(limit, 1, 200)
        };
        AddServer(payload, credential.Server);
        var response = await _endpoint.SendAsync(
            credential, HttpMethod.Post, "api/alchemy", payload, cancellationToken);
        if (!response.IsSuccess)
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Failure(response.Error!);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            var root = document.RootElement.TryGetProperty("results", out var results)
                ? results
                : document.RootElement;
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success(
                ReadTracks(root, Math.Clamp(limit, 1, 200)));
        }
        catch (JsonException)
        {
            return ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Failure(
                new(ProviderErrorKind.PermanentFailure));
        }
    });

    public Task<ProviderOutcome<ProviderIntelligenceMapPage>> GetMapAsync(
        ProviderExecutionContext context,
        ProviderPageRequest page) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        var offset = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(parsed, 0)
            : 0;
        var requested = Math.Clamp(page.Limit, 1, 200);
        var relative = $"api/map?n={(offset + requested + 1).ToString(CultureInfo.InvariantCulture)}";
        relative = AppendServer(relative, credential.Server);
        var response = await _endpoint.SendAsync(
            credential, HttpMethod.Get, relative, null, cancellationToken);
        if (!response.IsSuccess)
            return ProviderOutcome<ProviderIntelligenceMapPage>.Failure(response.Error!);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            var source = document.RootElement.TryGetProperty("items", out var items) &&
                         items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Skip(offset).Take(requested + 1).ToArray()
                : [];
            var hasMore = source.Length > requested;
            var mapped = source.Take(requested).Select(MapPoint).Where(item => item != null)
                .Cast<ProviderIntelligenceMapPoint>().ToArray();
            var projection = Text(document.RootElement, "projection") ?? "audiomuse";
            var next = hasMore ? (offset + requested).ToString(CultureInfo.InvariantCulture) : null;
            return ProviderOutcome<ProviderIntelligenceMapPage>.Success(
                new(mapped, projection, next, hasMore, Text(document.RootElement, "snapshot_version")));
        }
        catch (JsonException)
        {
            return ProviderOutcome<ProviderIntelligenceMapPage>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    });

    public Task<ProviderOutcome<bool>> DisconnectAsync(ProviderExecutionContext context) =>
        Task.FromResult(context.ProviderId == StableProviderId
            ? ProviderOutcome<bool>.Success(true)
            : ProviderOutcome<bool>.Failure(new(ProviderErrorKind.Forbidden)));

    private static ProviderOutcome<ProviderAnalysisProgress> ParseAnalysis(
        byte[] body,
        ProviderAnalysisState fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var id = Text(root, "task_id") ?? Text(root, "job_id");
            if (string.IsNullOrWhiteSpace(id))
                return ProviderOutcome<ProviderAnalysisProgress>.Failure(new(ProviderErrorKind.PermanentFailure));
            var stateText = Text(root, "state") ?? Text(root, "status");
            var state = stateText?.Trim().ToLowerInvariant() switch
            {
                "queued" or "pending" => ProviderAnalysisState.Queued,
                "started" or "progress" or "running" => ProviderAnalysisState.Running,
                "success" or "finished" or "completed" => ProviderAnalysisState.Completed,
                "revoked" or "canceled" or "cancelled" => ProviderAnalysisState.Canceled,
                "failure" or "failed" => ProviderAnalysisState.Failed,
                _ => fallback
            };
            var percentage = (int)Math.Clamp(Number(root, "progress") ?? 0, 0, 100);
            return ProviderOutcome<ProviderAnalysisProgress>.Success(
                new(id, state, percentage, 100, state == ProviderAnalysisState.Failed ? "analysis_failed" : null));
        }
        catch (JsonException)
        {
            return ProviderOutcome<ProviderAnalysisProgress>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    }

    private static IReadOnlyList<ProviderIntelligenceTrack> ReadTracks(JsonElement root, int limit)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];
        return root.EnumerateArray().Select(MapTrack).Where(item => item != null)
            .Cast<ProviderIntelligenceTrack>()
            .DistinctBy(item => item.TrackId, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArray();
    }

    private static ProviderIntelligenceTrack? MapTrack(JsonElement item)
    {
        var id = Text(item, "item_id") ?? Text(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var title = Text(item, "title") ?? id;
        var artist = Text(item, "author") ?? Text(item, "artist") ?? "Unknown artist";
        var score = Number(item, "similarity") ?? Number(item, "score") ??
            (Number(item, "distance") is { } distance ? 1 - distance : 1);
        return new(id, title, artist, Math.Clamp(score, 0, 1),
            Text(item, "album"), Text(item, "cluster_id"));
    }

    private static ProviderIntelligenceMapPoint? MapPoint(JsonElement item)
    {
        var id = Text(item, "item_id") ?? Text(item, "id");
        if (string.IsNullOrWhiteSpace(id) ||
            !item.TryGetProperty("embedding_2d", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Array)
            return null;
        var values = coordinates.EnumerateArray().Take(2).Select(value => value.TryGetDouble(out var number) ? number : 0).ToArray();
        if (values.Length != 2) return null;
        return new(id, Text(item, "title") ?? id,
            Text(item, "author") ?? Text(item, "artist") ?? "Unknown artist",
            values[0], values[1], Text(item, "album"), Text(item, "cluster_id"));
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.TryGetDouble(out var number)
            ? number
            : null;

    private static string WithServer(string relative, string? server) =>
        string.IsNullOrWhiteSpace(server)
            ? relative
            : relative + "?server=" + Uri.EscapeDataString(server);

    private static string AppendServer(string relative, string? server) =>
        string.IsNullOrWhiteSpace(server)
            ? relative
            : relative + "&server=" + Uri.EscapeDataString(server);

    private static void AddServer(IDictionary<string, object?> payload, string? server)
    {
        if (!string.IsNullOrWhiteSpace(server)) payload["server"] = server;
    }

    private static bool MatchesServer(JsonElement server, string? expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(Text(server, "server_id"), expected, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Text(server, "server_name"), expected, StringComparison.OrdinalIgnoreCase);

    private static string SafeId(string name)
    {
        var normalized = new string(name.ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "cluster" : normalized;
    }
}

public sealed class AudioMuseHealthProbeCapabilityAdapter : IProviderHealthProbeCapability
{
    private readonly AudioMuseEndpointClient _endpoint;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AudioMuseHealthProbeCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets)
        : this(new AudioMuseEndpointClient(
            clients.CreateClient(AudioMuseIntelligenceCapabilityAdapter.HttpClientName), secrets))
    { }

    internal AudioMuseHealthProbeCapabilityAdapter(AudioMuseEndpointClient endpoint) => _endpoint = endpoint;

    public string ProviderId => AudioMuseIntelligenceCapabilityAdapter.StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Health;

    public Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(
        ProviderExecutionContext context,
        ProviderHealthProbeRequest request) => _endpoint.ExecuteAsync(context, async (credential, cancellationToken) =>
    {
        if (request.TargetCapability != ProviderCapabilityKind.Intelligence)
            return ProviderOutcome<ProviderHealthProbeResult>.Failure(new(ProviderErrorKind.NotSupported));
        var timer = Stopwatch.StartNew();
        var response = await _endpoint.SendAsync(
            credential, HttpMethod.Get, WithServer("api/health", credential.Server), null, cancellationToken);
        timer.Stop();
        if (!response.IsSuccess)
            return ProviderOutcome<ProviderHealthProbeResult>.Failure(response.Error!);
        return ProviderOutcome<ProviderHealthProbeResult>.Success(new(
            ProviderProbeStatus.Healthy, DateTimeOffset.UtcNow, timer.Elapsed));
    });

    private static string WithServer(string relative, string? server) =>
        string.IsNullOrWhiteSpace(server)
            ? relative
            : relative + "?server=" + Uri.EscapeDataString(server);
}

internal sealed class AudioMuseEndpointClient(HttpClient http, IProviderAccountSecretAccessor secrets)
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    public async Task<ProviderOutcome<T>> ExecuteAsync<T>(
        ProviderExecutionContext context,
        Func<AudioMuseCredential, CancellationToken, Task<ProviderOutcome<T>>> operation)
    {
        if (!string.Equals(context.ProviderId, AudioMuseIntelligenceCapabilityAdapter.StableProviderId,
                StringComparison.Ordinal) || context.Account == null)
            return ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
        var remaining = context.Remaining(DateTimeOffset.UtcNow);
        if (remaining <= TimeSpan.Zero)
            return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Canceled));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            deadline.CancelAfter(remaining);
            return await secrets.UseAsync(context.Account, async bytes =>
            {
                AudioMuseCredential? credential;
                try
                {
                    credential = JsonSerializer.Deserialize<AudioMuseCredential>(bytes.Span,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    credential = null;
                }
                return credential is { BaseUri: not null }
                    ? await operation(credential, deadline.Token)
                    : ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
            }, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (KeyNotFoundException)
        {
            return ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
        }
        catch
        {
            return ProviderOutcome<T>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    public async Task<ProviderOutcome<byte[]>> SendAsync(
        AudioMuseCredential credential,
        HttpMethod method,
        string relative,
        object? payload,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(credential.BaseUri!, relative);
        if (!AudioMuseCredential.IsAllowed(uri, credential.BaseUri))
            return ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.Forbidden));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(credential.ApiToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.ApiToken);
        if (payload != null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return ProviderOutcome<byte[]>.Failure(ErrorFor(response));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[32 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes)
                    return ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.PermanentFailure));
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
            return ProviderOutcome<byte[]>.Success(buffer.ToArray());
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (HttpRequestException)
        {
            return ProviderOutcome<byte[]>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    private static ProviderError ErrorFor(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited,
            response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };
}

internal sealed record AudioMuseCredential
{
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("apiToken")]
    public string? ApiToken { get; init; }

    [JsonPropertyName("server")]
    public string? Server { get; init; }

    [JsonIgnore]
    public Uri? BaseUri => TryBaseUri(BaseUrl, out var uri) ? uri : null;

    public static bool TryBaseUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        uri = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/");
        return true;
    }

    public static bool IsAllowed(Uri uri, Uri? origin) => origin != null &&
        string.Equals(uri.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == origin.Port &&
        uri.AbsolutePath.StartsWith(origin.AbsolutePath, StringComparison.Ordinal);
}
