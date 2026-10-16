using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Configuration;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public sealed class BackendConnectionVerifierTests
{
    [Fact]
    public async Task Jellyfin_VerifiesTheExactActivePrincipalAfterAuthentication()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("AuthenticateByName", StringComparison.Ordinal)
            ? Json(new { AccessToken = "private-token-fixture", User = new { Id = "principal-fixture" } })
            : Json(new { Id = "principal-fixture", Name = "Administrator", Policy = new { IsAdministrator = true, IsDisabled = false } }));
        var factory = new Clients(handler);
        var identity = await new BackendConnectionVerifier(factory).VerifyAsync(BackendType.Jellyfin,
            "http://backend.test", "administrator", "private-password-fixture", default);
        Assert.NotNull(identity);
        Assert.True(identity.IsAdministrator);
        Assert.Equal("principal-fixture", identity.UserId);
        Assert.Equal("private-token-fixture", identity.AccessToken);
        Assert.Equal(BackendConnectionVerifier.HttpClientName, factory.Name);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Contains("private-password-fixture", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("private-token-fixture", handler.Requests[1].Authorization, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.DoesNotContain("private-", request.Address, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Jellyfin_RejectsDisabledOrDifferentPrincipals(bool disabled, bool different)
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("AuthenticateByName", StringComparison.Ordinal)
            ? Json(new { AccessToken = "private-token-fixture", User = new { Id = "principal-fixture" } })
            : Json(new { Id = different ? "other-principal" : "principal-fixture", Name = "Administrator", Policy = new { IsAdministrator = true, IsDisabled = disabled } }));
        Assert.Null(await new BackendConnectionVerifier(new Clients(handler)).VerifyAsync(BackendType.Jellyfin,
            "http://backend.test", "administrator", "private-password-fixture", default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Subsonic_UsesVerifiedBackendRolesAndKeepsCredentialsOutOfTheAddress(bool administrator)
    {
        var handler = new Handler(_ => Json(new Dictionary<string, object>
        {
            ["subsonic-response"] = new { status = "ok", user = new { username = "admin-fixture", adminRole = administrator } }
        }));
        var result = await new BackendConnectionVerifier(new Clients(handler)).VerifyAsync(BackendType.Subsonic,
            "http://backend.test", "admin-fixture", "private-password-fixture", default);
        Assert.NotNull(result);
        Assert.Equal(administrator, result.IsAdministrator);
        Assert.Equal("", result.AccessToken);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://backend.test/rest/getUser.view", request.Address);
        Assert.Contains("p=private-password-fixture", request.Body, StringComparison.Ordinal);
        Assert.Contains("username=admin-fixture", request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("failed", "admin-fixture")]
    [InlineData("ok", "other-principal")]
    public async Task Subsonic_RejectsFailedOrUnrelatedAuthentication(string status, string username)
    {
        var handler = new Handler(_ => Json(new Dictionary<string, object>
        {
            ["subsonic-response"] = new { status, user = new { username, adminRole = true } }
        }));
        Assert.Null(await new BackendConnectionVerifier(new Clients(handler)).VerifyAsync(BackendType.Subsonic,
            "http://backend.test", "admin-fixture", "private-password-fixture", default));
    }

    [Fact]
    public async Task AuthenticationFailureNeverAttemptsTheIdentityRead()
    {
        var handler = new Handler(_ => new(HttpStatusCode.Unauthorized));
        Assert.Null(await new BackendConnectionVerifier(new Clients(handler)).VerifyAsync(BackendType.Jellyfin,
            "http://backend.test", "admin-fixture", "private-password-fixture", default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task VerificationRejectsOversizedResponses()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) });
        await Assert.ThrowsAsync<HttpRequestException>(() => new BackendConnectionVerifier(new Clients(handler)).VerifyAsync(
            BackendType.Jellyfin, "http://backend.test", "admin-fixture", "private-password-fixture", default));
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed record Request(string Method, string Address, string Body, string Authorization);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Content == null ? "" : await request.Content.ReadAsStringAsync(token), request.Headers.Authorization?.ToString() ?? ""));
            return response(request);
        }
    }
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? Name { get; private set; }
        public HttpClient CreateClient(string name) { Name = name; return new(handler, false); }
    }
}
