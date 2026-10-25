using allstarr.Core.Providers.Lyrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Spotify;

public sealed class SpotifyLyricsCapabilityAdapter : IProviderLyricsCapability, IInternalLyricsCapability
{
    private const int MaximumLyricsBytes = 2_000_000;
    private const long MaximumTimestampMilliseconds = 24L * 60 * 60 * 1000;
    private static readonly Uri LyricsOrigin = new("https://spclient.wg.spotify.com/");
    private readonly SpotifyPlaylistCapabilityAdapter _owner;

    public SpotifyLyricsCapabilityAdapter(SpotifyPlaylistCapabilityAdapter owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public string ProviderId => SpotifyPlaylistCapabilityAdapter.StableProviderId;

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    ValueTask<LyricsSourceSnapshot> IInternalLyricsCapability.CaptureSourceAsync(CancellationToken token) =>
        ValueTask.FromResult(LyricsAwait.Static(ProviderId, LyricsSpeculationMode.RecentHealthy));
    Task<ProviderOutcome<ProviderLyricsResult>> IInternalLyricsCapability.FetchAsync(ProviderExecutionContext context,
        ProviderLyricsRequest request, LyricsExecution execution) => _owner.ReadLyricsAsync(context, request, execution.Completion);

    public Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
        ProviderExecutionContext context,
        ProviderLyricsRequest request) => _owner.ReadLyricsAsync(context, request);

    internal static Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsFromTokenAsync(
        HttpClient http,
        string token,
        ProviderLyricsRequest request,
        CancellationToken cancellationToken) => ReadLyricsFromTokenCoreAsync(http, token, request, cancellationToken);

    private static async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsFromTokenCoreAsync(
        HttpClient http,
        string token,
        ProviderLyricsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProviderTrackId.ProviderId != SpotifyPlaylistCapabilityAdapter.StableProviderId ||
            request.ProviderTrackId.ResourceKind != ProviderResourceKind.Track ||
            !IsValidTrackId(request.ProviderTrackId.Value))
            return Failure(ProviderErrorKind.Forbidden);

        var endpoint = new Uri(
            LyricsOrigin,
            $"color-lyrics/v2/track/{Uri.EscapeDataString(request.ProviderTrackId.Value)}?format=json&market=from_token");
        using var outbound = new HttpRequestMessage(HttpMethod.Get, endpoint);
        outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        SpotifyWebRequestProfile.Apply(outbound);
        try
        {
            using var response = await http.SendAsync(
                outbound, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !string.Equals(finalUri.AbsoluteUri, endpoint.AbsoluteUri, StringComparison.Ordinal))
                return Failure(ProviderErrorKind.PermanentFailure);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Available(ProviderLyricsAvailabilityState.Unavailable);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure(response.StatusCode == HttpStatusCode.Unauthorized
                    ? ProviderErrorKind.Unauthorized
                    : ProviderErrorKind.Forbidden);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return ProviderOutcome<ProviderLyricsResult>.Failure(new(
                    ProviderErrorKind.RateLimited, RetryAfter(response)));
            if ((int)response.StatusCode >= 500)
                return Failure(ProviderErrorKind.TransientFailure);
            if (!response.IsSuccessStatusCode || response.Headers.Location != null)
                return Failure(ProviderErrorKind.PermanentFailure);
            if (response.Content.Headers.ContentLength > MaximumLyricsBytes)
                return Failure(ProviderError.CompatibilityContractChanged());

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var body = new MemoryStream(Math.Min(
                (int?)response.Content.Headers.ContentLength ?? 16 * 1024, MaximumLyricsBytes));
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (body.Length > MaximumLyricsBytes - read)
                    return Failure(ProviderError.CompatibilityContractChanged());
                body.Write(buffer, 0, read);
            }

