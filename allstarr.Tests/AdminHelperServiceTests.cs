using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public class AdminHelperServiceTests
{
    [Fact]
    public void CreateJellyfinRequest_UsesStandardAuthorizationForApiKey()
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns("Production");
        var helper = new AdminHelperService(
            NullLogger<AdminHelperService>.Instance,
            Options.Create(new JellyfinSettings { ApiKey = "test-api-key" }),
            environment.Object);

        using var request = helper.CreateJellyfinRequest(HttpMethod.Get, "http://jellyfin.local/Users");

        var authorization = Assert.Single(request.Headers.GetValues("Authorization"));
        Assert.Contains("Client=\"Allstarr\"", authorization);
        Assert.Contains("Token=\"test-api-key\"", authorization);
        Assert.False(request.Headers.Contains("X-Emby-Authorization"));
    }
}
