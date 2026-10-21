using allstarr.Controllers;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class ChallengeSolverControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestRequiresAdministratorBeforeContactingSolver(bool signedIn)
    {
        var controller = new ChallengeSolverController(null!, null!)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        if (signedIn) controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "listener",
            UserName = "Listener",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            JellyfinAccessToken = "fixture-token",
            IsAdministrator = false,
            AllstarrUserId = Guid.NewGuid()
        };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Test(new("http://solver:8191", 60))).StatusCode);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TestUsesOnlyAnActiveAdministratorIdentity(bool active)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var id = Guid.NewGuid();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.Add(new UserRecord
            {
                Id = id,
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = "owner",
                DisplayName = "Owner",
                IsAdmin = true,
                Enabled = active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var transport = new HealthHandler();
        var solver = new CloudflareChallengeSolver(new HttpClient(transport),
            _ => Task.FromResult(new ChallengeSolverOptions("", 60)));
        var controller = new ChallengeSolverController(solver, new Factory(database.Options))
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "owner",
            UserName = "Owner",
            BackendType = "Jellyfin",
            BackendInstanceId = "fixture",
            JellyfinAccessToken = "fixture-token",
            IsAdministrator = true,
            AllstarrUserId = id,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var result = await controller.Test(new("http://solver:8191", 60));
        if (active)
        {
            var body = Assert.IsType<ChallengeSolverTestResult>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.True(body.Ok);
        }
        else Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(active ? 1 : 0, transport.Calls);
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
    private sealed class HealthHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\"}") });
        }
    }
}