            return ParseLyrics(body.GetBuffer().AsMemory(0, checked((int)body.Length)), request);
        }
        catch (OperationCanceledException)
        {
            return Failure(ProviderErrorKind.Canceled);
        }
        catch (HttpRequestException)
        {
            return Failure(ProviderErrorKind.TransientFailure);
        }
        catch (IOException)
        {
            return Failure(ProviderErrorKind.TransientFailure);
        }
        catch (JsonException)
        {
            return Failure(ProviderError.CompatibilityContractChanged());
        }
        catch (OverflowException)
        {
            return Failure(ProviderError.CompatibilityContractChanged());
        }
        catch (Exception)
        {
            return Failure(ProviderErrorKind.TransientFailure);
        }
    }

    private static ProviderOutcome<ProviderLyricsResult> ParseLyrics(
        ReadOnlyMemory<byte> body,
        ProviderLyricsRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("lyrics", out var lyrics) || lyrics.ValueKind != JsonValueKind.Object ||
                !lyrics.TryGetProperty("syncType", out var syncType) || syncType.ValueKind != JsonValueKind.String ||
                !lyrics.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
                return Failure(ProviderError.CompatibilityContractChanged());

            var sync = syncType.GetString();
            var timed = sync == "LINE_SYNCED";
            if (!timed && sync != "UNSYNCED")
                return Failure(ProviderError.CompatibilityContractChanged());

            var textLines = new List<string>();
            var timedLines = new List<(long Milliseconds, string Words)>();
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object ||
                    !line.TryGetProperty("words", out var wordsValue) ||
                    wordsValue.ValueKind != JsonValueKind.String)
                    return Failure(ProviderError.CompatibilityContractChanged());
                var words = wordsValue.GetString() ?? string.Empty;
                if (timed)
                {
                    if (!line.TryGetProperty("startTimeMs", out var start) ||
                        !TryMilliseconds(start, out var milliseconds))
                        return Failure(ProviderError.CompatibilityContractChanged());
                    timedLines.Add((milliseconds, words));
                }
                textLines.Add(words);
            }

            if (textLines.Count == 0 || textLines.All(string.IsNullOrWhiteSpace))
                return Available(ProviderLyricsAvailabilityState.Unavailable);

            var format = timed && request.PreferredFormat != ProviderLyricsFormat.PlainText
                ? ProviderLyricsFormat.LineTimed
                : ProviderLyricsFormat.PlainText;
            var content = format == ProviderLyricsFormat.LineTimed
                ? string.Join('\n', timedLines.Select(line =>
                    $"[{FormatTimestamp(line.Milliseconds)}]{line.Words}"))
                : string.Join('\n', textLines);
            if (Encoding.UTF8.GetByteCount(content) > MaximumLyricsBytes)
                return Failure(ProviderError.CompatibilityContractChanged());

            var revisionSource = timed
                ? $"LINE_SYNCED\n{string.Join('\n', timedLines.Select(line => $"{line.Milliseconds}:{line.Words}"))}"
                : $"UNSYNCED\n{string.Join('\n', textLines)}";
            var revision = "sha256:" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(revisionSource))).ToLowerInvariant();
            var result = new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available,
                SpotifyPlaylistCapabilityAdapter.StableProviderId,
                format,
                content,
                revision);
            return request.AvailabilityOnly
                ? Available(ProviderLyricsAvailabilityState.Available, revision: revision)
                : ProviderOutcome<ProviderLyricsResult>.Success(result);
        }
        catch (JsonException)
        {
            return Failure(ProviderError.CompatibilityContractChanged());
        }
        catch (ArgumentException)
        {
            return Failure(ProviderError.CompatibilityContractChanged());
        }
    }

    private static bool TryMilliseconds(JsonElement value, out long milliseconds)
    {
        milliseconds = 0;
        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds) &&
                milliseconds is >= 0 and <= MaximumTimestampMilliseconds;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var decimalValue) ||
            decimalValue != decimal.Truncate(decimalValue) || decimalValue < 0 ||
            decimalValue > MaximumTimestampMilliseconds)
            return false;
        milliseconds = (long)decimalValue;
        return true;
    }

    private static string FormatTimestamp(long milliseconds)
    {
        var minutes = milliseconds / 60_000;
        var seconds = (milliseconds % 60_000) / 1_000;
        var remainder = milliseconds % 1_000;
        return FormattableString.Invariant($"{minutes:00}:{seconds:00}.{remainder:000}");
    }

    private static bool IsValidTrackId(string value) => value.Length == 22 &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date
            ? date - DateTimeOffset.UtcNow
            : TimeSpan.FromSeconds(30));
        if (delay < TimeSpan.Zero) return TimeSpan.Zero;
        return delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay;
    }

    private static ProviderOutcome<ProviderLyricsResult> Available(
        ProviderLyricsAvailabilityState availability,
        string? revision = null) => ProviderOutcome<ProviderLyricsResult>.Success(new(
        availability,
        SpotifyPlaylistCapabilityAdapter.StableProviderId,
        revision: revision));

    private static ProviderOutcome<ProviderLyricsResult> Failure(ProviderErrorKind kind) =>
        ProviderOutcome<ProviderLyricsResult>.Failure(new(kind));

    private static ProviderOutcome<ProviderLyricsResult> Failure(ProviderError error) =>
        ProviderOutcome<ProviderLyricsResult>.Failure(error);
}
