using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Admin;

public sealed record AdminOidcCredential(string Backend, string Endpoint, string UserId, string Credential);
public sealed record AdminOidcBackendUser(string UserId, string Name, bool IsAdministrator, string AccessToken);

public sealed class AdminOidcBackendAuthentication(
    IConfiguration configuration,
    IOptions<JellyfinSettings> jellyfin,
    IOptions<SubsonicSettings> subsonic,
    IHttpClientFactory clients)
{
    public string Backend => string.Equals(configuration["Backend:Type"], "Subsonic", StringComparison.OrdinalIgnoreCase)
        ? "subsonic" : "jellyfin";
    public string Endpoint => (Backend == "subsonic" ? subsonic.Value.Url : jellyfin.Value.Url)?.TrimEnd('/') ?? "";

    public async Task<AdminOidcBackendUser?> ValidateAsync(AdminOidcCredential credential, CancellationToken cancellationToken)
    {
        if (credential.Backend != Backend || credential.Endpoint != Endpoint ||
            string.IsNullOrEmpty(Endpoint) || string.IsNullOrEmpty(credential.Credential)) return null;

        using var request = Backend == "jellyfin"
            ? new HttpRequestMessage(HttpMethod.Get, Endpoint + "/Users/Me")
            : new HttpRequestMessage(HttpMethod.Post, Endpoint + "/rest/getUser.view")
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
        if (Backend == "jellyfin")
            request.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(credential.Credential));
        using var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (Backend == "subsonic")
        {
            return AdminBackendIdentity.TryReadSubsonic(root, credential.UserId, out var user)
                ? user : null;
        }

        if (!root.TryGetProperty("Id", out var id) || id.GetString() != credential.UserId ||
            !root.TryGetProperty("Name", out var name) || string.IsNullOrEmpty(name.GetString()) ||
            !root.TryGetProperty("Policy", out var policy) ||
            !policy.TryGetProperty("IsDisabled", out var disabled) || disabled.ValueKind != JsonValueKind.False)
            return null;
        return new(credential.UserId, name.GetString()!,
            policy.TryGetProperty("IsAdministrator", out var admin) && admin.ValueKind == JsonValueKind.True,
            credential.Credential);
    }
}

internal static class AdminBackendIdentity
{
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
