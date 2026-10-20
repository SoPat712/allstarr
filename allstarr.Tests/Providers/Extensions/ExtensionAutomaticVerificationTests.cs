using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Extensions;
using Microsoft.AspNetCore.DataProtection;

namespace allstarr.Tests;

public sealed class ExtensionAutomaticVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-automatic-verification", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly Handler _handler = new();
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider().CreateProtector("fixture");

    [Fact]
    public async Task ExpiredSignInExchangesBrowserGrantAndKeepsProtectedStorage()
    {
        var client = await ExpiredClientAsync();
        var challenge = await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default);

        Assert.NotNull(challenge);
        Assert.StartsWith("https://api.zarz.moe/v2/session/challenge?", challenge.VerificationUrl, StringComparison.Ordinal);
        var view = await client.CompleteAutomaticVerificationAsync(challenge, "browser-grant", _ => Task.FromResult(true), default);

        Assert.Equal(ExtensionSessionStates.SignedIn, view.State);
        Assert.True(view.Restored);
        Assert.Equal("browser-grant", _handler.LastGrant);
        Assert.Equal(2, _handler.Exchanges);
        Assert.Null(view.VerificationUrl);
        Assert.DoesNotContain("session-secret", File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "*.protected"))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CappedRenewalStartsVerificationBeforeTheCurrentSignInExpires()
    {
        var client = await ExpiredClientAsync(expire: false);
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("renewal_capped", (await client.RefreshDueAsync(default))!.LastRenewalFailure);
        Assert.Equal(ExtensionSessionStates.SignedIn, (await client.ViewAsync(default)).State);

        var challenge = await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default);

        Assert.NotNull(challenge);
        Assert.True((await client.CompleteAutomaticVerificationAsync(challenge, "browser-grant", _ => Task.FromResult(true), default)).Restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignOutOrManualReconnectDiscardsTheBrowserResult(bool reconnect)
    {
        var client = await ExpiredClientAsync();
        var challenge = (await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default))!;
        if (reconnect) await client.StartVerificationViewAsync(default);
        else await client.ClearViewAsync(default);

        var view = await client.CompleteAutomaticVerificationAsync(challenge, "stale-grant", _ => Task.FromResult(true), default);

        Assert.NotEqual(ExtensionSessionStates.SignedIn, view.State);
        Assert.Equal(1, _handler.Exchanges);
        Assert.Equal(reconnect ? ExtensionSessionStates.VerificationPending : ExtensionSessionStates.SignedOut, view.State);
    }

    [Fact]
    public async Task AccountOrPackageChangeDuringExchangePreventsSavingTheSession()
    {
        var client = await ExpiredClientAsync();
        var before = File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.protected")));
        var challenge = (await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default))!;
        _handler.BeforeExchangeResponse = () => _handler.Authorized = false;

        var view = await client.CompleteAutomaticVerificationAsync(challenge, "browser-grant", _ => Task.FromResult(_handler.Authorized), default);

        Assert.NotEqual(ExtensionSessionStates.SignedIn, view.State);
        Assert.Equal(before, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.protected"))));
    }

    [Fact]
    public async Task RenewedSessionMustExtendExpiryBeforeItReplacesTheSavedSession()
    {
        var client = await ExpiredClientAsync();
        var before = File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.protected")));
        var challenge = (await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default))!;
        _handler.RenewedExpiry = _handler.OriginalExpiry;

        var view = await client.CompleteAutomaticVerificationAsync(challenge, "browser-grant", _ => Task.FromResult(true), default);

        Assert.False(view.Restored);
        Assert.Equal("unexpected_response", view.ReasonCode);
        Assert.Equal(before, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.protected"))));
    }

    [Fact]
    public async Task DirectBootstrapCannotSaveAfterTheConnectionChanges()
    {
        var client = await ExpiredClientAsync();
        var before = File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.protected")));
        _handler.BootstrapBody = "{\"session_id\":\"replacement\",\"session_secret\":\"replacement-secret\",\"expires_at\":\"2026-10-20T14:00:00Z\"}";
        _handler.BeforeBootstrapResponse = () => _handler.Authorized = false;

        Assert.Null(await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default, _ => Task.FromResult(_handler.Authorized)));

        Assert.Equal(before, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.protected"))));
    }

    [Fact]
    public async Task FailedBrowserAttemptRetainsTheExpiredSessionAndAllowsRetry()
    {
        var client = await ExpiredClientAsync();
        var before = File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.protected")));
        var challenge = (await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default))!;

        await client.ReleaseAutomaticVerificationAsync(challenge, default);

        Assert.Equal(ExtensionSessionStates.Expired, (await client.ViewAsync(default)).State);
        Assert.Equal(before, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.protected"))));
        Assert.NotNull(await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default));
    }

    [Fact]
    public async Task QueuedExpiryCannotRenewAReplacementOrSignedOutSession()
    {
        var client = await ExpiredClientAsync();
        Assert.Null(await client.PrepareAutomaticVerificationAsync("2020-01-01T00:00:00Z", default));
        await client.ClearViewAsync(default);
        Assert.Null(await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default));
        Assert.Equal(0, _handler.Bootstraps);
    }

    [Fact]
    public async Task PlaybackNoticingExpiryFirstStillAllowsAutomaticVerification()
    {
        var client = await ExpiredClientAsync();
        client.SignedFetch("GET", "/catalog", null, null);

        Assert.Equal(_handler.OriginalExpiry, await client.AutomaticVerificationExpiryAsync(default));
        var challenge = await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default);
        Assert.NotNull(challenge);
        Assert.Equal(1, _handler.Bootstraps);
        Assert.True((await client.CompleteAutomaticVerificationAsync(challenge, "browser-grant", _ => Task.FromResult(true), default)).Restored);
    }

    [Fact]
    public async Task ManualVerificationCannotBeTakenOverByBackgroundWork()
    {
        var client = await ExpiredClientAsync();
        await client.StartVerificationViewAsync(default);

        Assert.Null(await client.AutomaticVerificationExpiryAsync(default));
        Assert.Null(await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default));
        Assert.Equal(1, _handler.Bootstraps);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "SESSION_INVALID", "bootstrap_session")]
    [InlineData(HttpStatusCode.PreconditionRequired, "VERIFY_REQUIRED", "verify")]
    public async Task GatewayInvalidationRetainsRenewalIntentAcrossRestartButSignOutRemovesIt(
        HttpStatusCode status, string code, string action)
    {
        var client = await ExpiredClientAsync(expire: false);
        _handler.RejectCatalog = true;
        _handler.CatalogStatus = status;
        _handler.CatalogCode = code;
        _handler.CatalogAction = action;
        client.SignedFetch("GET", "/catalog", null, null);
        var restarted = Client();

        Assert.Equal(_handler.OriginalExpiry, await restarted.AutomaticVerificationExpiryAsync(default));
        Assert.NotNull(await restarted.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default));
        await restarted.ClearViewAsync(default);
        Assert.Null(await Client().AutomaticVerificationExpiryAsync(default));
    }

    [Fact]
    public async Task UnapprovedChallengeOriginNeverReachesTheBrowser()
    {
        var client = await ExpiredClientAsync();
        _handler.AuthUrl = "https://other.example.test/challenge";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default));

        Assert.Equal(ExtensionSessionStates.Expired, (await client.ViewAsync(default)).State);
    }

    private async Task<ExtensionSignedSessionClient> ExpiredClientAsync(bool expire = true)
    {
        var client = Client();
        await client.CompleteGrantViewAsync("initial-grant", default);
        if (expire) _clock.Advance(TimeSpan.FromHours(2));
        return client;
    }

    private ExtensionSignedSessionClient Client() => new(
            new("fixture", new Uri("https://api.zarz.moe/v2"), "fixture@1", "android", "spotiflac://session-grant/", "SPOTIFLAC-HMAC-V1", "X-Sig-", 300,
                new("/session/bootstrap", "/session/challenge", "/session/exchange", "/session/refresh")),
            new Factory(_handler), _protector, new HashSet<string>(["https://api.zarz.moe/"]), _root, "fixture", timeProvider: _clock);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string OriginalExpiry { get; } = "2026-10-19T13:00:00Z";
        public int Exchanges { get; private set; }
        public int Bootstraps { get; private set; }
        public string? LastGrant { get; private set; }
        public string? AuthUrl { get; set; }
        public string? BootstrapBody { get; set; }
        public string RenewedExpiry { get; set; } = "2026-10-20T14:00:00Z";
        public bool Authorized { get; set; } = true;
        public Action? BeforeExchangeResponse { get; set; }
        public Action? BeforeBootstrapResponse { get; set; }
        public bool RejectCatalog { get; set; }
        public HttpStatusCode CatalogStatus { get; set; } = HttpStatusCode.Unauthorized;
        public string CatalogCode { get; set; } = "SESSION_INVALID";
        public string CatalogAction { get; set; } = "bootstrap_session";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/bootstrap", StringComparison.Ordinal))
            {
                Bootstraps++;
                body = BootstrapBody ?? (AuthUrl == null ? "{\"challenge_id\":\"fixture-challenge\"}" : JsonSerializer.Serialize(new { auth_url = AuthUrl }));
                BeforeBootstrapResponse?.Invoke();
            }
            else if (RejectCatalog && request.RequestUri.AbsolutePath.EndsWith("/catalog", StringComparison.Ordinal))
                return new(CatalogStatus)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { origin = "gateway", code = CatalogCode, action = CatalogAction }), Encoding.UTF8, "application/json"),
                    RequestMessage = request
                };
            else if (request.RequestUri.AbsolutePath.EndsWith("/refresh", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { expires_at = OriginalExpiry });
            else
            {
                Exchanges++;
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                LastGrant = payload.RootElement.GetProperty("grant").GetString();
                BeforeExchangeResponse?.Invoke();
                body = JsonSerializer.Serialize(new
                {
                    session_id = "fixture-session",
                    session_secret = "session-secret",
                    expires_at = Exchanges == 1 ? OriginalExpiry : RenewedExpiry
                });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }
}
