using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task ServiceFeeds_PrepareThreeAtATimeWithoutCreatingLinksAndContinueOnSixHourCadence()
    {
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var (capability, account) = await FeedAccount(queue: queue);
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                Enumerable.Range(0, 4).Select(index => new ProviderPlaylistSummary(new("spotify", ProviderResourceKind.Playlist, "37i9dQZ" + index),
                    "Service mix " + index, new("spotify"), "revision")))));
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .Returns((ProviderExecutionContext _, ProviderPlaylistTracksRequest request) => Task.FromResult(request.PlaylistId.Value.EndsWith('0')
                ? ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.NotFound))
                : ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                new(request.PlaylistId, "Service mix", new("spotify"), "revision"),
                new("spotify", [new(0, new("spotify", ProviderResourceKind.Track, "song"), metadata: new(new("spotify", ProviderResourceKind.Track, "song"), "A song", [new("Artist")]))])))));
        await service.ListCardsAsync(owner);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.PlaylistLinks.ToArrayAsync());
        var first = await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]);
        Assert.NotNull(first);
        var handler = new ServiceFeedPreparationJobHandler(service);
        await queue.CompleteAsync(first, await handler.ExecuteAsync(new(first, null!), default));
        capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Exactly(3));
        Assert.Equal(2, (await service.ListCardsAsync(owner)).Count(item => item.Kind == "external" && item.Readiness.Ready));
        Assert.StartsWith("Your service playlist is unavailable", (await service.GetAsync(owner,
            (await service.ListCardsAsync(owner)).Single(item => item.SourcePlaylistId == "37i9dQZ0").Id)).Readiness.Message);
        var second = await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]);
        Assert.NotNull(second);
        await queue.CompleteAsync(second, await handler.ExecuteAsync(new(second, null!), default));
        Assert.Equal(3, (await service.ListCardsAsync(owner)).Count(item => item.Kind == "external" && item.Readiness.Ready));
        Assert.Empty(await db.PlaylistLinks.ToArrayAsync());
        Assert.Null(await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]));
        clock.UtcNow += TimeSpan.FromHours(6);
        await service.ListCardsAsync(owner);
        Assert.NotNull(await queue.ClaimNextAsync("feed-fixture", [PersonalMixService.FeedPrepareJobType]));
    }

    [Fact]
    public async Task ServiceFeeds_UsePlaylistMatchingForLocalCopiesAndReadPreparedCounts()
    {
        var (capability, account) = await FeedAccount();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        await using (var db = factory.CreateDbContext())
        {
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "music",
                BackendItemId = "local-song",
                Title = "A song",
                Artist = "Artist",
                Album = "Album",
                Isrc = "USAAA2600001",
                DurationMilliseconds = 180000
            });
            await db.SaveChangesAsync();
        }
        var resource = new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, "song");
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), "Discover Weekly", new("spotify"), "revision"),
                new("spotify", [new(0, resource, metadata: new(resource, "A song", [new("Artist")], albumTitle: "Album", duration: TimeSpan.FromSeconds(180), isrc: "USAAA2600001"))]))));
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        var prepared = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        Assert.Equal(1, prepared.Evidence!.FromMusic);
        var detail = await service.GetAsync(owner, card.Id);
        Assert.Equal("local-song", Assert.Single(detail.Songs).Song.Id);
        Assert.DoesNotContain(await service.ListCardsAsync(other), item => item.Id == card.Id);
        capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Once);
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<allstarr.Models.Domain.Song>()), Times.Never);
    }

    [Fact]
    public async Task ServiceFeeds_PreparePlayableRecordingsAndOpenWithoutProviderChecks()
    {
        var identities = new Mock<ITrackIdentityService>();
        var recording = Guid.NewGuid();
        identities.Setup(item => item.RememberSourceAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderExternalResourceId>(),
            It.IsAny<string>(), It.IsAny<ProviderIdentityScope>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(recording);
        var (capability, account) = await FeedAccount(identities: identities.Object);
        var track = new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, "song");
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), "Discover Weekly", new("spotify"), "revision"),
                new("spotify", [new(0, track, metadata: new(track, "A song", [new("Artist")]))]))));
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        var prepared = await service.GetAsync(owner, card.Id);
        var song = Assert.Single(prepared.Songs).Song;
        Assert.Equal("ext-allstarr-song-" + recording.ToString("D"), song.Id);
        Assert.Equal("allstarr", song.ExternalProvider);
        Assert.Equal(1, prepared.Evidence!.FromServices);
        await service.GetAsync(owner, card.Id);
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<allstarr.Models.Domain.Song>()), Times.Once);
        capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Once);
    }

    [Fact]
    public async Task ServiceFeeds_KeepOtherListenersServicePlaylistsWithAttributionAndExcludeUserPlaylists()
    {
        var (capability, _) = await FeedAccount();
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).DisplayName = "Josh Patra";
            await db.SaveChangesAsync();
        }
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify", [
                new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZother"), "Riju Patra's Top Songs 2024", new("spotify"), "revision"),
                new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZowner"), "Classical Essentials", new("other-user", "Riju Patra"), "revision"),
                new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZown"), "Josh Patra's Top Songs 2024", new("spotify"), "revision"),
                new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZblend"), "Rebecca + Josh", new("spotify"), "revision"),
                new(new("spotify", ProviderResourceKind.Playlist, "my-list"), "My songs", new("selected-user"), "revision")])));
        var cards = (await service.ListCardsAsync(owner)).Where(item => item.Kind == "external").ToArray();
        Assert.Equal(4, cards.Length);
        Assert.Equal("Riju Patra", cards.Single(item => item.SourcePlaylistId == "37i9dQZother").SavedFrom);
        Assert.Equal("Riju Patra", cards.Single(item => item.SourcePlaylistId == "37i9dQZowner").SavedFrom);
        Assert.All(cards.Where(item => item.SourcePlaylistId is "37i9dQZown" or "37i9dQZblend"), item => Assert.Null(item.SavedFrom));
    }
}
