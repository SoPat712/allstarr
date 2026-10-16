using System.Text;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;

namespace allstarr.Core.Configuration;

public interface IBackendConnectionVerifier
{
    Task<AdminOidcBackendUser?> VerifyAsync(BackendType backend, string endpoint, string username, string password, CancellationToken token);
}

public sealed class BackendConnectionVerifier(IHttpClientFactory clients) : IBackendConnectionVerifier
{
    public const string HttpClientName = "backend-setup";

    public async Task<AdminOidcBackendUser?> VerifyAsync(BackendType backend, string endpoint, string username, string password, CancellationToken token)
    {
        using var client = clients.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.MaxResponseContentBufferSize = 1024 * 1024;
        if (backend == BackendType.Subsonic)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/rest/getUser.view")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["u"] = username,
                    ["p"] = password,
                    ["username"] = username,
                    ["v"] = "1.16.1",
                    ["c"] = "allstarr-setup",
                    ["f"] = "json"
                })
            };
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            return AdminBackendIdentity.TryReadSubsonic(document.RootElement, username, out var user) ? user : null;
        }
        using var login = new HttpRequestMessage(HttpMethod.Post, endpoint + "/Users/AuthenticateByName")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { Username = username, Pw = password }), Encoding.UTF8, "application/json")
        };
        login.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader("", "AllstarrSetup", "WebUI", "allstarr-setup", "1"));
        using var result = await client.SendAsync(login, token);
        if (!result.IsSuccessStatusCode) return null;
        using var authentication = JsonDocument.Parse(await result.Content.ReadAsByteArrayAsync(token));
        var root = authentication.RootElement;
        if (!root.TryGetProperty("AccessToken", out var access) || string.IsNullOrWhiteSpace(access.GetString()) ||
            !root.TryGetProperty("User", out var userObject) || !userObject.TryGetProperty("Id", out var userId) ||
            string.IsNullOrWhiteSpace(userId.GetString())) return null;
        using var verify = new HttpRequestMessage(HttpMethod.Get, endpoint + "/Users/Me");
        verify.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(access.GetString()!));
        using var verified = await client.SendAsync(verify, token);
        if (!verified.IsSuccessStatusCode) return null;
        using var identity = JsonDocument.Parse(await verified.Content.ReadAsByteArrayAsync(token));
        return AdminBackendIdentity.TryReadJellyfin(identity.RootElement, userId.GetString()!, access.GetString()!, out var administrator)
            ? administrator : null;
    }
}
