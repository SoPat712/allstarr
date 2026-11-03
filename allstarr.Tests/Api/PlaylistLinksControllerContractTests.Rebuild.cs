using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed partial class PlaylistLinksControllerContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistRebuildRejectsOneTimeImportsWithConflict(bool administrator)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = owner,
                BackendType = "jellyfin",
                BackendInstanceId = "main",
                BackendPrincipalId = "owner",
                DisplayName = "Owner",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
                LastSeenAt = now
            });
            db.PlaylistLinks.Add(new PlaylistLinkRecord
            {
                Id = id,
                OwnerUserId = owner,
                SourceProviderId = "spotify",
                SourcePlaylistId = "source",
                SourcePlaylistIdHash = new string('a', 64),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "main",
                TargetPlaylistId = "old",
                Mode = PlaylistLinkMode.Materialized,
                ImportMode = PlaylistImportMode.OneTime,
                MaterializationMode = PlaylistMaterializationMode.Reconcile,
                RuleVersion = "rules",
                PolicyVersion = "policy",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        var controller = Controller(factory);
        Assert.IsType<UnauthorizedObjectResult>(await controller.Rebuild(id, default));
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(Guid.CreateVersion7(), false);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Rebuild(id, default)).StatusCode);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(administrator ? Guid.CreateVersion7() : owner, administrator);
        var result = Assert.IsType<ConflictObjectResult>(await controller.Rebuild(id, default));
        Assert.Equal("Imports keep your own order.", JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString());
    }

    private static AdminAuthSession Session(Guid userId, bool administrator) => new()
    {
        SessionId = "rebuild-session",
        UserId = "owner",
        UserName = "Owner",
        AllstarrUserId = userId,
        IsAdministrator = administrator,
        JellyfinAccessToken = "fixture",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };
}
