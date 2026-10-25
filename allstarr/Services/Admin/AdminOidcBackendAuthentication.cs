using System.Net;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Subsonic;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Admin;

public sealed record AdminOidcCredential(string Backend, string Endpoint, string UserId, string Credential);
public sealed record AdminOidcBackendUser(string UserId, string Name, bool IsAdministrator, string AccessToken);

public enum AdminBackendSessionValidationStatus
{
    Valid,
    Invalid,
    Unavailable
}

public sealed record AdminBackendSessionValidationResult(
    AdminBackendSessionValidationStatus Status,
    AdminOidcBackendUser? User = null);

public interface IAdminBackendSessionValidator
{
    Task<AdminBackendSessionValidationResult> ValidateSessionAsync(
        AdminAuthSession session,
        CancellationToken cancellationToken);
}

public sealed class AdminBackendSessionUnavailableException : Exception
{
    public AdminBackendSessionUnavailableException()
        : base("The media backend is temporarily unavailable. Try again.") { }
}

public sealed class AdminOidcBackendAuthentication(
    IConfiguration configuration,
    IOptions<JellyfinSettings> jellyfin,
    IOptions<SubsonicSettings> subsonic,
    IHttpClientFactory clients) : IAdminBackendSessionValidator
{
    public const string HttpClientName = "AdminBackendSessionValidation";
    private const int MaximumSessionResponseBytes = 256 * 1024;
    private static readonly TimeSpan BackendTimeout = TimeSpan.FromSeconds(15);

    public string Backend => string.Equals(configuration["Backend:Type"], "Subsonic", StringComparison.OrdinalIgnoreCase)
        ? "subsonic" : "jellyfin";
    public string Endpoint => EndpointFor(Backend);

    internal (string Backend, string Endpoint) SnapshotConfiguration()
    {
        var backend = Backend;
        return (backend, EndpointFor(backend));
    }

    private string EndpointFor(string backend) =>
        (backend == "subsonic" ? subsonic.Value.Url : jellyfin.Value.Url)?.TrimEnd('/') ?? "";

    public async Task<AdminOidcBackendUser?> ValidateAsync(AdminOidcCredential credential, CancellationToken cancellationToken)
    {
        var (backend, endpoint) = SnapshotConfiguration();
        if (credential.Backend != backend || credential.Endpoint != endpoint ||
            string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(credential.Credential)) return null;

        using var request = backend == "jellyfin"
            ? new HttpRequestMessage(HttpMethod.Get, endpoint + "/Users/Me")
            : new HttpRequestMessage(HttpMethod.Post, endpoint + "/rest/getUser.view")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["u"] = credential.UserId,
                    ["p"] = credential.Credential,
                    ["username"] = credential.UserId,
                    ["v"] = "1.16.1",
                    ["c"] = "allstarr-admin",
                    ["f"] = "json"
                })
            };
        if (backend == "jellyfin")
            request.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(credential.Credential));
        using var client = clients.CreateClient(HttpClientName);
        client.Timeout = BackendTimeout;
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!IsCurrentConfiguration(backend, endpoint)) return null;
        if (backend == "subsonic")
        {
            return AdminBackendIdentity.TryReadSubsonic(root, credential.UserId, out var user)
                ? user : null;
        }

        return AdminBackendIdentity.TryReadJellyfin(root, credential.UserId, credential.Credential, out var jellyfinUser)
            ? jellyfinUser : null;
    }

    public async Task<AdminBackendSessionValidationResult> ValidateSessionAsync(
        AdminAuthSession session,
        CancellationToken cancellationToken)
    {
        var (backend, endpoint) = SnapshotConfiguration();
        if (!string.Equals(session.BackendType, backend, StringComparison.OrdinalIgnoreCase))
            return new(AdminBackendSessionValidationStatus.Invalid);
        if (string.IsNullOrWhiteSpace(endpoint))
            return new(AdminBackendSessionValidationStatus.Unavailable);

        try
        {
            using var client = clients.CreateClient(HttpClientName);
            client.Timeout = BackendTimeout;
            using var request = CreateSessionRequest(session, backend, endpoint);
            if (request == null)
                return new(AdminBackendSessionValidationStatus.Unavailable);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return IsCurrentConfiguration(backend, endpoint)
                    ? new(AdminBackendSessionValidationStatus.Invalid)
                    : new(AdminBackendSessionValidationStatus.Unavailable);

            // Redirects are disabled on the dedicated client. Treat any redirect as a
            // temporary backend failure and never parse or forward its destination.
            if ((int)response.StatusCode is >= 300 and <= 399)
                return new(AdminBackendSessionValidationStatus.Unavailable);
            if ((int)response.StatusCode is 408 or 429 || (int)response.StatusCode >= 500)
                return new(AdminBackendSessionValidationStatus.Unavailable);

            if (!response.IsSuccessStatusCode)
            {
                if (backend != "subsonic")
                    return new(AdminBackendSessionValidationStatus.Unavailable);
                using var failedDocument = await ReadBoundedDocumentAsync(response.Content, cancellationToken);
                var failedResult = failedDocument == null
                    ? new(AdminBackendSessionValidationStatus.Unavailable)
                    : ParseSubsonicNonSuccess(failedDocument.RootElement);
                return IsCurrentConfiguration(backend, endpoint)
                    ? failedResult
                    : new(AdminBackendSessionValidationStatus.Unavailable);
            }

            using var document = await ReadBoundedDocumentAsync(response.Content, cancellationToken);
            if (document == null)
                return new(AdminBackendSessionValidationStatus.Unavailable);

            var result = backend == "subsonic"
                ? ParseSubsonicSession(document.RootElement, session.UserId, session.IsAdministrator)
                : ParseJellyfinSession(document.RootElement, session.UserId, session.IsAdministrator,
                    session.JellyfinAccessToken);
            return IsCurrentConfiguration(backend, endpoint)
                ? result
                : new(AdminBackendSessionValidationStatus.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(AdminBackendSessionValidationStatus.Unavailable);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or UriFormatException or InvalidOperationException)
        {
            return new(AdminBackendSessionValidationStatus.Unavailable);
        }
    }

    private HttpRequestMessage? CreateSessionRequest(
        AdminAuthSession session,
        string backend,
        string endpoint)
    {
        if (backend == "jellyfin")
        {
            if (string.IsNullOrWhiteSpace(session.JellyfinAccessToken)) return null;
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/Users/Me");
            request.Headers.TryAddWithoutValidation(
                "Authorization", AuthHeaderHelper.CreateAuthHeader(session.JellyfinAccessToken));
            return request;
        }

        if (!SubsonicSessionAuthentication.TryGetSessionReadParameters(session, out var authentication) ||
            authentication.Any(item => item.Key.Equals("p", StringComparison.OrdinalIgnoreCase)))
            return null;

        if (authentication.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return null;
        var parameters = authentication.ToList();
        var user = parameters.FirstOrDefault(item => item.Key.Equals("u", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(user.Value) &&
            !user.Value.Equals(session.UserId, StringComparison.OrdinalIgnoreCase))
            return null;
        var apiKey = parameters.FirstOrDefault(item => item.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase));
        if (parameters.Any(item => item.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(apiKey.Value) || parameters.Any(item =>
                    item.Key.Equals("t", StringComparison.OrdinalIgnoreCase) || item.Key.Equals("s", StringComparison.OrdinalIgnoreCase)))
                return null;
            parameters.RemoveAll(item => item.Key.Equals("u", StringComparison.OrdinalIgnoreCase));
        }
        else if (string.IsNullOrWhiteSpace(user.Value))
        {
            parameters.Add(new("u", session.UserId));
        }
        parameters.Add(new("username", session.UserId));
        parameters.Add(new("f", "json"));
        return new HttpRequestMessage(HttpMethod.Post, endpoint + "/rest/getUser.view")
        {
            Content = new FormUrlEncodedContent(parameters)
        };
    }

    private AdminBackendSessionValidationResult ParseSubsonicNonSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("subsonic-response", out var envelope) ||
            envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            !string.Equals(status.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
            return new(AdminBackendSessionValidationStatus.Unavailable);
        return HasDefinitiveSubsonicAuthFailure(envelope)
            ? new(AdminBackendSessionValidationStatus.Invalid)
            : new(AdminBackendSessionValidationStatus.Unavailable);
    }

    private bool IsCurrentConfiguration(string backend, string endpoint)
    {
        var current = SnapshotConfiguration();
        return string.Equals(current.Backend, backend, StringComparison.Ordinal) &&
               string.Equals(current.Endpoint, endpoint, StringComparison.Ordinal);
    }

    private static AdminBackendSessionValidationResult ParseJellyfinSession(
        JsonElement root,
        string expectedUserId,
        bool expectedAdministrator,
        string accessToken)
    {
        if (!root.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String)
            return new(AdminBackendSessionValidationStatus.Unavailable);
        var returnedId = id.GetString();
        if (string.IsNullOrWhiteSpace(returnedId))
            return new(AdminBackendSessionValidationStatus.Unavailable);
        if (!string.Equals(returnedId, expectedUserId, StringComparison.Ordinal))
            return new(AdminBackendSessionValidationStatus.Invalid);

        if (!root.TryGetProperty("Policy", out var policy) || policy.ValueKind != JsonValueKind.Object)
            return new(AdminBackendSessionValidationStatus.Unavailable);
        if (!policy.TryGetProperty("IsDisabled", out var disabled) ||
            (disabled.ValueKind != JsonValueKind.True && disabled.ValueKind != JsonValueKind.False))
            return new(AdminBackendSessionValidationStatus.Unavailable);
        if (disabled.ValueKind == JsonValueKind.True)
            return new(AdminBackendSessionValidationStatus.Invalid);
        if (!root.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()))
            return new(AdminBackendSessionValidationStatus.Unavailable);
        if (!policy.TryGetProperty("IsAdministrator", out var administrator) ||
            (administrator.ValueKind != JsonValueKind.True && administrator.ValueKind != JsonValueKind.False))
            return new(AdminBackendSessionValidationStatus.Unavailable);

        var user = new AdminOidcBackendUser(
            expectedUserId,
            name.GetString()!,
            administrator.ValueKind == JsonValueKind.True,
            accessToken);
        return user.IsAdministrator == expectedAdministrator
            ? new(AdminBackendSessionValidationStatus.Valid, user)
            : new(AdminBackendSessionValidationStatus.Invalid, user);
    }

    private static AdminBackendSessionValidationResult ParseSubsonicSession(
        JsonElement root,
        string expectedUserId,
        bool expectedAdministrator)
    {
        if (!root.TryGetProperty("subsonic-response", out var envelope) ||
            envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String)
            return new(AdminBackendSessionValidationStatus.Unavailable);

        var statusText = status.GetString();
        if (string.Equals(statusText, "failed", StringComparison.OrdinalIgnoreCase))
            return HasDefinitiveSubsonicAuthFailure(envelope)
                ? new(AdminBackendSessionValidationStatus.Invalid)
                : new(AdminBackendSessionValidationStatus.Unavailable);
        if (!string.Equals(statusText, "ok", StringComparison.OrdinalIgnoreCase))
            return new(AdminBackendSessionValidationStatus.Unavailable);
        if (!envelope.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            return new(AdminBackendSessionValidationStatus.Unavailable);
        if (!user.TryGetProperty("username", out var username) || username.ValueKind != JsonValueKind.String)
            return new(AdminBackendSessionValidationStatus.Unavailable);
        var returnedName = username.GetString();
        if (string.IsNullOrWhiteSpace(returnedName))
            return new(AdminBackendSessionValidationStatus.Unavailable);
        if (!string.Equals(returnedName, expectedUserId, StringComparison.OrdinalIgnoreCase))
            return new(AdminBackendSessionValidationStatus.Invalid);
        if (!user.TryGetProperty("adminRole", out var administrator) ||
            (administrator.ValueKind != JsonValueKind.True && administrator.ValueKind != JsonValueKind.False))
            return new(AdminBackendSessionValidationStatus.Unavailable);

        var identity = new AdminOidcBackendUser(returnedName, returnedName,
            administrator.ValueKind == JsonValueKind.True, "");
        return identity.IsAdministrator == expectedAdministrator
            ? new(AdminBackendSessionValidationStatus.Valid, identity)
            : new(AdminBackendSessionValidationStatus.Invalid, identity);
    }

    private static bool HasDefinitiveSubsonicAuthFailure(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("code", out var code))
            return false;
        var value = code.ValueKind switch
        {
            JsonValueKind.Number when code.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(code.GetString(), out var number) => number,
            _ => 0
        };
        return value is 40 or 44 or 50;
    }

    private static async Task<JsonDocument?> ReadBoundedDocumentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumSessionResponseBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (body.Length + read > MaximumSessionResponseBytes) return null;
            body.Write(buffer, 0, read);
        }

        return JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
}

