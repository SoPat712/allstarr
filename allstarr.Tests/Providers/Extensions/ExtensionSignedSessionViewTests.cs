using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace allstarr.Tests;

public sealed class ExtensionSignedSessionViewTests : IDisposable
{
    private const string Manifest = """
        {"id":"signed-demo","displayName":"Signed demo","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
         "capabilities":[{"kind":"Metadata","hooks":["searchTracks"],"accountScopes":[],"accountRequired":false}],
         "permissions":[{"kind":"Network","value":"https://api.example.test/","required":true}],
         "requiredRuntimeFeatures":["signedSession@1","sessionGrant@1"],
         "signedSession":{"namespace":"demo-v1","baseUrl":"https://api.example.test/v1","appVersion":"demo@1.0.0"}}
        """;
    private const string Script = "registerExtension({ searchTracks: function() { return session.status(); } });";
    private const string Callback = "spotiflac://session-grant/?cb_version=v2grant&state=signed-demo&grant=grant-1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-session-view", Guid.NewGuid().ToString("N"));
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider().CreateProtector("test");

    [Fact]
    public void NewSandbox_IsSignedOutWithoutPrivateIdentifiers()
    {
        var handler = new SessionHandler();
        var view = Sandbox(handler).SignedSessionStatus();

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Null(view.VerificationUrl);
        Assert.Null(view.ReasonCode);
        Assert.Equal(0, handler.Calls);
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain("install", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session_id", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartThenGrant_SignsInAndNeverReturnsSessionMaterial()
    {
        var handler = new SessionHandler();
        var sandbox = Sandbox(handler);

        var pending = sandbox.StartSignedSessionVerification();
        Assert.Equal(ExtensionSessionStates.VerificationPending, pending.State);
        Assert.StartsWith("https://api.example.test/", pending.VerificationUrl, StringComparison.Ordinal);
        Assert.Contains("challenge-1", pending.VerificationUrl, StringComparison.Ordinal);
        Assert.Equal(ExtensionSessionStates.VerificationPending, sandbox.SignedSessionStatus().State);

        var signedIn = sandbox.CompleteSignedSessionGrant(Callback);
        Assert.Equal(ExtensionSessionStates.SignedIn, signedIn.State);
        Assert.Equal("2099-01-01T00:00:00Z", signedIn.ExpiresAt);
        Assert.Null(signedIn.VerificationUrl);
        Assert.Equal("grant-1", handler.ExchangeGrant);
        foreach (var view in new[] { pending, signedIn, sandbox.SignedSessionStatus() })
        {
            var json = JsonSerializer.Serialize(view);
            Assert.DoesNotContain("session-1", json, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-1", json, StringComparison.Ordinal);
            Assert.DoesNotContain(handler.InstallId!, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExpiredSession_IsReportedAsExpiredAndCanBeCleared()
    {
        var handler = new SessionHandler { ExpiresAt = "2020-01-01T00:00:00Z" };
        var sandbox = Sandbox(handler);
        sandbox.CompleteSignedSessionGrant("grant-1");

        var expired = sandbox.SignedSessionStatus();
        Assert.Equal(ExtensionSessionStates.Expired, expired.State);
        Assert.Equal("2020-01-01T00:00:00Z", expired.ExpiresAt);

        Assert.Equal(ExtensionSessionStates.SignedOut, sandbox.ClearSignedSession().State);
        Assert.Equal(ExtensionSessionStates.SignedOut, sandbox.SignedSessionStatus().State);
    }

    [Theory]
    [InlineData("spotiflac://session-grant/?state=another-extension&grant=grant-1", "grant_wrong_extension")]
    [InlineData("spotiflac://session-grant/?state=signed-demo", "grant_invalid")]
    [InlineData("   ", "grant_required")]
    public void InvalidCallbacks_AreRejectedWithoutContactingTheProvider(string grant, string reason)
    {
        var handler = new SessionHandler();

        var view = Sandbox(handler).CompleteSignedSessionGrant(grant);

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Equal(reason, view.ReasonCode);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "grant_rejected")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "provider_unavailable")]
    [InlineData(HttpStatusCode.TooManyRequests, "provider_unavailable")]
    public void ExchangeFailures_MapToSafeReasons(HttpStatusCode status, string reason)
    {
        var handler = new SessionHandler { ExchangeStatus = status };

        var view = Sandbox(handler).CompleteSignedSessionGrant("grant-1");

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Equal(reason, view.ReasonCode);
    }

    [Fact]
    public void ProviderOutage_IsReportedWithoutTransportDetails()
    {
        var handler = new SessionHandler { Throw = new HttpRequestException("connect failed to 10.1.2.3:443 token=abc") };

        var view = Sandbox(handler).StartSignedSessionVerification();

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Equal("provider_unavailable", view.ReasonCode);
        Assert.DoesNotContain("10.1.2.3", JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapRejection_AndMalformedResponses_AreDistinct()
    {
        Assert.Equal("session_rejected", Sandbox(new SessionHandler { BootstrapStatus = HttpStatusCode.Forbidden })
            .StartSignedSessionVerification().ReasonCode);
        Assert.Equal("unexpected_response", Sandbox(new SessionHandler { BootstrapBody = "not json" })
            .StartSignedSessionVerification().ReasonCode);
        Assert.Equal("unexpected_response", Sandbox(new SessionHandler { BootstrapBody = "{}" })
            .StartSignedSessionVerification().ReasonCode);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://api.example.test/verify")]
    [InlineData("https://api.example.test@other.example.test/verify")]
    public void UnsafeVerificationLinks_AreNeverOffered(string authUrl)
    {
        var handler = new SessionHandler { BootstrapBody = JsonSerializer.Serialize(new { auth_url = authUrl }) };
        var sandbox = Sandbox(handler);

        var view = sandbox.StartSignedSessionVerification();

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Null(view.VerificationUrl);
        Assert.Equal("unexpected_response", view.ReasonCode);
    }

    [Fact]
    public void DeniedNetworkOrigin_IsReportedAsPermissionProblem()
    {
        var handler = new SessionHandler();
        var sandbox = Sandbox(handler, origins: new HashSet<string>());

        var view = sandbox.StartSignedSessionVerification();

        Assert.Equal("origin_not_approved", view.ReasonCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellation_StopsTheProviderRequest()
    {
        var handler = new SessionHandler { WaitForCancellation = true };
        var sandbox = Sandbox(handler);
        using var cancellation = new CancellationTokenSource();

        var start = Task.Run(() => sandbox.StartSignedSessionVerification(cancellation.Token));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(ExtensionSessionStates.SignedOut, sandbox.SignedSessionStatus().State);
    }

    [Fact]
    public void ScriptStatus_KeepsItsExistingShapeForExtensions()
    {
        var sandbox = Sandbox(new SessionHandler());

        var status = sandbox.InvokeJson("searchTracks", "{}");

        Assert.Contains("\"Authenticated\":false", status, StringComparison.Ordinal);
        Assert.Contains("\"InstallId\":", status, StringComparison.Ordinal);
    }

    private ExtensionSandbox Sandbox(SessionHandler handler, IReadOnlySet<string>? origins = null)
    {
        var permissions = new ExtensionRuntimePermissionSet(
            origins ?? new HashSet<string>(["https://api.example.test/"]), new HashSet<string>(), new HashSet<string>());
        var runtime = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        return new ExtensionSandbox(runtime, Manifest, Script, new Factory(handler), NullLogger.Instance,
            permissions, Path.Combine(runtime, "state"), _protector);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SessionHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? ExchangeGrant { get; private set; }
        public string? InstallId { get; private set; }
        public string ExpiresAt { get; init; } = "2099-01-01T00:00:00Z";
        public HttpStatusCode BootstrapStatus { get; init; } = HttpStatusCode.OK;
        public string BootstrapBody { get; init; } = "{\"challenge_id\":\"challenge-1\"}";
        public HttpStatusCode ExchangeStatus { get; init; } = HttpStatusCode.OK;
        public Exception? Throw { get; init; }
        public bool WaitForCancellation { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Throw != null) throw Throw;
            HttpResponseMessage response;
            if (request.RequestUri!.AbsolutePath.EndsWith("/bootstrap", StringComparison.Ordinal))
            {
                InstallId = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["install_id"];
                response = Json(BootstrapStatus, BootstrapBody);
            }
            else
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
                ExchangeGrant = body.GetProperty("grant").GetString();
                InstallId = body.GetProperty("install_id").GetString();
                response = Json(ExchangeStatus, JsonSerializer.Serialize(new
                {
                    session_id = "session-1",
                    session_secret = "secret-1",
                    expires_at = ExpiresAt
                }));
            }
            response.RequestMessage = request;
            return response;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }
}
