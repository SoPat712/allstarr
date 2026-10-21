using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class CrossProviderLinkResolverTests
{
    [Theory]
    [InlineData("USRC17607839", true)]
    [InlineData(null, true)]
    [InlineData("USRC17607840", false)]
    public async Task ExactIsrcOrAcceptedSearch_IsVerified(string? returnedIsrc, bool verified)
    {
        var metadata = new Mock<IProviderMetadataCapability>();
        var track = Track("Song", returnedIsrc);
        metadata.Setup(item => item.LookupByIsrcAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderIsrcLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(track));
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", [track], null, false)));
        var resolver = Create(metadata, ["deezer"]);

        var result = Assert.Single(await resolver.ResolveAsync(Source("USRC17607839"), [], Actor(), "fixture", default));

        Assert.Equal(verified, result.Verified);
        Assert.Equal("99", result.Id.Value);
        if (returnedIsrc == "USRC17607839")
            metadata.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
    }

    [Theory]
    [InlineData("Song", 180, true)]
    [InlineData("Song (Live)", 180, false)]
    [InlineData("Song", 240, false)]
    public async Task WithoutIsrc_SearchUsesAutomaticMatchRules(string title, int duration, bool verified)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderMetadataSearchRequest>(request => request.Query.Contains("Song") && request.Query.Contains("Artist"))))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", [Track(title, null, duration)], null, false)));
        var resolver = Create(metadata, ["deezer"]);

        var result = Assert.Single(await resolver.ResolveAsync(Source(null), [], Actor(), "fixture", default));

        Assert.Equal(verified, result.Verified);
        metadata.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludedOrDisabledMetadataProvider_IsNeverCalled(bool disabled)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        var resolver = Create(metadata, disabled ? ["deezer"] : [], disabled ? ["deezer"] : []);
        Assert.Empty(await resolver.ResolveAsync(Source("USRC17607839"), [], Actor(), "fixture", default));
        metadata.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("tidal", ProviderUseMode.ManualOnly)]
    [InlineData("tidal", ProviderUseMode.Off)]
    [InlineData("deezer", ProviderUseMode.ManualOnly)]
    [InlineData("deezer", ProviderUseMode.Off)]
    public async Task RestrictedSourcesAndTargetsNeverEnrichIdentity(string provider, ProviderUseMode mode)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        var resolver = Create(metadata.Object, ["deezer"], modes:
            ImmutableDictionary<string, ProviderUseMode>.Empty.Add(provider, mode));
        Assert.Empty(await resolver.ResolveAsync(Source("USRC17607839"), [], Actor(), "fixture", default));
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SuccessfulLookup_IsCachedAndKnownIdentitiesAreNotSearched()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(item => item.LookupByIsrcAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderIsrcLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(Track("Song", "USRC17607839")));
        var resolver = Create(metadata, ["deezer"]);
        await resolver.ResolveAsync(Source("USRC17607839"), [], Actor(), "fixture", default);
        await resolver.ResolveAsync(Source("USRC17607839"), [], Actor(), "fixture", default);
        Assert.Empty(await resolver.ResolveAsync(Source("USRC17607839"), [Track("Song", null).Id], Actor(), "fixture", default));
        metadata.Verify(item => item.LookupByIsrcAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderIsrcLookupRequest>()), Times.Once);
    }

    [Fact]
    public async Task UnsupportedIsrcLookup_SearchesByIsrcBeforeText()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(item => item.LookupByIsrcAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderIsrcLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Failure(new(ProviderErrorKind.NotSupported)));
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderMetadataSearchRequest>(request => request.Query == "USRC17607839")))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", [Track("Song", "USRC17607839")])));
        var result = Assert.Single(await Create(metadata, ["deezer"]).ResolveAsync(Source("USRC17607839"), [], Actor(), "fixture", default));
        Assert.True(result.Verified);
        Assert.Equal("isrc-lookup", result.Method);
        metadata.VerifyAll();
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AmbiguousSearchResults_RemainSuggestions()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", [new(new("deezer", ProviderResourceKind.Track, "99"), "Song", [new ProviderArtistCredit("Artist")]),
                new(new("deezer", ProviderResourceKind.Track, "100"), "Song", [new ProviderArtistCredit("Artist")])])));
        var results = await Create(metadata, ["deezer"]).ResolveAsync(Source(null), [], Actor(), "fixture", default);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.False(result.Verified));
    }

    [Fact]
    public async Task CancelledRequest_DoesNotCallProviders()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(metadata, ["deezer"]).ResolveAsync(
            Source(null), [], Actor(), "fixture", new CancellationToken(true)));
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisabledCrossServiceLinks_DoNotConsultMetadataOrPolicy()
    {
        var registry = new Mock<IProviderRegistry>(MockBehavior.Strict);
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        var resolver = new CrossProviderLinkResolver(registry.Object, policies.Object, new(), new TestMemoryApplicationCache(),
            Options.Create(new OdesliSettings { Enabled = false }), new SystemPlatformClock());
        Assert.Empty(await resolver.ResolveAsync(Source(null), [], Actor(), "fixture", default));
        registry.VerifyNoOtherCalls();
        policies.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublicFallback_WithoutKeyValidatesLinkedTrackMetadata()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", [])));
        metadata.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderTrackLookupRequest>(request => request.Id.Value == "99")))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(Track("Song", null)));
        var handler = new LinkHandler();
        var resolver = Create(metadata.Object, ["deezer"], odesli: OdesliServiceTests.Create(handler));
        var result = Assert.Single(await resolver.ResolveAsync(Source(null),
            [new("tidal", ProviderResourceKind.Track, "42")], Actor(), "fixture", default));
        Assert.True(result.Verified);
        Assert.Equal("99", result.ExternalId);
        Assert.Equal(3, handler.Calls);
        metadata.VerifyAll();
    }

    private sealed class LinkHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(request.RequestUri!.Host == "song.link"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("<a href='https://deezer.com/track/99'>Listen</a>")
                }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    internal static CrossProviderLinkResolver Create(Mock<IProviderMetadataCapability> metadata, string[] order, string[]? disabled = null)
        => Create(metadata.Object, order, disabled);

    internal static CrossProviderLinkResolver Create(IProviderMetadataCapability metadata, string[] order, string[]? disabled = null, OdesliService? odesli = null,
        ImmutableDictionary<string, ProviderUseMode>? modes = null)
    {
        IProviderMetadataCapability? registered = metadata;
        var registry = new Mock<IProviderRegistry>();
        registry.SetupGet(item => item.Providers).Returns([
            new("deezer", "Deezer", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
                [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1")], new())
        ]);
        registry.Setup(item => item.TryGetCapability<IProviderMetadataCapability>("deezer", ProviderCapabilityKind.Metadata, out registered)).Returns(true);
        var policy = new Mock<IEffectiveProviderPolicyResolver>();
        var snapshot = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Metadata, [.. order]),
            (disabled ?? []).ToImmutableHashSet(), "Any", 0.07)
        { ProviderModes = modes ?? ImmutableDictionary<string, ProviderUseMode>.Empty };
        policy.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        return new(registry.Object, policy.Object, new TrackMatchDecisionEngine(), new TestMemoryApplicationCache(),
            Options.Create(new OdesliSettings { Enabled = true }), new SystemPlatformClock(), odesli);
    }

    private static ProviderActorContext Actor() => new(ProviderActorKind.User, Guid.Parse("11111111-1111-1111-1111-111111111111"), new("jellyfin", "fixture", "listener"));
    private static ExternalTrackMatchSnapshot Source(string? isrc) => new("fixture", "tidal", "42", "Song", "Artist", null, null, 180000, isrc, null, null);
    private static ProviderTrackMetadata Track(string title, string? isrc, int duration = 180) =>
        new(new("deezer", ProviderResourceKind.Track, "99"), title, [new ProviderArtistCredit("Artist")], duration: TimeSpan.FromSeconds(duration), isrc: isrc);
}
