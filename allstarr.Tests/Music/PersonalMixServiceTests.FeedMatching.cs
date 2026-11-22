using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    private const string FeedMiss = "Missing Echo";

    [Fact]
    public async Task ServiceFeeds_EverySuggestedFeedHasItsSongsMatchedInTheBackground()
    {
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var feeds = Enumerable.Range(0, 5).Select(index => "37i9dQZfeed" + index).ToArray();
        var (_, account) = await MatchedFeedAccount(feeds, ["Alpha", "Bravo", "Charlie", "Delta"], queue);
        await service.ListCardsAsync(owner);
        var handler = new ServiceFeedPreparationJobHandler(service);
        while (await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]) is { } claim)
            await queue.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, null!), default));
        var cards = (await service.ListCardsAsync(owner)).Where(item => item.Kind == "external").ToArray();
        Assert.Equal(5, cards.Length);
        foreach (var card in cards)
        {
            var detail = await service.GetAsync(owner, card.Id);
            Assert.True(detail.Readiness.Ready);
            Assert.Equal(4, detail.Songs.Count);
            Assert.All(detail.Songs, item => Assert.Equal("deezer", item.Song.ExternalProvider));
        }
        Assert.Equal(20, FeedSearches().Length);
        await using var db = factory.CreateDbContext();
        Assert.All(await db.Jobs.Where(item => item.Type == PersonalMixService.FeedPrepareJobType).ToArrayAsync(), job => Assert.Equal(-1000, job.Priority));
        Assert.Empty(await db.PlaylistLinks.ToArrayAsync());
        Assert.Equal(20, await db.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "service-feed-match" && item.ProviderAccountId == account));
    }

    [Fact]
    public async Task ServiceFeeds_KnownMatchesAndRememberedMissesAreNotSearchedAgain()
    {
        var (_, account) = await MatchedFeedAccount(["37i9dQZfeed"], ["Alpha", FeedMiss, "Routed"]);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.Is<Song>(song => song.Title == "Routed"))).ReturnsAsync(true);
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        var first = await service.GetAsync(owner, card.Id);
        Assert.Equal(["Alpha", "Routed"], first.Songs.Select(item => item.Song.Title));
        Assert.Single(FeedSearches(), query => query.Contains("Alpha"));
        Assert.DoesNotContain(FeedSearches(), query => query.Contains("Routed"));
        // A song found nowhere costs the first search and two more.
        var missSearches = FeedSearches().Length - 1;
        Assert.Equal(3, missSearches);

        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromHours(7);
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        Assert.Empty(FeedSearches());
        Assert.Equal(["Alpha", "Routed"], (await service.GetAsync(owner, card.Id)).Songs.Select(item => item.Song.Title));

        clock.UtcNow += TimeSpan.FromDays(3);
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        Assert.Equal(missSearches, FeedSearches().Length);
        Assert.DoesNotContain(FeedSearches(), query => query.Contains("Alpha") || query.Contains("Routed"));
        Assert.Equal("Ready", (await service.GetAsync(owner, card.Id)).Readiness.Message);
    }

    [Fact]
    public async Task ServiceFeeds_ServicesThatAreAwayEndThePassAndOneFailingServiceDoesNot()
    {
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var (_, account) = await MatchedFeedAccount(["37i9dQZa", "37i9dQZb", "37i9dQZc", "37i9dQZd"], ["Alpha", "Bravo"], queue);
        var cooling = true;
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((ProtocolExecutionContext _, string query, int _) => cooling ? new([], ["deezer"]) : FeedSearchResult(query));
        await service.ListCardsAsync(owner);
        var handler = new ServiceFeedPreparationJobHandler(service);
        var runs = 0;
        while (await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]) is { } claim)
        {
            var completion = await handler.ExecuteAsync(new(claim, null!), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
            await queue.CompleteAsync(claim, completion);
            runs++;
        }
        // Five songs in a row found nowhere while a service was not answering: three searches each, then no more.
        Assert.Equal(2, runs);
        Assert.Equal(15, FeedSearches().Length);
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        Assert.Equal(15, FeedSearches().Length);
        var cards = (await service.ListCardsAsync(owner)).Where(item => item.Kind == "external").ToArray();
        Assert.All(cards, card => Assert.Equal("No playable songs right now.", card.Readiness.Message));
        await using (var db = factory.CreateDbContext())
            Assert.Equal(5, await db.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "service-feed-match"));

        // The next pass looks for the songs it had not reached; the five it is unsure about wait a day.
        cooling = false;
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromHours(6);
        await service.ListCardsAsync(owner);
        while (await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]) is { } claim)
            await queue.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, null!), default));
        Assert.Equal(3, FeedSearches().Length);
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromHours(24);
        await service.ListCardsAsync(owner);
        while (await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]) is { } claim)
            await queue.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, null!), default));
        Assert.Equal(5, FeedSearches().Length);
        Assert.All((await service.ListCardsAsync(owner)).Where(item => item.Kind == "external"), card => Assert.True(card.Readiness.Ready));

        // One service failing while one ahead of it has the song does not hold anything up.
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer", "tidal"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((ProtocolExecutionContext _, string query, int _) => new(FeedSearchResult(query).Songs, ["tidal"]));
        await using (var db = factory.CreateDbContext())
            await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "service-feed-match").ExecuteDeleteAsync();
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromHours(6);
        await service.ListCardsAsync(owner);
        while (await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]) is { } claim)
            await queue.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, null!), default));
        Assert.Equal(8, FeedSearches().Length);
        Assert.All((await service.ListCardsAsync(owner)).Where(item => item.Kind == "external"), card => Assert.True(card.Readiness.Ready));
    }

    [Fact]
    public async Task ServiceFeeds_AnInterruptedPassContinuesWithoutRepeatingSearches()
    {
        var (_, account) = await MatchedFeedAccount(["37i9dQZfeed"], ["Alpha", "Bravo", "Charlie", "Delta"]);
        using var shutdown = new CancellationTokenSource();
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((ProtocolExecutionContext _, string query, int _) =>
            {
                if (!query.Contains("Charlie") || shutdown.IsCancellationRequested) return Task.FromResult(FeedSearchResult(query));
                shutdown.Cancel();
                throw new OperationCanceledException(shutdown.Token);
            });
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareServiceFeedsAsync(owner, new(account, 0), shutdown.Token));
        Assert.Equal(3, FeedSearches().Length);
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        Assert.Single(FeedSearches(), query => query.Contains("Alpha"));
        Assert.Single(FeedSearches(), query => query.Contains("Bravo"));
        Assert.Equal(5, FeedSearches().Length);
        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta"], (await service.GetAsync(owner, card.Id)).Songs.Select(item => item.Song.Title));
    }

    private async Task<(Mock<IProviderPlaylistCapability> Capability, Guid Account)> MatchedFeedAccount(string[] feeds, string[] titles, DurableJobQueue? queue = null)
    {
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.Is<Song>(song => song.ExternalProvider == "spotify"))).ReturnsAsync(false);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((ProtocolExecutionContext _, string query, int _) => FeedSearchResult(query));
        var search = new TrackMatchPlayableSearch(gateway.Object, new TrackMatchDecisionEngine(), NullLogger<TrackMatchPlayableSearch>.Instance);
        var (capability, account) = await FeedAccount(queue: queue, search: search);
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                feeds.Select(id => new ProviderPlaylistSummary(new("spotify", ProviderResourceKind.Playlist, id), "Mix " + id, new("spotify"), "revision",
                    trackCount: titles.Length)).ToArray())));
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderPlaylistTracksRequest request) => ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                new(request.PlaylistId, "Mix", new("spotify"), "revision", trackCount: titles.Length),
                new("spotify", titles.Select((title, index) =>
                {
                    var track = new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, request.PlaylistId.Value + "-" + index);
                    return new ProviderPlaylistTrack(index, track, metadata: new(track, title, [new("Feed Artist")], albumTitle: title + " Album",
                        duration: TimeSpan.FromSeconds(200), isrc: FeedIsrc(title)));
                }).ToArray()))));
        return (capability, account);
    }

    private static string FeedIsrc(string title) => "USFED26" + title.Sum(letter => letter).ToString("00000");

    // The service has every song except the one named as missing.
    private static MatchingSongSearchResult FeedSearchResult(string query)
    {
        var title = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Routed" }.FirstOrDefault(query.Contains);
        return title == null ? new([], []) : new([new Song
        {
            Id = "ext-deezer-song-" + title.ToLowerInvariant(),
            Title = title,
            Artist = "Feed Artist",
            Artists = ["Feed Artist"],
            Album = title + " Album",
            Duration = 200,
            Isrc = FeedIsrc(title),
            ExternalProvider = "deezer",
            ExternalId = title.ToLowerInvariant(),
            ExplicitContentLyrics = 0
        }], []);
    }

    private string[] FeedSearches() => gateway.Invocations.Where(item => item.Method.Name == nameof(IProtocolProviderGateway.SearchMatchingSongsAsync))
        .Select(item => (string)item.Arguments[1]).ToArray();
}
