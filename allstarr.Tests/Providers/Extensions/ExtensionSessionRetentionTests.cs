using System.Net;
using System.Text.Json;
using allstarr.Core.Extensions;
using Microsoft.AspNetCore.DataProtection;

namespace allstarr.Tests;

public sealed class ExtensionSessionRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "session-retention", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 17, 8, 10, 0, TimeSpan.Zero));
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider().CreateProtector("test");
    private readonly List<(string Level, string Code, string Message)> _logs = [];
    private readonly Handler _handler = new();

    [Theory]
    [InlineData(401, "<html>proxy rejected</html>", "verification_required")]
    [InlineData(428, "{}", "verification_required")]
    [InlineData(503, "{}", "provider_unavailable")]
    [InlineData(502, "{\"origin\":\"gateway\",\"code\":\"SESSION_INVALID\",\"action\":\"bootstrap_session\"}", "provider_unavailable")]
    [InlineData(401, "{\"origin\":\"provider\",\"code\":\"SESSION_INVALID\",\"action\":\"bootstrap_session\"}", "verification_required")]
    public async Task SignedFetch_AmbiguousFailureRetainsStoredSessionAndRecordsSafeReason(int status, string body, string reason)
    {
        var client = await SignedInAsync();
        var before = File.ReadAllBytes(StatePath());
        _handler.Status = (HttpStatusCode)status;
        _handler.Body = body;

        client.SignedFetch("GET", "/catalog?private=value", null, null);

        Assert.Equal(before, File.ReadAllBytes(StatePath()));
        var view = await client.ViewAsync(default);
        Assert.Equal(ExtensionSessionStates.SignedIn, view.State);
        Assert.Equal(reason, view.ReasonCode);
        Assert.Equal(reason, view.LastRenewalFailure);
        var log = Assert.Single(_logs);
        Assert.Equal("session.retained", log.Code);
        Assert.Contains($"status={status}", log.Message);
        Assert.Contains("call=signed_fetch", log.Message);
        Assert.Contains("time_left=true", log.Message);
        AssertSafeLogs();
        _handler.Status = HttpStatusCode.OK;
        _handler.Body = "{}";
        client.SignedFetch("GET", "/catalog", null, null);
        Assert.Null((await client.ViewAsync(default)).ReasonCode);
    }

    [Theory]
    [InlineData(401, "SESSION_INVALID", "bootstrap_session", false)]
    [InlineData(428, "VERIFY_REQUIRED", "verify", false)]
    [InlineData(401, "SESSION_INVALID", "bootstrap_session", true)]
    public async Task ExplicitGatewayRejectionClearsSessionAndRecordsContract(int status, string code, string action, bool refresh)
    {
        var client = await SignedInAsync(refresh);
        _handler.Status = (HttpStatusCode)status;
        _handler.Body = JsonSerializer.Serialize(new { origin = "gateway", code, action });

        if (refresh) await client.RefreshDueAsync(default);
        else client.SignedFetch("GET", "/catalog", null, null);

        var view = await client.ViewAsync(default);
        Assert.NotEqual(ExtensionSessionStates.SignedIn, view.State);
        var log = Assert.Single(_logs, item => item.Code == "session.reset");
        Assert.Contains($"status={status}", log.Message);
        Assert.Contains("origin=gateway", log.Message);
        Assert.Contains($"code={code}", log.Message);
        Assert.Contains($"action={action}", log.Message);
        Assert.Contains(refresh ? "call=refresh" : "call=signed_fetch", log.Message);
        Assert.Contains("time_left=true", log.Message);
        AssertSafeLogs();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TransportFailureRetainsSessionAndRetries(bool refresh, bool timeout)
    {
        var client = await SignedInAsync(refresh);
        var before = File.ReadAllBytes(StatePath());
        _handler.Failure = timeout ? new OperationCanceledException() : new HttpRequestException(HttpRequestError.NameResolutionError, "private failure");

        if (refresh) await client.RefreshDueAsync(default);
        else if (timeout) Assert.ThrowsAny<OperationCanceledException>(() => client.SignedFetch("GET", "/catalog", null, null));
        else client.SignedFetch("GET", "/catalog", null, null);

        Assert.Equal(before, File.ReadAllBytes(StatePath()));
        Assert.Equal(timeout ? "provider_unavailable" : "network_not_ready", (await client.ViewAsync(default)).LastRenewalFailure);
        Assert.Contains(_logs, item => item.Code == "session.retained");
        AssertSafeLogs();
        _handler.Failure = null;
        if (refresh) Assert.Null((await client.RefreshDueAsync(default))!.LastRenewalFailure);
        else client.SignedFetch("GET", "/catalog", null, null);
        Assert.Null((await client.ViewAsync(default)).LastRenewalFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableStoredSessionFailsWithoutOverwritingFile(bool validProtection)
    {
        await SignedInAsync();
        var contents = validProtection ? _protector.Protect("null") : "unreadable protected file";
        File.WriteAllText(StatePath(), contents);
        var client = Client();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ViewAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteGrantViewAsync("fixture-grant", default));
        Assert.Equal(contents, File.ReadAllText(StatePath()));
        Assert.Contains(_logs, item => item.Code == "session.load-failed");
        AssertSafeLogs();
    }

    [Fact]
    public async Task ExpiredRestoreRetriesAfterNetworkFailure()
    {
        var client = await SignedInAsync(refresh: true);
        _clock.Advance(TimeSpan.FromHours(1));
        var before = File.ReadAllBytes(StatePath());
        _handler.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "private failure");

        var failed = await client.RefreshDueAsync(default);

        Assert.Equal("network_not_ready", failed!.LastRenewalFailure);
        Assert.Equal(before, File.ReadAllBytes(StatePath()));
        _handler.Failure = null;
        _handler.BootstrapBody = SessionBody(_clock.GetUtcNow().AddHours(6));
        Assert.True((await client.RefreshDueAsync(default))!.Restored);
        Assert.Contains(_logs, item => item.Code == "session.retained" && item.Message.Contains("call=restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingProtectionKeysPreserveTheStoredSession()
    {
        var original = await SignedInAsync();
        var before = File.ReadAllBytes(StatePath());
        var unreadable = Client(protector: new EphemeralDataProtectionProvider().CreateProtector("test"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => unreadable.ClearViewAsync(default));

        Assert.Equal(before, File.ReadAllBytes(StatePath()));
        Assert.Equal(ExtensionSessionStates.SignedIn, (await original.ViewAsync(default)).State);
        Assert.Contains(_logs, item => item.Code == "session.load-failed" && item.Message.Contains("reason=protection_failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailableProtectionKeyFileIsLoggedWithoutChangingSession()
    {
        await SignedInAsync();
        var before = File.ReadAllBytes(StatePath());
        var client = Client(protector: new UnavailableProtector());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ViewAsync(default));

        Assert.Equal(before, File.ReadAllBytes(StatePath()));
        Assert.Contains(_logs, item => item.Code == "session.load-failed");
        AssertSafeLogs();
    }

    [Theory]
    [InlineData(401)]
    [InlineData(428)]
    [InlineData(503)]
    public async Task RestoreRetriesAmbiguousHttpFailuresWithoutClearing(int status)
    {
        var client = await SignedInAsync(refresh: true);
        _clock.Advance(TimeSpan.FromHours(1));
        var before = File.ReadAllBytes(StatePath());
        _handler.BootstrapStatus = (HttpStatusCode)status;
        _handler.BootstrapBody = "{}";

        await client.RefreshDueAsync(default);
        Assert.Equal(before, File.ReadAllBytes(StatePath()));
        Assert.Contains(_logs, item => item.Code == "session.retained" && item.Message.Contains("call=restore", StringComparison.Ordinal));
        _handler.BootstrapStatus = HttpStatusCode.OK;
        _handler.BootstrapBody = SessionBody(_clock.GetUtcNow().AddHours(6));
        Assert.True((await client.RefreshDueAsync(default))!.Restored);
    }

    [Fact]
    public async Task RenewalTimeoutUsesInjectedClockAndRetainsSession()
    {
        var client = await SignedInAsync(refresh: true);
        var before = File.ReadAllBytes(StatePath());
        _handler.WaitForCancellation = true;
        var renewal = client.RefreshDueAsync(default);
        await _handler.Waiting.Task;
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal("provider_unavailable", (await renewal)!.LastRenewalFailure);
        Assert.Equal(before, File.ReadAllBytes(StatePath()));
        Assert.Contains(_logs, item => item.Code == "session.retained");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(428)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task RefreshHttpFailureKeepsSessionAndLogs(int status)
    {
        var client = await SignedInAsync(refresh: true);
        var before = File.ReadAllBytes(StatePath());
        _handler.Status = (HttpStatusCode)status;
        var view = await client.RefreshDueAsync(default);
        Assert.Equal(ExtensionSessionStates.SignedIn, view!.State);
        Assert.Equal(before, File.ReadAllBytes(StatePath()));
        Assert.Contains(_logs, item => item.Code == "session.retained" && item.Message.Contains("call=refresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserClearRecordsResetBeforeRemovingCredentials()
    {
        var client = await SignedInAsync();
        await client.ClearViewAsync(default);
        Assert.Equal(ExtensionSessionStates.SignedOut, (await client.ViewAsync(default)).State);
        Assert.Contains(_logs, item => item.Code == "session.reset" && item.Message.Contains("call=user_clear", StringComparison.Ordinal));
    }

    private async Task<ExtensionSignedSessionClient> SignedInAsync(bool refresh = false)
    {
        _handler.ExchangeBody = SessionBody(_clock.GetUtcNow().AddMinutes(refresh ? 30 : 180));
        var client = Client(refresh);
        Assert.Equal(ExtensionSessionStates.SignedIn, (await client.CompleteGrantViewAsync("fixture-grant", default)).State);
        return client;
    }

    private ExtensionSignedSessionClient Client(bool refresh = false, IDataProtector? protector = null) => new(
        new("fixture", new Uri("https://api.example.test/"), "1.0", "test", "fixture://callback", "sig", "X-Sig-", 30,
            new("/bootstrap", "/challenge", "/exchange", refresh ? "/refresh" : null)),
        new Factory(_handler), protector ?? _protector, new HashSet<string> { "https://api.example.test/" }, _root, "signed-demo",
        timeProvider: _clock, logSink: (level, code, message) => _logs.Add((level, code, message)));

    private string StatePath() => Assert.Single(Directory.GetFiles(_root, "*.protected"));
    private static string SessionBody(DateTimeOffset expiry) => JsonSerializer.Serialize(new
    {
        session_id = "private-session",
        session_secret = "private-secret",
        expires_at = expiry.ToString("O")
    });

    private void AssertSafeLogs()
    {
        var text = JsonSerializer.Serialize(_logs.Select(item => item.Message));
        foreach (var value in new[] { "private-session", "private-secret", "private failure", "fixture-grant", "<html>", "catalog?" })
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class UnavailableProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => throw new InvalidOperationException();
        public byte[] Unprotect(byte[] protectedData) => throw new FileNotFoundException("private failure");
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode BootstrapStatus { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        public string ExchangeBody { get; set; } = "{}";
        public string BootstrapBody { get; set; } = "{\"challenge_id\":\"fixture\"}";
        public Exception? Failure { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (WaitForCancellation)
            {
                Waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (Failure != null) throw Failure;
            var path = request.RequestUri!.AbsolutePath;
            var body = path == "/exchange" ? ExchangeBody : path == "/bootstrap" ? BootstrapBody : Body;
            if (path == "/refresh" && Status == HttpStatusCode.OK && Body == "{}")
                body = SessionBody(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));
            return new HttpResponseMessage(path == "/exchange" ? HttpStatusCode.OK : path == "/bootstrap" ? BootstrapStatus : Status)
            {
                RequestMessage = request,
                Content = new StringContent(body)
            };
        }
    }
}
