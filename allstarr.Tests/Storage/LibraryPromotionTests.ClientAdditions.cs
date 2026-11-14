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
using allstarr.Controllers.ClientApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Theory]
    [InlineData("ext-spotify-song-track", "song", true)]
    [InlineData("ext-source-album-release", "album", false)]
    public async Task ClientApi_AdditionsRetainTheRequestedItemInResponsesAndEvents(string itemId, string unit, bool direct)
    {
        await SetPromotionRoleAsync(false, direct);
        using var hub = new ClientEventHub(new() { CoalesceWindow = TimeSpan.Zero });
        using var listener = hub.Subscribe(owner, "listener")!;
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "source", "release"))
            .ReturnsAsync(new allstarr.Models.Domain.Album { Songs = [new() { Id = recordingId.ToString() }] });
        var additions = ClientAdditions(RequestService(clientEvents: hub), gateway.Object);

        var added = await additions.AddAsync(Context, new(itemId, unit));

        Assert.Equal(itemId, added.RequestedItemId);
        Assert.Null(added.LibraryItemId);
        Assert.Equal(itemId, Assert.Single(await ClientAdditions(RequestService()).ListAsync(Context)).RequestedItemId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var update = JsonSerializer.Deserialize<JsonElement>((await listener.ReadAsync(timeout.Token))!.Json);
        Assert.Equal(itemId, update.GetProperty("requestedItemId").GetString());
        Assert.False(update.TryGetProperty("libraryItemId", out _));
    }

    [Fact]
    public async Task ClientApi_AdditionKeysReplayCurrentStateArePersonalAndExpire()
    {
        await SetPromotionRoleAsync(false, false);
        var additions = ClientAdditions(RequestService());
        var request = new AddClientLibraryItem("ext-spotify-song-track", "song");
        var first = await additions.AddIdempotentAsync(Context, request, "tap");
        Assert.False(first.Replayed);
        await additions.CancelAsync(Context, first.Addition.Id);
        var replay = await ClientAdditions(RequestService()).AddIdempotentAsync(Context, request, "tap");
        Assert.True(replay.Replayed);
        Assert.Equal(first.Addition.Id, replay.Addition.Id);
        Assert.Equal("cancelled", replay.Addition.State);
        var other = await AddPromotionUserAsync(false);
        Assert.NotEqual(first.Addition.Id, (await additions.AddIdempotentAsync(other, request, "tap")).Addition.Id);
        await Assert.ThrowsAsync<PromotionException>(() => additions.AddIdempotentAsync(Context, request with { Unit = "album" }, "tap"));
        await Assert.ThrowsAsync<PromotionException>(() => additions.AddIdempotentAsync(Context, request with { ItemId = "ext-other-song-track" }, "tap"));
        await using (var db = factory.CreateDbContext())
        {
            foreach (var receipt in await db.AuditEvents.Where(item => item.Action == "library-addition.accepted").ToArrayAsync())
                receipt.CreatedAt = DateTimeOffset.UtcNow.AddHours(-25);
            await db.SaveChangesAsync();
        }
        var expired = await additions.AddIdempotentAsync(Context, request with { Unit = "album" }, "tap");
        Assert.False(expired.Replayed);
        Assert.NotEqual(first.Addition.Id, expired.Addition.Id);
    }

    [Fact]
    public async Task ClientApi_AdditionKeyIsSavedWithAcceptanceBeforePublishing()
    {
        var events = new Mock<IClientEventPublisher>();
        events.Setup(item => item.Publish(owner, It.IsAny<ClientEvent>(), null)).Throws(new IOException("Connection closed."));
        var request = new AddClientLibraryItem("ext-spotify-song-track", "song");
        await Assert.ThrowsAsync<IOException>(() => ClientAdditions(RequestService(clientEvents: events.Object))
            .AddIdempotentAsync(Context, request, "tap"));

        var replay = await ClientAdditions(RequestService()).AddIdempotentAsync(Context, request, "tap");

        Assert.True(replay.Replayed);
        await using var db = factory.CreateDbContext();
        Assert.Equal((await db.Set<PromotionRunRecord>().SingleAsync()).Id, replay.Addition.Id);
    }

    [Fact]
    public async Task ClientApi_AdditionPostReturnsAcceptedThenOkAndConflictsForChangedBody()
    {
        await SetPromotionRoleAsync(false, false);
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = Context;
        var controller = new LibraryAdditionsController(ClientAdditions(RequestService()))
        { ControllerContext = new() { HttpContext = http } };
        var request = new AddClientLibraryItem(recordingId.ToString(), "song");
        var first = Assert.IsType<AcceptedResult>((await controller.Add(request, "tap", default)).Result);
        var replay = Assert.IsType<OkObjectResult>((await controller.Add(request, "tap", default)).Result);
        Assert.Equal(Assert.IsType<ClientLibraryAddition>(first.Value).Id, Assert.IsType<ClientLibraryAddition>(replay.Value).Id);
        Assert.Equal(409, Assert.IsType<ObjectResult>((await controller.Add(request with { Unit = "album" }, "tap", default)).Result).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_AdditionsKeepDistinctRequestedRowsWhenAcquisitionIsShared(bool direct)
    {
        await SetPromotionRoleAsync(false, direct);
        var additions = ClientAdditions(RequestService());
        var first = await additions.AddAsync(Context, new("ext-spotify-song-track", "song"));
        var second = await additions.AddAsync(Context, new($"ext-allstarr-song-{recordingId:D}", "song"));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("ext-spotify-song-track", first.RequestedItemId);
        Assert.Equal($"ext-allstarr-song-{recordingId:D}", second.RequestedItemId);
        var saved = await additions.ListAsync(Context);
        Assert.Contains(saved, item => item.RequestedItemId == first.RequestedItemId);
        Assert.Contains(saved, item => item.RequestedItemId == second.RequestedItemId);
        await using var db = factory.CreateDbContext();
        Assert.Equal(direct ? 1 : 0, await db.Set<PromotionRunRecord>().CountAsync());
    }

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
        await new PromotionProfiles(factory).SaveMineAsync(Context, new() { NamingConfirmed = true, DefaultUnit = "song" });
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
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "principal",
            ItemId = recordingId.ToString(),
            CorrelationId = "heart-addition"
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
