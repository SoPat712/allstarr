using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Providers.Qobuz;

/// <summary>Resolves signed Qobuz media requests using only the supplied account credential.</summary>
public sealed class QobuzMediaClient(HttpClient http, QobuzBundleService bundles, ILogger<QobuzMediaClient> logger)
{
    private const string BaseUrl = "https://www.qobuz.com/api.json/0.2/";
    private const int FormatMp3320 = 5;
    private const int FormatFlac16 = 6;
    private const int FormatFlac24Low = 7;
    private const int FormatFlac24High = 27;

    internal async Task<QobuzDownloadResult> ResolveDownloadAsync(
        string trackId,
        string? userAuthToken,
        string? quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        if (string.IsNullOrWhiteSpace(userAuthToken))
            throw new HttpRequestException("Qobuz account credentials are unavailable.", null, HttpStatusCode.Unauthorized);

        var formats = GetFormatPriority(GetFormatId(quality));
        for (var refreshAttempt = 0; refreshAttempt < 2; refreshAttempt++)
        {
            var (appId, secrets) = await GetSigningCredentialsAsync(cancellationToken);
            var signingRejected = false;
            HttpRequestException? unavailable = null;
            foreach (var format in formats)
            {
                foreach (var secret in secrets)
                {
                    try
                    {
                        return await TryGetTrackDownloadUrlAsync(
                            trackId, format, appId, secret, userAuthToken, cancellationToken);
                    }
                    catch (StaleSigningException)
                    {
                        signingRejected = true;
                    }
                    catch (HttpRequestException exception) when (
                        exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                    {
                        unavailable = exception;
                        signingRejected = false;
                        break;
                    }
                }
                if (signingRejected) break;
            }

            if (!signingRejected)
                throw unavailable ?? new HttpRequestException("No Qobuz media is available.", null, HttpStatusCode.NotFound);
            if (refreshAttempt == 1) throw new StaleSigningException();
            cancellationToken.ThrowIfCancellationRequested();
            await bundles.RefreshAsync(appId, cancellationToken);
        }
        throw new StaleSigningException();
    }

    private async Task<(string AppId, List<string> Secrets)> GetSigningCredentialsAsync(CancellationToken cancellationToken)
    {
        var credentials = await bundles.GetSigningCredentialsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(credentials.AppId) || credentials.Secrets.Count == 0 ||
            credentials.Secrets.Any(string.IsNullOrWhiteSpace))
            throw new HttpRequestException("Qobuz signing credentials are unavailable.", null, HttpStatusCode.BadGateway);
        return (credentials.AppId, credentials.Secrets.Distinct(StringComparer.Ordinal).ToList());
    }

    private async Task<QobuzDownloadResult> TryGetTrackDownloadUrlAsync(
        string trackId,
        int formatId,
        string appId,
        string secret,
        string userAuthToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = ComputeMD5Signature(trackId, formatId, unix, secret);
        var url = $"{BaseUrl}track/getFileUrl?format_id={formatId}&intent=stream&request_ts={unix}&track_id={Uri.EscapeDataString(trackId)}&request_sig={signature}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        request.Headers.Add("X-App-Id", appId);
        request.Headers.Add("X-User-Auth-Token", userAuthToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                IsStaleSigningResponse(await response.Content.ReadAsStringAsync(cancellationToken)))
                throw new StaleSigningException();
            throw new HttpRequestException("Qobuz media request failed.", null, response.StatusCode);
        }

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            if (!root.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(urlElement.GetString()))
                throw new HttpRequestException("No Qobuz media is available.", null, HttpStatusCode.NotFound);
            var mimeType = root.TryGetProperty("mime_type", out var mime) && mime.ValueKind == JsonValueKind.String
                ? mime.GetString() : null;
            var bitDepth = root.TryGetProperty("bit_depth", out var depth) ? depth.GetInt32() : 16;
            var samplingRate = root.TryGetProperty("sampling_rate", out var rate) ? rate.GetDouble() : 44.1;
            var isSample = root.TryGetProperty("sample", out var sample) && sample.GetBoolean();
            if (samplingRate == 0) isSample = true;
            if (!double.IsFinite(samplingRate)) throw InvalidResponse();
            var downgraded = root.TryGetProperty("restrictions", out var restrictions) &&
                restrictions.ValueKind == JsonValueKind.Array && restrictions.EnumerateArray().Any(restriction =>
                    restriction.ValueKind == JsonValueKind.Object && restriction.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String && code.GetString() == "FormatRestrictedByFormatAvailability");
            if (downgraded) logger.LogDebug("Qobuz returned a lower available quality tier.");
            return new QobuzDownloadResult
            {
                Url = urlElement.GetString()!,
                FormatId = formatId,
                MimeType = mimeType,
                BitDepth = bitDepth,
                SamplingRate = samplingRate,
                IsSample = isSample,
                WasQualityDowngraded = downgraded
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw InvalidResponse();
        }
    }

    private static bool IsStaleSigningResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.String) return false;
            var normalized = new string(code.GetString()!.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return normalized is "invalidappid" or "invalidrequestsignature";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpRequestException InvalidResponse() =>
        new("Qobuz returned an invalid media response.", null, HttpStatusCode.BadGateway);

    private sealed class StaleSigningException() : HttpRequestException(
        "Qobuz signing credentials were rejected.", null, HttpStatusCode.BadRequest);

    private static string ComputeMD5Signature(string trackId, int formatId, long timestamp, string secret)
    {
        var toSign = $"trackgetFileUrlformat_id{formatId}intentstreamtrack_id{trackId}{timestamp}{secret}";

        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(toSign));
        var signature = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

        return signature;
    }

    private static int GetFormatId(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
        {
            return FormatFlac24High;
        }

        return quality.ToUpperInvariant() switch
        {
            "FLAC" => FormatFlac24High,
            "FLAC_24_HIGH" or "24_192" => FormatFlac24High,
            "FLAC_24_LOW" or "24_96" => FormatFlac24Low,
            "FLAC_16" or "CD" => FormatFlac16,
            "MP3_320" or "MP3" => FormatMp3320,
            _ => FormatFlac24High
        };
    }

    private static IReadOnlyList<int> GetFormatPriority(int preferredFormat)
    {
        int[] formats = [FormatFlac24High, FormatFlac24Low, FormatFlac16, FormatMp3320];
        return formats.SkipWhile(format => format != preferredFormat).ToArray();
    }

    internal sealed class QobuzDownloadResult
    {
        public string Url { get; set; } = string.Empty;
        public int FormatId { get; set; }
        public string? MimeType { get; set; }
        public int BitDepth { get; set; }
        public double SamplingRate { get; set; }
        public bool IsSample { get; set; }
        public bool WasQualityDowngraded { get; set; }
    }
}
