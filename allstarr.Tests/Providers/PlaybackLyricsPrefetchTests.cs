using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Moq;

namespace allstarr.Tests;

public sealed class PlaybackLyricsPrefetchTests
{
    [Theory]
    [InlineData("valid", 1)]
    [InlineData("revoked", 0)]
    [InlineData("wrong-owner", 0)]
    [InlineData("wrong-backend", 0)]
    public async Task PrefetchUsesSharedLyricsOnlyForTheExactActiveListener(string scope, int calls)
    {
        var user = Guid.NewGuid();
        var payload = new PlaybackSignalPayload(new IntelligenceScope(user, "jellyfin", "backend"),
            PlaybackTransition.Start, "ext-spotify-song-track", "device", "session", 0, DateTimeOffset.UtcNow, "signal");
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, scope == "wrong-backend" ? "other" : "backend",
            "principal", new AllstarrPrincipal(scope == "wrong-owner" ? Guid.NewGuid() : user, "jellyfin", scope == "wrong-backend" ? "other" : "backend",
                "principal", "Listener", false), "prefetch", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var access = new Mock<IBackendLibraryAccessResolver>(MockBehavior.Strict);
        access.Setup(item => item.ResolveUserAsync(user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope == "revoked" ? BackendLibraryAccessContext.Unavailable : new(context, new(true, [])));
        var tracks = new Mock<IPlaybackTrackResolver>(MockBehavior.Strict);
        tracks.Setup(item => item.ResolveAsync(payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaybackTrackSnapshot(null, null, payload.ItemId, "Fixture [A]", "Artist", "Album", null,
                ProviderId: "spotify", ProviderTrackReference: "spotify:track"));
        var lyrics = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        lyrics.Setup(item => item.FindAsync(It.Is<ProtocolExecutionContext>(viewer =>
                    viewer.Actor!.EffectiveUserId == user && viewer.BackendInstanceId == "backend"),
                It.Is<Song>(song => song.Title == "Fixture" && song.Artist == "Artist"),
                payload.ItemId, "spotify", "track", "track"))
            .ReturnsAsync(new LyricsInfo { SyncedLyrics = "[00:01.000]Synthetic words" });
        await new PlaybackLyricsPrefetch(tracks.Object, lyrics.Object, access.Object).PrefetchAsync(payload, default);
        lyrics.Verify(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Exactly(calls));
    }
}
