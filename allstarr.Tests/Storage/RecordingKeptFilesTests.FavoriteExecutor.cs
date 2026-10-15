using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "allstarr")]
    [InlineData(ProtocolKind.Subsonic, "allstarr")]
    [InlineData(ProtocolKind.Jellyfin, "deezer")]
    [InlineData(ProtocolKind.Subsonic, "deezer")]
    public async Task ProtocolMetadataUsesRetainedDisplayWithoutProviderCalls(ProtocolKind protocol, string provider)
    {
        await Service().PublishAsync(Request() with
        { Metadata = new("Offline title", ["Offline artist"], "Offline album", 87_654) });
        await DisableSourceAccountAsync();
        await SetListenerBackendAsync(protocol);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var context = PlaybackContext(protocol, other, CanonicalItemId());

        var song = await KeptGateway(router.Object).GetSongAsync(context, provider,
            provider == "allstarr" ? recordingId.ToString("D") : "source-track");

        Assert.NotNull(song);
        Assert.Equal(CanonicalItemId(), song.Id);
        Assert.Equal("Offline title", song.Title);
        Assert.Equal("Offline artist", song.Artist);
        Assert.Equal("Offline album", song.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(87_654), song.PreciseDuration);
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("same")]
    [InlineData("different")]
    [InlineData("missing")]
    public async Task FavoriteExecutorPublishesOnlyDisplayMetadataForTheSameRecording(string metadataKind)
    {
        var intent = await SeedFavoriteIntentAsync(owner, "ext-deezer-song-source-track", existingJobId: jobId);
        var metadataRecordingId = metadataKind == "different"
            ? (await identities.CreateRecordingAsync(Actor(owner), "other-metadata-recording")).Recording.Id
            : recordingId;
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        var descriptor = ExecutorDescriptor();
        providers.Setup(item => item.FindByCapability(ProviderCapabilityKind.Download, true)).Returns([descriptor]);
        router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(It.Is<ProviderRouteRequest>(
                request => request.OperationId == "recording-keep")))
            .ReturnsAsync(ExecutorPlan(descriptor));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.Is<ProtocolExecutionContext>(execution =>
                    execution.Principal!.UserId == owner && execution.VerifiedBackendPrincipalId == owner.ToString("N")),
                "deezer", "source-track"))
            .ReturnsAsync(metadataKind == "missing" ? null : new Song
            {
                Id = CanonicalRecordingRecord.DefaultPublicId(metadataRecordingId),
                CanonicalRecordingId = metadataRecordingId,
                Title = "Kept title",
                Artist = "First",
                Artists = ["First", "Second"],
                Album = "Kept album",
                Duration = 123,
                PreciseDuration = TimeSpan.FromMilliseconds(123_457),
                ExplicitContentLyrics = 1,
                CoverArtUrl = "https://fixture.invalid/image?token=private-artwork-fixture",
                LocalPath = "/private/fixture/audio.flac",
                JellyfinMetadata = new() { ["PrivateField"] = "private-provider-fixture" }
            });
        var executor = Executor(router.Object, providers.Object, gateway.Object);
        await using var db = new AllstarrDbContext(database.Options);
        var favorite = await db.Set<FavoriteEventRecord>().AsNoTracking().SingleAsync(item => item.Id == intent.EventId);

        var result = await executor.ExecuteAsync(favorite, DownloadAction(favorite), intent.AttemptNumber, default);

        if (metadataKind != "same")
        {
            Assert.False(result.Succeeded);
            Assert.Equal(metadataKind == "missing" ? "favorite_keep_metadata_unavailable" : "favorite_keep_metadata_mismatch", result.ErrorCode);
            Assert.Equal(metadataKind == "missing", result.Retryable);
            Assert.Empty(await db.Set<RecordingKeptRouteEntity>().AsNoTracking().ToArrayAsync());
            Assert.Equal(ProviderDownloadArtifactState.Verified,
                (await artifacts.FindByJobAsync(jobId, "deezer"))!.State);
            router.VerifyAll();
            providers.VerifyAll();
            gateway.VerifyAll();
            return;
        }
        Assert.True(result.Succeeded, result.SafeMessage);
        var song = Assert.IsType<Song>(await Service().FindSongAsync(Actor(other), CanonicalItemId()));
        Assert.Equal("Kept title", song.Title);
        Assert.Equal(new[] { "First", "Second" }, song.Artists);
        Assert.Equal("Kept album", song.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(123_457), song.PreciseDuration);
        Assert.Null(song.CoverArtUrl);
        Assert.Null(song.LocalPath);
        Assert.Null(song.JellyfinMetadata);
        Assert.Equal(ProviderDownloadArtifactState.Verified,
            (await artifacts.FindByJobAsync(jobId, "deezer"))!.State);
        Assert.Single(await db.Set<RecordingKeptRouteEntity>().AsNoTracking().ToArrayAsync());
        router.VerifyAll();
        providers.VerifyAll();
        gateway.VerifyAll();
    }

    [Fact]
    public async Task FavoriteExecutorReusesHouseholdBytesWithoutProviderOrMetadataCalls()
    {
        var initial = await Service().PublishAsync(Request() with
        { Metadata = new("Already kept", ["Artist"], "Album", 200_000) });
        await DisableSourceAccountAsync();
        var intent = await SeedFavoriteIntentAsync(other, CanonicalItemId());
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var executor = Executor(router.Object, providers.Object, gateway.Object);
        await using var db = new AllstarrDbContext(database.Options);
        var favorite = await db.Set<FavoriteEventRecord>().AsNoTracking().SingleAsync(item => item.Id == intent.EventId);

        var result = await executor.ExecuteAsync(favorite, DownloadAction(favorite), intent.AttemptNumber, default);

        Assert.True(result.Succeeded, result.SafeMessage);
        Assert.Equal(initial.ManagedFileId, (await Service().FindAsync(Actor(other), recordingId))!.ManagedFileId);
        Assert.Null(await artifacts.FindByJobAsync(intent.JobId, "deezer"));
        Assert.Equal(1, await db.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null &&
            item.ReferenceKey == $"heart:{intent.StateId:N}"));
    }

    private FavoriteDownloadActionExecutor Executor(IProviderRouter router, IProviderRegistry providers,
        IProtocolProviderGateway gateway) => new(new(router, providers, artifacts, new SystemPlatformClock(),
            NullLogger<ManagedTrackDownloadService>.Instance), factory, new TestBackendLibraryAccess(factory, "music"),
            Service(), identities, gateway, new SystemPlatformClock());

    private static FavoriteActionRecord DownloadAction(FavoriteEventRecord favorite) => new()
    {
        Id = Guid.CreateVersion7(),
        EventId = favorite.Id,
        OwnerUserId = favorite.OwnerUserId,
        ActionType = "download",
        IdempotencyKey = $"fixture-download:{favorite.Id:N}",
        AttemptCount = 1
    };

    private ProviderRoutePlan<IProviderDownloadCapability> ExecutorPlan(ProviderDescriptor descriptor)
    {
        var context = SourceContext();
        var track = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "source-track");
        var request = new ProviderRouteRequest(ProviderCapabilityKind.Download, context.Actor, context.Policy,
            "recording-keep", "executor-fixture", context.Deadline, ["deezer"], sourceTrackId: track);
        return new(request,
            [new(0, descriptor, descriptor.Capabilities.Single(),
                new Mock<IProviderDownloadCapability>(MockBehavior.Strict).Object, context, track)],
            new(request.CorrelationId, ProviderCapabilityKind.Download, "deezer", account, []));
    }

    private static ProviderDescriptor ExecutorDescriptor() => new("deezer", "Fixture", "Kept executor fixture",
        ProviderOrigin.BuiltIn, "1", "1.0",
        [new(ProviderCapabilityKind.Download, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.Optional, "1.0", ["checkAvailability", "download"], [ProviderAccountScope.Personal])],
        new());
}
