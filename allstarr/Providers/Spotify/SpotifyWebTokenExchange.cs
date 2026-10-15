using System.Collections.Concurrent;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OtpNet;
using allstarr.Core.Capabilities;
using allstarr.Models.Settings;

namespace allstarr.Core.Providers.Spotify;

internal sealed record SpotifyWebTokenResult(string? AccessToken, bool IsAnonymous, string? ReasonCode, long? ExpiresAtMilliseconds = null)
{
    public bool Success => !string.IsNullOrWhiteSpace(AccessToken);
}

internal sealed class SpotifyWebTokenExchange(
    HttpClient http,
    IProviderAccountSecretAccessor? secretsAccessor,
    SpotifyApiSettings compatibility,
    TimeProvider clock)
{
    // Keep scope gates stable while requests may be waiting; expired token values are cleared independently.
    private readonly ConcurrentDictionary<TokenScope, TokenScopeState> scopes = [];
    private readonly SemaphoreSlim totpSecretsGate = new(1, 1);
    private TotpSecret? cachedSecret;
    private string? cachedSecretsFingerprint;
    private DateTimeOffset secretsExpireAt;

    public async Task<SpotifyWebTokenResult> GetAsync(ProviderAccountContext? account, CancellationToken cancellationToken)
    {
        var settings = new SpotifyApiSettings
        { TotpSecretsUrl = compatibility.TotpSecretsUrl, TokenEndpoint = compatibility.TokenEndpoint };
        if (!ValidSettings(settings)) return new(null, false, "invalid_compatibility_settings");
        var scope = Scope(account, settings);
        PruneExpiredTokens();
        var state = scopes.GetOrAdd(scope, static _ => new TokenScopeState());
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (state.Token is { } cached && cached.ExpiresAtMilliseconds > now + 30_000)
                return cached;
            state.Token = null;
            var result = account == null
                ? await ExchangeAsync(null, cancellationToken, settings, allowAnonymous: true)
                : await (secretsAccessor ?? throw new InvalidOperationException("Scoped token reads require a secret accessor."))
                    .UseAsync(account, secret =>
                {
                    var cookie = SpotifySessionCookie.Normalize(Encoding.UTF8.GetString(secret.Span));
                    return string.IsNullOrWhiteSpace(cookie)
                        ? Task.FromResult(new SpotifyWebTokenResult(null, false, "account_needs_configuration"))
                        : ExchangeAsync(cookie, cancellationToken, settings, allowAnonymous: false);
                }, cancellationToken);
            if (result.Success && result.ExpiresAtMilliseconds > now + 30_000)
                state.Token = result;
            return result;
        }
        finally { state.Gate.Release(); }
    }

    public async Task InvalidateAsync(ProviderAccountContext? account, string accessToken, CancellationToken cancellationToken)
    {
        var settings = new SpotifyApiSettings
        { TotpSecretsUrl = compatibility.TotpSecretsUrl, TokenEndpoint = compatibility.TokenEndpoint };
        var scope = Scope(account, settings);
        if (!scopes.TryGetValue(scope, out var state)) return;
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Token?.AccessToken == accessToken) state.Token = null;
        }
        finally { state.Gate.Release(); }
    }

    private void PruneExpiredTokens()
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var state in scopes.Values)
        {
            if (!state.Gate.Wait(0)) continue;
            try
            {
                if (state.Token?.ExpiresAtMilliseconds <= now + 30_000) state.Token = null;
            }
            finally { state.Gate.Release(); }
        }
    }

    private static TokenScope Scope(ProviderAccountContext? account, SpotifyApiSettings settings) => new(
        account?.AccountId, account?.Revision, account?.SecretReferenceId,
        Fingerprint($"{settings.TotpSecretsUrl}\n{settings.TokenEndpoint}"));

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record TokenScope(Guid? AccountId, long? Revision, Guid? SecretReferenceId, string CompatibilityFingerprint);

    private sealed class TokenScopeState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SpotifyWebTokenResult? Token { get; set; }
    }

    private static bool ValidSettings(SpotifyApiSettings settings) =>
        Uri.TryCreate(settings.TotpSecretsUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        settings.TokenEndpoint is SpotifyApiSettings.DefaultTokenEndpoint or "https://api-partner.spotify.com/api/token";

    private static readonly Uri SpotifyOrigin = new("https://open.spotify.com/");

    // Explicit credential probes verify the supplied cookie afresh instead of reusing an operational token.
    public static Task<SpotifyWebTokenResult> ExchangeAsync(HttpClient http, string? cookie,
        CancellationToken cancellationToken, SpotifyApiSettings? settings = null, bool allowAnonymous = false)
    {
        settings ??= new();
        return ValidSettings(settings)
            ? new SpotifyWebTokenExchange(http, null, settings, TimeProvider.System)
                .ExchangeAsync(cookie, cancellationToken, settings, allowAnonymous)
            : Task.FromResult(new SpotifyWebTokenResult(null, false, "invalid_compatibility_settings"));
    }

    private async Task<SpotifyWebTokenResult> ExchangeAsync(string? cookie, CancellationToken cancellationToken,
        SpotifyApiSettings settings, bool allowAnonymous)
    {
        try
        {
            var secretResult = await GetTotpSecretAsync(settings, cancellationToken);
            if (secretResult.Secret == null)
                return new(null, false, secretResult.ReasonCode ?? "totp_secrets_invalid");
            var secret = secretResult.Secret;

            using var timeRequest = new HttpRequestMessage(HttpMethod.Head, SpotifyOrigin);
            SpotifyWebRequestProfile.Apply(timeRequest);
            using var timeResponse = await http.SendAsync(timeRequest, cancellationToken);
            var serverTime = timeResponse.Headers.Date?.ToUnixTimeSeconds();
            if (!timeResponse.IsSuccessStatusCode || serverTime == null) return new(null, false, "spotify_time_unavailable");

            var transformed = secret.Secret.Select((value, index) => (byte)(value ^ ((index % 33) + 9))).ToArray();
            var key = Encoding.UTF8.GetBytes(string.Concat(transformed.Select(value => value.ToString())));
            var otp = new Totp(key, step: 30, totpSize: 6).ComputeTotp(DateTime.UnixEpoch.AddSeconds(serverTime.Value));
            var clientTime = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var uri = new Uri($"{settings.TokenEndpoint}?reason=init&productType=web-player&totp={otp}&totpServer={otp}&totpVer={secret.Version}&sTime={serverTime}&cTime={clientTime}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(cookie))
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
                if (token.IsAnonymous && !allowAnonymous)
                    return new(null, true, "anonymous_session");
                if (allowAnonymous && string.IsNullOrWhiteSpace(cookie) && !token.IsAnonymous)
                    return new(null, false, "unexpected_authenticated_session");
                if (token.AccessTokenExpirationTimestampMs is { } expiresAt &&
                    expiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds())
                    return new(null, false, "expired_session");
                return new(token.AccessToken, token.IsAnonymous, null, token.AccessTokenExpirationTimestampMs);
            }
            catch (JsonException) { return new(null, false, "invalid_response"); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(null, false, $"exchange_exception_{ex.GetType().Name.ToLowerInvariant()}"); }
    }

    private async Task<(TotpSecret? Secret, string? ReasonCode)> GetTotpSecretAsync(
        SpotifyApiSettings settings, CancellationToken cancellationToken)
    {
        var secretsFingerprint = Fingerprint(settings.TotpSecretsUrl);
        await totpSecretsGate.WaitAsync(cancellationToken);
        try
        {
            if (cachedSecret != null && cachedSecretsFingerprint == secretsFingerprint &&
                clock.GetUtcNow() < secretsExpireAt)
                return (cachedSecret, null);

            using var secretsRequest = new HttpRequestMessage(HttpMethod.Get, settings.TotpSecretsUrl);
            SpotifyWebRequestProfile.Apply(secretsRequest);
            using var secretsResponse = await http.SendAsync(secretsRequest, cancellationToken);
            if (!secretsResponse.IsSuccessStatusCode)
                return (null, $"totp_secrets_http_{(int)secretsResponse.StatusCode}");

            TotpSecret[]? secrets;
            try
            {
                secrets = JsonSerializer.Deserialize<TotpSecret[]>(
                    await secretsResponse.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (JsonException)
            {
                return (null, "totp_secrets_invalid");
            }

            var secret = secrets?.OrderByDescending(item => item.Version).FirstOrDefault();
            if (secret == null || secret.Secret.Count == 0)
                return (null, "totp_secrets_invalid");
            cachedSecret = secret;
            cachedSecretsFingerprint = secretsFingerprint;
            secretsExpireAt = clock.GetUtcNow().AddMinutes(5);
            return (secret, null);
        }
        finally
        {
            totpSecretsGate.Release();
        }
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
