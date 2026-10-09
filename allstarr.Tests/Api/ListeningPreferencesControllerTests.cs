using allstarr.Controllers;
using allstarr.Core.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed class ListeningPreferencesControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadSaveAndResetUseOnlyTheSignedInUser(bool administrator)
    {
        var user = Guid.CreateVersion7();
        var snapshot = new PersonalListeningPreferences(new(), new(), true, "current");
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(s => s.GetPreferencesAsync(user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        settings.Setup(s => s.UpdatePreferencesAsync(user,
                new ListeningPreferences("CleanOnly", false, true), "current", It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot with { Revision = "updated" });
        settings.Setup(s => s.UpdatePreferencesAsync(user, null, "updated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var controller = Controller(settings.Object, user, administrator);

        Assert.IsType<OkObjectResult>(await controller.Get(default));
        Assert.IsType<OkObjectResult>(await controller.Update(new("CleanOnly", false, true, "current"), default));
        Assert.IsType<OkObjectResult>(await controller.Reset(new("updated"), default));
        settings.VerifyAll();
        settings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingOrUnlinkedSessionsCannotReadOrWrite()
    {
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        var controller = Controller(settings.Object, null, false);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Get(default)).StatusCode);
        controller.HttpContext.Items.Clear();
        Assert.IsType<UnauthorizedResult>(await controller.Update(new("All", true, true, "revision"), default));
        settings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaleAndDisabledSessionsReturnSafeErrors()
    {
        var user = Guid.CreateVersion7();
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(s => s.UpdatePreferencesAsync(user, null, "stale", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RuntimeSettingConflictException("private storage detail"));
        settings.Setup(s => s.GetPreferencesAsync(user, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("private user detail"));
        var controller = Controller(settings.Object, user, false);
        var conflict = Assert.IsType<ConflictObjectResult>(await controller.Reset(new("stale"), default));
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        var forbidden = Assert.IsType<ObjectResult>(await controller.Get(default));
        Assert.Equal(403, forbidden.StatusCode);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(forbidden.Value));
    }

    private static ListeningPreferencesController Controller(IDurableRuntimeSettings settings,
        Guid? user, bool administrator)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "backend-user",
            UserName = "Listener",
            IsAdministrator = administrator,
            AllstarrUserId = user,
            JellyfinAccessToken = "fixture-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new(settings) { ControllerContext = new ControllerContext { HttpContext = http } };
    }
}
