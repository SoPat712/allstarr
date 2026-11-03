using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Secrets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class AdminPlaylistConsentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinConsentStoresOnlyReturnedTokenAndRevocationLogsOut(bool logoutFails)
    {
        var principal = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        var session = await _sessions.CreateSessionAsync("alice-id", "Alice", false, "session-token", null,
            backendType: "Jellyfin", allstarrUserId: principal.UserId);
        var controller = Controller(session.SessionId, "Jellyfin");
        var result = Assert.IsType<OkObjectResult>(await controller.GrantPlaylistConsent(new() { Password = "private-password" }, default));
        Assert.Equal("Alice", _backend.RequestedUser);
        Assert.Contains("Client=\"Allstarr\"", _backend.Authorization);
        Assert.Contains("Device=\"Allstarr playlist editing\"", _backend.Authorization);
        Assert.Contains($"allstarr-playlist-{principal.UserId:N}", _backend.Authorization);
        Assert.DoesNotContain("private-password", JsonSerializer.Serialize(result.Value));
        var grant = await _secrets.GetJellyfinPlaylistGrantAsync(principal);
        Assert.NotNull(grant);
        using (var lease = await _secrets.OpenAsync(grant.ReferenceId, new(principal.UserId, BackendCredentialScope.JellyfinPurpose)))
        {
            var stored = lease.ReadUtf8();
            Assert.Contains("jellyfin-fixture-token", stored);
            Assert.DoesNotContain("private-password", stored);
        }
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(BackendCredentialScope.JellyfinPurpose, (await db.SecretReferences.SingleAsync()).Purpose);
        _backend.LogoutFails = logoutFails;
        Assert.IsType<OkObjectResult>(await controller.RevokePlaylistConsent(default));
        Assert.True(_backend.LoggedOut);
        Assert.Contains("jellyfin-fixture-token", _backend.Authorization);
        Assert.Null(await _secrets.GetJellyfinPlaylistGrantAsync(principal));
        Assert.NotNull(await _sessions.GetValidSessionAsync(session.SessionId));
    }

    [Fact]
    public async Task JellyfinConsentRejectsForeignPrincipalAndCapsPasswords()
    {
        var principal = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        var session = await _sessions.CreateSessionAsync("alice-id", "Alice", false, "session-token", null,
            backendType: "Jellyfin", allstarrUserId: principal.UserId);
        var controller = Controller(session.SessionId, "Jellyfin");
        Assert.IsType<BadRequestObjectResult>(await controller.GrantPlaylistConsent(new() { Password = new string('x', 2001) }, default));
        _backend.ReturnedUser = "bob-id";
        Assert.IsType<UnauthorizedObjectResult>(await controller.GrantPlaylistConsent(new() { Password = "fixture" }, default));
        Assert.Null(await _secrets.GetJellyfinPlaylistGrantAsync(principal));
        var method = typeof(AdminAuthController).GetMethod(nameof(AdminAuthController.GrantPlaylistConsent))!;
        Assert.Single(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), false));
    }
}
