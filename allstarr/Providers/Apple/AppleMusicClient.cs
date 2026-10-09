using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;

namespace allstarr.Core.Providers.AppleMusicKit;

public sealed record AppleMusicCredential(string MusicUserToken, string Storefront)
{
    public override string ToString() => $"Apple Music credential ({Storefront})";
    public static AppleMusicCredential? Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? token = null;
            var storefront = "us";
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                if (property.Name.Equals("MusicUserToken", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("media-user-token", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("mediaUserToken", StringComparison.OrdinalIgnoreCase))
                    token = property.Value.GetString();
                if (property.Name.Equals("Storefront", StringComparison.OrdinalIgnoreCase))
                    storefront = property.Value.GetString()?.Trim().ToLowerInvariant() ?? "";
            }
            return string.IsNullOrWhiteSpace(token) || token.Length > 16_384 ||
                   token.Any(character => char.IsControl(character) || character is ';' or ',') ||
                   storefront.Length != 2 || storefront.Any(character => character is < 'a' or > 'z')
                ? null : new(token.Trim(), storefront);
        }
        catch (JsonException) { return null; }
    }
}

public sealed class AppleMusicClient(HttpClient http, AppleWebTokenProvider tokens,
    IProviderAccountSecretAccessor secrets)
{
    public const string HttpClientName = "AppleMusicApi";
    public const string ProviderId = "apple-musickit";
    public static readonly Uri ApiOrigin = new("https://amp-api.music.apple.com/");
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    public static string NormalizeProviderId(string providerId) => providerId.Trim().ToLowerInvariant() switch
    {
        "apple-download" or "applemusic" or "apple-music" or "apple_music" => ProviderId,
        var value => value
    };

    public static ProviderError? Validate(ProviderExecutionContext context, bool personal)
    {
        if (context.ProviderId != ProviderId || !context.Policy.AllowsProvider(ProviderId))
            return new(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested) return new(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow)) return new(ProviderErrorKind.CapabilityUnavailable);
        if (!personal) return null;
        if (context.Actor.Kind == ProviderActorKind.PublicRead) return new(ProviderErrorKind.Forbidden);
        if (context.Account is not { Scope: ProviderAccountScope.Personal, SecretReferenceId: not null } account)
            return new(ProviderErrorKind.AccountNeedsConfiguration);
        return account.ProviderId != ProviderId || account.OwnerUserId != context.Actor.EffectiveUserId
            ? new(ProviderErrorKind.Forbidden) : null;
    }

    public async Task<ProviderOutcome<T>> ExecuteAsync<T>(ProviderExecutionContext context, bool personal,
        Func<AppleMusicCredential?, CancellationToken, Task<ProviderOutcome<T>>> operation)
    {
        var error = Validate(context, personal);
        if (error != null) return ProviderOutcome<T>.Failure(error);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            deadline.CancelAfter(context.Deadline - DateTimeOffset.UtcNow);
            if (!personal) return await operation(null, deadline.Token);
            return await secrets.UseAsync(context.Account!, async bytes =>
            {
                var credential = AppleMusicCredential.Read(bytes);
                return credential == null
                    ? ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration))
                    : await operation(credential, deadline.Token);
            }, deadline.Token);
        }
        catch (OperationCanceledException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Canceled)); }
        catch (KeyNotFoundException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration)); }
        catch (UnauthorizedAccessException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.Forbidden)); }
        catch (AppleWebTokenUnavailableException) { return ProviderOutcome<T>.Failure(ProviderError.AppleWebTokenUnavailable()); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { return ProviderOutcome<T>.Failure(ProviderError.CompatibilityContractChanged()); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.TransientFailure)); }
    }

    public async Task<AppleMusicHttpResult> SendAsync(AppleMusicCredential? credential, string relative,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(ApiOrigin, relative);
        if (!IsApiOrigin(uri) || !uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
            return new(null, null, new(ProviderErrorKind.Forbidden));
        var bearer = await tokens.GetLeaseAsync(cancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Token);
            request.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");
            if (credential != null)
                request.Headers.TryAddWithoutValidation("Cookie", $"media-user-token={credential.MusicUserToken}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
                return new(null, null, new(ProviderErrorKind.Forbidden));
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                bearer = await tokens.RefreshAsync(bearer, cancellationToken);
                continue;
            }
            var etag = response.Headers.ETag?.Tag;
            if (!response.IsSuccessStatusCode) return new(null, etag, Error(response, credential != null));
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken);
            return new(await response.Content.ReadAsByteArrayAsync(cancellationToken), etag, null);
        }
        return new(null, null, new(ProviderErrorKind.TransientFailure));
    }

    public Task<HttpResponseMessage> SendSidecarAsync(ProviderExecutionContext context, HttpClient transport,
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var error = Validate(context, personal: true);
        if (error != null) throw new UnauthorizedAccessException(error.SafeMessage);
        return secrets.UseAsync(context.Account!, async bytes =>
        {
            var credential = AppleMusicCredential.Read(bytes) ?? throw new KeyNotFoundException("The Apple account needs configuration.");
            var scope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                $"{context.Account!.AccountId:N}:{context.Account.Revision}"))).ToLowerInvariant();
            request.Headers.Remove("Music-User-Token");
            request.Headers.Remove("X-Allstarr-Account-Context");
            request.Headers.Remove("X-Apple-Storefront");
            request.Headers.TryAddWithoutValidation("Music-User-Token", credential.MusicUserToken);
            request.Headers.TryAddWithoutValidation("X-Allstarr-Account-Context", scope);
            request.Headers.TryAddWithoutValidation("X-Apple-Storefront", credential.Storefront);
            try { return await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            finally
            {
                request.Headers.Remove("Music-User-Token");
                request.Headers.Remove("X-Allstarr-Account-Context");
                request.Headers.Remove("X-Apple-Storefront");
            }
        }, cancellationToken);
    }

    public Task<ProviderOutcome<string>> ResolveCatalogTrackAsync(ProviderExecutionContext context, string trackId) =>
        ExecuteAsync(context, true, async (credential, ct) =>
        {
            if (trackId.All(char.IsAsciiDigit) && trackId.Length > 0) return ProviderOutcome<string>.Success(trackId);
            if (!AppleMusicKitMetadataCapabilityAdapter.IsLibrary(trackId))
                return ProviderOutcome<string>.Failure(new(ProviderErrorKind.NotSupported));
            var response = await SendAsync(credential, $"v1/me/library/songs/{Uri.EscapeDataString(trackId)}", ct);
            if (!response.Outcome.IsSuccess) return ProviderOutcome<string>.Failure(response.Outcome.Error!);
            using var document = JsonDocument.Parse(response.Body!);
            if (document.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() == 1 &&
                data[0].TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty("playParams", out var parameters) &&
                parameters.TryGetProperty("catalogId", out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } catalogId && catalogId.All(char.IsAsciiDigit))
                return ProviderOutcome<string>.Success(catalogId);
            return ProviderOutcome<string>.Failure(new(ProviderErrorKind.NotSupported));
        });

    private static bool IsApiOrigin(Uri uri) => uri.Scheme == "https" && uri.Host == ApiOrigin.Host &&
        uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo);

    private static ProviderError Error(HttpResponseMessage response, bool personal) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when personal => new(ProviderErrorKind.AccountNeedsReauthentication),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderError.AppleWebTokenUnavailable(),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited,
            response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };
}

public sealed record AppleMusicHttpResult(byte[]? Body, string? ETag, ProviderError? Error)
{
    public ProviderOutcome<byte[]> Outcome => Error == null
        ? ProviderOutcome<byte[]>.Success(Body!) : ProviderOutcome<byte[]>.Failure(Error);
}
