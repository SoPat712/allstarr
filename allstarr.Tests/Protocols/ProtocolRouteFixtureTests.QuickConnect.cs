using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinQuickConnect_AnonymousFlowPreservesNativeStateQueriesAndPayloads()
    {
        const string secret = "fixture-secret";
        const string code = "ABCD";
        const string noTokenAuthorization =
            "MediaBrowser Client=\"Streamyfin\", Device=iOS, " +
            "DeviceId=\"fixture-device\", Version=\"0.55.2\"";
        const string userAuthorization =
            "MediaBrowser Client=\"Streamyfin\", Device=iOS, " +
            "DeviceId=\"fixture-device\", Version=\"0.55.2\", Token=\"fixture-token\"";
        const string tokenBody = "{\"secret\":\"fixture-secret\"}";
        var authorized = false;
        var observed = new List<(ObservedRequest Request, string? Authorization)>();

        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.SingleOrDefault()
                : null;
            var observedRequest = Observe(request);
            observed.Add((observedRequest, authorization));

            var path = request.RequestUri!.AbsolutePath;
            if (path == "/QuickConnect/Enabled")
                return Json(StatusCodes.Status200OK, "true");

            if (path == "/QuickConnect/Initiate")
            {
                Assert.Equal("POST", observedRequest.Method);
                Assert.True(string.IsNullOrEmpty(observedRequest.Body));
                Assert.Equal(noTokenAuthorization, authorization);
                return Json(StatusCodes.Status200OK, QuickConnectState(authorized));
            }

            if (path == "/QuickConnect/Connect")
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (query["Secret"] != secret)
                    return Json(StatusCodes.Status404NotFound, "{\"Message\":\"Unknown secret\"}");
                return Json(StatusCodes.Status200OK, QuickConnectState(authorized));
            }

            if (path == "/QuickConnect/Authorize")
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (authorization != userAuthorization ||
                    query["code"] != code ||
                    query["userId"] != "fixture-user")
                    return Json(StatusCodes.Status403Forbidden, "{\"Message\":\"Authorization denied\"}");

                authorized = true;
                return Json(StatusCodes.Status200OK, "true");
            }

            if (path == "/Users/Me")
                return Json(StatusCodes.Status200OK, "{\"Id\":\"fixture-user\"}");

            if (path == "/Users/AuthenticateWithQuickConnect")
            {
                if (authorization != noTokenAuthorization || observedRequest.Body != tokenBody || !authorized)
                    return Json(StatusCodes.Status404NotFound, "{\"Message\":\"Unknown secret\"}");

                return Json(
                    StatusCodes.Status200OK,
                    "{\"User\":{\"Id\":\"fixture-user\"},\"SessionInfo\":{\"Id\":\"fixture-session\"}," +
                    "\"AccessToken\":\"fixture-access-token\",\"ServerId\":\"fixture-server\"}");
            }

            throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
        });
        using var client = factory.CreateClient();

        using var enabled = await client.GetAsync("/QuickConnect/Enabled");

        using var initiate = new HttpRequestMessage(
            HttpMethod.Post,
            "/QuickConnect/Initiate?client=Desk%20One&redirect=%2Fpair");
        initiate.Headers.TryAddWithoutValidation("Authorization", noTokenAuthorization);
        using var initiated = await client.SendAsync(initiate);

        using var pending = await client.GetAsync("/QuickConnect/Connect?Secret=fixture-secret");

        using var rejectedTokenRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/Users/AuthenticateWithQuickConnect")
        {
            Content = new StringContent(
                "{\"secret\":\"unapproved-secret\"}",
                Encoding.UTF8,
                "application/json")
        };
        rejectedTokenRequest.Headers.TryAddWithoutValidation("Authorization", noTokenAuthorization);
        using var rejectedTokenExchange = await client.SendAsync(rejectedTokenRequest);

        using var authorize = new HttpRequestMessage(
            HttpMethod.Post,
            "/QuickConnect/Authorize?code=ABCD&userId=fixture-user&return=%2Fpair");
        authorize.Headers.TryAddWithoutValidation("Authorization", userAuthorization);
        using var authorizedResponse = await client.SendAsync(authorize);

        using var authenticated = await client.GetAsync("/QuickConnect/Connect?Secret=fixture-secret");
        using var authenticate = new HttpRequestMessage(
            HttpMethod.Post,
            "/Users/AuthenticateWithQuickConnect")
        {
            Content = new StringContent(tokenBody, Encoding.UTF8, "application/json")
        };
        authenticate.Headers.TryAddWithoutValidation("Authorization", noTokenAuthorization);
        using var authenticatedResponse = await client.SendAsync(authenticate);

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal("true", await enabled.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, initiated.StatusCode);
        Assert.Equal(QuickConnectState(false), await initiated.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal(QuickConnectState(false), await pending.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, rejectedTokenExchange.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorizedResponse.StatusCode);
        Assert.Equal("true", await authorizedResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(QuickConnectState(true), await authenticated.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, authenticatedResponse.StatusCode);
        Assert.Equal(
            "{\"User\":{\"Id\":\"fixture-user\"},\"SessionInfo\":{\"Id\":\"fixture-session\"}," +
            "\"AccessToken\":\"fixture-access-token\",\"ServerId\":\"fixture-server\"}",
            await authenticatedResponse.Content.ReadAsStringAsync());

        var initiation = Assert.Single(
            observed,
            entry => entry.Request.PathAndQuery == "/QuickConnect/Initiate?client=Desk%20One&redirect=%2Fpair");
        Assert.Equal(noTokenAuthorization, initiation.Authorization);
        var pendingPolls = observed
            .Where(entry => entry.Request.PathAndQuery == "/QuickConnect/Connect?Secret=fixture-secret")
            .ToArray();
        Assert.Equal(2, pendingPolls.Length);
        Assert.All(pendingPolls, entry => Assert.Null(entry.Authorization));
        var authorizeRelay = Assert.Single(
            observed,
            entry => entry.Request.PathAndQuery ==
                "/QuickConnect/Authorize?code=ABCD&userId=fixture-user&return=%2Fpair");
        Assert.Equal(userAuthorization, authorizeRelay.Authorization);
        Assert.Equal("POST", authorizeRelay.Request.Method);
        Assert.True(string.IsNullOrEmpty(authorizeRelay.Request.Body));
        var tokenRelays = observed
            .Where(entry => entry.Request.PathAndQuery == "/Users/AuthenticateWithQuickConnect")
            .Select(entry => entry.Request.Body)
            .ToArray();
        Assert.Equal(2, tokenRelays.Length);
        Assert.Equal("{\"secret\":\"unapproved-secret\"}", tokenRelays[0]);
        Assert.Equal(tokenBody, tokenRelays[1]);
        Assert.All(
            observed.Where(entry => entry.Request.PathAndQuery == "/Users/AuthenticateWithQuickConnect"),
            entry => Assert.Equal(noTokenAuthorization, entry.Authorization));
        Assert.All(
            observed.Where(entry => entry.Request.PathAndQuery == "/Users/AuthenticateWithQuickConnect"),
            entry => Assert.Equal("application/json; charset=utf-8", entry.Request.ContentType));
    }

    [Fact]
    public async Task JellyfinQuickConnect_UpstreamDisabledScalarStateIsPreserved()
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add(Observe(request));
            return request.RequestUri!.AbsolutePath == "/QuickConnect/Enabled"
                ? Json(StatusCodes.Status200OK, "false")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        "Quick connect is disabled",
                        Encoding.UTF8,
                        "text/plain")
                };
        });
        using var client = factory.CreateClient();

        using var enabled = await client.GetAsync("/QuickConnect/Enabled");
        using var initiate = await client.PostAsync("/QuickConnect/Initiate", null);

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal("false", await enabled.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, initiate.StatusCode);
        Assert.Equal("Quick connect is disabled", await initiate.Content.ReadAsStringAsync());
        Assert.Equal(
            ["/QuickConnect/Enabled", "/QuickConnect/Initiate"],
            observed.Select(request => request.PathAndQuery).ToArray());
    }

    [Fact]
    public async Task JellyfinQuickConnect_AuthorizeRequiresAuthentication()
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add(Observe(request));
            return Json(StatusCodes.Status200OK, "true");
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/QuickConnect/Authorize?Code=ABCD", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(observed);
    }

    [Theory]
    [InlineData("POST", "/QuickConnect/Connect")]
    [InlineData("GET", "/QuickConnect/Authorize")]
    [InlineData("GET", "/QuickConnect/Connect/extra")]
    [InlineData("POST", "/QuickConnect/Initiate/extra")]
    public async Task JellyfinQuickConnect_NearPathsAndUnexpectedMethodsRemainProtected(
        string method,
        string path)
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add(Observe(request));
            return Json(StatusCodes.Status200OK, "{\"unexpected\":true}");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{\"fixture\":true}", Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(observed);
    }

    private static string QuickConnectState(bool authenticated) =>
        JsonSerializer.Serialize(new
        {
            Authenticated = authenticated,
            Secret = "fixture-secret",
            Code = "ABCD",
            DeviceId = "fixture-device",
            DeviceName = "Fixture Device",
            AppName = "Fixture App",
            AppVersion = "1.0",
            DateAdded = "2026-01-01T00:00:00Z"
        });
}
