using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ExtensionControlPlaneServiceTests
{
    [Fact]
    public async Task CoordinatorStartupRenewalRetriesTransientDnsFailureWithBoundedBackoff()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 17, 8, 10, 0, TimeSpan.Zero));
        var package = await _service.StageAsync(Package("7.0.0", "startup-renewal", signedSession: true, refreshSession: true));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [new("network", "https://api.example.test/", true), new("secret", "accountToken", true)]);

        var handler = new StartupRetryHandler(clock, transientRefreshFailures: 2);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var registry = new ProviderRegistry([]);
        var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "startup-renewal-downloads") };
        var coordinator = new ExtensionRuntimeCoordinator(_factory, _service, registry, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), options), options,
            _configuration, NullLogger<ExtensionRuntimeCoordinator>.Instance, timeProvider: clock);

        package = await coordinator.ActivateAsync(package.Id, package.Revision);
        Assert.Equal(ExtensionSessionStates.SignedIn,
            (await coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1")).State);

        var pending = coordinator.RefreshSignedSessionsAsync();
        foreach (var expected in new[] { 10, 20 })
        {
            var delay = await clock.NextTimerAsync();
            while (delay == TimeSpan.FromSeconds(30) || delay == TimeSpan.FromSeconds(35))
                delay = await clock.NextTimerAsync();
            Assert.Equal(TimeSpan.FromSeconds(expected), delay);
            clock.Advance(TimeSpan.FromSeconds(expected));
        }

        await pending;

        Assert.Equal(3, handler.RefreshCalls);
        Assert.Equal(ExtensionSessionStates.SignedIn,
            (await coordinator.SignedSessionStatusAsync(package.Id)).State);
        await using var db = await _factory.CreateDbContextAsync();
        var logs = await db.ExtensionLogs
            .Where(item => item.ExtensionPackageId == package.Id)
            .ToListAsync();
        Assert.Contains(logs, item => item.EventCode == "session.renewed");
        var retained = logs.Where(item => item.EventCode == "session.retained").ToArray();
        Assert.Equal(2, retained.Length);
        Assert.All(retained, item =>
        {
            Assert.Contains("call=refresh", item.Message, StringComparison.Ordinal);
            Assert.Contains("status=none", item.Message, StringComparison.Ordinal);
            Assert.Contains("reason=network_not_ready", item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("session-1", item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-1", item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("network not ready", item.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    private sealed class StartupRetryHandler(ManualTimeProvider clock, int transientRefreshFailures) : HttpMessageHandler
    {
        public int RefreshCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/refresh")
            {
                RefreshCalls++;
                if (RefreshCalls <= transientRefreshFailures)
                {
                    throw new HttpRequestException(HttpRequestError.NameResolutionError, "network not ready");
                }

                return Task.FromResult(Json(request, HttpStatusCode.OK, SessionBody(clock.GetUtcNow().AddHours(6))));
            }

            if (path == "/v1/session/exchange")
                return Task.FromResult(Json(request, HttpStatusCode.OK, SessionBody(clock.GetUtcNow().AddMinutes(30))));

            return Task.FromResult(Json(request, HttpStatusCode.OK, "{\"challenge_id\":\"challenge-1\"}"));
        }

        private static HttpResponseMessage Json(HttpRequestMessage request, HttpStatusCode status, string body) => new(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private static string SessionBody(DateTimeOffset expiresAt) => JsonSerializer.Serialize(new
        {
            session_id = "session-1",
            session_secret = "secret-1",
            expires_at = expiresAt.ToString("O")
        });
    }
}
