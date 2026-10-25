using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolRecordingLyricsTests
{
    [Theory]
    [InlineData("apple-download", "apple-musickit", false)]
    [InlineData("apple-musickit", "apple-musickit", false)]
    [InlineData("spotiflac-fixture", "spotiflac-fixture", true)]
    public async Task CanonicalGatewayUsesTheVerifiedAliasAndRealRecordingId(string requestedProvider, string provider, bool household)
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.ResolveTargetAsync(It.Is<ProviderExecutionContext>(execution => execution.ProviderId == provider),
                recording, It.Is<ProviderTrackIdentityTarget>(target => target.ProviderId == provider), context.CancellationToken))
            .ReturnsAsync(new TrackIdentityResolution(recording, Guid.CreateVersion7(),
                new(provider, ProviderResourceKind.Track, "verified-alias"), ProviderIdentityScope.Catalog,
                null, ProviderIdentityVerification.Verified, "fixture", 1));
        var capability = Capability(provider);
        capability.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderLyricsRequest>(request => request.CanonicalRecordingId == recording &&
                    request.ProviderTrackId.Value == "verified-alias" && request.ProviderTrackId.ProviderId == provider &&
                    request.Isrc == "USABC1234567")))
            .ReturnsAsync(ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available,
                provider, ProviderLyricsFormat.LineTimed, "[00:01.234]Verified words")));
        var result = await Gateway(provider, capability.Object, identities.Object).GetRecordingLyricsAsync(context,
            requestedProvider, recording, householdOnly: household, isrc: "USABC1234567");
        Assert.Equal("[00:01.234]Verified words", result?.Content);
        identities.VerifyAll();
        capability.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrSuggestionOnlyAliasCannotReachTheProvider(bool suggestion)
    {
        const string provider = "fixture";
        var context = Context();
        var recording = Guid.CreateVersion7();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.ResolveTargetAsync(It.IsAny<ProviderExecutionContext>(), recording,
                It.IsAny<ProviderTrackIdentityTarget>(), context.CancellationToken))
            .ReturnsAsync(suggestion ? new TrackIdentityResolution(recording, Guid.CreateVersion7(),
                new(provider, ProviderResourceKind.Track, "unverified"), ProviderIdentityScope.Catalog,
                null, ProviderIdentityVerification.Verified, "automatic-suggestion", 1) : null);
        var capability = Capability(provider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Gateway(provider, capability.Object, identities.Object)
            .GetRecordingLyricsAsync(context, provider, recording));
        capability.Verify(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResolverFindsMissingSourceIdsThroughAuthorizedRecordingAliases(bool native, bool household)
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var song = new Song
        {
            Id = native ? "native-song" : "ext-deezer-song-source",
            IsLocal = native,
            CanonicalRecordingId = native ? Guid.CreateVersion7() : recording,
            Title = "Fixture",
            Artist = "Artist",
            Album = "Album",
            Duration = 120
        };
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["spotiflac-fixture"]);
        providers.Setup(item => item.GetRecordingLyricsAsync(context, "spotiflac-fixture", recording,
                ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, household, null))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "spotiflac-fixture",
                ProviderLyricsFormat.LineTimed, "[00:01.234]Alias words"));
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        if (native) index.Setup(item => item.FindByBackendItemAsync(context, "native-song", context.CancellationToken))
            .ReturnsAsync(new IndexedLibraryTrack(Guid.CreateVersion7(), "native-song", "/never-read/fixture.flac",
                "Fixture", "Artist", "Album", null, 120_000, null, null, null, null, recording,
                new Dictionary<string, string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance,
            libraryIndex: index.Object);
        var result = await resolver.FindAllAsync(context, song, "native-song", new ProtocolLyricsSources(HouseholdOnly: household));
        Assert.Equal("[00:01.234]Alias words", Assert.Single(result).Lyrics?.SyncedLyrics);
        providers.VerifyAll();
        providers.VerifyNoOtherCalls();
        index.VerifyAll();
        if (!native) index.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NativeMetadataWithoutAnAuthorizedIndexCannotAssertACanonicalIdentity()
    {
        var context = Context();
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["fixture"]);
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAllAsync(context, new Song { Id = "native", IsLocal = true, CanonicalRecordingId = Guid.CreateVersion7() }, "native");
        Assert.Null(Assert.Single(result).Lyrics);
        providers.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProviderCannotClaimBackendProvenanceWhenItsResultWins()
    {
        var context = Context();
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["fixture", "backend"]);
        providers.Setup(item => item.GetLyricsAsync(context, "fixture", "track", ProviderLyricsFormat.LineTimed,
                "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "backend",
                ProviderLyricsFormat.LineTimed, "[00:01.234]Provider words"));
        var song = new Song
        {
            Id = "ext-fixture-song-track",
            ExternalProvider = "fixture",
            ExternalId = "track",
            Title = "Fixture",
            Artist = "Artist",
            Album = "Album",
            Duration = 120
        };
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAsync(context, song, "track", new ProtocolLyricsSources(Backend: _ =>
                Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(new() { PlainLyrics = "Native words" })));
        Assert.Equal("fixture", result?.Source);
        Assert.Equal("[00:01.234]Provider words", result?.SyncedLyrics);
    }

    private static Mock<IProviderLyricsCapability> Capability(string provider)
    {
        var capability = new Mock<IProviderLyricsCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns(provider);
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Lyrics);
        return capability;
    }

    private static ProtocolProviderGateway Gateway(string provider, IProviderLyricsCapability capability, ITrackIdentityService identities)
    {
        var descriptor = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None, "1.0", ["fetchLyrics"], []);
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(provider, "Fixture", "Fixture",
            ProviderOrigin.BuiltIn, "1", "1.0", [descriptor], new ProviderPermissionDescriptor()), [capability])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unknown, false));
        return new ProtocolProviderGateway(new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), identities), registry, Mock.Of<IHttpClientFactory>());
    }

    private static ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "backend", "listener",
        new AllstarrPrincipal(Guid.CreateVersion7(), "jellyfin", "backend", "listener", "Listener", false),
        "recording-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
}
