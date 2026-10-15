using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Fact]
    public async Task LegacyDeliveryStatus_OffersOnlyEligibleAccountsWithoutBindingOnRead()
    {
        var own = await AddScrobbleAccountAsync();
        var shared = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        var otherUser = Guid.CreateVersion7();
        var receipt = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(User(otherUser, "jellyfin", "backend", "status-other", "Other listener", clock.UtcNow));
            db.ProviderAccounts.AddRange(new ProviderAccountRecord
            {
                Id = shared,
                ProviderId = "lastfm",
                DisplayName = "Shared",
                Enabled = true,
                Revision = 1,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            },
                new ProviderAccountRecord
                {
                    Id = foreign,
                    OwnerUserId = otherUser,
                    ProviderId = "lastfm",
                    DisplayName = "Private",
                    Enabled = true,
                    Revision = 1,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow
                });
            db.PlaybackDeliveryCheckpoints.Add(new()
            {
                Id = receipt,
                OwnerUserId = user,
                SignalKey = new string('a', 64),
                TargetId = "lastfm",
                Kind = PlaybackScrobbleDeliveryKind.Completed,
                State = ScopedPlaybackScrobbleOutcome.PermanentFailure,
                ProviderCode = "destination-unbound",
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var controller = new ScrobbleDeliveriesController(factory, new(factory, jobs))
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = user.ToString(),
            UserName = "Listener",
            IsAdministrator = true,
            BackendType = "Jellyfin",
            AllstarrUserId = user,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        var response = Assert.IsType<OkObjectResult>(await controller.List(attentionOnly: true, targetId: "lastfm"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { own.Id, shared }.Order(), item.GetProperty("availableDestinationAccountIds")
            .EnumerateArray().Select(value => value.GetGuid()).Order());
        Assert.Equal(own.Id, item.GetProperty("suggestedDestinationAccountId").GetGuid());
        await using var verify = await factory.CreateDbContextAsync();
        var unchanged = await verify.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(receipt, unchanged.Id);
        Assert.Null(unchanged.DestinationAccountId);
        Assert.Equal(0, unchanged.Revision);
    }
}
