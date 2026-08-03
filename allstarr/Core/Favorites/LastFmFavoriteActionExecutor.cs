using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using allstarr.Core.Intelligence;
using allstarr.Core.ManagedFiles;

namespace allstarr.Core.Favorites;

public sealed class LastFmFavoriteActionExecutor(
    IHttpClientFactory clients,
    IScopedRecommendationAccountAccessor accounts,
    FavoriteTrackMetadataResolver metadataResolver) : IFavoriteActionExecutor
{
    public const string HttpClientName = "LastFmFavorite";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly Uri Endpoint = new("https://ws.audioscrobbler.com/2.0/");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string ActionType => FavoriteActionPipeline.LastFmAction;

    public async Task<FavoriteActionExecutionResult> ExecuteAsync(
        FavoriteEventRecord favoriteEvent,
        FavoriteActionRecord action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(favoriteEvent.LibraryScopeId))
            return FavoriteActionExecutionResult.Success();

        var track = await metadataResolver.ResolveAsync(favoriteEvent, cancellationToken);
        if (track == null || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
            return FavoriteActionExecutionResult.Success();

        var scope = new IntelligenceScope(
            favoriteEvent.TenantId,
            favoriteEvent.OwnerUserId,
            favoriteEvent.Protocol,
            favoriteEvent.BackendInstanceId,
            favoriteEvent.LibraryScopeId);
        try
        {
            return await accounts.UseAsync(scope, "lastfm", async (secret, token) =>
            {
                var values = BuildRequestValues(favoriteEvent.Operation, track, secret);
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new FormUrlEncodedContent(values)
                };
                using var http = clients.CreateClient(HttpClientName);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var body = await ReadResponseBodyAsync(response.Content, token);
                return Classify(response.StatusCode, body);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FavoriteActionExecutionResult.Retry(
                "lastfm-timeout", "Last.fm favorite delivery will retry.");
        }
        catch (NotSupportedException)
        {
            // The account may have been removed after the favorite event was recorded.
            return FavoriteActionExecutionResult.Success();
        }
        catch (KeyNotFoundException)
        {
            return FavoriteActionExecutionResult.Failure(
                "lastfm-account-secret-missing", "The selected Last.fm account needs configuration.");
        }
        catch (JsonException)
        {
            return FavoriteActionExecutionResult.Failure(
                "lastfm-account-config-invalid", "The selected Last.fm account needs configuration.");
        }
        catch (InvalidOperationException)
        {
            return FavoriteActionExecutionResult.Failure(
                "lastfm-account-config-invalid", "The selected Last.fm account needs configuration.");
        }
        catch (HttpRequestException)
        {
            return FavoriteActionExecutionResult.Retry(
                "lastfm-http", "Last.fm favorite delivery will retry.");
        }
        catch (TimeoutException)
        {
            return FavoriteActionExecutionResult.Retry(
                "lastfm-timeout", "Last.fm favorite delivery will retry.");
        }
        catch (InvalidDataException)
        {
            return FavoriteActionExecutionResult.Retry(
                "lastfm-invalid-response", "Last.fm returned an invalid favorite response.");
        }
        catch (DecoderFallbackException)
        {
            return FavoriteActionExecutionResult.Retry(
                "lastfm-invalid-response", "Last.fm returned an invalid favorite response.");
        }
    }

    internal static SortedDictionary<string, string> BuildRequestValues(
        FavoriteOperation operation,
        ManagedTrackPathValues track,
        JsonElement secret)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = RequiredSecret(secret, "apiKey"),
            ["artist"] = track.Artist.Trim(),
            ["method"] = operation switch
            {
                FavoriteOperation.Favorite => "track.love",
                FavoriteOperation.Unfavorite => "track.unlove",
                _ => throw new InvalidOperationException("The favorite operation is invalid.")
            },
            ["sk"] = RequiredSecret(secret, "sessionKey"),
            ["track"] = track.Title.Trim()
        };
        if (values["artist"].Length == 0 || values["track"].Length == 0)
            throw new InvalidOperationException("The favorite track metadata is incomplete.");
        values["api_sig"] = Sign(values, RequiredSecret(secret, "sharedSecret"));
        return values;
    }

    internal static FavoriteActionExecutionResult Classify(HttpStatusCode status, string body)
    {
        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)status >= 500)
            return FavoriteActionExecutionResult.Retry(
                "lastfm-http-" + (int)status, "Last.fm favorite delivery will retry.");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return FavoriteActionExecutionResult.Failure(
                "lastfm-auth", "Reconnect the selected Last.fm account and replace its credentials.");

        try
        {
            var trimmed = body.TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '{')
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (TryJsonError(root, out var errorCode)) return ApiError(errorCode);
                if (IsJsonSuccess(root))
                    return status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices
                        ? FavoriteActionExecutionResult.Success()
                        : HttpResult(status);
            }
            else if (trimmed.Length > 0 && trimmed[0] == '<')
            {
                using var reader = XmlReader.Create(new StringReader(body),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                var root = System.Xml.Linq.XDocument.Load(reader).Root;
                if (root != null)
                {
                    var error = root.Element("error");
                    if (error != null && int.TryParse(error.Attribute("code")?.Value,
                            out var errorCode))
                        return ApiError(errorCode);
                    if (string.Equals(root.Attribute("status")?.Value, "ok",
                            StringComparison.OrdinalIgnoreCase))
                        return status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices
                            ? FavoriteActionExecutionResult.Success()
                            : HttpResult(status);
                }
            }
        }
        catch (JsonException) { }
        catch (XmlException) { }
        catch (InvalidOperationException) { }

        return status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices
            ? FavoriteActionExecutionResult.Retry(
                "lastfm-invalid-response", "Last.fm returned an invalid favorite response.")
            : HttpResult(status);
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        var bytes = new byte[MaximumResponseBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), token);
            if (count == 0) break;
            length += count;
        }
        if (length > MaximumResponseBytes)
            throw new InvalidDataException("The Last.fm response exceeded the size limit.");
        return StrictUtf8.GetString(bytes, 0, length);
    }

    private static FavoriteActionExecutionResult ApiError(int code) => code switch
    {
        4 or 9 => FavoriteActionExecutionResult.Failure(
            "lastfm-api-" + code, "Reconnect the selected Last.fm account and replace its credentials."),
        10 or 13 or 26 => FavoriteActionExecutionResult.Failure(
            "lastfm-api-" + code, "The selected Last.fm account needs configuration."),
        8 or 11 or 16 or 29 => FavoriteActionExecutionResult.Retry(
            "lastfm-api-" + code, "Last.fm favorite delivery will retry."),
        _ => FavoriteActionExecutionResult.Failure(
            "lastfm-api-" + code, "Last.fm rejected the favorite request.")
    };

    private static FavoriteActionExecutionResult HttpResult(HttpStatusCode status) =>
        FavoriteActionExecutionResult.Failure(
            "lastfm-http-" + (int)status, "Last.fm rejected the favorite request.");

    private static bool TryJsonError(JsonElement root, out int code)
    {
        code = 0;
        if (!root.TryGetProperty("error", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out code)) return true;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out code);
    }

    private static bool IsJsonSuccess(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), "ok", StringComparison.OrdinalIgnoreCase);

    private static string RequiredSecret(JsonElement secret, string property)
    {
        foreach (var value in secret.EnumerateObject())
            if (value.Name.Equals(property, StringComparison.OrdinalIgnoreCase) &&
                value.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.Value.GetString()))
                return value.Value.GetString()!;
        throw new KeyNotFoundException("The selected Last.fm account is incomplete.");
    }

    private static string Sign(IEnumerable<KeyValuePair<string, string>> values, string sharedSecret)
    {
        var text = string.Concat(values.Where(item => item.Key != "api_sig")
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + item.Value)) + sharedSecret;
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
