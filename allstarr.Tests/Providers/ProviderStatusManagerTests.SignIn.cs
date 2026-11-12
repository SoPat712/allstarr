using System.Net;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderStatusManagerTests
{
    [Theory]
    [InlineData("deezer", 401, "{}")]
    [InlineData("deezer", 403, "{}")]
    [InlineData("deezer", 200, "{\"results\":{\"USER\":{\"USER_ID\":0}}}")]
    [InlineData("qobuz", 401, "{}")]
    [InlineData("qobuz", 403, "{}")]
    [InlineData("qobuz", 200, "{\"status\":\"error\",\"code\":401,\"message\":\"Invalid user auth token\"}")]
    [InlineData("lastfm", 200, "{\"error\":4,\"message\":\"Authentication failed\"}")]
    [InlineData("lastfm", 200, "{\"error\":9,\"message\":\"Invalid session key\"}")]
    [InlineData("lastfm", 200, "{\"error\":14,\"message\":\"Unauthorized token\"}")]
    [InlineData("listenbrainz", 200, "{\"valid\":false}")]
    [InlineData("listenbrainz", 401, "{\"error\":\"Invalid token\"}")]
    [InlineData("multi-scrobbler", 200, "{\"valid\":false}")]
    [InlineData("koito", 200, "{\"valid\":false}")]
    public async Task RejectedManagedCredentialRequiresSignIn(string provider, int status, string payload)
    {
        using var handler = new AppleProviderTestFactory.Handler(request =>
        {
            if (provider == "qobuz")
                Assert.EndsWith("/favorite/getUserFavorites", request.RequestUri!.AbsolutePath);
            return Json((HttpStatusCode)status, payload);
        });
        var manager = CreateSignInProbeManager(provider, handler);
        var account = Guid.CreateVersion7();
        var secrets = SignInProbeSecrets(provider);

        var result = await manager.TestManagedProviderCapabilityAsync(
            provider, SignInProbeCapability(provider), account, secrets, accountRevision: 7);

        Assert.Equal(ProviderHealthState.Degraded, result.Health);
        Assert.Equal("account-needs-reauthentication", result.ReasonCode);
        Assert.False(result.IsReady);
        Assert.Equal(result.ReasonCode, manager.GetManagedStatus(
            provider, SignInProbeCapability(provider), account, secrets, 7).ReasonCode);
    }

    [Theory]
    [InlineData("playlist", 401, "{}")]
    [InlineData("lyrics", 200, "{\"accessToken\":\"fixture-access\",\"isAnonymous\":true}")]
    [InlineData("playlist", 200, "{\"accessToken\":\"fixture-access\",\"accessTokenExpirationTimestampMs\":1}")]
    public async Task RejectedSpotifySessionRequiresSignIn(string capability, int status, string payload)
    {
        using var handler = new QueuedResponseHandler(
            SpotifyTotpSecrets(), SpotifyTime(), Json((HttpStatusCode)status, payload));
        var manager = CreateSignInProbeManager("spotify", handler);

        var result = await manager.TestManagedProviderCapabilityAsync(
            "spotify", capability, Guid.CreateVersion7(), SignInProbeSecrets("spotify"));

        Assert.Equal(ProviderHealthState.Degraded, result.Health);
        Assert.Equal("account-needs-reauthentication", result.ReasonCode);
        Assert.False(result.IsReady);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("playlist", 401, "account-needs-reauthentication")]
    [InlineData("playlist", 403, "account-needs-reauthentication")]
    [InlineData("metadata", 401, "apple-web-token-unavailable")]
    [InlineData("metadata", 403, "apple-web-token-unavailable")]
    public async Task AppleRejectionRequiresSignInOnlyForPersonalLibrary(string capability, int status, string reason)
    {
        using var handler = new AppleProviderTestFactory.Handler(request =>
        {
            var personal = capability == "playlist";
            Assert.Equal(personal ? "/v1/me/library/playlists" : "/v1/catalog/us/search",
                request.RequestUri!.AbsolutePath);
            Assert.Equal(personal, request.Headers.Contains("Cookie"));
            return Json((HttpStatusCode)status, "{}");
        });
        var manager = CreateSignInProbeManager("apple-musickit", handler);

        var result = await manager.TestManagedProviderCapabilityAsync(
            "apple-musickit", capability, Guid.CreateVersion7(), SignInProbeSecrets("apple-musickit"));

        Assert.Equal(ProviderHealthState.Degraded, result.Health);
        Assert.Equal(reason, result.ReasonCode);
    }

    [Theory]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    [InlineData("spotify")]
    [InlineData("apple-musickit")]
    [InlineData("lastfm")]
    [InlineData("listenbrainz")]
    [InlineData("multi-scrobbler")]
    [InlineData("koito")]
    public async Task UnreachableCredentialProbeDoesNotRequireSignIn(string provider)
    {
        var requests = 0;
        using var handler = new AppleProviderTestFactory.Handler(_ =>
        {
            requests++;
            throw new HttpRequestException("Fixture connection failed.");
        });
        var manager = CreateSignInProbeManager(provider, handler);

        var result = await manager.TestManagedProviderCapabilityAsync(
            provider, SignInProbeCapability(provider), Guid.CreateVersion7(), SignInProbeSecrets(provider));

        Assert.True(requests > 0);
        Assert.Equal(ProviderHealthState.Degraded, result.Health);
        Assert.NotEqual("account-needs-reauthentication", result.ReasonCode);
        Assert.False(result.SignInExpired);
        Assert.Null(result.SignInCheckedAt);
    }

    [Theory]
    [InlineData("deezer", 503)]
    [InlineData("qobuz", 429)]
    [InlineData("lastfm", 503)]
    [InlineData("listenbrainz", 429)]
    [InlineData("apple-musickit", 503)]
    [InlineData("spotify", 429)]
    public async Task ServiceFailuresDoNotExpireManagedSignIn(string provider, int status)
    {
        using var handler = new AppleProviderTestFactory.Handler(_ => Json((HttpStatusCode)status, "{}"));
        var manager = CreateSignInProbeManager(provider, handler);
        var result = await manager.TestManagedProviderCapabilityAsync(
            provider, SignInProbeCapability(provider), Guid.NewGuid(), SignInProbeSecrets(provider));
        Assert.NotEqual("account-needs-reauthentication", result.ReasonCode);
        Assert.False(result.SignInExpired);
        Assert.Null(result.SignInCheckedAt);
    }

    [Fact]
    public async Task QobuzMetadataRejectsTheAccountToken()
    {
        using var handler = new AppleProviderTestFactory.Handler(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        var manager = CreateSignInProbeManager("qobuz", handler);
        var result = await manager.TestManagedProviderCapabilityAsync("qobuz", "metadata", Guid.NewGuid(), SignInProbeSecrets("qobuz"));
        Assert.Equal("account-needs-reauthentication", result.ReasonCode);
        Assert.True(result.SignInExpired);
    }

    private static ProviderStatusManager CreateSignInProbeManager(string provider, HttpMessageHandler handler)
    {
        var apple = provider == "apple-musickit" ? AppleProviderTestFactory.Client(new HttpClient(handler, false)) : null;
        return CreateManager(new Dictionary<string, string?>(),
            httpClientFactory: new HandlerHttpClientFactory(handler),
            spotifySettings: new SpotifyApiSettings { Enabled = true },
            services: apple == null ? null :
                Mock.Of<IServiceProvider>(services => services.GetService(typeof(AppleMusicClient)) == apple));
    }

    private static string SignInProbeCapability(string provider) => provider switch
    {
        "deezer" or "qobuz" => "streaming",
        "spotify" or "apple-musickit" => "playlist",
        _ => "scrobbling"
    };

    private static Dictionary<string, string> SignInProbeSecrets(string provider) => provider switch
    {
        "deezer" => new() { ["arl"] = "fixture-cookie" },
        "qobuz" => new() { ["userauthtoken"] = "fixture-token", ["userid"] = "fixture-user" },
        "spotify" => new() { ["sessioncookie"] = "fixture-cookie" },
        "apple-musickit" => new() { ["musicusertoken"] = "fixture-token" },
        "lastfm" => new()
        {
            ["apikey"] = "fixture-key",
            ["sharedsecret"] = "fixture-secret",
            ["sessionkey"] = "fixture-session"
        },
        _ => new() { ["token"] = "fixture-token", ["baseurl"] = "https://listen.example.test/1" }
    };
}
