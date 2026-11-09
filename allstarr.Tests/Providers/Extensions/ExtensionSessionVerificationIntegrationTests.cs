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

        Assert.Empty(await db.Jobs.Where(item => item.State != DurableJobState.Cancelled).ToListAsync());
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
        await restarted.Restored;

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
        var job = Assert.Single(await db.Jobs.Where(item => item.State != DurableJobState.Cancelled).ToListAsync());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalVerificationDoesNotSuppressANewNeedWithTheSameExpiry(bool failed)
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        await fixture.Runtime.RefreshSignedSessionsAsync();
        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        await fixture.Queue.CompleteAsync(claim, failed
            ? DurableJobCompletion.Failure("session_verification_incomplete", "Verification did not finish.")
            : DurableJobCompletion.Success());

        await Task.WhenAll(fixture.Runtime.RefreshSignedSessionsAsync(), fixture.Runtime.RefreshSignedSessionsAsync());
        await using var db = await _factory.CreateDbContextAsync();
        var jobs = await db.Jobs.Where(item => item.State != DurableJobState.Cancelled).ToListAsync();
        Assert.Equal(2, jobs.Count);
        var pending = Assert.Single(jobs, item => item.State == DurableJobState.Pending);
        Assert.Equal(claim.Payload.GetProperty("ExpiresAt").GetString(),
            JsonDocument.Parse(pending.PayloadJson).RootElement.GetProperty("ExpiresAt").GetString());
        Assert.Equal(3, pending.MaxAttempts);
        Assert.NotEqual(jobs.Single(item => item.Id == claim.JobId).IdempotencyKey, pending.IdempotencyKey);

        if (failed)
        {
            Assert.Null(await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]));
            Assert.Equal(fixture.Clock.GetUtcNow().AddHours(1), pending.AvailableAt);
            fixture.Clock.Advance(TimeSpan.FromHours(1));
        }
        var next = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        await fixture.Runtime.RefreshSignedSessionsAsync();
        await fixture.Queue.CompleteAsync(next, DurableJobCompletion.Retry("session_verification_unavailable", "Try again."));
        await fixture.Runtime.RefreshSignedSessionsAsync();
        Assert.Equal(2, await db.Jobs.CountAsync(item => item.State != DurableJobState.Cancelled));
    }

    [Fact]
    public async Task RepeatedFailedVerificationUsesAGrowingCooldown()
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        await fixture.Runtime.RefreshSignedSessionsAsync();
        foreach (var hours in new[] { 1, 2 })
        {
            var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
            await fixture.Queue.CompleteAsync(claim, DurableJobCompletion.Failure("session_verification_unavailable", "Try later."));
            await fixture.Runtime.RefreshSignedSessionsAsync();
            await fixture.Runtime.RefreshSignedSessionsAsync();
            await using var db = await _factory.CreateDbContextAsync();
            var pending = Assert.Single(await db.Jobs.Where(item => item.State == DurableJobState.Pending).ToListAsync());
            Assert.Equal(fixture.Clock.GetUtcNow().AddHours(hours), pending.AvailableAt);
            Assert.Null(await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]));
            fixture.Clock.Advance(TimeSpan.FromHours(hours));
        }
    }

    [Theory]
    [InlineData("status", "session_verification_http_error")]
    [InlineData("timeout", "session_verification_timeout")]
    [InlineData("callback", "session_verification_callback_missing")]
    public async Task VerifierFailuresHaveDistinctSafeJobErrors(string failure, string code)
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        fixture.Handler.VerifierFailure = failure;
        await fixture.Runtime.RefreshSignedSessionsAsync();
        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        var result = await new ExtensionSessionVerificationJobHandler(fixture.Runtime).ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(TimeSpan.FromMinutes(5), result.RetryDelay);
        Assert.DoesNotContain("private", result.SafeMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", result.SafeMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", result.SafeMessage!, StringComparison.Ordinal);
        if (failure == "status") Assert.Contains("503", result.SafeMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenewalRequiringVerificationQueuesBeforeExpiryForBothAccounts()
    {
        var fixture = await AutomaticVerificationFixtureAsync(expire: false);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = new ProviderAccountRecord
            {
                Id = Guid.NewGuid(),
                ProviderId = fixture.Package.ExtensionId,
                DisplayName = "Personal",
                Enabled = true,
                OwnerUserId = _reviewer,
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.ProviderAccounts.Add(account);
            await db.SaveChangesAsync();
            await fixture.Runtime.CompleteSignedSessionGrantAsync(fixture.Package.Id, fixture.Package.Revision, "personal-grant", accountId: account.Id);
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        fixture.Handler.RefreshRejected = true;
        await fixture.Runtime.RefreshSignedSessionsAsync();
        await fixture.Runtime.RefreshSignedSessionsAsync();
        await using var check = await _factory.CreateDbContextAsync();
        var jobs = await check.Jobs.Where(item => item.State == DurableJobState.Pending).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Equal(2, jobs.Select(item => item.ProviderAccountId).Distinct().Count());
    }

    [Fact]
    public async Task BackgroundRestoreQueuesEvenWhenFirstRenewalRanBeforeAnyRuntime()
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        var restarted = fixture.Restart();
        await restarted.RefreshSignedSessionsAsync();
        await restarted.StartAsync(default);
        await restarted.Restored;
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.Where(item => item.State == DurableJobState.Pending).ToListAsync());
    }

    [Fact]
    public async Task ActivationQueuesFirstSignInImmediately()
    {
        var fixture = await AutomaticVerificationFixtureAsync(signIn: false);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.Where(item => item.State == DurableJobState.Pending).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationCannotSucceedWithAnUnusableBootstrapOrExchange(bool bootstrap)
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        fixture.Handler.ReturnUnusableSession = true;
        fixture.Handler.BootstrapSession = bootstrap;
        await fixture.Runtime.RefreshSignedSessionsAsync();
        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        var result = await new ExtensionSessionVerificationJobHandler(fixture.Runtime).ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
        Assert.Equal(bootstrap ? "session_bootstrap_unavailable" : "session_verification_incomplete", result.ErrorCode);
        Assert.NotEqual(ExtensionSessionStates.SignedIn, (await fixture.Runtime.SignedSessionStatusAsync(fixture.Package.Id)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionUpdateValidatesTheRetainedSessionAndQueuesImmediatelyWhenRejected(bool rejected)
    {
        var fixture = await AutomaticVerificationFixtureAsync(expire: false);
        var oldFiles = Directory.GetFiles(Path.Combine(_root, "extensions", ".runtime"), "*.protected", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        fixture.Handler.SessionHours = 6;
        fixture.Handler.RefreshRejected = rejected;

        var updated = await UpdateVerificationPackageAsync(fixture);

        var view = await fixture.Runtime.SignedSessionStatusAsync(updated.Id);
        Assert.Equal(ExtensionSessionStates.SignedIn, view.State);
        Assert.Equal(rejected ? "verification_required" : null, view.ReasonCode);
        Assert.Equal(1, fixture.Handler.RefreshCalls);
        Assert.All(oldFiles, item => Assert.Equal(item.Value, File.ReadAllBytes(item.Key)));
        await using var db = await _factory.CreateDbContextAsync();
        var pending = await db.Jobs.Where(item => item.State == DurableJobState.Pending).ToListAsync();
        if (rejected)
            Assert.Equal(updated.Id, JsonDocument.Parse(Assert.Single(pending).PayloadJson).RootElement.GetProperty("PackageId").GetGuid());
        else Assert.Empty(pending);
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("baseUrl")]
    [InlineData("platform")]
    public async Task VersionUpdateDoesNotAdoptAnIncompatibleSession(string changed)
    {
        var fixture = await AutomaticVerificationFixtureAsync(expire: false);
        var updated = await UpdateVerificationPackageAsync(fixture, changed);
        Assert.Equal(ExtensionSessionStates.SignedOut, (await fixture.Runtime.SignedSessionStatusAsync(updated.Id)).State);
        Assert.Equal(0, fixture.Handler.RefreshCalls);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.Where(item => item.State == DurableJobState.Pending).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationReportsFailureWhenTheReturnedSessionIsRejected(bool bootstrap)
    {
        var fixture = await AutomaticVerificationFixtureAsync();
        await fixture.Runtime.RefreshSignedSessionsAsync();
        fixture.Handler.BootstrapSession = bootstrap;
        fixture.Handler.RefreshRejected = true;
        var claim = (await fixture.Queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]))!;
        var result = await new ExtensionSessionVerificationJobHandler(fixture.Runtime).ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
        Assert.Equal("session_verification_unusable", result.ErrorCode);
    }

    private async Task<ExtensionPackageRecord> UpdateVerificationPackageAsync(AutomaticVerificationFixture fixture, string? changed = null)
    {
        var verified = Package("9.0.0", "verification-update", signedSession: true, refreshSession: true);
        var path = Path.Combine(verified.PackageRoot, "manifest.json");
        var manifest = File.ReadAllText(path).Replace("api.example.test", "api.zarz.moe", StringComparison.Ordinal)
            .Replace("fixture@1.0.0", "fixture@2.0.0", StringComparison.Ordinal);
        manifest = changed switch
        {
            "namespace" => manifest.Replace("fixture-v1", "another-scope", StringComparison.Ordinal),
            "baseUrl" => manifest.Replace("api.zarz.moe/v1", "api.zarz.moe/v2", StringComparison.Ordinal),
            "platform" => manifest.Replace("\"appVersion\":", "\"platform\":\"desktop\",\"appVersion\":", StringComparison.Ordinal),
            _ => manifest
        };
        File.WriteAllText(path, manifest);
        verified = verified with { Manifest = ExtensionSdkV1.ParseManifest(manifest), ContentSha256 = ExtensionSdkV1.ComputePackageContentSha256(verified.PackageRoot) };
        var package = await _service.StageAsync(verified);
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [new("network", "https://api.zarz.moe/", true), new("secret", "accountToken", true)]);
        return await fixture.Runtime.ActivateAsync(package.Id, package.Revision);
    }

    private async Task<AutomaticVerificationFixture> AutomaticVerificationFixtureAsync(bool holdBrowser = false, string serviceHost = "api.zarz.moe", bool expire = true, bool signIn = true)
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
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), new VerificationClock(clock));
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
        if (signIn)
        {
            var initial = await queue.ClaimNextAsync("fixture", [ExtensionSessionVerificationJobHandler.Type]);
            if (initial != null) await queue.CompleteAsync(initial, DurableJobCompletion.Cancelled());
            await runtime.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "initial-grant");
            if (expire) clock.Advance(TimeSpan.FromHours(2));
        }
        return new(package, runtime, handler, queue, CreateRuntime, clock);
    }

    private sealed record AutomaticVerificationFixture(ExtensionPackageRecord Package, ExtensionRuntimeCoordinator Runtime,
        AutomaticVerificationHandler Handler, DurableJobQueue Queue, Func<ExtensionRuntimeCoordinator> Restart, ManualTimeProvider Clock);

    private sealed class VerificationClock(ManualTimeProvider clock) : allstarr.Core.Operations.IPlatformClock
    {
        public DateTimeOffset UtcNow => clock.GetUtcNow();
    }

    private sealed class AutomaticVerificationHandler(ManualTimeProvider clock, bool holdBrowser) : HttpMessageHandler
    {
        public int BrowserCalls { get; private set; }
        public int ExchangeCalls { get; private set; }
        public bool BrowserUnavailable { get; set; }
        public bool RefreshRejected { get; set; }
        public bool ReturnUnusableSession { get; set; }
        public bool BootstrapSession { get; set; }
        public int SessionHours { get; set; } = 1;
        public int RefreshCalls { get; private set; }
        public string? VerifierFailure { get; set; }
        public TaskCompletionSource BrowserEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBrowser { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string SessionBody() => JsonSerializer.Serialize(new
            {
                session_id = "fixture-session",
                session_secret = "fixture-secret",
                expires_at = clock.GetUtcNow().AddHours(ReturnUnusableSession ? -1 : SessionHours).ToString("O")
            });
            string body;
            if (request.RequestUri!.Host == "verifier.test")
            {
                BrowserCalls++;
                BrowserEntered.TrySetResult();
                if (holdBrowser) await ReleaseBrowser.Task.WaitAsync(cancellationToken);
                if (BrowserUnavailable) throw new HttpRequestException("private challenge details");
                if (VerifierFailure == "status")
                    return new(HttpStatusCode.ServiceUnavailable) { RequestMessage = request, Content = new StringContent("private challenge details") };
                if (VerifierFailure == "timeout") throw new TaskCanceledException("private challenge details");
                body = VerifierFailure == "callback" ? "{\"private\":\"challenge details\"}" :
                    "{\"callback_url\":\"spotiflac://session-grant?state=fixture-provider&grant=browser-grant\"}";
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/refresh", StringComparison.Ordinal))
            {
                RefreshCalls++;
                if (RefreshRejected)
                    return new(HttpStatusCode.PreconditionRequired) { RequestMessage = request, Content = new StringContent("{}") };
                body = SessionBody();
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/exchange", StringComparison.Ordinal))
            {
                ExchangeCalls++;
                body = SessionBody();
            }
            else body = BootstrapSession ? SessionBody() : "{\"challenge_id\":\"fixture-challenge\"}";
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }
}
