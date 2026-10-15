using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyFavoriteDisplayReusesHouseholdKeptMetadataWithoutProvidersOrAudioReads(bool canonical)
    {
        await Service().PublishAsync(Request() with
        { Metadata = new KeptRecordingMetadata("Household title", ["Household artist"], "Household album", 91_234) });
        await RetireSourceAccountAsync();
        var (clock, jobs, _, _) = CreateInterestPipeline();
        var itemId = canonical ? CanonicalItemId() : "ext-deezer-song-source-track";
        var request = Mutation(other, itemId, FavoriteOperation.Favorite, "legacy");
        await new FavoriteActionPipeline(factory, jobs, clock).RecordAsync(request);
        var reads = await CountKeptReadsAsync();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var pipeline = new FavoriteActionPipeline(factory, jobs, clock, Service(), gateway.Object);
        Assert.Equal(1, await new FavoriteDisplayBackfillQueue(factory, jobs, clock).EnqueuePendingAsync());
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("kept-display", [FavoriteDisplayBackfillQueue.JobType]));

        var result = await new FavoriteDisplayBackfillJobHandler(factory, pipeline, clock)
            .ExecuteAsync(new(claim, services), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        var saved = Assert.Single(await new FavoriteViewReader(factory).ReadAsync(request.ExecutionContext));
        Assert.Equal("Household title", saved.Display.Title);
        Assert.Equal(new[] { "Household artist" }, saved.Display.Artists);
        Assert.Equal(91_234, saved.Display.DurationMilliseconds);
        Assert.Equal(itemId, saved.ItemId);
        Assert.Equal(0, reads.Bytes);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FavoriteDisplayBackfillAcceptsOnlyProvenCanonicalMetadataAliases(bool proven)
    {
        const string itemId = "ext-deezer-song-source-track";
        var (clock, jobs, _, _) = CreateInterestPipeline();
        await new FavoriteActionPipeline(factory, jobs, clock).RecordAsync(Mutation(other, itemId, FavoriteOperation.Favorite, "legacy"));
        var returnedId = proven ? CanonicalItemId() :
            (await identities.CreateRecordingAsync(Actor(other), "Other recording")).Recording.PublicId;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "source-track"))
            .ReturnsAsync(new allstarr.Models.Domain.Song { Id = returnedId, Title = "Canonical metadata", Artist = "Artist" });
        Assert.Equal(1, await new FavoriteDisplayBackfillQueue(factory, jobs, clock).EnqueuePendingAsync());
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("alias-display", [FavoriteDisplayBackfillQueue.JobType]));
        using var handler = new FavoriteDisplayBackfillJobHandler(factory,
            new(factory, jobs, clock, Service(), gateway.Object), clock);

        var completion = await handler.ExecuteAsync(new(claim, services), default);

        Assert.Equal(proven ? DurableJobCompletionKind.Succeeded : DurableJobCompletionKind.Retry, completion.Kind);
        await using var db = new allstarr.Core.Storage.AllstarrDbContext(database.Options);
        var state = await db.Set<FavoriteStateRecord>().SingleAsync();
        if (proven) Assert.Equal("Canonical metadata", FavoriteDisplaySnapshot.Read(itemId, state.DisplayMetadataJson)?.Title);
        else Assert.Null(state.DisplayMetadataJson);
    }
}
