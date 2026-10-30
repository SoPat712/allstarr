using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Migrations;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class MigrationWizardFlowTests : IAsyncLifetime
{
    private readonly Guid owner = Guid.NewGuid(), other = Guid.NewGuid(), account = Guid.NewGuid();
    private readonly SystemPlatformClock clock = new();
    private readonly Mock<IProviderPlaylistCapability> reader = new(MockBehavior.Strict);
    private readonly Mock<IProviderRouter> router = new(MockBehavior.Strict);
    private readonly Mock<IProtocolProviderGateway> gateway = new(MockBehavior.Strict);
    private readonly Mock<IBackendPlaylistTarget> target = new(MockBehavior.Strict);
    private readonly Mock<IBackendPlaylistTargetResolver> targets = new(MockBehavior.Strict);
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private DurableJobQueue queue = null!;
    private TrackMatchCommandService matches = null!;
    private PlaylistPersistenceService playlists = null!;
    private PlaylistOrchestrationService orchestration = null!;
    private TrackIdentityService identities = null!;
    private FavoriteActionPipeline favorites = null!;
    private int count = 2;
    private long revision = 1;
    private string sourceUser = "source-listener";
    private bool expired, failSecondPage, loseCreate, needsReview, unsupportedLast;
    private int creates, finds, nativeHearts;
    private BackendPlaylistSnapshot? destination;
    private DateTimeOffset? destinationCreatedAt;
    private readonly List<string?> cursors = [];

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        await using (var db = factory.CreateDbContext())
        {
            db.Users.AddRange(User(owner), User(other));
            db.ProviderAccounts.Add(new()
            {
                Id = account,
                OwnerUserId = owner,
                ProviderId = "spotify",
                Enabled = true,
                Revision = revision,
                DisplayName = "Spotify"
            });
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                BackendLibraryId = "music",
                BackendItemId = "local-one",
                FilePath = "/music/local.flac",
                Title = "Local song",
                Artist = "Artist",
                Album = "Album",
                DurationMilliseconds = 180000,
                Isrc = "USAAA2600001"
            });
            await db.SaveChangesAsync();
        }
        var options = new DurableJobOptions();
        queue = new(factory, options, new(options), clock);
        favorites = new(factory, queue, clock);
        var storage = new DurableStorageState(new());
        storage.Set(DurableStorageReadiness.Ready);
        identities = new(factory, storage, clock);
        var access = new TestBackendLibraryAccess(factory, "music");
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync((ProtocolExecutionContext _, string query, int _) => new MatchingSongSearchResult(
                query.StartsWith("Stream song", StringComparison.Ordinal)
                    ? [new Song { Id = "ext-deezer-song-stream", ExternalProvider = "deezer", ExternalId = "stream",
                        Title = "Stream song", Artist = "Artist", Album = "Album", Duration = needsReview ? 220 : 180 }] : [], []));
        matches = new(factory, new(), new(factory), clock, access,
            new(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance));
        playlists = new(factory, new(factory), clock, matches, access, providerGateway: gateway.Object);
        targets.Setup(item => item.Resolve("jellyfin")).Returns(target.Object);
        target.Setup(item => item.FavoriteAsync(It.IsAny<BackendPlaylistTargetContext>(), "local-one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { nativeHearts++; return true; });
        target.SetupGet(item => item.Capabilities).Returns(new BackendPlaylistTargetCapabilities(true, true, true, true, true, true, false, false, false));
        target.Setup(item => item.ListPageAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<string>(), It.IsAny<int>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackendPlaylistTargetContext _, string name, int _, int _, CancellationToken _) =>
            {
                finds++;
                return new(BackendPlaylistTargetStatus.Success, destination?.Name == name
                    ? [new(destination.BackendPlaylistId, destination.Name, destination.Members.Count, CreatedAt: destinationCreatedAt)] : []);
            });
        target.Setup(item => item.ReadAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackendPlaylistTargetContext _, string _, CancellationToken _) => new(BackendPlaylistTargetStatus.Success, destination!));
        target.Setup(item => item.WriteAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<BackendPlaylistWriteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackendPlaylistTargetContext _, BackendPlaylistWriteRequest request, CancellationToken _) =>
            {
                if (request.BackendPlaylistId == null) { creates++; destinationCreatedAt = clock.UtcNow; }
                var members = request.OrderedBackendItemIds.Select(item => new BackendPlaylistMember(item, item, 180000)).ToArray();
                destination = new(request.BackendPlaylistId ?? "destination", request.Metadata.Name, members,
                    BackendPlaylistSnapshot.ComputeFingerprint("destination", request.Metadata.Name, members), Description: request.Metadata.Description,
                    ReportedTrackCount: members.Length, DurationMilliseconds: members.LongLength * 180000);
                if (loseCreate) { loseCreate = false; throw new HttpRequestException("Lost response"); }
                return new(BackendPlaylistTargetStatus.Success, new BackendPlaylistWriteReceipt(destination, true, []));
            });
        orchestration = new(factory, Mock.Of<IProviderPlaylistSourceGateway>(MockBehavior.Strict), targets.Object, new(), new(), matches,
            clock, new(), access, providerGateway: gateway.Object, jobs: queue);
        reader.Setup(item => item.GetSourceUserAsync(It.IsAny<ProviderExecutionContext>())).ReturnsAsync(() =>
            expired ? ProviderOutcome<string>.Failure(new(ProviderErrorKind.Unauthorized)) : ProviderOutcome<string>.Success(sourceUser));
        reader.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify", [Summary("owned"), Summary("followed")])));
        reader.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderPlaylistTracksRequest request) => Page(request));
        reader.SetupGet(item => item.ProviderId).Returns("spotify");
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new(request,
                [new(0, null!, null!, reader.Object, new(request.Actor, "spotify", new(account, "spotify", ProviderAccountScope.Personal, revision,
                    ownerUserId: owner), request.Policy, "migration", request.CorrelationId, request.Deadline, request.CancellationToken), null)],
                new(request.CorrelationId, ProviderCapabilityKind.Playlist, "spotify", account, [])));
    }

    [Fact]
    public async Task DiscoveryListsOwnedFollowedAndLikesAndNeverInventsUnsupportedCounts()
    {
        var run = await Start();
        var discovery = JsonSerializer.SerializeToElement(await Service().DiscoverAsync(Context(owner), run, null, default));
        Assert.Equal(3, discovery.GetProperty("items").GetArrayLength());
        Assert.All(discovery.GetProperty("unsupported").EnumerateArray(), item =>
        {
            Assert.Equal("not-supported-yet", item.GetProperty("support").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("count").ValueKind);
        });
        Assert.Equal(run, await Service().StartAsync(Context(owner), account, "request", default));
        reader.Verify(item => item.MutatePlaylistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistMutationRequest>()), Times.Never);
    }

    [Fact]
    public async Task PagedCollectionRestartsAtSavedCursorAndReconcilesEveryState()
    {
        count = 101;
        failSecondPage = true;
        var run = await Selected("liked-songs");
        Assert.Equal(DurableJobCompletionKind.Deferred, (await Step()).Kind);
        var status = await AssertEquation(run);
        Assert.Equal(100, status.Collections.Single(item => item.SourceId == "liked-songs").Collection.Read);
        Assert.Equal(DurableJobCompletionKind.Failed, (await Step()).Kind);
        await AssertEquation(run);
        failSecondPage = false;
        await Service().ChangeAsync(Context(owner), run, "resume", null, default);
        await Finish(run);
        Assert.Equal(new string?[] { null, "100", "100", null, "100" }, cursors.Where(item => item != "discovery").ToArray());
        Assert.Equal(101, (await AssertEquation(run)).Total.Selected);
        reader.Verify(item => item.MutatePlaylistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistMutationRequest>()), Times.Never);
    }

    [Fact]
    public async Task InterruptedPageDoesNotCommitAnIncompleteEntryCheckpoint()
    {
        var run = await Selected("liked-songs");
        var calls = 0;
        var interrupted = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        interrupted.Setup(item => item.RememberSourceAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderExternalResourceId>(),
                It.IsAny<string>(), It.IsAny<ProviderIdentityScope>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((ProviderExecutionContext context, ProviderExternalResourceId resource, string publicId, ProviderIdentityScope scope, string? isrc, CancellationToken token) =>
                ++calls == 2 ? throw new HttpRequestException("Interrupted page") : identities.RememberSourceAsync(context, resource, publicId, scope, isrc, token));
        Assert.Equal(DurableJobCompletionKind.Failed, (await Step(interrupted.Object)).Kind);
        await using (var partial = factory.CreateDbContext()) Assert.Empty(await partial.PlaylistSourceEntries.ToArrayAsync());
        await Service().ChangeAsync(Context(owner), run, "resume", null, default);
        await Finish(run);
        Assert.Equal(2, (await AssertEquation(run)).Total.Selected);
    }

    [Fact]
    public async Task ExpiredLoginMayResumeOnlyForSameSourceUserAndCancellationKeepsPages()
    {
        var run = await Selected("liked-songs");
        expired = true;
        Assert.Equal(DurableJobCompletionKind.Failed, (await Step()).Kind);
        await AssertEquation(run);
        expired = false;
        sourceUser = "different-source-listener";
        revision++;
        await using (var db = factory.CreateDbContext()) { (await db.ProviderAccounts.SingleAsync()).Revision = revision; await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().ChangeAsync(Context(owner), run, "resume", null, default));
        sourceUser = "source-listener";
        await Service().ChangeAsync(Context(owner), run, "resume", null, default);
        await Step();
        await Service().ChangeAsync(Context(owner), run, "cancel", null, default);
        Assert.Equal(DurableJobCompletionKind.Cancelled, (await Step()).Kind);
        Assert.Equal(2, (await AssertEquation(run)).Total.Selected);
        await Service().ChangeAsync(Context(owner), run, "resume", null, default);
        await Finish(run);
    }

    [Fact]
    public async Task LikesApplyAsMembershipOnlyAndLaterUnheartSurvivesRepeatApply()
    {
        var run = await Selected("liked-songs");
        await Finish(run);
        var summary = await AssertEquation(run);
        Assert.Equal("review", summary.Run.State);
        Assert.Equal(2, summary.Total.Accepted);
        Assert.Equal(1, summary.Total.Local);
        Assert.Equal(1, summary.Total.Streamed);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.Single(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
        Assert.Equal(1, nativeHearts);
        await using (var hearts = factory.CreateDbContext()) Assert.Equal(2, await hearts.FavoriteStates.CountAsync(item => item.IsFavorite));
        await favorites.RecordAsync(new(Context(owner), "ext-deezer-song-stream", FavoriteOperation.Unfavorite, "manual"));
        await using (var gap = factory.CreateDbContext())
            await gap.PlaylistSyncEntryResults.Where(item => item.LibraryTrackId == null).ExecuteDeleteAsync();
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        await using var verify = factory.CreateDbContext();
        Assert.False((await verify.FavoriteStates.SingleAsync(item => item.ItemId == "ext-deezer-song-stream")).IsFavorite);
        Assert.All(await verify.FavoriteActions.ToArrayAsync(), item => Assert.Equal("virtual-liked", item.ActionType));
        Assert.DoesNotContain(await verify.Jobs.ToArrayAsync(), item => item.Type.Contains("keep") || item.Type.Contains("download"));
        Assert.Equal(2, await verify.PlaylistSyncEntryResults.CountAsync());
        Assert.Contains(await verify.PlaylistSyncEntryResults.ToArrayAsync(), item => item.Outcome == PlaylistEntryOutcome.Skipped);
        target.Verify(item => item.WriteAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<BackendPlaylistWriteRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreationIntentCanResumeWhenTheBackendWriteWasNeverClaimed()
    {
        var run = await Selected("owned");
        await Finish(run);
        await using (var interrupted = factory.CreateDbContext())
        {
            var row = await interrupted.Set<CollectionMigrationCollectionRecord>().SingleAsync(item => item.RunId == run && item.SourceId == "owned");
            var collection = JsonSerializer.Deserialize<MigrationCollection>(row.PayloadJson)!;
            collection.CreateStartedAt = clock.UtcNow;
            collection.DestinationIdsBefore = [];
            row.PayloadJson = JsonSerializer.Serialize(collection);
            await interrupted.SaveChangesAsync();
        }
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task LostDestinationCreateResponseIsReconciledBeforeRetry()
    {
        var run = await Selected("owned");
        await Finish(run);
        loseCreate = true;
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        Assert.Equal(DurableJobCompletionKind.Failed, (await Step()).Kind);
        Assert.Equal(1, creates);
        await Service().ChangeAsync(Context(owner), run, "resume", null, default);
        await Finish(run);
        Assert.Equal(1, creates);
        Assert.True(finds >= 2);
        Assert.Equal("owned", destination!.Name);
        Assert.Equal("completed", (await AssertEquation(run)).Run.State);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task PlaylistReviewCorrectionCanBeAppliedAfterTheFirstReceipt()
    {
        needsReview = true;
        var run = await Selected("owned");
        await Finish(run);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.Single(destination!.Members);
        var status = await Service().StatusAsync(Context(owner), run, default);
        var collection = status.Collections.Single(item => item.SourceId == "owned");
        var page = await Service().ReviewAsync(Context(owner), run, collection.Id, 0, 100, default);
        var fixedMatch = await matches.ResolveSnapshotAsync(new(owner, false), page[1].ExternalSnapshotId,
            new("local", BackendItemId: "local-one"), "review", default);
        Assert.True(fixedMatch.Succeeded);
        var priorReceipt = collection.Collection.ReceiptRunId;
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        var next = await Service().StatusAsync(Context(owner), run, default);
        Assert.NotEqual(priorReceipt, next.Collections.Single(item => item.SourceId == "owned").Collection.ReceiptRunId);
        Assert.Equal(1, creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistSyncIsExplicitAndAlwaysUsesOnDemandMembership(bool keepInSync)
    {
        var run = await Selected("owned", keepInSync);
        await Finish(run);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        await using var db = factory.CreateDbContext();
        var link = await db.PlaylistLinks.SingleAsync();
        Assert.Equal(keepInSync ? PlaylistImportMode.Linked : PlaylistImportMode.OneTime, link.ImportMode);
        Assert.Equal(PlaylistTrackRetention.OnDemand, link.TrackRetention);
        Assert.Equal(PlaylistLinkMode.Hybrid, link.Mode);
        Assert.Equal(keepInSync, link.ScheduleId.HasValue);
        if (keepInSync) Assert.True((await db.JobSchedules.SingleAsync()).Enabled);
        else Assert.Empty(await db.JobSchedules.ToArrayAsync());
        reader.Verify(item => item.MutatePlaylistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistMutationRequest>()), Times.Never);
    }

    [Fact]
    public async Task ApplyHonorsTheExistingPreviewAcceptanceThreshold()
    {
        var run = await Selected("liked-songs");
        await Finish(run);
        await using (var review = factory.CreateDbContext())
        {
            var match = await review.TrackMatches.SingleAsync(item => item.LibraryTrackId != null);
            match.Confidence = 0;
            match.Threshold = 1;
            await review.SaveChangesAsync();
        }
        Assert.Equal(1, (await AssertEquation(run)).Total.Accepted);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.Single(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
        Assert.Equal(0, nativeHearts);
    }

    [Fact]
    public async Task UnsupportedAndReviewRowsStayCountedAndHaveExistingMatchDetails()
    {
        count = 3;
        needsReview = true;
        unsupportedLast = true;
        var run = await Selected("liked-songs");
        await Finish(run);
        var status = await AssertEquation(run);
        Assert.Equal(1, status.Total.Unsupported);
        Assert.True(status.Total.Review + status.Total.Unmatched > 0);
        var collection = status.Collections.Single(item => item.SourceId == "liked-songs");
        var page = await Service().ReviewAsync(Context(owner), run, collection.Id, 0, 100, default);
        Assert.Equal(3, page.Count);
        Assert.NotNull(page[1].Match);
        Assert.NotEqual("[]", page[1].Match!.CandidateResultsJson);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        await using (var hearts = factory.CreateDbContext()) Assert.Single(await hearts.FavoriteStates.Where(item => item.IsFavorite).ToArrayAsync());
        var fixedMatch = await matches.ResolveSnapshotAsync(new(owner, false), page[1].ExternalSnapshotId,
            new("local", BackendItemId: "local-one"), "review", default);
        Assert.True(fixedMatch.Succeeded);
        Assert.Equal(2, (await AssertEquation(run)).Total.Accepted);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        await using var receipts = factory.CreateDbContext();
        Assert.Equal(2, await receipts.PlaylistSyncEntryResults.CountAsync());
    }

    [Fact]
    public async Task RunIsolationReturnsForbiddenEvenForUntargetedAdministrator()
    {
        var run = await Start();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().StatusAsync(Context(other), run, default));
        await using (var db = factory.CreateDbContext()) { (await db.Users.SingleAsync(item => item.Id == other)).IsAdmin = true; await db.SaveChangesAsync(); }
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            AllstarrUserId = other,
            BackendType = "jellyfin",
            BackendInstanceId = "main",
            UserId = other.ToString("N"),
            UserName = "Listener",
            IsAdministrator = true,
            JellyfinAccessToken = "unused",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var controller = new CollectionMigrationsController(Service(), new(factory, clock), factory) { ControllerContext = new() { HttpContext = http } };
        Assert.IsType<ForbidResult>(await controller.Status(run, null, default));
        Assert.IsType<OkObjectResult>(await controller.Status(run, owner, default));
    }

    private CollectionMigrationService Service(ITrackIdentityService? identityService = null) => new(factory, queue, router.Object, matches, playlists, orchestration, targets.Object,
        favorites, new(), clock, identityService ?? identities);
    private Task<Guid> Start() => Service().StartAsync(Context(owner), account, "request", default);
    private async Task<Guid> Selected(string source, bool keepInSync = false)
    {
        var id = await Start();
        await Service().DiscoverAsync(Context(owner), id, null, default);
        await Service().ChangeAsync(Context(owner), id, "choose", [new(source, keepInSync)], default);
        return id;
    }
    private async Task<DurableJobCompletion> Step(ITrackIdentityService? identityService = null)
    {
        await using (var db = factory.CreateDbContext())
            await db.Jobs.Where(item => item.Type == CollectionMigrationService.Type).ExecuteUpdateAsync(update => update.SetProperty(item => item.AvailableAt, clock.UtcNow.AddSeconds(-1)));
        var claim = await queue.ClaimNextAsync("migration-test", [CollectionMigrationService.Type]);
        Assert.NotNull(claim);
        var result = await Service(identityService).ExecuteAsync(new(claim, null!) { ReportProgressAsync = (update, token) => queue.ReportProgressAsync(claim, update, token) }, default);
        await queue.CompleteAsync(claim, result);
        return result;
    }
    private async Task Finish(Guid id)
    {
        for (var step = 0; step < 30; step++)
        {
            var result = await Step();
            await AssertEquation(id);
            if (result.Kind == DurableJobCompletionKind.Succeeded) return;
            Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
        }
        Assert.Fail("The bounded import did not finish.");
    }
    private async Task<MigrationStatus> AssertEquation(Guid id)
    {
        var status = await Service().StatusAsync(Context(owner), id, default);
        foreach (var counts in status.Collections.Select(item => item.Counts).Append(status.Total))
            Assert.Equal(counts.Selected, counts.Accepted + counts.Review + counts.Unmatched + counts.Unsupported + counts.Skipped);
        return status;
    }
    private ProviderOutcome<ProviderPlaylistTrackPage> Page(ProviderPlaylistTracksRequest request)
    {
        if (request.Page.Limit == 1) cursors.Add("discovery"); else cursors.Add(request.Page.Cursor);
        if (failSecondPage && request.Page.Cursor != null) return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.Unauthorized));
        var offset = request.Page.Cursor == null ? 0 : int.Parse(request.Page.Cursor);
        var tracks = Enumerable.Range(offset, Math.Min(request.Page.Limit, count - offset)).Select(index =>
        {
            var id = new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, $"track{index}");
            return new ProviderPlaylistTrack(index, id, metadata: unsupportedLast && index == count - 1 ? null : new(id, index == 0 ? "Local song" : "Stream song", [new("Artist")],
                albumTitle: "Album", duration: TimeSpan.FromSeconds(180), isrc: index == 0 ? "USAAA2600001" : null));
        }).ToArray();
        var next = offset + tracks.Length < count ? (offset + tracks.Length).ToString() : null;
        return ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(Summary(request.PlaylistId.Value), new("spotify", tracks, next, next != null, "source-revision")));
    }
    private ProviderPlaylistSummary Summary(string id) => new(new("spotify", ProviderResourceKind.Playlist, id), id, new(id == "followed" ? "someone-else" : sourceUser), "source-revision", trackCount: count);
    private UserRecord User(Guid id) => new() { Id = id, BackendType = "jellyfin", BackendInstanceId = "main", BackendPrincipalId = id.ToString("N"), DisplayName = "Listener", Enabled = true };
    private ProtocolExecutionContext Context(Guid id) => new(ProtocolKind.Jellyfin, "main", id.ToString("N"),
        new(id, "jellyfin", "main", id.ToString("N"), "Listener", false), "migration-test", clock.UtcNow.AddHours(1), default);
    public async Task DisposeAsync() => await database.DisposeAsync();
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
