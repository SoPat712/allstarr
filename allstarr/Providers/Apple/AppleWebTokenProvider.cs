using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace allstarr.Core.Providers.AppleMusicKit;

public sealed class AppleWebTokenUnavailableException() : Exception("Apple web token is unavailable.");

/// <summary>Identifies the web token observation used by one Apple API request.</summary>
public sealed class AppleWebTokenLease
{
    internal AppleWebTokenLease(string token, long generation)
    {
        Token = token;
        Generation = generation;
    }

    public string Token { get; }
    public long Generation { get; }

    public override string ToString() => $"Apple web token lease (generation {Generation})";
}

/// <summary>Caches the public bearer published by Apple's web player, independently of account credentials.</summary>
public sealed class AppleWebTokenProvider(HttpClient http, TimeProvider? timeProvider = null)
{
    public const string HttpClientName = "AppleWebPlayer";
    private const int MaximumHomepageBytes = 8 * 1024 * 1024;
    private const int MaximumScriptBytes = 16 * 1024 * 1024;
    private const int MaximumTokenCharacters = 16 * 1024;
    private static readonly Uri Homepage = new("https://music.apple.com/");
    private static readonly Regex IndexScriptPattern = new(
        """["'](?<uri>(?:https://[^"'<>\s]+)?/assets/index[~-][A-Za-z0-9_.~-]+\.js(?:\?[^"'<>\s]*)?)["']""",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TokenPattern = new(
        """["'](?<token>eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)["']""",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Observation? _observation;
    private long _generation;

    public string? FailureCode => Volatile.Read(ref _observation)?.Failed == true
        ? "apple-web-token-unavailable" : null;

    public DateTimeOffset? ObservedAt => Volatile.Read(ref _observation)?.ObservedAt;

    public async Task<string> GetAsync(CancellationToken cancellationToken) =>
        (await GetLeaseAsync(cancellationToken)).Token;

    public async Task<AppleWebTokenLease> GetLeaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadCached(Volatile.Read(ref _observation)) is { } token) return token;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadCached(Volatile.Read(ref _observation)) is { } current) return current;
            return await LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppleWebTokenLease> RefreshAsync(
        AppleWebTokenLease rejectedLease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(rejectedLease);
        var observed = Volatile.Read(ref _observation);
        var cached = ReadCached(observed);
        if (observed != null && observed.Generation != rejectedLease.Generation)
            return cached ?? throw new AppleWebTokenUnavailableException();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentObservation = Volatile.Read(ref _observation);
            var current = ReadCached(currentObservation);
            // Compare the generation acquired before the API request, not when its
            // rejection arrives. Delayed rejections reuse even an unchanged JWT.
            // If that result has expired, a new request must acquire a fresh lease.
            if (currentObservation != null && currentObservation.Generation != rejectedLease.Generation)
                return current ?? throw new AppleWebTokenUnavailableException();
            return await LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AppleWebTokenLease? ReadCached(Observation? observation)
    {
        if (observation == null || observation.ValidUntil <= _time.GetUtcNow()) return null;
        if (observation.Failed) throw new AppleWebTokenUnavailableException();
        return observation.Lease;
    }

    private async Task<AppleWebTokenLease> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var homepage = await ReadTextAsync(Homepage, MaximumHomepageBytes, cancellationToken);
            var script = IndexScriptPattern.Match(homepage);
            if (!script.Success || !Uri.TryCreate(Homepage, script.Groups["uri"].Value, out var scriptUri) ||
                !IsPublicOrigin(scriptUri)) throw new AppleWebTokenUnavailableException();
            var javascript = await ReadTextAsync(scriptUri, MaximumScriptBytes, cancellationToken);
            foreach (Match candidate in TokenPattern.Matches(javascript))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = candidate.Groups["token"].Value;
                var now = _time.GetUtcNow();
                if (!TryGetExpiry(token, now, out var expiresAt)) continue;
                var validUntil = expiresAt.AddSeconds(-60);
                if (validUntil > now.AddHours(6)) validUntil = now.AddHours(6);
                cancellationToken.ThrowIfCancellationRequested();
                var lease = new AppleWebTokenLease(token, ++_generation);
                Volatile.Write(ref _observation, new Observation(lease, lease.Generation, validUntil, false, now));
                return lease;
            }
            throw new AppleWebTokenUnavailableException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AppleWebTokenUnavailableException or
            OperationCanceledException or HttpRequestException or IOException or JsonException or FormatException or
            ArgumentException or InvalidOperationException or RegexMatchTimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            Volatile.Write(ref _observation, new Observation(null, ++_generation, now.AddSeconds(30), true, now));
            throw new AppleWebTokenUnavailableException();
        }
    }

    private async Task<string> ReadTextAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPublicOrigin(uri)) throw new AppleWebTokenUnavailableException();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects == 3 || response.Headers.Location is not { } location ||
                    !Uri.TryCreate(uri, location, out var redirect) || !IsPublicOrigin(redirect))
                    throw new AppleWebTokenUnavailableException();
                uri = redirect;
                continue;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximumBytes)
                throw new AppleWebTokenUnavailableException();
            await response.Content.LoadIntoBufferAsync(maximumBytes, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }

    private static bool IsPublicOrigin(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.IdnHost, Homepage.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    private static bool TryGetExpiry(string token, DateTimeOffset now, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (token.Length > MaximumTokenCharacters) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            using var header = JsonDocument.Parse(DecodeBase64Url(parts[0]), new JsonDocumentOptions { MaxDepth = 8 });
            using var payload = JsonDocument.Parse(DecodeBase64Url(parts[1]), new JsonDocumentOptions { MaxDepth = 16 });
            if (header.RootElement.ValueKind != JsonValueKind.Object ||
                payload.RootElement.ValueKind != JsonValueKind.Object ||
                !payload.RootElement.TryGetProperty("exp", out var expiry) || !expiry.TryGetInt64(out var seconds))
                return false;
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return expiresAt > now;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
            ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
    }

    private sealed record Observation(
        AppleWebTokenLease? Lease, long Generation, DateTimeOffset ValidUntil, bool Failed, DateTimeOffset ObservedAt);
}
