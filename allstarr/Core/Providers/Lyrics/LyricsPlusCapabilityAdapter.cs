using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Providers.Lyrics;

public sealed class LyricsPlusCapabilityAdapter(
    HttpClient http,
    IApplicationCache? cache,
    Func<CancellationToken, Task<string>> configuredEndpoint,
    Func<TimeSpan>? lyricsTtl = null) : IProviderLyricsCapability
{
    public const string HttpClientName = "LyricsPlus";
    private const int MaximumBytes = 2_000_000;
    private static readonly string[] PublicEndpoints =
        ["https://lyricsplus.binimum.org/", "https://lyricsplus.prjktla.workers.dev/"];
    public string ProviderId => "lyricsplus";
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    public async Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
        ProviderExecutionContext context, ProviderLyricsRequest request)
    {
        if (context.ProviderId != ProviderId || !context.Policy.AllowsProvider(ProviderId) ||
            request.ProviderTrackId.ProviderId != ProviderId || request.ProviderTrackId.ResourceKind != ProviderResourceKind.Track)
            return Failure(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested) return Failure(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow)) return Failure(ProviderErrorKind.CapabilityUnavailable);
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0)
            return Unavailable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(context.Remaining(DateTimeOffset.UtcNow));
        var token = deadline.Token;
        try
        {
            var configured = await configuredEndpoint(token).WaitAsync(token);
            var endpoints = string.IsNullOrWhiteSpace(configured) ? PublicEndpoints : [configured];
            var bases = new List<Uri>();
            foreach (var endpoint in endpoints)
            {
                if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(endpoint, out var uri, out _))
                    return Failure(ProviderErrorKind.AccountNeedsConfiguration);
                bases.Add(uri!);
            }
            var key = "lyrics:v2:lyricsplus:" + Hash(JsonSerializer.Serialize(new
            {
                Endpoints = bases.Select(uri => uri.AbsoluteUri),
                request.TrackTitle,
                request.ArtistNames,
                request.AlbumTitle,
                request.DurationSeconds,
                request.PreferredFormat
            }));
            try
            {
                var cached = cache == null ? null : await cache.GetAsync<ProviderLyricsResult>(key).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (cached?.Content != null) return Project(cached, request.AvailabilityOnly);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { }

            ProviderOutcome<ProviderLyricsResult>? lastFailure = null;
            foreach (var baseUri in bases)
            {
                token.ThrowIfCancellationRequested();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(3));
                ProviderOutcome<ProviderLyricsResult> result;
                try { result = await ReadAsync(baseUri, request, attempt.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { result = Failure(ProviderErrorKind.TransientFailure); }
                if (!result.IsSuccess) { lastFailure = result; continue; }
                if (result.RequireValue().Content == null) continue;
                token.ThrowIfCancellationRequested();
                try { if (cache != null) await cache.SetAsync(key, result.RequireValue(), lyricsTtl?.Invoke() ?? TimeSpan.FromDays(7)).WaitAsync(token); }
                catch (Exception exception) when (exception is not OperationCanceledException) { }
                token.ThrowIfCancellationRequested();
                return Project(result.RequireValue(), request.AvailabilityOnly);
            }
            return lastFailure ?? Unavailable();
        }
        catch (OperationCanceledException) { return Failure(ProviderErrorKind.Canceled); }
        catch { return Failure(ProviderErrorKind.TransientFailure); }
    }

    private async Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(
        Uri baseUri, ProviderLyricsRequest request, CancellationToken token)
    {
        var query = $"title={Uri.EscapeDataString(request.TrackTitle!)}&artist={Uri.EscapeDataString(string.Join(", ", request.ArtistNames))}" +
            $"&album={Uri.EscapeDataString(request.AlbumTitle ?? "")}" +
            (request.DurationSeconds.HasValue ? $"&duration={request.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture)}" : "");
        var endpoint = new Uri(baseUri, "v2/lyrics/get?" + query);
        using var outbound = new HttpRequestMessage(HttpMethod.Get, endpoint);
        outbound.Headers.Accept.ParseAdd("application/json");
        outbound.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
        try
        {
            using var response = await http.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint)
                return Failure(ProviderErrorKind.PermanentFailure);
            if (response.StatusCode == HttpStatusCode.NotFound) return Unavailable();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.RateLimited,
                    TimeSpan.FromSeconds(Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 30, 0, 300))));
            if (!response.IsSuccessStatusCode || response.Headers.Location != null)
                return Failure((int)response.StatusCode >= 500 ? ProviderErrorKind.TransientFailure : ProviderErrorKind.PermanentFailure);
            if (response.Content.Headers.ContentLength > MaximumBytes) return ContractFailure();
            await response.Content.LoadIntoBufferAsync(MaximumBytes, token);
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            if (bytes.Length > MaximumBytes) return ContractFailure();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return Parse(document.RootElement, request);
        }
        catch (JsonException) { return ContractFailure(); }
        catch (HttpRequestException) { return Failure(ProviderErrorKind.TransientFailure); }
        catch (IOException) { return Failure(ProviderErrorKind.TransientFailure); }
    }

    private static ProviderOutcome<ProviderLyricsResult> Parse(JsonElement root, ProviderLyricsRequest request)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || !root.TryGetProperty("lyrics", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return ContractFailure();
        var timed = type.GetString() is "Word" or "Line";
        if (!timed && type.GetString() != "None") return ContractFailure();
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, expected) in new[] { ("title", request.TrackTitle!), ("artist", string.Join(", ", request.ArtistNames)) })
                if (metadata.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()) && FuzzyMatcher.CalculateSimilarity(expected, value.GetString()!) < 90)
                    return Unavailable();
        }
        var format = timed && request.PreferredFormat != ProviderLyricsFormat.PlainText
            ? ProviderLyricsFormat.LineTimed : ProviderLyricsFormat.PlainText;
        var output = new List<string>();
        var hasWords = false;
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                return ContractFailure();
            long ms = 0;
            if (timed && (!line.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number ||
                !time.TryGetInt64(out ms) || ms is < 0 or > 86_400_000)) return ContractFailure();
            var words = text.GetString()!;
            hasWords |= !string.IsNullOrWhiteSpace(words);
            if (words.Contains('\r') || words.Contains('\n')) return ContractFailure();
            output.Add(format == ProviderLyricsFormat.LineTimed
                ? FormattableString.Invariant($"[{ms / 60_000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}]{words}") : words);
        }
        if (!hasWords) return Unavailable();
        var content = string.Join('\n', output);
        return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available,
            "lyricsplus", format, content, "sha256:" + Hash(content)));
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ProviderOutcome<ProviderLyricsResult> Project(ProviderLyricsResult result, bool availability) =>
        ProviderOutcome<ProviderLyricsResult>.Success(availability
            ? new(result.Availability, result.Source, revision: result.Revision) : result);
    private static ProviderOutcome<ProviderLyricsResult> Unavailable() =>
        ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, "lyricsplus"));
    private static ProviderOutcome<ProviderLyricsResult> Failure(ProviderErrorKind kind) => ProviderOutcome<ProviderLyricsResult>.Failure(new(kind));
    private static ProviderOutcome<ProviderLyricsResult> ContractFailure() => ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.CompatibilityContractChanged());
}
