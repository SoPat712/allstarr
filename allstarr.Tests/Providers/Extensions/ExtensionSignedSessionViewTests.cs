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
    public async Task NewSandbox_IsSignedOutWithoutPrivateIdentifiers()
    {
        var handler = new SessionHandler();
        var view = await Sandbox(handler).SignedSessionStatusAsync();

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Null(view.VerificationUrl);
        Assert.Null(view.ReasonCode);
        Assert.Equal(0, handler.Calls);
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain("install", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session_id", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartThenGrant_SignsInAndNeverReturnsSessionMaterial()
    {
        var handler = new SessionHandler();
        var sandbox = Sandbox(handler);

        var pending = await sandbox.StartSignedSessionVerificationAsync();
        Assert.Equal(ExtensionSessionStates.VerificationPending, pending.State);
        Assert.StartsWith("https://api.example.test/", pending.VerificationUrl, StringComparison.Ordinal);
        Assert.Contains("challenge-1", pending.VerificationUrl, StringComparison.Ordinal);
        Assert.Equal(ExtensionSessionStates.VerificationPending, (await sandbox.SignedSessionStatusAsync()).State);

        var signedIn = await sandbox.CompleteSignedSessionGrantAsync(Callback);
        Assert.Equal(ExtensionSessionStates.SignedIn, signedIn.State);
        Assert.Equal("2099-01-01T00:00:00Z", signedIn.ExpiresAt);
        Assert.Null(signedIn.VerificationUrl);
        Assert.Equal("grant-1", handler.ExchangeGrant);
        foreach (var view in new[] { pending, signedIn, await sandbox.SignedSessionStatusAsync() })
        {
            var json = JsonSerializer.Serialize(view);
            Assert.DoesNotContain("session-1", json, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-1", json, StringComparison.Ordinal);
            Assert.DoesNotContain(handler.InstallId!, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExpiredSession_IsReportedAsExpiredAndCanBeCleared()
    {
        var handler = new SessionHandler { ExpiresAt = "2020-01-01T00:00:00Z" };
        var sandbox = Sandbox(handler);
        await sandbox.CompleteSignedSessionGrantAsync("grant-1");

        var expired = await sandbox.SignedSessionStatusAsync();
        Assert.Equal(ExtensionSessionStates.Expired, expired.State);
        Assert.Equal("2020-01-01T00:00:00Z", expired.ExpiresAt);

        Assert.Equal(ExtensionSessionStates.SignedOut, (await sandbox.ClearSignedSessionAsync()).State);
        Assert.Equal(ExtensionSessionStates.SignedOut, (await sandbox.SignedSessionStatusAsync()).State);
    }

    [Theory]
    [InlineData("spotiflac://session-grant/?state=another-extension&grant=grant-1", "grant_wrong_extension")]
    [InlineData("spotiflac://session-grant/?state=signed-demo", "grant_invalid")]
    [InlineData("   ", "grant_required")]
    public async Task InvalidCallbacks_AreRejectedWithoutContactingTheProvider(string grant, string reason)
    {
        var handler = new SessionHandler();

        var view = await Sandbox(handler).CompleteSignedSessionGrantAsync(grant);

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Equal(reason, view.ReasonCode);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "grant_rejected")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "provider_unavailable")]
    [InlineData(HttpStatusCode.TooManyRequests, "provider_unavailable")]
    public async Task ExchangeFailures_MapToSafeReasons(HttpStatusCode status, string reason)
    {
        var handler = new SessionHandler { ExchangeStatus = status };

        var view = await Sandbox(handler).CompleteSignedSessionGrantAsync("grant-1");

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Equal(reason, view.ReasonCode);
    }

    [Fact]
    public async Task ProviderOutage_IsReportedWithoutTransportDetails()
    {
        var handler = new SessionHandler { Throw = new HttpRequestException("connect failed to 10.1.2.3:443 token=abc") };

        var view = await Sandbox(handler).StartSignedSessionVerificationAsync();

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Equal("provider_unavailable", view.ReasonCode);
        Assert.DoesNotContain("10.1.2.3", JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapRejection_AndMalformedResponses_AreDistinct()
    {
        Assert.Equal("session_rejected", (await Sandbox(new SessionHandler { BootstrapStatus = HttpStatusCode.Forbidden })
            .StartSignedSessionVerificationAsync()).ReasonCode);
        Assert.Equal("unexpected_response", (await Sandbox(new SessionHandler { BootstrapBody = "not json" })
            .StartSignedSessionVerificationAsync()).ReasonCode);
        Assert.Equal("unexpected_response", (await Sandbox(new SessionHandler { BootstrapBody = "{}" })
            .StartSignedSessionVerificationAsync()).ReasonCode);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://api.example.test/verify")]
    [InlineData("https://api.example.test@other.example.test/verify")]
    public async Task UnsafeVerificationLinks_AreNeverOffered(string authUrl)
    {
        var handler = new SessionHandler { BootstrapBody = JsonSerializer.Serialize(new { auth_url = authUrl }) };
        var sandbox = Sandbox(handler);

        var view = await sandbox.StartSignedSessionVerificationAsync();

        Assert.Equal(ExtensionSessionStates.SignedOut, view.State);
        Assert.Null(view.VerificationUrl);
        Assert.Equal("unexpected_response", view.ReasonCode);
    }

    [Fact]
    public async Task DeniedNetworkOrigin_IsReportedAsPermissionProblem()
    {
        var handler = new SessionHandler();
        var sandbox = Sandbox(handler, origins: new HashSet<string>());

        var view = await sandbox.StartSignedSessionVerificationAsync();

        Assert.Equal("origin_not_approved", view.ReasonCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellation_StopsTheProviderRequest()
    {
        var handler = new SessionHandler { WaitForCancellation = true };
        var sandbox = Sandbox(handler);
        using var cancellation = new CancellationTokenSource();

        var start = sandbox.StartSignedSessionVerificationAsync(cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(ExtensionSessionStates.SignedOut, (await sandbox.SignedSessionStatusAsync()).State);
    }

    [Fact]
    public async Task ConcurrentSessionOperations_NeverOverlapProviderRequests()
    {
        var handler = new SessionHandler { Delay = TimeSpan.FromMilliseconds(50) };
        var sandbox = Sandbox(handler);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => sandbox.StartSignedSessionVerificationAsync())));

        Assert.Equal(4, handler.Calls);
        Assert.Equal(1, handler.MaximumConcurrency);
    }

    [Fact]
    public async Task ExistingSignIn_MovesIntoTheSourceSessionDirectory()
    {
        var handler = new SessionHandler();
        var runtime = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var legacy = Sandbox(handler, runtime: runtime);
        Assert.Equal(ExtensionSessionStates.SignedIn, (await legacy.CompleteSignedSessionGrantAsync("grant-1")).State);
        var sessions = Path.Combine(runtime, "state", "sessions", "registry");

        var current = Sandbox(handler, runtime: runtime, sessionState: sessions);

        Assert.Equal(ExtensionSessionStates.SignedIn, (await current.SignedSessionStatusAsync()).State);
        Assert.Empty(Directory.GetFiles(Path.Combine(runtime, "state"), "signed-session-*.protected"));
        Assert.Single(Directory.GetFiles(sessions, "signed-session-*.protected"));
        Assert.Equal(ExtensionSessionStates.SignedOut, (await Sandbox(handler, runtime: runtime,
            sessionState: Path.Combine(runtime, "state", "sessions", "other")).SignedSessionStatusAsync()).State);
    }

    [Fact]
    public void ScriptStatus_KeepsItsExistingShapeForExtensions()
    {
        var sandbox = Sandbox(new SessionHandler());

        var status = sandbox.InvokeJson("searchTracks", "{}");

        Assert.Contains("\"Authenticated\":false", status, StringComparison.Ordinal);
        Assert.Contains("\"InstallId\":", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundRenewal_RenewsOnlyWhenDueAndPublishesSafeStatus()
    {
        var handler = new SessionHandler { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O") };
        var sandbox = Sandbox(handler, refresh: true);
        await sandbox.CompleteSignedSessionGrantAsync("grant-1");

        var renewed = await sandbox.RefreshSignedSessionIfDueAsync();

        Assert.NotNull(renewed);
        Assert.True(renewed.RenewsAutomatically);
        Assert.Equal(handler.RenewedExpiresAt, renewed.ExpiresAt);
        Assert.Null(renewed.LastRenewalFailure);
        Assert.Null(await sandbox.RefreshSignedSessionIfDueAsync());
        Assert.Equal(1, handler.RefreshCalls);
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain("secret-1", JsonSerializer.Serialize(renewed), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(61, true, true)]
    [InlineData(-1, true, true)]
    [InlineData(30, false, true)]
    [InlineData(30, true, false)]
    public async Task BackgroundRenewal_SkipsWithoutStartingVerification(int minutes, bool signedIn, bool refresh)
    {
        var handler = new SessionHandler { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes).ToString("O") };
        var sandbox = Sandbox(handler, refresh: refresh);
        if (signedIn) await sandbox.CompleteSignedSessionGrantAsync("grant-1");
        var calls = handler.Calls;

        Assert.Null(await sandbox.RefreshSignedSessionIfDueAsync());
        Assert.Equal(calls, handler.Calls);
        Assert.Equal(0, handler.RefreshCalls);
    }

    [Theory]
    [InlineData(401, null)]
    [InlineData(429, null)]
    [InlineData(503, null)]
    [InlineData(200, "not json")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"expires_at\":\"2020-01-01T00:00:00Z\"}")]
    public async Task BackgroundRenewal_FailureKeepsSessionAndRetriesNextTick(int status, string? body)
    {
        var handler = new SessionHandler
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O"),
            RefreshStatus = (HttpStatusCode)status,
            RefreshBody = body
        };
        var runtime = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var sandbox = Sandbox(handler, runtime: runtime, refresh: true);
        await sandbox.CompleteSignedSessionGrantAsync("grant-1");
        var statePath = Assert.Single(Directory.GetFiles(Path.Combine(runtime, "state"), "*.protected"));
        var before = await File.ReadAllBytesAsync(statePath);

        var failed = await sandbox.RefreshSignedSessionIfDueAsync();

        Assert.NotNull(failed);
        Assert.Equal(ExtensionSessionStates.SignedIn, failed.State);
        Assert.Equal(handler.ExpiresAt, failed.ExpiresAt);
        Assert.NotNull(failed.LastRenewalFailure);
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath));
        Assert.Equal(failed.LastRenewalFailure, (await sandbox.SignedSessionStatusAsync()).LastRenewalFailure);
        Assert.Equal(2, handler.Calls);
        handler.RefreshStatus = HttpStatusCode.OK;
        handler.RefreshBody = null;
        Assert.Null((await sandbox.RefreshSignedSessionIfDueAsync())!.LastRenewalFailure);
        Assert.Equal(2, handler.RefreshCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundRenewal_AndSignedFetchShareOneGate(bool fetchFirst)
    {
        var handler = new SessionHandler
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O"),
            HoldRefresh = true
        };
        var sandbox = Sandbox(handler, refresh: true,
            script: "registerExtension({ searchTracks: function() { return session.signedFetch('GET', '/catalog'); } });");
        await sandbox.CompleteSignedSessionGrantAsync("grant-1");
        Task Fetch() => Task.Run(() => sandbox.InvokeJson("searchTracks", "{}"));
        Task Renew() => sandbox.RefreshSignedSessionIfDueAsync();
        var first = fetchFirst ? Fetch() : Renew();
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = fetchFirst ? Renew() : Fetch();
        try { Assert.False(second.IsCompleted); }
        finally { handler.ReleaseRefresh.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, handler.MaximumConcurrency);
        Assert.Equal(1, handler.RefreshCalls);
        Assert.Equal(3, handler.Calls); // Exchange, one renewal, and the signed catalog request.
        Assert.Equal(handler.RenewedExpiresAt, (await sandbox.SignedSessionStatusAsync()).ExpiresAt);
    }

    private ExtensionSandbox Sandbox(SessionHandler handler, IReadOnlySet<string>? origins = null,
        string? runtime = null, string? sessionState = null, bool refresh = false, string? script = null)
    {
        var permissions = new ExtensionRuntimePermissionSet(
            origins ?? new HashSet<string>(["https://api.example.test/"]), new HashSet<string>(), new HashSet<string>());
        runtime ??= Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var manifest = refresh ? Manifest.Replace("\"appVersion\":\"demo@1.0.0\"", "\"appVersion\":\"demo@1.0.0\",\"endpoints\":{\"refresh\":\"/refresh\"}", StringComparison.Ordinal) : Manifest;
        return new ExtensionSandbox(runtime, manifest, script ?? Script, new Factory(handler), NullLogger.Instance,
            permissions, Path.Combine(runtime, "state"), _protector, sessionState);
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
        private int _active;
        private int _calls;
        private int _maximumConcurrency;
        public int RefreshCalls { get; private set; }
        public HttpStatusCode RefreshStatus { get; set; } = HttpStatusCode.OK;
        public string? RefreshBody { get; set; }
        public string RenewedExpiresAt { get; } = DateTimeOffset.UtcNow.AddHours(6).ToString("O");
        public bool HoldRefresh { get; init; }
        public TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public string? ExchangeGrant { get; private set; }
        public string? InstallId { get; private set; }
        public string ExpiresAt { get; init; } = "2099-01-01T00:00:00Z";
        public HttpStatusCode BootstrapStatus { get; init; } = HttpStatusCode.OK;
        public string BootstrapBody { get; init; } = "{\"challenge_id\":\"challenge-1\"}";
        public HttpStatusCode ExchangeStatus { get; init; } = HttpStatusCode.OK;
        public Exception? Throw { get; init; }
        public bool WaitForCancellation { get; init; }
        public TimeSpan Delay { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int observed;
            while (active > (observed = Volatile.Read(ref _maximumConcurrency)) &&
                   Interlocked.CompareExchange(ref _maximumConcurrency, active, observed) != observed) { }
            try
            {
                Entered.TrySetResult();
                if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
                if (Throw != null) throw Throw;
                HttpResponseMessage response;
                if (request.RequestUri!.AbsolutePath.EndsWith("/bootstrap", StringComparison.Ordinal))
                {
                    InstallId = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["install_id"];
                    response = Json(BootstrapStatus, BootstrapBody);
                }
                else if (request.RequestUri.AbsolutePath == "/v1/refresh")
                {
                    RefreshCalls++;
                    Assert.False(request.Content == null);
                    RefreshEntered.TrySetResult();
                    if (HoldRefresh) await ReleaseRefresh.Task.WaitAsync(cancellationToken);
                    response = Json(RefreshStatus, RefreshBody ?? JsonSerializer.Serialize(new { expires_at = RenewedExpiresAt }));
                }
                else if (request.RequestUri.AbsolutePath == "/v1/catalog")
                {
                    Assert.Contains(request.Headers, header => header.Key.EndsWith("Session", StringComparison.OrdinalIgnoreCase));
                    response = Json(HttpStatusCode.OK, "{}");
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
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }
}
