using System.Collections.Immutable;
using allstarr.Core.Settings;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Moq;

namespace allstarr.Tests;

public sealed class PlaylistUrlResolverTests
{
    [Theory]
    [InlineData("spotify:playlist:mix", "spotify", ProviderResourceKind.Playlist, "mix")]
    [InlineData("spotify:album:record", "spotify", ProviderResourceKind.Album, "record")]
    [InlineData("https://open.spotify.com/playlist/mix?si=share", "spotify", ProviderResourceKind.Playlist, "mix")]
    [InlineData("https://open.spotify.com/intl-de/album/record", "spotify", ProviderResourceKind.Album, "record")]
    [InlineData("https://open.spotify.com/track/song", "spotify", ProviderResourceKind.Track, "song")]
    [InlineData("https://music.apple.com/us/playlist/morning/pl.mix", "apple-musickit", ProviderResourceKind.Playlist, "pl.mix")]
    [InlineData("https://music.apple.com/gb/album/record/123", "apple-musickit", ProviderResourceKind.Album, "123")]
    [InlineData("https://music.apple.com/us/album/record/123?i=456", "apple-musickit", ProviderResourceKind.Track, "456")]
    [InlineData("https://www.deezer.com/en/playlist/42", "deezer", ProviderResourceKind.Playlist, "42")]
    [InlineData("https://deezer.com/album/12", "deezer", ProviderResourceKind.Album, "12")]
    [InlineData("https://play.qobuz.com/playlist/42", "qobuz", ProviderResourceKind.Playlist, "42")]
    [InlineData("https://www.qobuz.com/us-en/album/title/abc", "qobuz", ProviderResourceKind.Album, "abc")]
    public void BuiltInLinksAreParsedWithoutRequests(string url, string provider, ProviderResourceKind kind, string id)
    {
        var resource = PlaylistUrlResolver.ParseBuiltIn(new Uri(url), provider);
        Assert.Equal(new(provider, kind, id), resource);
    }

    [Theory]
    [InlineData("https://open.spotify.com.attacker.test/playlist/mix")]
    [InlineData("https://attacker.test/open.spotify.com/playlist/mix")]
    [InlineData("https://open.spotify.com/user/profile")]
    public void UnownedLinksAreNotParsed(string url) =>
        Assert.Null(PlaylistUrlResolver.ParseBuiltIn(new Uri(url), "spotify"));

    [Theory]
    [InlineData("tidal.com", "https://tidal.com/browse/playlist/mix")]
    [InlineData("listen.tidal.com", "https://listen.tidal.com/playlist/mix")]
    [InlineData("music.apple.com", "https://music.apple.com/us/playlist/mix/pl.mix")]
    [InlineData("jiosaavn.com", "https://jiosaavn.com/featured/mix/token")]
    [InlineData("www.jiosaavn.com", "https://www.jiosaavn.com/s/playlist/listener/mix/token")]
    [InlineData("open.spotify.com", "https://open.spotify.com/playlist/mix")]
    [InlineData("music.amazon.com", "https://music.amazon.com/playlists/mix")]
    [InlineData("music.amazon.co.uk", "https://music.amazon.co.uk/playlists/mix")]
    [InlineData("pandora.com", "https://pandora.com/playlist/mix")]
    [InlineData("pandora.app.link", "https://pandora.app.link/share")]
    public void DeclaredHostPatternsMatchWithoutContactingTheService(string pattern, string url)
    {
        Assert.True(PlaylistUrlResolver.MatchesPattern(new Uri(url), pattern));
        Assert.False(PlaylistUrlResolver.MatchesPattern(new Uri("https://unrelated.test/?url=" + Uri.EscapeDataString(url)), pattern));
        Assert.False(PlaylistUrlResolver.MatchesPattern(new Uri("https://" + pattern + ".unrelated.test/playlist/mix"), pattern));
    }

