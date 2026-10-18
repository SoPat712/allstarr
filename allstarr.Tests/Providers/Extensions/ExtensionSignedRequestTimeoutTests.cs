using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ExtensionSignedRequestTimeoutTests : IDisposable
{
    private const string Manifest = """
        {"id":"signed-timeout","displayName":"Signed timeout","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
         "capabilities":[{"kind":"Download","hooks":["download"],"accountScopes":[],"accountRequired":false}],
         "permissions":[{"kind":"Network","value":"https://api.example.test/","required":true}],
         "requiredRuntimeFeatures":["signedSession@1","sessionGrant@1"],
         "signedSession":{"namespace":"timeout-v1","baseUrl":"https://api.example.test/v1","appVersion":"1.0.0"}}
        """;
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-signed-timeout", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider clock = new();
    private readonly ControlledHandler handler = new();
    private readonly IDataProtector protector = new EphemeralDataProtectionProvider().CreateProtector("test");
    private static ExtensionRuntimePermissionSet Permissions => new(
        new HashSet<string>(["https://api.example.test/"]), new HashSet<string>(), new HashSet<string>());

    [Fact]
    public async Task SignedDownloadRequest_CanPrepareForMoreThanThirtySeconds()
    {
        var sandbox = await SandboxAsync();
        var pending = Task.Run(() => sandbox.InvokeJson("download", "{}"));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Timeout.InfiniteTimeSpan, handler.Client!.Timeout);
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.False(handler.Canceled.Task.IsCompleted);
        handler.Release.TrySetResult();
        using var response = JsonDocument.Parse((await pending.WaitAsync(TimeSpan.FromSeconds(10)))!);

        Assert.Equal(200, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(1, handler.MaximumConcurrency);
    }

    [Theory]
    [InlineData(120, 300, 120)]
    [InlineData(45, 300, 45)]
    [InlineData(120, 40, 40)]
    public async Task SignedDownloadRequest_StopsAtConfiguredLimitOrRemainingBudget(
        int requestSeconds, int budgetSeconds, int expectedSeconds)
    {
        var sandbox = await SandboxAsync(requestSeconds, budgetSeconds);
        var pending = Task.Run(() => sandbox.InvokeJson("download", "{}"));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(expectedSeconds - 1));
        Assert.False(handler.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (expectedSeconds == budgetSeconds)
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("Extension hook exceeded its time budget.", error.Message);
        }
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedRequest_OutsideDownloadStillStopsAtThirtySeconds(bool insideHook)
    {
        Task pending;
        if (insideHook)
        {
            var sandbox = await SandboxAsync();
            pending = Task.Run(() => sandbox.InvokeJson("searchTracks", "{}"));
        }
        else
        {
            var bridge = Bridge();
            bridge.SessionCompleteGrant("fixture-grant");
            pending = Task.Run(() => bridge.SessionSignedFetch("POST", "/prepare", "{}", null));
        }
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(handler.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task SignedRequest_WaitingForSessionReturnsBusyWithoutOverlappingRequests()
    {
        handler.IgnoreCancellation = true;
        var bridge = Bridge();
        bridge.SessionCompleteGrant("fixture-grant");
        var first = Task.Run(() => bridge.SessionSignedFetch("POST", "/prepare", "{}", null));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(30));
        var second = Task.Run(() => bridge.SessionSignedFetch("POST", "/prepare", "{}", null));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(35));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(35));
            using var response = JsonDocument.Parse(JsonSerializer.Serialize(await second.WaitAsync(TimeSpan.FromSeconds(10))));
            Assert.Equal("SESSION_BUSY", response.RootElement.GetProperty("error").GetString());
            Assert.Equal(1, handler.PreparationCalls);
            Assert.Equal(1, handler.MaximumConcurrency);
        }
        finally
        {
            handler.Release.TrySetResult();
            try { await first.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SignedDownloadRequest_KeepsTheAdministratorGateWaitBounded()
    {
        var sandbox = await SandboxAsync();
        var first = Task.Run(() => sandbox.InvokeJson("download", "{}"));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await clock.WaitForTimersAsync(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));
        var waiting = sandbox.StartSignedSessionVerificationAsync();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(35));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(35));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("The extension session is busy.", error.Message);
            Assert.False(handler.Canceled.Task.IsCompleted);
            Assert.Equal(1, handler.MaximumConcurrency);
        }
        finally { handler.Release.TrySetResult(); }
        Assert.NotNull(await first.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task SignedDownloadRequest_StopsWhenTheCallerCancels()
    {
        var sandbox = await SandboxAsync();
        using var caller = new CancellationTokenSource();
        var pending = Task.Run(() => sandbox.InvokeJson("download", "{}", caller.Token));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(handler.Canceled.Task.IsCompleted);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(301)]
    public void SignedDownloadRequestConfiguration_RejectsOutOfBoundsValues(int seconds)
    {
        var options = new ExtensionExecutionOptions { SignedDownloadRequestSeconds = seconds };
        Assert.Throws<InvalidOperationException>(() => options.GetSignedDownloadRequestTimeout());
    }

    [Theory]
    [InlineData(30)]
    [InlineData(300)]
    public void SignedDownloadRequestConfiguration_AcceptsBounds(int seconds)
    {
        var options = new ExtensionExecutionOptions { SignedDownloadRequestSeconds = seconds };
        Assert.Equal(TimeSpan.FromSeconds(seconds), options.GetSignedDownloadRequestTimeout());
    }

    private async Task<ExtensionSandbox> SandboxAsync(int requestSeconds = 120, int budgetSeconds = 300)
    {
        var sandbox = new ExtensionSandbox(root, Manifest,
            "function work(){return session.signedFetch('POST','/prepare',{});} registerExtension({download:work,searchTracks:work});",
            new Factory(handler), NullLogger.Instance, Permissions, root, protector,
            hookTimeout: TimeSpan.FromMinutes(1), downloadTimeout: TimeSpan.FromSeconds(budgetSeconds), timeProvider: clock,
            executionOptions: requestSeconds == 120 ? null : new ExtensionExecutionOptions { SignedDownloadRequestSeconds = requestSeconds });
        await sandbox.CompleteSignedSessionGrantAsync("fixture-grant");
        return sandbox;
    }

    private ExtensionHostBridge Bridge() => new(root, new Factory(handler), NullLogger.Instance, Permissions,
        ExtensionSdkV1.ParseManifest(Manifest).SignedSession, protector, "signed-timeout", timeProvider: clock);

    public void Dispose()
    {
        handler.Release.TrySetResult();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Factory(ControlledHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler.Client = new(handler, disposeHandler: false);
    }

    private sealed class ControlledHandler : HttpMessageHandler
    {
        private int active;
        public int MaximumConcurrency { get; private set; }
        public int PreparationCalls { get; private set; }
        public bool IgnoreCancellation { get; set; }
        public HttpClient? Client { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref active);
            MaximumConcurrency = Math.Max(MaximumConcurrency, count);
            try
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/prepare", StringComparison.Ordinal))
                {
                    PreparationCalls++;
                    using var registration = cancellationToken.Register(() => Canceled.TrySetResult());
                    Entered.TrySetResult();
                    try { await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken); }
                    catch (OperationCanceledException)
                    {
                        Canceled.TrySetResult();
                        throw;
                    }
                }
                var body = request.RequestUri.AbsolutePath.EndsWith("/exchange", StringComparison.Ordinal)
                    ? """{"session_id":"fixture-session","session_secret":"fixture-secret","expires_at":"2099-01-01T00:00:00Z"}"""
                    : "{}";
                return new(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
