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
    [InlineData("capability", "unknown")]
    [InlineData("account-resolution", "unknown")]
    [InlineData("track-selection", "unknown")]
    [InlineData("concurrency", "unknown")]
    [InlineData("resolve", "degraded")]
    [InlineData("first-byte", "degraded")]
    [InlineData("response-range", "degraded")]
    public async Task Latest_DistinguishesUnmeasuredChecksFromPlaybackFailures(string stage, string health)
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
                DetailsJson = JsonSerializer.Serialize(ProviderCtsDiagnosticResult.Failure(409, "artistgrid", null, stage, "sample_unavailable")),
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
        Assert.Equal("sample_unavailable", measurement.GetProperty("FailureCode").GetString());
        Assert.Equal(0, measurement.GetProperty("LatencyMs").GetDouble());
    }
}
