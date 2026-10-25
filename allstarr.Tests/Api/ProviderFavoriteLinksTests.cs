using System.Reflection;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Filters;
using allstarr.Controllers;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace allstarr.Tests;

public sealed class ProviderFavoriteLinksTests
{
    [Fact]
    public void LinkApiUsesTheAdministrativePortAndBoundedRequestContract()
    {
        var controller = typeof(FavoriteActionPipeline).Assembly.GetType("allstarr.Controllers.ProviderFavoriteLinksController");
        Assert.NotNull(controller);
        Assert.Equal("api/admin/provider-favorite-links", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Contains(controller.GetCustomAttributes<ServiceFilterAttribute>(), item => item.ServiceType == typeof(AdminPortFilter));
        var limit = Assert.Single(controller.CustomAttributes, item => item.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(4096L, limit.ConstructorArguments[0].Value);
    }

    [Theory]
    [InlineData("FavoriteLinkPair")]
    [InlineData("CreateFavoriteLinkRequest")]
    [InlineData("RetryFavoriteLinkRequest")]
    public void LinkRequestsRejectUnknownFields(string name)
    {
        var type = typeof(FavoriteActionPipeline).Assembly.GetType("allstarr.Core.Favorites." + name);
        Assert.NotNull(type);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"unexpected\":true}", type,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task ControllerExposesOnlySafeReceiptsAndCurrentDialectInventory()
    {
        await using var f = await FavoriteLinkTests.Fixture.CreateAsync();
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = f.Admin;
        var controller = new ProviderFavoriteLinksController(f.Service) { ControllerContext = new() { HttpContext = http } };
        var inventory = Assert.IsType<FavoriteLinkPage<FavoriteLinkMember>>(Assert.IsType<OkObjectResult>(await controller.Members()).Value);
        Assert.Equal("jellyfin", inventory.ActiveProtocol);
        Assert.Equal(2, inventory.Items.Count);
        Assert.All(inventory.Items, item =>
        {
            Assert.Equal("Instance ".Length + 8, item.InstanceLabel.Length);
            Assert.Equal((item.Protocol == "jellyfin" ? "JF-" : "SUB-") + item.UserId.ToString("N")[..12], item.ProfileCode);
        });
        var json = JsonSerializer.Serialize(inventory);
        Assert.DoesNotContain(f.Jellyfin.BackendPrincipalId, json);
        Assert.DoesNotContain(f.Subsonic.BackendPrincipalId, json);
        var request = f.CreateRequest();
        var created = Assert.IsType<ObjectResult>(await controller.Create(request, default));
        Assert.Equal(201, created.StatusCode);
        var receipt = Assert.IsType<FavoriteLinkReceipt>(created.Value);
        var replay = Assert.IsType<ObjectResult>(await controller.Create(request, default));
        Assert.Equal(200, replay.StatusCode);
        Assert.True(Assert.IsType<FavoriteLinkReceipt>(replay.Value).Replayed);
        var detail = Assert.IsType<FavoriteLinkDetail>(Assert.IsType<OkObjectResult>(await controller.Detail(receipt.LinkId, default)).Value);
        Assert.Equal("merging", detail.State);
        Assert.Equal(0, detail.Union.ProcessedRows);
        Assert.Null(detail.Union.CompletedAt);
        Assert.Equal("private, no-store", http.Response.Headers.CacheControl);
        var invalid = Assert.IsType<ObjectResult>(await controller.Members(limit: 201));
        Assert.Equal(400, invalid.StatusCode);
        Assert.Equal("favorite_link_invalid", JsonSerializer.SerializeToElement(invalid.Value).GetProperty("code").GetString());
        var missing = Assert.IsType<ObjectResult>(await controller.Detail(Guid.NewGuid(), default));
        Assert.Equal(404, missing.StatusCode);
        f.Configuration["Backend:Type"] = "Subsonic";
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = FavoriteLinkTests.Fixture.Session(f.Subsonic, true);
        await using (var db = f.Factory.CreateDbContext())
        {
            var user = await db.Users.FindAsync(f.Subsonic.Id);
            user!.IsAdmin = true;
            await db.SaveChangesAsync();
        }
        Assert.Equal("subsonic", Assert.IsType<FavoriteLinkPage<FavoriteLinkMember>>(Assert.IsType<OkObjectResult>(await controller.Members()).Value).ActiveProtocol);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceAuthorizationStopsMissingOrListenerSessionsBeforeBodyBinding(bool listener)
    {
        await using var f = await FavoriteLinkTests.Fixture.CreateAsync();
        var http = new DefaultHttpContext();
        if (listener) http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = FavoriteLinkTests.Fixture.Session(f.Jellyfin, false);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var resource = new ResourceExecutingContext(action, [], []);
        var called = false;
        await new FavoriteLinkAuthorizationFilter(f.Service).OnResourceExecutionAsync(resource, () =>
        { called = true; return Task.FromResult(new ResourceExecutedContext(action, [])); });
        Assert.False(called);
        var result = Assert.IsAssignableFrom<ObjectResult>(resource.Result);
        Assert.Equal(listener ? 403 : 401, result.StatusCode);
        Assert.Equal(listener ? "administrator_required" : "authentication_required", JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString());
    }

    [Fact]
    public void InvalidModelErrorsAreStaticAndCannotReflectTheSubmittedPrincipal()
    {
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        action.ModelState.AddModelError("principal", "private-principal-payload");
        var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());
        new FavoriteLinkInputFilter().OnActionExecuting(executing);
        var result = Assert.IsType<BadRequestObjectResult>(executing.Result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("favorite_link_invalid", json);
        Assert.DoesNotContain("private-principal-payload", json);
    }
}
