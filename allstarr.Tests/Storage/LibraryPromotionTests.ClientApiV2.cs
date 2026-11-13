using allstarr.Controllers.ClientApiV2;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ClientApiV2_AdditionsReturnAcceptedBodiesThenReplayAndCancel(bool direct, bool album)
    {
        await SetPromotionRoleAsync(false, direct);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "source", "release"))
            .ReturnsAsync(new Album { Songs = [new() { Id = recordingId.ToString() }] });
        var endpoints = new V2LibraryEndpoints(null!, null!, null!, ClientAdditions(RequestService(), gateway.Object), null!, null!);
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = Context;
        var controller = new LibraryAdditionsController(endpoints) { ControllerContext = new() { HttpContext = http } };
        var request = new V2AddLibraryItem { ItemId = album ? V2Ids.Album("ext-source-album-release") : V2Ids.Track(recordingId), Unit = album ? "album" : "song" };
        var accepted = Assert.IsType<ObjectResult>(await controller.Add(request, "tap"));
        Assert.Equal(202, accepted.StatusCode);
        var addition = Assert.IsType<V2LibraryAddition>(accepted.Value);
        Assert.Equal(V2Ids.Track(recordingId), addition.ItemId);
        Assert.Equal(request.ItemId, addition.RequestedItemId);
        Assert.Equal(request.Unit, addition.Unit);
        Assert.Equal(direct ? "adding" : "waitingForApproval", addition.State);
        var replay = Assert.IsType<ObjectResult>(await controller.Add(request, "tap"));
        Assert.Equal(200, replay.StatusCode);
        Assert.Equal(addition.Id, Assert.IsType<V2LibraryAddition>(replay.Value).Id);
        var cancelled = Assert.IsType<ObjectResult>(await controller.Cancel(addition.Id.ToString("D")));
        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal("cancelled", Assert.IsType<V2LibraryAddition>(cancelled.Value).State);
    }
}