    [Theory]
    [InlineData(ProviderUseMode.Automatic, true)]
    [InlineData(ProviderUseMode.ManualOnly, true)]
    [InlineData(ProviderUseMode.Off, false)]
    public async Task ExplicitLinksHonorTheSourceSwitch(ProviderUseMode mode, bool allowed)
    {
        var capability = Mock.Of<IProviderPlaylistCapability>(item => item.ProviderId == "spotify" && item.Capability == ProviderCapabilityKind.Playlist);
        var registration = PublicPlaylistTestRegistry.Registration("spotify", capability);
        var registry = new ProviderRegistry([new(registration.Descriptor with { DefaultUseMode = mode }, [capability])]);
        var router = Router(registry, capability);
        var result = await new PlaylistUrlResolver(registry, router.Object).ResolveAsync(
            "https://open.spotify.com/playlist/mix", Actor(), [], "url-fixture");
        Assert.Equal(allowed, result.Resource != null);
        Assert.Equal(allowed ? null : "source-off", result.ErrorCode);
        router.Verify(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()), allowed ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task RequiredAccountsAreCheckedBeforeRouting()
    {
        var capability = Mock.Of<IProviderPlaylistCapability>(item => item.ProviderId == "deezer" && item.Capability == ProviderCapabilityKind.Playlist);
        var registry = new ProviderRegistry([new(new("deezer", "Deezer", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                "1", ["getUserPlaylists", "getPlaylistTracks"], [ProviderAccountScope.Personal])], new()), [capability])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var result = await new PlaylistUrlResolver(registry, router.Object).ResolveAsync(
            "https://deezer.com/playlist/42", Actor(), [], "url-fixture");
        Assert.Equal("account-required", result.ErrorCode);
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("not a link")]
    [InlineData("file:///playlist/42")]
    [InlineData("https://user:password@open.spotify.com/playlist/mix")]
    [InlineData("https://unknown.test/playlist/mix")]
    public async Task InvalidOrUnknownLinksDoNotRoute(string url)
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var result = await new PlaylistUrlResolver(new ProviderRegistry([]), router.Object).ResolveAsync(url, Actor(), [], "url-fixture");
        Assert.Equal("no-source", result.ErrorCode);
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("listen.fixture.test", "https://listen.fixture.test/browse/playlist/mix")]
    [InlineData("listen.fixture.test/browse/", "https://listen.fixture.test/browse/playlist/mix")]
    [InlineData("https://listen.fixture.test/*", "https://listen.fixture.test/browse/playlist/mix")]
    [InlineData(@"^https://listen\.fixture\.test/browse/(playlist|album)/[^/?]+", "https://listen.fixture.test/browse/playlist/mix")]
    [InlineData("tidal:", "tidal:playlist:mix")]
    [InlineData("tidal:", "tidal://playlist/mix")]
    public async Task DeclaredExtensionPatternsCallOnlyTheSelectedHandler(string pattern, string url)
    {
        var capability = new Mock<IProviderPlaylistCapability>();
        capability.SetupGet(item => item.ProviderId).Returns("link-fixture");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        var urls = capability.As<IProviderUrlCapability>();
        urls.SetupGet(item => item.UrlPatterns).Returns([pattern]);
        urls.Setup(item => item.ResolveUrlAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<string>()))
            .ReturnsAsync(ProviderOutcome<ProviderExternalResourceId>.Success(new("link-fixture", ProviderResourceKind.Playlist, "mix")));
        var descriptor = new ProviderDescriptor("link-fixture", "Link fixture", "Fixture", ProviderOrigin.Extension, "1", "1",
            [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                "1", ["getPlaylistTracks"], supportsPublicRead: true, supportsPlaylistDiscovery: false)], new(), entryPoint: "index.js");
        var registry = new ProviderRegistry([new(descriptor, [capability.Object])]);
        var resolver = new PlaylistUrlResolver(registry, Router(registry, capability.Object).Object);
        var result = await resolver.ResolveAsync(url, Actor(), [], "url-fixture");
        Assert.Equal("mix", result.Resource?.Value);
        urls.Verify(item => item.ResolveUrlAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<string>()), Times.Once());
        Assert.Equal("no-source", (await resolver.ResolveAsync("https://listen.fixture.test.attacker.test/browse/playlist/mix",
            Actor(), [], "url-fixture")).ErrorCode);
        urls.Verify(item => item.ResolveUrlAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<string>()), Times.Once());
    }

    [Theory]
    [InlineData(ProviderUseMode.Automatic, ProviderUseMode.Off, false)]
    [InlineData(ProviderUseMode.Off, ProviderUseMode.ManualOnly, true)]
    public async Task PersonalSourceSwitchOverridesTheDefaultBeforeAnyHook(ProviderUseMode defaultMode, ProviderUseMode personalMode, bool allowed)
    {
        var capability = Mock.Of<IProviderPlaylistCapability>(item => item.ProviderId == "spotify" && item.Capability == ProviderCapabilityKind.Playlist);
        var registration = PublicPlaylistTestRegistry.Registration("spotify", capability);
        var registry = new ProviderRegistry([new(registration.Descriptor with { DefaultUseMode = defaultMode }, [capability])]);
        var actor = Actor();
        var snapshot = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "any", 0)
        {
            ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("spotify", personalMode)
        };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(actor.EffectiveUserId!.Value, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var router = Router(registry, capability);
        var result = await new PlaylistUrlResolver(registry, router.Object, policies.Object).ResolveAsync(
            "https://open.spotify.com/playlist/mix", actor, [], "url-fixture");
        Assert.Equal(allowed, result.Resource != null);
        policies.Verify(item => item.ResolveForUserAsync(actor.EffectiveUserId!.Value, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledAndOtherListenersAccountsCannotBeSelected(bool enabled, bool otherOwner)
    {
        var capability = Mock.Of<IProviderPlaylistCapability>(item => item.ProviderId == "deezer" && item.Capability == ProviderCapabilityKind.Playlist);
        var registry = new ProviderRegistry([new(new("deezer", "Deezer", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                "1", ["getUserPlaylists", "getPlaylistTracks"], [ProviderAccountScope.Personal])], new()), [capability])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var actor = Actor();
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            ProviderId = "deezer",
            OwnerUserId = otherOwner ? Guid.CreateVersion7() : actor.EffectiveUserId,
            Enabled = enabled,
            DisplayName = "Fixture"
        };
        var result = await new PlaylistUrlResolver(registry, router.Object).ResolveAsync("https://deezer.com/playlist/42",
            actor, [account], "url-fixture");
        Assert.Equal("account-required", result.ErrorCode);
        router.VerifyNoOtherCalls();
    }

    internal static ProviderActorContext Actor() => new(ProviderActorKind.User, Guid.CreateVersion7(), new("jellyfin", "fixture", "listener"));

    internal static Mock<IProviderRouter> Router(IProviderRegistry registry, IProviderPlaylistCapability capability)
    {
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .Returns<ProviderRouteRequest>(request =>
            {
                Assert.Equal(ProviderRoutePurpose.ManualSelection, request.Purpose);
                var descriptor = registry.GetRequired(capability.ProviderId);
                var context = new ProviderExecutionContext(request.Actor, capability.ProviderId, null, request.Policy,
                    request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
                return Task.FromResult(new ProviderRoutePlan<IProviderPlaylistCapability>(request,
                    [new(0, descriptor, descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Playlist), capability, context, null)],
                    new(request.CorrelationId, request.Capability, capability.ProviderId, null, [])));
            });
        return router;
    }
}
