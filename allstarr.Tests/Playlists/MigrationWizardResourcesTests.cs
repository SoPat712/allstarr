using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Migrations;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class MigrationWizardFlowTests
{
    private bool libraryAvailable, libraryNeedsReview, libraryCorroborated, libraryFailSecond, libraryNoUpc, libraryMbid;
    private int libraryCount = 1;
    private readonly List<string?> libraryCursors = [];
    private ProviderOutcome<ProviderLibraryPage> LibraryPage(string kind, ProviderPageRequest page)
    {
        if (!libraryAvailable || libraryFailSecond && page.Cursor != null)
            return ProviderOutcome<ProviderLibraryPage>.Failure(new(ProviderErrorKind.CapabilityUnavailable));
        libraryCursors.Add(page.Cursor);
        var offset = page.Cursor == null ? 0 : int.Parse(page.Cursor);
        var items = Enumerable.Range(offset, Math.Min(page.Limit, libraryCount - offset)).Select(index =>
            new ProviderLibraryResource($"source{index}", kind == "saved-albums" ? "Release" : "Artist", kind == "saved-albums" ? ["Artist"] : [],
                [], "2024-01-01", 12, "https://i.scdn.co/image/fixture", libraryNoUpc ? null : "012345678901", false,
                Albums: libraryCorroborated ? ["Release"] : [], MusicBrainzArtistId: libraryMbid ? "6e305db9-291d-414b-bf4a-8f3aedbc7cfa" : null)).ToArray();
        var next = offset + items.Length;
        return ProviderOutcome<ProviderLibraryPage>.Success(new(items, libraryCount, next < libraryCount ? next.ToString() : null));
    }
    private void ResourceCatalog()
    {
        libraryAvailable = true;
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 0, It.IsAny<int>(), It.IsAny<int>(), "deezer"))
            .ReturnsAsync(new SearchResult
            {
                Albums = [new() { Id = "ext-deezer-album-release", Title = "Release" }],
                Artists = [new() { Id = "ext-deezer-artist-artist", Name = "Artist", ImageUrl = "https://example.invalid/artist" }]
            });
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "release"))
            .ReturnsAsync(() => new Album
            {
                Id = "ext-deezer-album-release",
                Title = "Release",
                Artist = "Artist",
                Year = 2024,
                SongCount = libraryNeedsReview ? 16 : 12,
                Upc = "012345678901",
                IsExplicit = false
            });
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "artist"))
            .ReturnsAsync([new() { Title = "Release", Artist = "Artist" }]);
    }

    [Fact]
    public async Task LibraryDiscoveryOffersActualCountsAndRejectsSyncForResourceCollections()
    {
        ResourceCatalog();
        var run = await Start();
        var discovery = JsonSerializer.SerializeToElement(await Service().DiscoverAsync(Context(owner), run, null, default));
        Assert.Empty(discovery.GetProperty("unsupported").EnumerateArray());
        foreach (var kind in new[] { "saved-albums", "followed-artists" })
        {
            var choice = Assert.Single(discovery.GetProperty("items").EnumerateArray(), item => item.GetProperty("kind").GetString() == kind);
            Assert.Equal(1, choice.GetProperty("count").GetInt32());
            await Assert.ThrowsAsync<ArgumentException>(() => Service().ChangeAsync(Context(owner), run, "choose", [new(kind, true)], default));
        }
    }

    [Fact]
    public async Task ResourcePagingResumesFromCheckpointAndVerifiesFullLibrary()
    {
        ResourceCatalog(); libraryCount = 101; libraryFailSecond = true;
        var run = await Selected("saved-albums");
        await Step();
        Assert.Equal(DurableJobCompletionKind.Failed, (await Step()).Kind);
        Assert.Equal(100, (await AssertEquation(run)).Total.Selected);
        libraryFailSecond = false;
        await Service().ChangeAsync(Context(owner), run, "resume", null, default);
        await Finish(run);
        Assert.Equal(101, (await AssertEquation(run)).Total.Accepted);
        Assert.Contains("100", libraryCursors);
        Assert.Equal(101, (await Service().StatusAsync(Context(owner), run, default)).Collections.Single(item => item.SourceId == "saved-albums").Collection.Verified);
    }

    [Theory]
    [InlineData("saved-albums")]
    [InlineData("followed-artists")]
    public async Task ResourceApplyIsMembershipOnlyAndRespectsLaterManualUnheart(string kind)
    {
        ResourceCatalog(); libraryCorroborated = true;
        var run = await Selected(kind);
        await Finish(run);
        Assert.Equal(1, (await AssertEquation(run)).Total.Accepted);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        var heart = Assert.Single(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
        Assert.Equal(kind == "saved-albums" ? ProviderResourceKind.Album : ProviderResourceKind.Artist, heart.Resource.ResourceKind);
        await using (var db = factory.CreateDbContext())
        {
            var intent = Assert.Single(await db.FavoriteEvents.ToArrayAsync());
            Assert.StartsWith("migration:", intent.SourceRevision);
            Assert.All(await db.FavoriteActions.ToArrayAsync(), action => Assert.Equal("virtual-liked", action.ActionType));
            Assert.DoesNotContain(await db.Jobs.ToArrayAsync(), item => item.Type.Contains("keep") || item.Type.Contains("download"));
        }
        var releases = new NewReleasesService(factory, clock, router.Object, new Mock<IDurableRuntimeSettings>().Object);
        var followed = await releases.ListAsync(owner);
        Assert.Equal(kind == "followed-artists" ? 1 : 0, followed.Count);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        await using (var db = factory.CreateDbContext()) Assert.Single(await db.FavoriteEvents.ToArrayAsync());
        if (kind == "followed-artists")
        {
            await using (var db = factory.CreateDbContext()) Assert.Single(await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "release-follows").ToArrayAsync());
            await releases.UnfollowAsync(owner, "deezer", "artist");
            await Service().ChangeAsync(Context(owner), run, "apply", null, default);
            await Finish(run);
            Assert.Empty(await releases.ListAsync(owner));
        }
        await favorites.RecordAsync(new(Context(owner), heart.ItemId, FavoriteOperation.Unfavorite, "manual"));
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.Set<CollectionMigrationCollectionRecord>().SingleAsync(item => item.RunId == run && item.SourceId == kind);
            var collection = JsonSerializer.Deserialize<MigrationCollection>(row.PayloadJson)!;
            collection.Resources![0].Applied = false;
            row.PayloadJson = JsonSerializer.Serialize(collection);
            await db.SaveChangesAsync();
        }
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.Empty(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
        Assert.Empty(await new FavoriteViewReader(factory).ReadAsync(Context(other)));
        reader.Verify(item => item.MutatePlaylistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistMutationRequest>()), Times.Never);
        target.Verify(item => item.WriteAsync(It.IsAny<allstarr.Core.Playlists.Targets.BackendPlaylistTargetContext>(), It.IsAny<allstarr.Core.Playlists.Targets.BackendPlaylistWriteRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DistinctAlbumEditionsWithTheSameTitleStayInReviewWithoutSourceUpc()
    {
        ResourceCatalog(); libraryNoUpc = true;
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 0, 10, 0, "deezer"))
            .ReturnsAsync(new SearchResult { Albums = [new() { Id = "ext-deezer-album-release" }, new() { Id = "ext-deezer-album-other" }] });
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other"))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-other",
                Title = "Release",
                Artist = "Artist",
                Year = 2024,
                SongCount = 12,
                Upc = "012345678902",
                IsExplicit = false
            });
        var run = await Selected("saved-albums");
        await Finish(run);
        var status = await AssertEquation(run);
        Assert.Equal(1, status.Total.Review);
        var collection = status.Collections.Single(item => item.SourceId == "saved-albums");
        var entry = Assert.Single(await Service().ReviewAsync(Context(owner), run, collection.Id, 0, 50, default));
        Assert.Equal(2, entry.Resource!.Candidates.Count);
        Assert.Null(entry.Resource.Chosen);
    }

    [Fact]
    public async Task ArtistIdentityMayBeCorroboratedByMusicBrainzWithoutAlbumEvidence()
    {
        ResourceCatalog(); libraryMbid = true;
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist", 0, 0, 10, "deezer"))
            .ReturnsAsync(new SearchResult
            {
                Artists = [new() { Id = "ext-deezer-artist-artist", Name = "Artist",
                ProviderIds = new() { ["musicbrainz"] = "6e305db9-291d-414b-bf4a-8f3aedbc7cfa" } }]
            });
        var run = await Selected("followed-artists");
        await Finish(run);
        Assert.Equal(1, (await AssertEquation(run)).Total.Accepted);
    }

    [Fact]
    public async Task CapabilityFailureKeepsOnlyTheFailedCategoryUnsupported()
    {
        ResourceCatalog();
        reader.Setup(item => item.GetLibraryResourcesAsync(It.IsAny<ProviderExecutionContext>(), "saved-albums", It.IsAny<ProviderPageRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderLibraryPage>.Failure(new(ProviderErrorKind.PermanentFailure)));
        var run = await Start();
        var discovery = JsonSerializer.SerializeToElement(await Service().DiscoverAsync(Context(owner), run, null, default));
        var unsupported = Assert.Single(discovery.GetProperty("unsupported").EnumerateArray());
        Assert.Equal("saved-albums", unsupported.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, unsupported.GetProperty("count").ValueKind);
        Assert.Contains(discovery.GetProperty("items").EnumerateArray(), item => item.GetProperty("kind").GetString() == "followed-artists");
    }

    [Fact]
    public async Task TemporaryLibraryFailureDoesNotAdvertiseMissingCapability()
    {
        ResourceCatalog();
        reader.Setup(item => item.GetLibraryResourcesAsync(It.IsAny<ProviderExecutionContext>(), "saved-albums", It.IsAny<ProviderPageRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderLibraryPage>.Failure(new(ProviderErrorKind.TransientFailure)));
        var run = await Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DiscoverAsync(Context(owner), run, null, default));
    }

    [Fact]
    public async Task RepeatedSourceArtistIdentityFollowsForReleasesOnlyOnce()
    {
        ResourceCatalog(); libraryCorroborated = true; libraryCount = 2;
        var run = await Selected("followed-artists");
        await Finish(run);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "release-follows").ToArrayAsync());
        Assert.Single(await db.FavoriteEvents.ToArrayAsync());
    }

    [Fact]
    public async Task NoResourceCandidatesRemainUnmatchedAndCanBeSkipped()
    {
        ResourceCatalog();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 0, It.IsAny<int>(), It.IsAny<int>(), "deezer"))
            .ReturnsAsync(new SearchResult());
        var run = await Selected("saved-albums");
        await Finish(run);
        var status = await AssertEquation(run);
        Assert.Equal(1, status.Total.Unmatched);
        var collection = status.Collections.Single(item => item.SourceId == "saved-albums");
        var entry = Assert.Single(await Service().ReviewAsync(Context(owner), run, collection.Id, 0, 50, default));
        Assert.Empty(entry.Resource!.Candidates);
        await Service().ChooseResourceAsync(Context(owner), run, collection.Id, entry.SourceEntryId, null, null, true, default);
        Assert.Equal(1, (await AssertEquation(run)).Total.Skipped);
    }

    [Theory]
    [InlineData("saved-albums")]
    [InlineData("followed-artists")]
    public async Task ResourceReviewKeepsCandidatesAndOnlyTheListenerMayChooseOrSkip(string kind)
    {
        ResourceCatalog(); libraryNeedsReview = true;
        var run = await Selected(kind);
        await Finish(run);
        var status = await AssertEquation(run);
        Assert.Equal(1, status.Total.Review);
        var collection = status.Collections.Single(item => item.SourceId == kind);
        var review = Assert.Single(await Service().ReviewAsync(Context(owner), run, collection.Id, 0, 100, default));
        var candidate = Assert.Single(review.Resource!.Candidates);
        Assert.Equal("deezer", candidate.ProviderId);
        if (kind == "saved-albums")
        {
            Assert.Equal(16, candidate.TrackCount);
            Assert.EndsWith("(Clean)", candidate.Title);
        }
        else Assert.Equal("Release", Assert.Single(candidate.Albums));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().ChooseResourceAsync(Context(other), run, collection.Id, review.SourceEntryId, "deezer", candidate.ResourceId, false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().ChooseResourceAsync(Context(owner), run, collection.Id, review.SourceEntryId, "deezer", "not-listed", false, default));
        await Service().ChooseResourceAsync(Context(owner), run, collection.Id, review.SourceEntryId, null, null, true, default);
        Assert.Equal(1, (await AssertEquation(run)).Total.Skipped);
        await Service().ChooseResourceAsync(Context(owner), run, collection.Id, review.SourceEntryId, "deezer", candidate.ResourceId, false, default);
        Assert.Equal(1, (await AssertEquation(run)).Total.Accepted);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.Single(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
    }
}
