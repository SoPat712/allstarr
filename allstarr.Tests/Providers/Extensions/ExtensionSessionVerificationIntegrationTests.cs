using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ExtensionControlPlaneServiceTests
{
    [Fact]
    public async Task OtherSignedSessionServicesKeepManualVerification()
    {
        var fixture = await AutomaticVerificationFixtureAsync(serviceHost: "api.example.test");
        await fixture.Runtime.RefreshSignedSessionsAsync();
        await using var db = await _factory.CreateDbContextAsync();

        Assert.Empty(await db.Jobs.ToListAsync());
        Assert.Equal(0, fixture.Handler.BrowserCalls);
        Assert.Equal(ExtensionSessionStates.Expired, (await fixture.Runtime.SignedSessionStatusAsync(fixture.Package.Id)).State);
    }

    [Fact]
    public async Task RecoveredVerificationWaitsForRuntimeStartupAndUsesTheSavedSession()
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        await fixture.Runtime.RefreshSignedSessionsAsync();
        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        var restarted = fixture.Restart();
        var handler = new ExtensionSessionVerificationJobHandler(restarted);
        var execution = new DurableJobExecutionContext(claim, Mock.Of<IServiceProvider>());

        Assert.Equal(DurableJobCompletionKind.Deferred, (await handler.ExecuteAsync(execution, default)).Kind);
        Assert.Equal(0, fixture.Handler.BrowserCalls);
        await restarted.StartAsync(default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(execution, default)).Kind);
        Assert.Equal(1, fixture.Handler.BrowserCalls);
        Assert.Equal(ExtensionSessionStates.SignedIn, (await restarted.SignedSessionStatusAsync(fixture.Package.Id)).State);
    }

    [Fact]
    public async Task ExpiredSessionQueuesOneDurableVerificationAndRestoresTheExactConnection()
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        await fixture.Runtime.RefreshSignedSessionsAsync();
        await fixture.Runtime.RefreshSignedSessionsAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var job = Assert.Single(await db.Jobs.ToListAsync());
        Assert.Equal(ExtensionSessionVerificationJobHandler.Type, job.Type);
        Assert.DoesNotContain("grant", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("challenge", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(job.ProviderAccountId);

        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        var outcome = await new ExtensionSessionVerificationJobHandler(fixture.Runtime).ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, outcome.Kind);
        Assert.Equal(1, fixture.Handler.BrowserCalls);
        Assert.Equal(2, fixture.Handler.ExchangeCalls);
        Assert.Equal(ExtensionSessionStates.SignedIn, (await fixture.Runtime.SignedSessionStatusAsync(fixture.Package.Id)).State);
        Assert.Contains(await db.ExtensionLogs.ToListAsync(), item => item.EventCode == "session.restored");
    }

    [Theory]
    [InlineData("signout")]
    [InlineData("remove")]
    [InlineData("revision")]
    [InlineData("disable-package")]
    public async Task BrowserResultCannotRestoreRevokedOrChangedConnection(string change)
    {
        var fixture = await AutomaticVerificationFixtureAsync(holdBrowser: true);
        await fixture.Runtime.RefreshSignedSessionsAsync();
        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        var pending = new ExtensionSessionVerificationJobHandler(fixture.Runtime).ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        await fixture.Handler.BrowserEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change == "signout")
            await fixture.Runtime.ClearSignedSessionAsync(fixture.Package.Id, fixture.Package.Revision);
        else if (change == "disable-package")
            await fixture.Runtime.DisableAsync(fixture.Package.Id, fixture.Package.Revision);
        else
        {
            await using var db = await _factory.CreateDbContextAsync();
            var account = await db.ProviderAccounts.SingleAsync(item => item.Id == claim.ProviderAccountId);
            account.Revision++;
            if (change == "remove") account.SettingsJson = "{\"__removed\":true}";
            await db.SaveChangesAsync();
        }
        fixture.Handler.ReleaseBrowser.TrySetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(1, fixture.Handler.ExchangeCalls);
    }

    [Fact]
    public async Task VerificationPolicyCannotBeUsedForOrdinaryProviderWork()
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        await fixture.Runtime.RefreshSignedSessionsAsync();
        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        var authorizer = new DurableJobContextAuthorizer(_factory);
        Assert.True((await authorizer.ReauthorizeAsync(claim)).Authorized);
        Assert.False((await authorizer.ReauthorizeAsync(claim with { Type = "provider.download" })).Authorized);
        Assert.False((await authorizer.ReauthorizeAsync(claim with { ProviderCapability = "metadata" })).Authorized);
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.SingleAsync(item => item.Id == claim.ProviderAccountId);
        account.Revision++;
        await db.SaveChangesAsync();
        Assert.False((await authorizer.ReauthorizeAsync(claim)).Authorized);
    }

    [Fact]
    public async Task BrowserOutageRetainsSessionAndReturnsBoundedDurableRetry()
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        fixture.Handler.BrowserUnavailable = true;
        await fixture.Runtime.RefreshSignedSessionsAsync();
        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        var result = await new ExtensionSessionVerificationJobHandler(fixture.Runtime).ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), result.RetryDelay);
        Assert.Equal(ExtensionSessionStates.Expired, (await fixture.Runtime.SignedSessionStatusAsync(fixture.Package.Id)).State);
        Assert.Equal(1, fixture.Handler.ExchangeCalls);
        Assert.DoesNotContain("private", result.SafeMessage!, StringComparison.Ordinal);
    }

    private async Task<AutomaticVerificationFixture> AutomaticVerificationFixtureAsync(bool holdBrowser = false, string serviceHost = "api.zarz.moe")
    {
        var verified = Package("8.0.0", "automatic-verification", signedSession: true, refreshSession: true);
        var manifestPath = Path.Combine(verified.PackageRoot, "manifest.json");
        var manifest = File.ReadAllText(manifestPath).Replace("https://api.example.test/", $"https://{serviceHost}/", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest);
        verified = verified with { Manifest = ExtensionSdkV1.ParseManifest(manifest), ContentSha256 = ExtensionSdkV1.ComputePackageContentSha256(verified.PackageRoot) };
        var package = await _service.StageAsync(verified);
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [new("network", $"https://{serviceHost}/", true), new("secret", "accountToken", true)]);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var handler = new AutomaticVerificationHandler(clock, holdBrowser);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), new Clock());
        var config = new ConfigurationBuilder().AddConfiguration(_configuration).AddInMemoryCollection(new Dictionary<string, string?>
        { ["Extensions:SessionVerification:BaseUrl"] = "http://verifier.test" }).Build();
        var registry = new ProviderRegistry([]);
        var downloads = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "automatic-downloads") };
        var protection = new EphemeralDataProtectionProvider();
        ExtensionRuntimeCoordinator CreateRuntime() => new(_factory, _service, registry, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), downloads, audioVerifier: FixtureAudioVerifier.Instance), downloads,
            config, NullLogger<ExtensionRuntimeCoordinator>.Instance, dataProtectionProvider: protection, timeProvider: clock,
            sessionVerification: new(clients.Object, config), sessionJobs: queue);
        var runtime = CreateRuntime();
        package = await runtime.ActivateAsync(package.Id, package.Revision);
        await runtime.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "initial-grant");
        clock.Advance(TimeSpan.FromHours(2));
        return new(package, runtime, handler, queue, CreateRuntime);
    }

    private sealed record AutomaticVerificationFixture(ExtensionPackageRecord Package, ExtensionRuntimeCoordinator Runtime,
        AutomaticVerificationHandler Handler, DurableJobQueue Queue, Func<ExtensionRuntimeCoordinator> Restart);

    private sealed class AutomaticVerificationHandler(ManualTimeProvider clock, bool holdBrowser) : HttpMessageHandler
    {
        public int BrowserCalls { get; private set; }
        public int ExchangeCalls { get; private set; }
        public bool BrowserUnavailable { get; set; }
        public TaskCompletionSource BrowserEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBrowser { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.RequestUri!.Host == "verifier.test")
            {
                BrowserCalls++;
                BrowserEntered.TrySetResult();
                if (holdBrowser) await ReleaseBrowser.Task.WaitAsync(cancellationToken);
                if (BrowserUnavailable) throw new HttpRequestException("private challenge details");
                body = "{\"callback_url\":\"spotiflac://session-grant?state=fixture-provider&grant=browser-grant\"}";
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/exchange", StringComparison.Ordinal))
            {
                ExchangeCalls++;
                body = JsonSerializer.Serialize(new
                {
                    session_id = "fixture-session",
                    session_secret = "fixture-secret",
                    expires_at = clock.GetUtcNow().AddHours(1).ToString("O")
                });
            }
            else body = "{\"challenge_id\":\"fixture-challenge\"}";
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }
}