internal static class AdminBackendIdentity
{
    public static bool TryReadJellyfin(JsonElement root, string expectedUserId, string accessToken, out AdminOidcBackendUser identity)
    {
        identity = null!;
        if (!root.TryGetProperty("Id", out var id) || id.GetString() != expectedUserId ||
            !root.TryGetProperty("Name", out var name) || string.IsNullOrEmpty(name.GetString()) ||
            !root.TryGetProperty("Policy", out var policy) ||
            !policy.TryGetProperty("IsDisabled", out var disabled) || disabled.ValueKind != JsonValueKind.False)
            return false;
        identity = new(expectedUserId, name.GetString()!,
            policy.TryGetProperty("IsAdministrator", out var admin) && admin.ValueKind == JsonValueKind.True, accessToken);
        return true;
    }

    public static bool TryReadSubsonic(JsonElement root, string username, out AdminOidcBackendUser identity,
        bool allowMissingUserName = false)
    {
        identity = null!;
        if (!root.TryGetProperty("subsonic-response", out var envelope) ||
            !envelope.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase) ||
            !envelope.TryGetProperty("user", out var user)) return false;
        var name = user.TryGetProperty("username", out var returnedName) ? returnedName.GetString() : allowMissingUserName ? username : null;
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, username, StringComparison.OrdinalIgnoreCase)) return false;
        identity = new(name, name,
            user.TryGetProperty("adminRole", out var admin) && admin.ValueKind == JsonValueKind.True, "");
        return true;
    }
}
