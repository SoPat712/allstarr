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
    [Fact]
    public async Task SpicyPlaybackPrefetchFeedsTheSameAuthorizedForegroundCache()
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var cache = new SpicyLyricsTestCache();
        var source = new allstarr.Core.Providers.Lyrics.SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), cache);
        var user = SpicyLyricsTestData.UserId;
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal",
            new AllstarrPrincipal(user, "jellyfin", "backend", "principal", "Listener", false), "prefetch", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var payload = new PlaybackSignalPayload(new IntelligenceScope(user, "jellyfin", "backend"), PlaybackTransition.Start,
            "ext-spotify-song-" + SpicyLyricsTestData.TrackId, "device", "session", 0, DateTimeOffset.UtcNow, "signal");
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(new BackendLibraryAccessContext(context, new(true, [])));
        var tracks = new Mock<IPlaybackTrackResolver>();
        tracks.Setup(item => item.ResolveAsync(payload, It.IsAny<CancellationToken>())).ReturnsAsync(new PlaybackTrackSnapshot(null, null,
            payload.ItemId, "Fixture", "Artist", "Album", null, ProviderId: "spotify", ProviderTrackReference: "spotify:" + SpicyLyricsTestData.TrackId));
        var providers = new Mock<allstarr.Core.Protocols.IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(allstarr.Core.Capabilities.ProviderCapabilityKind.Lyrics)).Returns(["spicylyrics"]);
        providers.Setup(item => item.GetLyricsAsync(It.IsAny<ProtocolExecutionContext>(), "spicylyrics", SpicyLyricsTestData.TrackId,
                allstarr.Core.Capabilities.ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", It.IsAny<int?>(), null))
            .Returns(async (ProtocolExecutionContext protocol, string _, string id, allstarr.Core.Capabilities.ProviderLyricsFormat? preferred,
                string? title, IReadOnlyList<string>? artists, string? album, int? duration, string? isrc) =>
            {
                var binding = new allstarr.Core.Capabilities.ProviderExecutionContext(protocol.Actor!, "spicylyrics", SpicyLyricsTestData.Context().Account,
                    SpicyLyricsTestData.Context().Policy, "lyrics", protocol.CorrelationId, protocol.Deadline, protocol.CancellationToken);
                return (await source.FetchLyricsAsync(binding, SpicyLyricsTestData.Request(id))).RequireValue();
            });
        var resolver = new ProtocolLyricsResolver(providers.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProtocolLyricsResolver>.Instance);
        await new PlaybackLyricsPrefetch(tracks.Object, resolver, access.Object).PrefetchAsync(payload, default);
        var foreground = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId, context.Principal,
            "foreground", context.Deadline, default);
        var result = await resolver.FindAsync(foreground, new Song { Title = "Fixture", Artist = "Artist", Album = "Album", SpotifyId = SpicyLyricsTestData.TrackId }, "foreground-item");
        Assert.Equal("spicylyrics", result!.Source);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, cache.Writes);
    }

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
