using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OtpNet;

namespace allstarr.Core.Providers.Spotify;

internal sealed record SpotifyWebTokenResult(string? AccessToken, bool IsAnonymous, string? ReasonCode)
{
    public bool Success => !string.IsNullOrWhiteSpace(AccessToken) && !IsAnonymous;
}

internal static class SpotifyWebTokenExchange
{
    private static readonly Uri SecretsUri = new("https://raw.githubusercontent.com/xyloflake/spot-secrets-go/refs/heads/main/secrets/secretBytes.json");
    private static readonly Uri SpotifyOrigin = new("https://open.spotify.com/");

    public static async Task<SpotifyWebTokenResult> ExchangeAsync(HttpClient http, string cookie, CancellationToken cancellationToken)
    {
        try
        {
            using var secretsRequest = new HttpRequestMessage(HttpMethod.Get, SecretsUri);
            SpotifyWebRequestProfile.Apply(secretsRequest);
            using var secretsResponse = await http.SendAsync(secretsRequest, cancellationToken);
            if (!secretsResponse.IsSuccessStatusCode) return new(null, false, $"totp_secrets_http_{(int)secretsResponse.StatusCode}");
            var secrets = JsonSerializer.Deserialize<TotpSecret[]>(await secretsResponse.Content.ReadAsStringAsync(cancellationToken));
            var secret = secrets?.OrderByDescending(item => item.Version).FirstOrDefault();
            if (secret == null || secret.Secret.Count == 0) return new(null, false, "totp_secrets_invalid");

            using var timeRequest = new HttpRequestMessage(HttpMethod.Head, SpotifyOrigin);
            SpotifyWebRequestProfile.Apply(timeRequest);
            using var timeResponse = await http.SendAsync(timeRequest, cancellationToken);
            var serverTime = timeResponse.Headers.Date?.ToUnixTimeSeconds();
            if (!timeResponse.IsSuccessStatusCode || serverTime == null) return new(null, false, "spotify_time_unavailable");

            var transformed = secret.Secret.Select((value, index) => (byte)(value ^ ((index % 33) + 9))).ToArray();
            var key = Encoding.UTF8.GetBytes(string.Concat(transformed.Select(value => value.ToString())));
            var otp = new Totp(key, step: 30, totpSize: 6).ComputeTotp(DateTime.UnixEpoch.AddSeconds(serverTime.Value));
            var clientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var uri = new Uri($"https://open.spotify.com/api/token?reason=init&productType=web-player&totp={otp}&totpServer={otp}&totpVer={secret.Version}&sTime={serverTime}&cTime={clientTime}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Cookie", $"sp_dc={cookie}");
            SpotifyWebRequestProfile.Apply(request);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(null, false, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "provider_unauthorized",
                    System.Net.HttpStatusCode.Forbidden => "provider_forbidden",
                    _ => $"upstream_http_{(int)response.StatusCode}"
                });
            try
            {
                var token = JsonSerializer.Deserialize<TokenResponse>(await response.Content.ReadAsStringAsync(cancellationToken));
                if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                    return new(null, false, "invalid_response");
                if (token.IsAnonymous)
                    return new(null, true, "anonymous_session");
                if (token.AccessTokenExpirationTimestampMs is { } expiresAt &&
                    expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    return new(null, false, "expired_session");
                return new(token.AccessToken, false, null);
            }
            catch (JsonException) { return new(null, false, "invalid_response"); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(null, false, $"exchange_exception_{ex.GetType().Name.ToLowerInvariant()}"); }
    }

    private sealed class TotpSecret
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("secret")] public List<byte> Secret { get; set; } = [];
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
        [JsonPropertyName("isAnonymous")] public bool IsAnonymous { get; set; }
        [JsonPropertyName("accessTokenExpirationTimestampMs")]
        public long? AccessTokenExpirationTimestampMs { get; set; }
    }
}

internal static class SpotifyWebRequestProfile
{
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    internal const string AppPlatform = "WebPlayer";
    internal const string AppVersion = "1.2.46.25.g7f189073";

    internal static void Apply(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("app-platform", AppPlatform);
        request.Headers.TryAddWithoutValidation("spotify-app-version", AppVersion);
    }
}
