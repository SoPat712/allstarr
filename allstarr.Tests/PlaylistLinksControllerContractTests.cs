using allstarr.Controllers;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace allstarr.Tests;

public sealed class PlaylistLinksControllerContractTests
{
    [Fact]
    public void Routes_MatchTheWebUiContract()
    {
        var controller = typeof(PlaylistLinksController);
        Assert.Equal("api/admin/playlist-links", controller.GetCustomAttributes(typeof(RouteAttribute), true)
            .Cast<RouteAttribute>().Single().Template);
        var expected = new Dictionary<string, string?>
        {
            [nameof(PlaylistLinksController.List)] = null,
            [nameof(PlaylistLinksController.Create)] = null,
            [nameof(PlaylistLinksController.Update)] = "{id:guid}",
            [nameof(PlaylistLinksController.Refresh)] = "{id:guid}/refresh",
            [nameof(PlaylistLinksController.Preview)] = "{id:guid}/preview",
            [nameof(PlaylistLinksController.Run)] = "{id:guid}/run",
            [nameof(PlaylistLinksController.SetOverride)] = "matches/{externalSnapshotId:guid}/override",
            [nameof(PlaylistLinksController.ClearOverride)] = "matches/overrides/{overrideId:guid}",
            [nameof(PlaylistLinksController.CreateSchedule)] = "{id:guid}/schedules",
            [nameof(PlaylistLinksController.UpdateSchedule)] = "schedules/{scheduleId:guid}",
            [nameof(PlaylistLinksController.CreateBackendCredential)] = "backend-credentials",
            [nameof(PlaylistLinksController.RotateBackendCredential)] = "backend-credentials/{referenceId:guid}"
        };
        foreach (var (name, template) in expected)
        {
            var method = controller.GetMethod(name)!;
            var route = method.GetCustomAttributes(true).OfType<HttpMethodAttribute>().Single();
            Assert.Equal(template, route.Template);
        }
    }

    [Fact]
    public async Task List_RejectsMissingOrUnlinkedAdminSessionBeforeStorageAccess()
    {
        var controller = Controller();
        Assert.IsType<UnauthorizedObjectResult>(await controller.List("music", CancellationToken.None));

        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session", UserId = "backend-user", UserName = "User", IsAdministrator = false,
            JellyfinAccessToken = "not-used-by-playlist-api", ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        var forbidden = Assert.IsType<ObjectResult>(await controller.List("music", CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public void RequestContracts_ContainStableReferencesAndNoRawCredentialFields()
    {
        var create = typeof(CreatePlaylistLinkRequest).GetProperties().Select(item => item.Name).ToArray();
        Assert.Contains("TargetCredentialReferenceId", create);
        Assert.DoesNotContain(create, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                                               name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                                               name.Contains("Cookie", StringComparison.OrdinalIgnoreCase) ||
                                               name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Decision", "LibraryTrackId", "Reason" },
            typeof(SetMatchOverrideRequest).GetProperties().Select(item => item.Name));
    }

    [Fact]
    public async Task BackendCredentialEndpoint_RequiresLinkedSessionAndRequestDoesNotRenderPassword()
    {
        var request = new BackendCredentialRequest
        {
            TargetProtocol = "subsonic", Username = "listener", Password = "do-not-echo"
        };
        Assert.DoesNotContain("do-not-echo", request.ToString(), StringComparison.Ordinal);
        var result = await Controller().CreateBackendCredential(request, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    private static PlaylistLinksController Controller()
    {
        var controller = new PlaylistLinksController(null!, null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
