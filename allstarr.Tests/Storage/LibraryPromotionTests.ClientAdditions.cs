using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Capabilities;
using allstarr.Core.Enrichment;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;
using allstarr.Core.ClientEvents;
using allstarr.Core.Favorites;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Theory]
    [InlineData(true, "song")]
    [InlineData(false, "album")]
    public async Task ClientApi_AdditionsReuseApprovalRulesAndCancel(bool direct, string unit)
    {
        await SetPromotionRoleAsync(false, direct);
        var batch = RequestService();
        var additions = ClientAdditions(batch);
        var added = await additions.AddAsync(Context, new("ext-allstarr-song-" + recordingId, unit));
        Assert.Equal(unit, added.Unit);
        Assert.Equal(direct ? "adding" : "waitingForApproval", added.State);
        Assert.Equal(added.Id, (await additions.AddAsync(Context, new("ext-allstarr-song-" + recordingId, unit))).Id);
        Assert.Equal(added.Id, Assert.Single(await additions.ListAsync(Context)).Id);
        var other = await AddPromotionUserAsync(true);
        Assert.Empty(await additions.ListAsync(other));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => additions.CancelAsync(other, added.Id));
        Assert.Equal("cancelled", (await additions.CancelAsync(Context, added.Id)).State);
        Assert.Equal("cancelled", Assert.Single(await additions.ListAsync(Context)).State);
        await using var db = factory.CreateDbContext();
        var json = direct ? (await db.Set<PromotionRunRecord>().SingleAsync()).RequestJson :
            (await db.PromotionRequests.SingleAsync()).RequestJson;
        Assert.Equal(unit, JsonSerializer.Deserialize<PromoteSongRequest>(json)!.Unit);
    }

    [Fact]
    public async Task ClientApi_AdditionsReadTheSavedUnitOfEarlierPromotions()
    {
        await new PromotionProfiles(factory).SaveMineAsync(Context, new() { DefaultUnit = "song" });
        var batch = RequestService();
        await batch.StartAsync(Context, Request());
        Assert.Equal("song", Assert.Single(await ClientAdditions(batch).ListAsync(Context)).Unit);
    }

    [Fact]
    public async Task ClientApi_AdditionsShowApprovalDeclineAndCompletion()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var additions = ClientAdditions(batch);
        var pending = await additions.AddAsync(Context, new(recordingId.ToString(), "song"));
        await batch.DeclineRequestAsync(administrator, pending.Id, "Please choose the original release.");
        var declined = Assert.Single(await additions.ListAsync(Context));
        Assert.Equal("declined", declined.State);
        Assert.Contains("original release", declined.Reason);
        var next = await additions.AddAsync(Context, new(recordingId.ToString(), "song"));
        var approved = await batch.ApproveRequestAsync(administrator, next.Id);
        Assert.Equal("adding", (await additions.ListAsync(Context)).Single(item => item.Id == next.Id).State);
        await using (var db = factory.CreateDbContext())
        {
            var run = await db.Set<PromotionRunRecord>().SingleAsync(item => item.Id == approved.RunId);
            run.Outcome = "Needs review";
            await db.SaveChangesAsync();
        }
        Assert.Equal("needsLook", (await additions.ListAsync(Context)).Single(item => item.Id == next.Id).State);
        await using (var db = factory.CreateDbContext())
        {
            var run = await db.Set<PromotionRunRecord>().SingleAsync(item => item.Id == approved.RunId);
            run.Outcome = "Placed";
            var plan = System.Text.Json.Nodes.JsonNode.Parse(run.PlanJson)!;
            plan["Tracks"]![0]!["Outcome"] = "Placed";
            run.PlanJson = plan.ToJsonString();
            await db.SaveChangesAsync();
        }
        Assert.Equal("done", (await additions.ListAsync(Context)).Single(item => item.Id == next.Id).State);
    }

    [Fact]
    public async Task ClientApi_AddAlbumResolvesItsRepresentativeSongAndRejectsOtherUnits()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "source", "release"))
            .ReturnsAsync(new allstarr.Models.Domain.Album
            {
                Songs = [new() { Id = recordingId.ToString(), Title = "Song", Album = "Album" }]
            });
        var additions = ClientAdditions(RequestService(), gateway.Object);
        Assert.Equal("album", (await additions.AddAsync(Context, new("ext-source-album-release", "album"))).Unit);
        await Assert.ThrowsAsync<ArgumentException>(() => additions.AddAsync(Context, new("ext-source-album-release", "song")));
        await Assert.ThrowsAsync<ArgumentException>(() => additions.AddAsync(Context, new(recordingId.ToString(), "playlist")));
        gateway.VerifyAll();
    }

    private ClientLibraryAdditions ClientAdditions(LibraryPromotionService promotions, IProtocolProviderGateway? gateway = null)
    {
        var identities = new Mock<allstarr.Core.Matching.ITrackIdentityService>();
        identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new allstarr.Core.Matching.CanonicalRecordingIdentity(recordingId, owner, null, null, 0));
        return new(factory, promotions, new PromotionProfiles(factory), roots, identities.Object,
            new TestBackendLibraryAccess(factory, "music"), gateway ?? Mock.Of<IProtocolProviderGateway>());
    }

    [Fact]
    public async Task FavoriteAddition_UsesSongUnitApprovalAndStableRetryKey()
    {
        await SetPromotionRoleAsync(false, false);
        var batch = RequestService();
        var executor = new FavoriteLibraryAdditionExecutor(ClientAdditions(batch));
        var favorite = new FavoriteEventRecord
        {
            Id = Guid.CreateVersion7(), OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "backend",
            BackendPrincipalId = "principal", ItemId = recordingId.ToString(), CorrelationId = "heart-addition"
        };
        Assert.True((await executor.ExecuteAsync(favorite, new(), 1, default)).Succeeded);
        Assert.True((await executor.ExecuteAsync(favorite, new(), 2, default)).Succeeded);
        await using var db = factory.CreateDbContext();
        var request = Assert.Single(await db.PromotionRequests.ToArrayAsync());
        Assert.Equal("song", request.Unit);
        Assert.Equal("pending", request.State);
        Assert.Empty(await db.Set<PromotionRunRecord>().ToArrayAsync());
        var administrator = await AddPromotionUserAsync(true);
        await batch.DeclineRequestAsync(administrator, request.Id, null);
        Assert.True((await executor.ExecuteAsync(favorite, new(), 3, default)).Succeeded);
        Assert.Equal(1, await db.PromotionRequests.CountAsync());
    }

    [Fact]
    public async Task ClientApi_AdditionEventsFollowOwnersThroughApprovalAndCancellation()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        using var hub = new ClientEventHub(new() { CoalesceWindow = TimeSpan.Zero });
        using var listener = hub.Subscribe(owner, "listener")!;
        using var admin = hub.Subscribe(administrator.Principal!.UserId, "administrator")!;
        var batch = RequestService(clientEvents: hub);
        var additions = ClientAdditions(batch);
        var pending = await additions.AddAsync(Context, new(recordingId.ToString(), "song"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var created = await listener.ReadAsync(timeout.Token);
        Assert.Equal("libraryRequest.updated", created!.Type);
        Assert.Contains("waitingForApproval", created.Json);
        Assert.Equal(0, admin.PendingCount);
        await batch.ApproveRequestAsync(administrator, pending.Id);
        Assert.Equal("libraryAddition.updated", (await listener.ReadAsync(timeout.Token))!.Type);
        Assert.Contains("adding", (await listener.ReadAsync(timeout.Token))!.Json);
        Assert.Equal(0, admin.PendingCount);
        Assert.Equal("cancelled", (await additions.CancelAsync(Context, pending.Id)).State);
        var updates = new[] { (await listener.ReadAsync(timeout.Token))!, (await listener.ReadAsync(timeout.Token))! };
        Assert.Contains(updates, item => item.Type == "libraryAddition.updated" && item.Json.Contains("cancelled", StringComparison.Ordinal));
        Assert.Contains(updates, item => item.Type == "libraryRequest.updated" && item.Json.Contains("cancelled", StringComparison.Ordinal));
        Assert.Equal(0, admin.PendingCount);
        var declined = await additions.AddAsync(Context, new(recordingId.ToString(), "album"));
        await batch.DeclineRequestAsync(administrator, declined.Id, "Choose another release.");
        Assert.Contains("declined", (await listener.ReadAsync(timeout.Token))!.Json);
        Assert.Equal(0, admin.PendingCount);
    }
}
