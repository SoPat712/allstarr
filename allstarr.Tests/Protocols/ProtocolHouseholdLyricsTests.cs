using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolHouseholdLyricsTests
{
    [Theory]
    [InlineData(ProviderAccountRequirement.Required)]
    [InlineData(ProviderAccountRequirement.Optional)]
    public async Task HouseholdPreviewSelectsSharedEvenWhenPersonalWouldNormallyWin(ProviderAccountRequirement requirement)
    {
        var context = Context();
        var personal = new ProviderAccountContext(Guid.CreateVersion7(), "fixture", ProviderAccountScope.Personal, 2,
            ownerUserId: context.Principal!.UserId);
        var shared = new ProviderAccountContext(Guid.CreateVersion7(), "fixture", ProviderAccountScope.Shared, 3);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderRouteAccountRequest request, CancellationToken _) =>
            {
                var selected = request.AllowedScopes!.Contains(ProviderAccountScope.Personal) ? personal : shared;
                return new ProviderRouteAccountResolution(selected, selected.Revision);
            });
        var served = new List<Guid?>();
        var capability = Capability(served);
        var gateway = Gateway(requirement, capability.Object, accounts.Object);
        Assert.NotNull(await gateway.GetLyricsAsync(context, "fixture", "exact"));
        Assert.NotNull(await gateway.GetHouseholdLyricsAsync(context, "fixture", "exact"));
        Assert.Equal(new Guid?[] { personal.AccountId, shared.AccountId }, served);
    }

    [Theory]
    [InlineData(ProviderAccountRequirement.Required)]
    [InlineData(ProviderAccountRequirement.Optional)]
    public async Task CanonicalHouseholdAliasesUseOnlyTheSharedAccount(ProviderAccountRequirement requirement)
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var personal = new ProviderAccountContext(Guid.CreateVersion7(), "fixture", ProviderAccountScope.Personal, 2,
            ownerUserId: context.Principal!.UserId);
        var shared = new ProviderAccountContext(Guid.CreateVersion7(), "fixture", ProviderAccountScope.Shared, 3);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), context.CancellationToken))
            .ReturnsAsync((ProviderRouteAccountRequest request, CancellationToken _) =>
            {
                var selected = request.AllowedScopes!.Contains(ProviderAccountScope.Personal) ? personal : shared;
                return new ProviderRouteAccountResolution(selected, selected.Revision);
            });
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.ResolveTargetAsync(It.IsAny<ProviderExecutionContext>(), recording,
                It.IsAny<ProviderTrackIdentityTarget>(), context.CancellationToken))
            .ReturnsAsync((ProviderExecutionContext execution, Guid _, ProviderTrackIdentityTarget _, CancellationToken _) =>
                new TrackIdentityResolution(recording, Guid.CreateVersion7(), new("fixture", ProviderResourceKind.Track,
                    execution.Account!.Scope == ProviderAccountScope.Shared ? "shared-alias" : "personal-alias"),
                    ProviderIdentityScope.Account, execution.Account.AccountId, ProviderIdentityVerification.Verified, "fixture", 1));
        var served = new List<Guid?>();
        var capability = Capability(served);
        var gateway = Gateway(requirement, capability.Object, accounts.Object, identities.Object);
        Assert.NotNull(await gateway.GetRecordingLyricsAsync(context, "fixture", recording));
        Assert.NotNull(await gateway.GetRecordingLyricsAsync(context, "fixture", recording, householdOnly: true));
        Assert.Equal(new Guid?[] { personal.AccountId, shared.AccountId }, served);
        identities.VerifyAll();
    }

    [Fact]
    public async Task AccountFreeHouseholdSourceNeverResolvesAnAccount()
    {
        var served = new List<Guid?>();
        var capability = Capability(served);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        var result = await Gateway(ProviderAccountRequirement.None, capability.Object, accounts.Object)
            .GetHouseholdLyricsAsync(Context(), "fixture", "exact");
        Assert.Equal("[00:01.234]Words", result?.Content);
        Assert.Null(Assert.Single(served));
        accounts.VerifyNoOtherCalls();
    }

    private static Mock<IProviderLyricsCapability> Capability(List<Guid?> served)
    {
        var capability = new Mock<IProviderLyricsCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("fixture");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Lyrics);
        capability.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .Callback<ProviderExecutionContext, ProviderLyricsRequest>((context, _) => served.Add(context.Account?.AccountId))
            .ReturnsAsync(ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available,
                "fixture", ProviderLyricsFormat.LineTimed, "[00:01.234]Words")));
        return capability;
    }

    private static ProtocolProviderGateway Gateway(ProviderAccountRequirement requirement,
        IProviderLyricsCapability capability, IProviderRouteAccountResolver accounts, ITrackIdentityService? identities = null)
    {
        var descriptor = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Lyrics,
            ProviderCapabilitySupportState.Supported, requirement, "1.0", ["fetchLyrics"],
            requirement == ProviderAccountRequirement.None ? [] : [ProviderAccountScope.Personal, ProviderAccountScope.Shared]);
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor("fixture", "Fixture", "Fixture",
            ProviderOrigin.BuiltIn, "1", "1.0", [descriptor], new ProviderPermissionDescriptor()), [capability])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unknown, false));
        var router = new ProviderRouter(registry, accounts, health.Object, Mock.Of<IProviderRouteSidecarSource>(), identities ?? Mock.Of<ITrackIdentityService>());
        return new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>());
    }

    private static ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "backend", "listener",
        new AllstarrPrincipal(Guid.CreateVersion7(), "jellyfin", "backend", "listener", "Listener", false),
        "household-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
}
