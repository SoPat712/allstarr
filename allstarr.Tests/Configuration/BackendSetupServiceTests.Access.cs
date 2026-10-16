using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Configuration;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed partial class BackendSetupServiceTests
{
    private async Task<AdminAuthSession> PrepareAccessAsync()
    {
        Accept();
        await Service().SaveAsync(Request(false), default);
        configuration["Backend:Type"] = "Jellyfin";
        configuration["Jellyfin:Url"] = "http://backend-fixture:8096";
        var user = new UserRecord
        {
            Id = Guid.CreateVersion7(),
            BackendType = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = "principal-fixture",
            DisplayName = "Administrator",
            IsAdmin = true,
            Enabled = true,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
            LastSeenAt = clock.UtcNow
        };
        await using var db = new AllstarrDbContext(database.Options);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new()
        {
            SessionId = "access-fixture",
            UserId = user.BackendPrincipalId,
            UserName = user.DisplayName,
            IsAdministrator = true,
            AllstarrUserId = user.Id,
            BackendType = "Jellyfin",
            BackendInstanceId = "primary",
            JellyfinAccessToken = "",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
    }

    private async Task<BackendServiceAccessRequest> AccessRequestAsync(AdminAuthSession actor) => new()
    {
        Username = "admin-fixture",
        Password = "private-password-fixture",
        ExpectedRevision = (await Service().GetServiceAccessAsync(actor, default)).Revision
    };

    [Fact]
    public async Task ScanningAccessCanBeConnectedRotatedAndExplicitlyDisabledWithoutChangingTheBackend()
    {
        var actor = await PrepareAccessAsync();
        for (var revision = 1; revision <= 2; revision++)
        {
            await Service().SaveServiceAccessAsync(actor, await AccessRequestAsync(actor), default);
            await using var db = new AllstarrDbContext(database.Options);
            var reference = Assert.Single(await db.SecretReferences.ToListAsync());
            Assert.Equal(revision, reference.ActiveVersion);
            Assert.Equal(revision, await db.SecretVersions.CountAsync());
            using var lease = await secrets.OpenAsync(reference.Id, new SecretAccessContext(null,
                StartupConfiguration.ServicePurpose("Jellyfin", "http://backend-fixture:8096"), true));
            Assert.Equal("private-token-fixture", lease.ReadUtf8());
            Assert.DoesNotContain("private-", JsonSerializer.Serialize(await db.RuntimeSettings.ToListAsync()), StringComparison.Ordinal);
        }
        var removal = await AccessRequestAsync(actor);
        removal.Enabled = false;
        removal.Password = "";
        await Service().SaveServiceAccessAsync(actor, removal, default);
        Assert.False((await Service().GetServiceAccessAsync(actor, default)).Configured);
        await using var final = new AllstarrDbContext(database.Options);
        Assert.NotNull((await final.SecretReferences.SingleAsync()).RevokedAt);
        Assert.Equal(1, (await final.RuntimeSettings.SingleAsync(item => item.Key == "Backend:Url")).Revision);
        Assert.Equal(2, await final.SecretVersions.CountAsync());
        verifier.Verify(item => item.VerifyAsync(It.IsAny<BackendType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ScanningVerificationDoesNotHoldAWriterAndAdminRevocationAbortsPersistence()
    {
        var actor = await PrepareAccessAsync();
        var request = await AccessRequestAsync(actor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifier.Setup(item => item.VerifyAsync(It.IsAny<BackendType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await resume.Task; return new AdminOidcBackendUser(actor.UserId, "Administrator", true, "private-token-fixture"); });
        var saving = Service().SaveServiceAccessAsync(actor, request, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Run(async () =>
            {
                await using var independent = new AllstarrDbContext(database.Options);
                await independent.Users.Where(item => item.Id == actor.AllstarrUserId).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsAdmin, false));
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => saving);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Equal(3, await db.RuntimeSettings.CountAsync());
    }

    [Fact]
    public async Task ScanningRejectsAnotherPrincipalAndStaleReferenceRevision()
    {
        var actor = await PrepareAccessAsync();
        var stale = await AccessRequestAsync(actor);
        await Service().SaveServiceAccessAsync(actor, await AccessRequestAsync(actor), default);
        await Assert.ThrowsAsync<allstarr.Core.Settings.RuntimeSettingConflictException>(() => Service().SaveServiceAccessAsync(actor, stale, default));
        verifier.Setup(item => item.VerifyAsync(It.IsAny<BackendType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminOidcBackendUser("different-principal", "Other administrator", true, "other-token"));
        var other = await AccessRequestAsync(actor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().SaveServiceAccessAsync(actor, other, default));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(1, (await db.SecretReferences.SingleAsync()).ActiveVersion);
    }

    [Fact]
    public async Task AccessControllerRequiresAnAdministratorAndRestartsOnlyAfterTheSavedResponse()
    {
        var actor = await PrepareAccessAsync();
        var lifetime = new Mock<IHostApplicationLifetime>();
        var completion = new CompletionFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(completion);
        var antiforgery = new Mock<IAntiforgery>();
        antiforgery.Setup(item => item.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(true);
        var controller = new AdminAccessController(null!, Service(), lifetime.Object, antiforgery.Object)
        { ControllerContext = new() { HttpContext = context } };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetBackend(default)).StatusCode);
        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = actor;
        antiforgery.Setup(item => item.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(false);
        var rejected = await AccessRequestAsync(actor);
        Assert.IsType<BadRequestObjectResult>(await controller.SaveBackend(rejected, default));
        Assert.Equal("", rejected.Password);
        Assert.Empty(completion.Callbacks);
        antiforgery.Setup(item => item.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(true);
        var request = await AccessRequestAsync(actor);
        var result = Assert.IsType<OkObjectResult>(await controller.SaveBackend(request, default));
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        Assert.Equal("", request.Password);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        lifetime.Verify(item => item.StopApplication(), Times.Never);
        await Assert.Single(completion.Callbacks)();
        lifetime.Verify(item => item.StopApplication(), Times.Once);
    }
}
