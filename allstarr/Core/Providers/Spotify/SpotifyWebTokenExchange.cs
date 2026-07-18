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
        using var secretsResponse = await http.GetAsync(SecretsUri, cancellationToken);
        if (!secretsResponse.IsSuccessStatusCode) return new(null, false, $"totp_secrets_http_{(int)secretsResponse.StatusCode}");
        var secrets = JsonSerializer.Deserialize<TotpSecret[]>(await secretsResponse.Content.ReadAsStringAsync(cancellationToken));
        var secret = secrets?.OrderByDescending(item => item.Version).FirstOrDefault();
        if (secret == null || secret.Secret.Count == 0) return new(null, false, "totp_secrets_invalid");

        using var timeRequest = new HttpRequestMessage(HttpMethod.Head, SpotifyOrigin);
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
        request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
        request.Headers.TryAddWithoutValidation("spotify-app-version", "1.2.46.25.g7f189073");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new(null, false, response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "provider_unauthorized" : $"upstream_http_{(int)response.StatusCode}");
        try
        {
            var token = JsonSerializer.Deserialize<TokenResponse>(await response.Content.ReadAsStringAsync(cancellationToken));
            return token == null || string.IsNullOrWhiteSpace(token.AccessToken)
                ? new(null, false, "invalid_response")
                : new(token.AccessToken, token.IsAnonymous, token.IsAnonymous ? "anonymous_session" : null);
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
    }
}
