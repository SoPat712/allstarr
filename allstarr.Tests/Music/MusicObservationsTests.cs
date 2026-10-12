using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class MusicObservationsTests
{
    [Theory]
    [InlineData(null, null, MusicFavorites.DefaultSourceRevision)]
    [InlineData(null, "revision", "revision")]
    [InlineData("request", "revision", "request")]
    public async Task Favorites_ForwardTheExactScopeOperationAndRevision(string? key, string? revision, string expected)
    {
        var context = Context();
        using var cancellation = new CancellationTokenSource();
        var headers = new HeaderDictionary();
        if (key != null) headers["Idempotency-Key"] = key;
        if (revision != null) headers["X-Allstarr-Source-Revision"] = revision;
        var pipeline = new Mock<IFavoriteActionPipeline>(MockBehavior.Strict);
        pipeline.Setup(item => item.RecordAsync(
                new FavoriteMutationRequest(context, "song", FavoriteOperation.Favorite, expected), cancellation.Token))
            .ReturnsAsync(new FavoriteEventReceipt(Guid.NewGuid(), Guid.NewGuid(), true, default));

        await new MusicFavorites(pipeline.Object, NullLogger.Instance).RecordAsync(
            context, "song", FavoriteOperation.Favorite, headers, cancellation.Token);

        pipeline.VerifyAll();
        pipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Observations_DoNotRecordUnlinkedUsersOrEmptyPlaybackItems()
    {
        var favorites = new Mock<IFavoriteActionPipeline>(MockBehavior.Strict);
        var playback = new Mock<IPlaybackSignalPipeline>(MockBehavior.Strict);
        await new MusicFavorites(favorites.Object, NullLogger.Instance).RecordAsync(
            Context(false), "song", FavoriteOperation.Favorite, new HeaderDictionary(), default);
        var observations = new MusicPlaybackObservations(playback.Object, NullLogger.Instance);
        Assert.True(await observations.RecordAsync(Signal(Context(false)), default));
        Assert.True(await observations.RecordAsync(Signal(Context()) with { ItemId = " " }, default));
        favorites.VerifyNoOtherCalls();
        playback.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Playback_ForwardsTheUnchangedSignalAndCancellation()
    {
        var signal = Signal(Context());
        using var cancellation = new CancellationTokenSource();
        var pipeline = new Mock<IPlaybackSignalPipeline>(MockBehavior.Strict);
        pipeline.Setup(item => item.RecordAsync(signal, cancellation.Token)).ReturnsAsync(true);
        Assert.True(await new MusicPlaybackObservations(pipeline.Object, NullLogger.Instance)
            .RecordAsync(signal, cancellation.Token));
        pipeline.VerifyAll();
        pipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OptionalRecordingFailuresDoNotRewriteSuccessfulResponses()
    {
        var favorites = new Mock<IFavoriteActionPipeline>();
        favorites.Setup(item => item.RecordAsync(It.IsAny<FavoriteMutationRequest>(), default))
            .ThrowsAsync(new InvalidOperationException("fixture"));
        await new MusicFavorites(favorites.Object, NullLogger.Instance).RecordAsync(
            Context(), "song", FavoriteOperation.Favorite, new HeaderDictionary(), default);
        var playback = new Mock<IPlaybackSignalPipeline>();
        playback.Setup(item => item.RecordAsync(It.IsAny<PlaybackSignalRequest>(), default))
            .ThrowsAsync(new InvalidOperationException("fixture"));
        Assert.True(await new MusicPlaybackObservations(playback.Object, NullLogger.Instance)
            .RecordAsync(Signal(Context()), default));
        favorites.Verify(item => item.RecordAsync(It.IsAny<FavoriteMutationRequest>(), default), Times.Once);
        playback.Verify(item => item.RecordAsync(It.IsAny<PlaybackSignalRequest>(), default), Times.Once);
    }

    [Fact]
    public async Task CancellationStopsRecordingFurtherWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var favorites = new Mock<IFavoriteActionPipeline>();
        favorites.Setup(item => item.RecordAsync(It.IsAny<FavoriteMutationRequest>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MusicFavorites(favorites.Object, NullLogger.Instance).RecordAsync(
                Context(), "song", FavoriteOperation.Favorite, new HeaderDictionary(), cancellation.Token));
        var playback = new Mock<IPlaybackSignalPipeline>();
        playback.Setup(item => item.RecordAsync(It.IsAny<PlaybackSignalRequest>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        Assert.False(await new MusicPlaybackObservations(playback.Object, NullLogger.Instance)
            .RecordAsync(Signal(Context()), cancellation.Token));
    }

    internal static ProtocolExecutionContext Context(bool linked = true) => new(
        ProtocolKind.Jellyfin, "fixture", "listener",
        linked ? new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "fixture", "listener", "Listener", false) : null,
        "observation-fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private static PlaybackSignalRequest Signal(ProtocolExecutionContext context) =>
        new(context, PlaybackTransition.Progress, "song", "device", "session", 123, DateTimeOffset.UtcNow);
}
