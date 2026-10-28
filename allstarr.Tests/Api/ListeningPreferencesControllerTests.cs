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
                new ListeningPreferences("CleanOnly", false, true, null, null, null), "current", It.IsAny<CancellationToken>()))
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
    public async Task AdministratorCanReadAndUpdateTheSelectedListenersLock()
    {
        var actor = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();
        var snapshot = new PersonalListeningPreferences(new("CleanOnly"), new(), false, "next")
        {
            CleanOnlyLocked = true
        };
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.GetManagedPreferencesAsync(target, actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        settings.Setup(item => item.UpdateCleanOnlyLockAsync(target, true, "current", actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var controller = Controller(settings.Object, actor, true);

        var read = Assert.IsType<OkObjectResult>(await controller.GetCleanOnlyLock(target, default));
        Assert.True(Assert.IsType<PersonalListeningPreferences>(read.Value).CleanOnlyLocked);
        var updated = Assert.IsType<OkObjectResult>(await controller.UpdateCleanOnlyLock(target, new(true, "current"), default));
        Assert.Equal("CleanOnly", Assert.IsType<PersonalListeningPreferences>(updated.Value).Values.ExplicitFilter);
        settings.VerifyAll();
        settings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListenerCannotReadOrChangeAnotherListenersLock()
    {
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        var controller = Controller(settings.Object, Guid.CreateVersion7(), false);
        var target = Guid.CreateVersion7();

        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetCleanOnlyLock(target, default)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.UpdateCleanOnlyLock(target, new(false, "current"), default)).StatusCode);
        controller.HttpContext.Items.Clear();
        Assert.IsType<UnauthorizedResult>(await controller.UpdateCleanOnlyLock(target, new(false, "current"), default));
        settings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LockEndpointsReturnSafeConflictAndAuthorizationErrors()
    {
        var actor = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.UpdateCleanOnlyLockAsync(target, false, "stale", actor, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RuntimeSettingConflictException("private storage detail"));
        settings.Setup(item => item.GetManagedPreferencesAsync(target, actor, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("private user detail"));
        var controller = Controller(settings.Object, actor, true);

        var conflict = Assert.IsType<ConflictObjectResult>(await controller.UpdateCleanOnlyLock(target, new(false, "stale"), default));
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        var forbidden = Assert.IsType<ObjectResult>(await controller.GetCleanOnlyLock(target, default));
        Assert.Equal(403, forbidden.StatusCode);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(forbidden.Value));
        settings.VerifyAll();
    }

    [Fact]
    public async Task LyricsOrderUsesTheSignedInListenerAndTheExistingRevision()
    {
        var user = Guid.NewGuid();
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.UpdatePreferencesAsync(user,
                new ListeningPreferences("All", true, true, "lyricsplus,spotify,lrclib", null, null), "revision", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new(), new(), false, "next"));
        var controller = Controller(settings.Object, user, false);
        Assert.IsType<OkObjectResult>(await controller.Update(new("All", true, true, "revision", "lyricsplus,spotify,lrclib"), default));
        settings.VerifyAll();
        settings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlaybackStartUsesOnlyTheSignedInListenerAndTheExistingRevision()
    {
        var user = Guid.CreateVersion7();
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.UpdatePreferencesAsync(user,
                new ListeningPreferences("All", true, true, null, null, "BestQuality"), "revision", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new(PlaybackStart: "BestQuality"), new(), false, "next"));
        var controller = Controller(settings.Object, user, false);
        var result = Assert.IsType<OkObjectResult>(await controller.Update(
            new("All", true, true, "revision", PlaybackStart: "BestQuality"), default));
        Assert.Equal("BestQuality", Assert.IsType<PersonalListeningPreferences>(result.Value).Values.PlaybackStart);
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
