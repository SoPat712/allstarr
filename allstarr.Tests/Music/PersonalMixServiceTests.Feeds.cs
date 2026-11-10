using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task SpotifyHomeFeedsUseTheExistingSixHourSnapshotAndPreparationQueue()
    {
        var (_, account) = await FeedAccount();
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.ProviderAccounts.SingleAsync(item => item.Id == account);
            row.SecretReferenceId = Guid.NewGuid();
            db.SecretReferences.Add(new() { Id = row.SecretReferenceId.Value, UserId = owner, Purpose = $"provider-account:spotify:{account:N}", ActiveVersion = 1 });
            await db.SaveChangesAsync();
        }
        var handler = new SpotifyHomeFixtureHandler();
        using var http = new HttpClient(handler);
        IProviderPlaylistCapability? adapter = new SpotifyPlaylistCapabilityAdapter(http, new HomeFeedSecrets());
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.TryGetCapability("spotify", ProviderCapabilityKind.Playlist, out adapter)).Returns(true);
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        service = new(factory, clock, tags, taste, new(gateway.Object, timeProvider: clock), gateway.Object,
            NewReleaseService(), settings.Object, policies.Object, access.Object, registry: registry.Object, jobQueue: queue);
        var cards = (await service.ListCardsAsync(owner)).Where(item => item.Kind == "external").ToArray();
        Assert.Equal(11, cards.Length);
        Assert.All(cards, card =>
        {
            Assert.Equal("yourServices", card.Section);
            Assert.Equal(account, card.ProviderAccountId);
            Assert.Empty(card.Songs);
            Assert.False(card.Added);
        });
        clock.UtcNow = clock.UtcNow.AddHours(6).AddTicks(-1);
        await service.ListCardsAsync(owner);
        Assert.Equal(1, handler.HomeRequests);
        Assert.Equal(1, handler.LibraryRequests);
        clock.UtcNow = clock.UtcNow.AddTicks(1);
        await service.ListCardsAsync(owner);
        Assert.Equal(1, handler.HomeRequests);
        Assert.Equal(1, handler.LibraryRequests);
        await service.PrepareServiceFeedsAsync(owner, new(account, cards.Length), default);
        Assert.Equal(2, handler.HomeRequests);
        Assert.Equal(2, handler.LibraryRequests);
        await using var read = factory.CreateDbContext();
        Assert.Empty(await read.PlaylistLinks.ToArrayAsync());
        Assert.Contains(await read.Jobs.ToArrayAsync(), item => item.Type == PersonalMixService.FeedPrepareJobType);
    }

    private sealed class HomeFeedSecrets : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken) =>
            operation(System.Text.Encoding.UTF8.GetBytes("fixture-cookie"));
    }

    private async Task<(Mock<IProviderPlaylistCapability> Capability, Guid Account)> FeedAccount(string provider = "spotify", Guid? listener = null,
        ILibraryIndexService? local = null, ITrackIdentityService? identities = null, IPlaylistPersistenceService? store = null, DurableJobQueue? queue = null, MixCoverImageService? covers = null)
    {
        var account = new ProviderAccountRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = listener ?? owner,
            ProviderId = provider,
            Enabled = true,
            Revision = 1,
            CreatedAt = clock.UtcNow
        };
        await using var db = factory.CreateDbContext();
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var capability = new Mock<IProviderPlaylistCapability>();
        capability.SetupGet(item => item.ProviderId).Returns(provider);
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(provider,
                [new(new(provider, ProviderResourceKind.Playlist, "37i9dQZweekly"), "Discover Weekly", new("spotify"), "revision", artwork: new(new(provider, ProviderResourceKind.Playlist, "37i9dQZweekly"), revision: "cover-revision"), trackCount: 30),
                 new(new(provider, ProviderResourceKind.Playlist, "own-list"), "My songs", new("selected-user"), "revision")])));
        var registry = new Mock<IProviderRegistry>();
        IProviderPlaylistCapability? implementation = capability.Object;
        registry.Setup(item => item.TryGetCapability(provider, ProviderCapabilityKind.Playlist, out implementation)).Returns(true);
        registry.Setup(item => item.GetRequiredCapability<IProviderPlaylistCapability>(provider, ProviderCapabilityKind.Playlist)).Returns(capability.Object);
        service = new(factory, clock, tags, taste, new(gateway.Object, timeProvider: clock), gateway.Object,
            NewReleaseService(), settings.Object, policies.Object, access.Object, registry: registry.Object, libraryIndex: local, identities: identities, playlistStore: store, jobQueue: queue, coverImages: covers);
        return (capability, account.Id);
    }

    [Fact]
    public async Task ServiceFeeds_AppearWithoutCreatingPlaylistLinksAndCacheForSixHours()
    {
        var (capability, account) = await FeedAccount();
        var first = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        Assert.Equal("Discover Weekly", first.Name);
        Assert.Equal("spotify", first.Source);
        Assert.Equal("37i9dQZweekly", first.SourcePlaylistId);
        Assert.Equal(30, first.SongCount);
        Assert.Equal($"/api/admin/playlist-sources/{account}/playlists/37i9dQZweekly/artwork?revision=cover-revision", first.ArtworkUrl);
        var input = MixCoverInput.From(first);
        Assert.Equal(first.ArtworkUrl, input.ServiceArtwork);
        Assert.Empty(input.Art!);
        Assert.False(first.Added);
        Assert.Equal("yourServices", first.Section);
        clock.UtcNow = clock.UtcNow.AddHours(6).AddTicks(-1);
        Assert.Equal(first.Id, Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external").Id);
        capability.Verify(item => item.GetUserPlaylistsAsync(It.Is<ProviderExecutionContext>(context => context.Account!.AccountId == account),
            It.IsAny<ProviderUserPlaylistsRequest>()), Times.Once);
        clock.UtcNow = clock.UtcNow.AddTicks(1);
        await service.ListCardsAsync(owner);
        capability.Verify(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()), Times.Exactly(2));
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.PlaylistLinks.ToArrayAsync());
        Assert.Empty(await db.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task ServiceFeeds_RefreshOlderCoverSnapshotsWithoutLosingCardsOnFailure()
    {
        var (capability, _) = await FeedAccount();
        var first = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        await using (var db = factory.CreateDbContext())
        {
            var saved = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "service-feeds");
            saved.Id = Guid.NewGuid();
            saved.SnapshotVersion++;
            saved.ProviderRevision = "1";
            db.ExternalMetadataSnapshots.Add(saved);
            await db.SaveChangesAsync();
        }
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.TransientFailure)));
        var retained = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        Assert.Equal(first.Id, retained.Id);
        Assert.Equal(first.ArtworkUrl, retained.ArtworkUrl);
        Assert.Equal(first.SongCount, retained.SongCount);
        await service.ListCardsAsync(owner);
        capability.Verify(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceFeeds_KeepOneCardForASyncedSourceAndRespectAccountOwnership(bool publicSource)
    {
        var (_, account) = await FeedAccount();
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid user, CancellationToken _) => Task.FromResult(new BackendLibraryAccessContext(Context(user), new(true, []))));
        var link = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = publicSource ? null : account,
            SourceProviderId = "spotify",
            SourcePlaylistId = "37i9dQZweekly",
            SourcePlaylistIdHash = new string('a', 64),
            TargetProtocol = protocol,
            TargetBackendInstanceId = "main",
            Mode = PlaylistLinkMode.Virtual,
            RuleVersion = "1",
            PolicyVersion = "1",
            CreatedAt = clock.UtcNow
        };
        await using (var db = factory.CreateDbContext())
        {
            db.PlaylistLinks.Add(link);
            db.PlaylistSourceSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                PlaylistLinkId = link.Id,
                ProviderAccountId = link.ProviderAccountId,
                SnapshotVersion = 1,
                Name = "Discover Weekly",
                ProviderRevision = "1",
                PayloadSha256 = new string('a', 64),
                CorrelationId = "fixture",
                RetrievedAt = clock.UtcNow,
                PublishedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        Assert.Equal(link.Id, card.Id);
        Assert.True(card.Added);
        Assert.Equal($"/api/admin/playlist-sources/{account}/playlists/37i9dQZweekly/artwork?revision=cover-revision", card.ArtworkUrl);
        Assert.Equal(30, card.SongCount);
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await Controller(owner, true).List());
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(response.Value).EnumerateArray().Single(item => item.GetProperty("Id").GetGuid() == card.Id);
        Assert.Equal($"/api/admin/mixes/{card.Id}/cover?v={MixCoverImageService.Version(MixCoverInput.From(card))}",
            Assert.Single(payload.GetProperty("coverArtwork").EnumerateArray()).GetString());
        Assert.DoesNotContain(await service.ListCardsAsync(other), item => item.Kind == "external");
        await using (var db = factory.CreateDbContext())
        {
            (await db.ProviderAccounts.SingleAsync(item => item.Id == account)).Enabled = false;
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain(await service.ListCardsAsync(owner), item => item.Kind == "external");
    }

    [Fact]
    public async Task ServiceFeeds_KeepTheSyncedSourceCoverWhenDiscoveryHasNoCoverOrCount()
    {
        var (capability, account) = await FeedAccount();
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                [new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), "Discover Weekly", new("spotify"), "revision")])));
        var unsynced = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        Assert.Null(unsynced.SongCount);
        var link = await ExternalLink("spotify", "37i9dQZweekly", artwork: "stored-cover");
        await using (var db = factory.CreateDbContext())
        {
            (await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id)).ProviderAccountId = account;
            var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking().SingleAsync(item => item.PlaylistLinkId == link.Id);
            snapshot.Id = Guid.NewGuid();
            snapshot.SnapshotVersion++;
            snapshot.ProviderAccountId = account;
            db.PlaylistSourceSnapshots.Add(snapshot);
            (await db.ProviderAccounts.SingleAsync(item => item.Id == link.ProviderAccountId)).Enabled = false;
            await db.SaveChangesAsync();
        }
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        Assert.Equal(link.Id, card.Id);
        Assert.Equal($"/api/admin/playlist-sources/{account}/playlists/37i9dQZweekly/artwork?revision=stored-cover", card.ArtworkUrl);
        Assert.Equal(card.ArtworkUrl, MixCoverInput.From(card).ServiceArtwork);
        Assert.Null(card.SongCount);
    }

    [Fact]
    public async Task ServiceFeeds_DoNotTurnOneTimeImportsIntoSyncingLinks()
    {
        await FeedAccount();
        var imported = await ExternalLink("spotify", "37i9dQZweekly", schedule: false);
        await using (var db = factory.CreateDbContext())
        {
            (await db.PlaylistLinks.SingleAsync(item => item.Id == imported.Id)).ImportMode = PlaylistImportMode.OneTime;
            await db.SaveChangesAsync();
        }
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        Assert.NotEqual(imported.Id, card.Id);
        Assert.False(card.Added);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetInAppsAsync(owner, imported.Id, true, default));
        await using var read = factory.CreateDbContext();
        Assert.Empty(await read.JobSchedules.ToArrayAsync());
        Assert.Equal(PlaylistImportMode.OneTime, (await read.PlaylistLinks.SingleAsync()).ImportMode);
    }

    [Fact]
    public async Task ServiceFeeds_UseEachAccountsCacheButShowASharedChartOnce()
    {
        await FeedAccount();
        var first = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var (capability, account) = await FeedAccount();
        Assert.Equal(first.Id, Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external").Id);
        capability.Verify(item => item.GetUserPlaylistsAsync(It.Is<ProviderExecutionContext>(context => context.Account!.AccountId == account),
            It.IsAny<ProviderUserPlaylistsRequest>()), Times.Once);
        capability.Verify(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()), Times.Once);
    }

    [Fact]
    public async Task ServiceFeeds_OpenPreparedSongsWithoutReadingTheService()
    {
        var (capability, account) = await FeedAccount();
        var resource = new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, "song");
        var metadata = new ProviderTrackMetadata(resource, "A song", [new("An artist")], albumTitle: "An album");
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), "Discover Weekly", new("spotify"), "revision", trackCount: 1),
                new("spotify", [new(0, resource, metadata: metadata)]))));
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        var pending = await service.GetAsync(owner, card.Id);
        Assert.Empty(pending.Songs);
        Assert.StartsWith("Preparing", pending.Readiness.Message);
        capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Never);
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        var detail = await service.GetAsync(owner, card.Id);
        Assert.Equal("A song", Assert.Single(detail.Songs).Song.Title);
        Assert.False(detail.Added);
        Assert.Equal(1, detail.SongCount);
        await service.GetAsync(owner, card.Id);
        capability.Verify(item => item.GetPlaylistTracksAsync(It.Is<ProviderExecutionContext>(context => context.Account!.AccountId == account),
            It.IsAny<ProviderPlaylistTracksRequest>()), Times.Once);
        clock.UtcNow = clock.UtcNow.AddHours(6);
        await service.GetAsync(owner, card.Id);
        capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Once);
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ServiceFeeds_PreferTheExistingLocalRecordingBeforeCheckingStreaming()
    {
        var local = new Mock<ILibraryIndexService>();
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(item => item.ResolveRecordingsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, CanonicalRecordingIdentity?>());
        local.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["ext-spotify-song-song"] = "local-song" });
        var (capability, account) = await FeedAccount(local: local.Object, identities: identities.Object);
        var resource = new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, "song");
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), "Discover Weekly", new("spotify"), "revision"),
                new("spotify", [new(0, resource, metadata: new(resource, "A song", [new("Artist")]))]))));
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        await using (var db = factory.CreateDbContext())
        {
            db.LibraryTracks.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, Protocol = protocol, BackendInstanceId = "main", BackendLibraryId = "music", BackendItemId = "local-song" });
            await db.SaveChangesAsync();
        }
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        await service.PrepareServiceFeedsAsync(owner, new(account, 0), default);
        var song = Assert.Single((await service.GetAsync(owner, card.Id)).Songs).Song;
        Assert.True(song.IsLocal);
        Assert.Equal("local-song", song.Id);
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Never);
    }

    [Fact]
    public async Task ServiceFeeds_AddCreatesSyncWorkOnlyAfterTheListenerChoosesIt()
    {
        var store = new Mock<IPlaylistPersistenceService>();
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var (_, account) = await FeedAccount(store: store.Object, queue: queue);
        store.Setup(item => item.CreateLinkAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<PlaylistLinkInput>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, PlaylistLinkInput input, CancellationToken token) =>
            {
                Assert.Equal(account, input.ProviderAccountId);
                Assert.Equal(PlaylistLinkMode.Virtual, input.Mode);
                Assert.Null(input.TargetPlaylistId);
                var link = new PlaylistLinkRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ProviderAccountId = account,
                    SourceProviderId = input.SourceProviderId,
                    SourcePlaylistId = input.SourcePlaylistId,
                    SourcePlaylistIdHash = input.SourcePlaylistIdHash,
                    TargetProtocol = protocol,
                    TargetBackendInstanceId = "main",
                    Mode = PlaylistLinkMode.Virtual,
                    RuleVersion = "1",
                    PolicyVersion = "1",
                    Enabled = false,
                    CreatedAt = clock.UtcNow
                };
                await using var db = factory.CreateDbContext();
                db.PlaylistLinks.Add(link);
                await db.SaveChangesAsync(token);
                return link;
            });
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        store.Verify(item => item.CreateLinkAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<PlaylistLinkInput>(), It.IsAny<CancellationToken>()), Times.Never);
        await service.SetInAppsAsync(owner, card.Id, true, default);
        await using var read = factory.CreateDbContext();
        var link = await read.PlaylistLinks.SingleAsync();
        Assert.True(link.Enabled);
        Assert.True((await read.JobSchedules.SingleAsync()).Enabled);
        Assert.Equal("playlist.materialize", (await read.Jobs.SingleAsync(item => item.Type == "playlist.materialize")).Type);
        await service.SetInAppsAsync(owner, card.Id, false, default);
        await read.Entry(link).ReloadAsync();
        Assert.False(link.Enabled);
        Assert.Equal(1, await read.Jobs.CountAsync(item => item.Type == "playlist.materialize"));
    }

    [Fact]
    public async Task ServiceFeeds_FailedRefreshKeepsTheLastListWithoutRetryingInsideTheWindow()
    {
        var (capability, _) = await FeedAccount();
        var first = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.TransientFailure)));
        clock.UtcNow = clock.UtcNow.AddHours(6);
        Assert.Equal(first.Id, Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external").Id);
        await service.ListCardsAsync(owner);
        capability.Verify(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()), Times.Exactly(2));
    }
}
