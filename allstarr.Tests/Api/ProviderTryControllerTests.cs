using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderTryControllerTests
{
    [Fact]
    public async Task SpicyTryExplainsMissingSelectorBeforeAnyAccountRead()
    {
        using var http = new HttpClient(new SpicyLyricsTestHandler("unused"));
        var registry = new ProviderRegistry([allstarr.Core.Providers.Lyrics.SpicyLyricsCapabilityAdapter.CreateRegistration(
            new(http, new SpicyLyricsTestSecrets()))]);
        var controller = Controller(registry);
        SignIn(controller);
        var response = Assert.IsType<BadRequestObjectResult>(await controller.Execute("spicylyrics", new() { Capability = "lyrics", Operation = "lyrics", Title = "Title" }));
        Assert.Equal("A Spotify track link or 22-character ID is required.", JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_RequiresAdministrator(bool authenticated)
    {
        var controller = Controller(new Mock<IProviderRegistry>().Object);
        if (authenticated) controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(false);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Describe("fixture")).StatusCode);
    }

    [Fact]
    public async Task Discovery_UnknownProviderIs404()
    {
        var controller = Controller(new Mock<IProviderRegistry>().Object);
        SignIn(controller);
        Assert.IsType<NotFoundResult>(await controller.Describe("missing"));
    }

    [Fact]
    public async Task Discovery_SerializesCamelCaseAndOmitsKindsOnTrackOperation()
    {
        var descriptor = ProviderTryOperationsTests.Descriptor(new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack"]));
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.TryGet("fixture", out descriptor)).Returns(true);
        var controller = Controller(registry.Object);
        SignIn(controller);
        var result = Assert.IsType<OkObjectResult>(await controller.Describe("fixture"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { PropertyNamingPolicy = null }));
        Assert.Equal("fixture", json.RootElement.GetProperty("provider").GetString());
        var capability = json.RootElement.GetProperty("capabilities")[0];
        Assert.Equal("none", capability.GetProperty("accountRequirement").GetString());
        Assert.False(capability.GetProperty("operations")[1].TryGetProperty("kinds", out _));
    }

    [Fact]
    public async Task Execute_ValidatesBeforeResolvingIdentity()
    {
        var controller = Controller(new Mock<IProviderRegistry>().Object);
        SignIn(controller);
        var result = Assert.IsType<BadRequestObjectResult>(await controller.Execute("fixture",
            new() { Capability = "metadata", Operation = "search" }));
        Assert.Equal("A search query is required.", JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Execute_UnknownOperationIs404()
    {
        var controller = Controller(new Mock<IProviderRegistry>().Object);
        SignIn(controller);
        Assert.IsType<NotFoundResult>(await controller.Execute("fixture", new() { Capability = "metadata", Operation = "other" }));
    }

    [Fact]
    public void InvalidJsonModel_UsesThePlainErrorContract()
    {
        var context = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()),
            [], new Dictionary<string, object?>(), new object());
        context.ModelState.AddModelError("accountId", "Invalid identifier");
        new ProviderTryInputValidationAttribute().OnActionExecuting(context);
        var result = Assert.IsType<BadRequestObjectResult>(context.Result);
        Assert.Equal("Request inputs are invalid.", JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString());
    }

    private static ProviderTryController Controller(IProviderRegistry registry) => new(registry, null!, null!, null!)
    { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
    private static void SignIn(ProviderTryController controller) => controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] =
        Session(true);
    private static AdminAuthSession Session(bool administrator) => new()
    {
        SessionId = "session",
        UserId = "backend-user",
        UserName = "Fixture",
        JellyfinAccessToken = "fixture-token",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        IsAdministrator = administrator,
        AllstarrUserId = Guid.NewGuid()
    };
}
