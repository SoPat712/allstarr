using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
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

public sealed class SpotifyLikesImportTests : IAsyncLifetime
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid other = Guid.NewGuid();
    private readonly Guid account = Guid.NewGuid();
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private DurableJobQueue queue = null!;
    private FavoriteActionPipeline favorites = null!;
    private readonly Mock<IProviderPlaylistCapability> reader = new(MockBehavior.Strict);
    private readonly Mock<IProtocolProviderGateway> gateway = new(MockBehavior.Strict);
    private readonly Mock<IProviderRouter> router = new(MockBehavior.Strict);
    private readonly List<ProviderPlaylistTrack> tracks = [];
    private readonly SystemPlatformClock clock = new();

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        await using var db = factory.CreateDbContext();
        db.Users.AddRange(User(owner), User(other));
        db.ProviderAccounts.Add(new()
        {
            Id = account,
            OwnerUserId = owner,
            ProviderId = "spotify",
            Enabled = true,
            Revision = 1,
            DisplayName = "Spotify"
        });
        await db.SaveChangesAsync();
        var options = new DurableJobOptions();
        queue = new(factory, options, new(options), clock);
        favorites = new(factory, queue, clock);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync((ProtocolExecutionContext _, string query, int _) => tracks
                .Where(track => query.StartsWith(track.Metadata!.Title, StringComparison.Ordinal))
                .Select(track => Song(track.TrackId.Value, track.Metadata!.Title)).ToArray());
        reader.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderPlaylistTracksRequest request) => Page(request));
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderPlaylistCapability>(request,
                [new(0, null!, null!, reader.Object, new(request.Actor, "spotify",
                    new(account, "spotify", ProviderAccountScope.Personal, 1, ownerUserId: owner), request.Policy,
                    "likes", request.CorrelationId, request.Deadline, request.CancellationToken), null)],
                new(request.CorrelationId, ProviderCapabilityKind.Playlist, "spotify", account, [])));
    }

    [Fact]
    public async Task ImportHeartsImmediatelyWithoutDownloadAndRerunAddsOnlyNewLikes()
    {
        Add("first");
        var requestId = Guid.NewGuid();
        var run = await Service().StartAsync(Context(owner), account, requestId);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Execute()).Kind);
        Assert.Equal(run.JobId, (await Service().StartAsync(Context(owner), account, requestId)).JobId);
        var status = await Service().GetAsync(Context(owner), run.JobId);
        Assert.Equal(1, status!.Imported);
        Assert.Single(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
        await using (var db = factory.CreateDbContext())
        {
            Assert.All(await db.FavoriteActions.ToArrayAsync(), action => Assert.Equal("virtual-liked", action.ActionType));
            Assert.Empty(await db.Jobs.Where(job => job.Type == "recording.keep").ToArrayAsync());
            Assert.Empty(await db.ManagedFileReferences.ToArrayAsync());
            var display = (await ActivityJobPresentation.ResolveAsync(db, [await db.Jobs.SingleAsync(job => job.Id == run.JobId)]))[run.JobId];
            Assert.Equal("Import Spotify likes", display.DisplayName);
            Assert.Contains("1 imported", display.Summary);
        }
        var favoriteClaim = (await queue.ClaimNextAsync("favorite-test", [FavoriteActionPipeline.JobType]))!;
        var favoriteResult = await new FavoriteActionJobHandler(factory, [], clock).ExecuteAsync(new(favoriteClaim, null!), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, favoriteResult.Kind);
        await queue.CompleteAsync(favoriteClaim, favoriteResult);
        Add("second");
        var rerun = await Start();
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Execute()).Kind);
        status = await Service().GetAsync(Context(owner), rerun.JobId);
        Assert.Equal((1, 1, 0, 0), (status!.Imported, status.AlreadyHearted, status.NeedsReview, status.NotFound));
        Assert.Equal(2, (await new FavoriteViewReader(factory).ReadAsync(Context(owner))).Count);
        Assert.Empty(await new FavoriteViewReader(factory).ReadAsync(Context(other)));
        tracks.Clear();
        Add("second");
        await Start();
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Execute()).Kind);
        Assert.Equal(2, (await new FavoriteViewReader(factory).ReadAsync(Context(owner))).Count);
    }

    [Fact]
    public async Task PartialFailureResumesWithoutRepeatingCompletedSongs()
    {
        Add("first");
        Add("second");
        var fail = true;
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), "second Artist", 15))
            .Returns(() => fail ? throw new HttpRequestException("unavailable") : Task.FromResult<IReadOnlyList<Song>>([Song("second", "second")]));
        var run = await Start();
        Assert.Equal(DurableJobCompletionKind.Retry, (await Execute()).Kind);
        var partial = await Service().GetAsync(Context(owner), run.JobId);
        Assert.Equal(1, partial!.Imported);
        fail = false;
        await using (var db = factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == run.JobId);
            job.AvailableAt = clock.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Execute()).Kind);
        Assert.Equal(2, (await Service().GetAsync(Context(owner), run.JobId))!.Imported);
        gateway.Verify(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), "first Artist", 15), Times.Once);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(2, await verify.FavoriteEvents.CountAsync());
    }

    [Fact]
    public async Task SummarySeparatesExistingReviewAndMissingAndProgressIsDurable()
    {
        Add("existing");
        Add("review");
        Add("missing");
        await favorites.RecordAsync(new(Context(owner), "ext-deezer-song-existing", FavoriteOperation.Favorite, "manual"));
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync((ProtocolExecutionContext _, string query, int _) => query.StartsWith("existing", StringComparison.Ordinal)
                ? [Song("existing", "existing")]
                : query.StartsWith("review", StringComparison.Ordinal) ? [Song("review", "review", 220)] : []);
        var run = await Start();
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Execute()).Kind);
        var status = await Service().GetAsync(Context(owner), run.JobId);
        Assert.Equal((0, 1, 1, 1), (status!.Imported, status.AlreadyHearted, status.NeedsReview, status.NotFound));
        Assert.Equal(3, status.Processed);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.FavoriteStates.ToArrayAsync());
        Assert.NotEmpty(await db.AuditEvents.Where(item => item.Category == "job-progress" && item.SubjectId == run.JobId.ToString("D")).ToArrayAsync());
    }

    [Fact]
    public async Task ReceiptsSurviveCheckpointGapAndLaterUnheart()
    {
        var context = Context(owner);
        Assert.True(await favorites.ImportSpotifyLikeAsync(context, "ext-deezer-song-one", "spotify-like:one"));
        Assert.True(await new FavoriteActionPipeline(factory, queue, clock)
            .ImportSpotifyLikeAsync(context, "ext-deezer-song-one", "spotify-like:one"));
        await favorites.RecordAsync(new(context, "ext-deezer-song-one", FavoriteOperation.Unfavorite, "manual"));
        Assert.False(await favorites.ImportSpotifyLikeAsync(Context(owner, "next-run"), "ext-deezer-song-one", "spotify-like:one"));
        Assert.Empty(await new FavoriteViewReader(factory).ReadAsync(context));
    }

    [Fact]
    public async Task SourceAndStatusBelongToCallingListenerIncludingAdministrators()
    {
        var run = await Start();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().StartAsync(Context(other), account, Guid.NewGuid()));
        Assert.Null(await Service().GetAsync(Context(other), run.JobId));
        var http = new DefaultHttpContext();
        var controller = new SpotifyLikesController(Service(), new(factory, clock))
        { ControllerContext = new() { HttpContext = http } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.Get(run.JobId, default));
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = other.ToString("N"),
            UserName = "Other",
            IsAdministrator = true,
            AllstarrUserId = other,
            BackendType = "jellyfin",
            BackendInstanceId = "main",
            JellyfinAccessToken = "unused",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == other)).IsAdmin = true;
            await db.SaveChangesAsync();
        }
        Assert.IsType<NotFoundResult>(await controller.Get(run.JobId, default));
    }

    [Fact]
    public async Task PageFailureDoesNotTurnUnreadLikesIntoNotFound()
    {
        reader.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.TransientFailure)));
        var run = await Start();
        Assert.Equal(DurableJobCompletionKind.Retry, (await Execute()).Kind);
        Assert.Equal(0, (await Service().GetAsync(Context(owner), run.JobId))!.Processed);
    }

    [Fact]
    public async Task ResumeStartsAtFailedPageAndSourceChangesPreserveCompletedHearts()
    {
        Add("first");
        Add("second");
        var fail = true;
        reader.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderPlaylistTracksRequest request) =>
                request.Page.Cursor == "1" && fail
                    ? ProviderOutcome<ProviderPlaylistTrackPage>.Failure(new(ProviderErrorKind.TransientFailure))
                    : ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                        new(request.PlaylistId, "Liked Songs", new("selected-account"), fail ? "revision" : "changed", trackCount: 2),
                        new("spotify", [tracks[request.Page.Cursor == null ? 0 : 1]], request.Page.Cursor == null ? "1" : null,
                            request.Page.Cursor == null, "revision"))));
        var run = await Start();
        Assert.Equal(DurableJobCompletionKind.Retry, (await Execute()).Kind);
        fail = false;
        await using (var db = factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == run.JobId);
            job.AvailableAt = clock.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(DurableJobCompletionKind.Failed, (await Execute()).Kind);
        reader.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(),
            It.Is<ProviderPlaylistTracksRequest>(request => request.Page.Cursor == null)), Times.Once);
        Assert.Single(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
        Assert.Equal(1, (await Service().GetAsync(Context(owner), run.JobId))!.Imported);
    }

    private SpotifyLikesImport Service() => new(factory, queue, router.Object,
        new(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance), favorites, new(new()), clock);
    private Task<DurableJobEnqueueResult> Start() => Service().StartAsync(Context(owner), account, Guid.NewGuid());
    private async Task<DurableJobCompletion> Execute()
    {
        var claim = (await queue.ClaimNextAsync("likes-test", [SpotifyLikesImport.Type]))!;
        var context = new DurableJobExecutionContext(claim, null!)
        { ReportProgressAsync = (update, token) => queue.ReportProgressAsync(claim, update, token) };
        var result = await Service().ExecuteAsync(context, default);
        await queue.CompleteAsync(claim, result);
        return result;
    }
    private void Add(string id)
    {
        var resource = new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, id);
        tracks.Add(new(tracks.Count, resource, metadata: new(resource, id, [new("Artist")], duration: TimeSpan.FromSeconds(180))));
    }
    private ProviderOutcome<ProviderPlaylistTrackPage> Page(ProviderPlaylistTracksRequest request) => ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
        new(request.PlaylistId, "Liked Songs", new("selected-account"), "revision", trackCount: tracks.Count),
        new("spotify", tracks.ToArray(), snapshotVersion: "revision")));
    private static Song Song(string id, string title, int duration = 180) => new()
    { Id = $"ext-deezer-song-{id}", ExternalProvider = "deezer", ExternalId = id, Title = title, Artist = "Artist", Duration = duration };
    private UserRecord User(Guid id) => new() { Id = id, BackendType = "jellyfin", BackendInstanceId = "main", BackendPrincipalId = id.ToString("N"), DisplayName = "Listener" };
    private ProtocolExecutionContext Context(Guid id, string correlation = "run") => new(ProtocolKind.Jellyfin, "main", id.ToString("N"),
        new AllstarrPrincipal(id, "jellyfin", "main", id.ToString("N"), "Listener", false), correlation, clock.UtcNow.AddMinutes(5), default);
    public async Task DisposeAsync() => await database.DisposeAsync();
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
