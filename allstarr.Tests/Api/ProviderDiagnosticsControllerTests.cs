using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderDiagnosticsControllerTests
{
    [Theory]
    [InlineData("capability", "capability-unavailable", "unknown")]
    [InlineData("account-resolution", "account-needs-configuration", "unknown")]
    [InlineData("track-selection", "sample-unavailable", "unknown")]
    [InlineData("track-selection", "No verified catalog recordings are available to test.", "unknown")]
    [InlineData("track-selection", "service_unavailable", "degraded")]
    [InlineData("concurrency", "busy", "unknown")]
    [InlineData("resolve", "sample-unavailable", "unknown")]
    [InlineData("resolve", "NotFound", "unknown")]
    [InlineData("resolve", "incompatible-media", "unknown")]
    [InlineData("first-byte", "http-404", "unknown")]
    [InlineData("first-byte", "http-410", "unknown")]
    [InlineData("resolve", "transient-failure", "degraded")]
    [InlineData("first-byte", "http-503", "degraded")]
    [InlineData("response-range", "range-response-mismatch", "degraded")]
    public async Task Latest_DistinguishesUnavailableSamplesFromPlaybackFailures(string stage, string reason, string health)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        await using (var db = factory.CreateDbContext())
        {
            db.AuditEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                Category = "provider-cts",
                Action = "scheduled.measure",
                Outcome = "recorded",
                DetailsJson = JsonSerializer.Serialize(ProviderCtsDiagnosticResult.Failure(409, "artistgrid", null, stage, reason)),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        IProviderStreamingCapability? streaming = Mock.Of<IProviderStreamingCapability>();
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.TryGetCapability<IProviderStreamingCapability>("artistgrid", ProviderCapabilityKind.Streaming, out streaming))
            .Returns(true);
        using var selector = new ProviderCtsTrackSelector(factory);
        var media = new ProviderCtsDiagnosticRunner(registry.Object, Mock.Of<IProviderRouteAccountResolver>(), selector,
            Mock.Of<IProviderOutcomeObserver>());
        var controller = new ProviderDiagnosticsController(factory, media, Mock.Of<IEffectiveProviderPolicyResolver>(), registry.Object)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "admin",
            UserName = "Fixture",
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            IsAdministrator = true,
            AllstarrUserId = Guid.NewGuid()
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.LatestDeepStream(default)).Value));
        var measurement = Assert.Single(json.RootElement.GetProperty("measurements").EnumerateArray());
        Assert.Equal(health, measurement.GetProperty("Health").GetString());
        Assert.Equal(reason, measurement.GetProperty("FailureCode").GetString());
        Assert.Equal(0, measurement.GetProperty("LatencyMs").GetDouble());
    }
}
