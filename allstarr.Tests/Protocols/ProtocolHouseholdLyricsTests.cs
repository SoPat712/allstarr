using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using allstarr.Core.Settings;
using allstarr.Core.Providers.Lyrics;
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
    [Fact]
    public async Task SpicyHouseholdRouteSelectsOnlyItsSharedRequiredAccount()
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var source = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets());
        var registry = new ProviderRegistry([SpicyLyricsCapabilityAdapter.CreateRegistration(source)]);
        var account = new ProviderAccountContext(Guid.NewGuid(), "spicylyrics", ProviderAccountScope.Shared, 1, secretReferenceId: SpicyLyricsTestData.SecretId);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderRouteAccountRequest request, CancellationToken _) =>
            {
                Assert.Equal(new[] { ProviderAccountScope.Shared }, request.AllowedScopes);
                return new ProviderRouteAccountResolution(account, 1);
            });
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>())).Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unknown, false));
        var router = new ProviderRouter(registry, accounts.Object, health.Object, Mock.Of<IProviderRouteSidecarSource>(), Mock.Of<ITrackIdentityService>());
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(), audioVerifier: FixtureAudioVerifier.Instance);
        var result = await gateway.GetHouseholdLyricsAsync(Context(), "spicylyrics", SpicyLyricsTestData.TrackId);
        Assert.Equal("First plain line\nSecond plain line", result!.Content);
        Assert.Equal(1, handler.Calls);
        accounts.VerifyAll();
    }

    [Fact]
    public async Task AutomaticFallbackRejectsDefaultManualOnlyWhileExplicitReadRemainsAvailable()
    {
        var served = new List<Guid?>();
        var capability = Capability(served);
        var gateway = Gateway(ProviderAccountRequirement.None, capability.Object, Mock.Of<IProviderRouteAccountResolver>(),
            defaultMode: ProviderUseMode.ManualOnly);
        var context = Context();
        var target = new ProviderExternalResourceId("fixture", ProviderResourceKind.Track, "exact");
        var execution = (IProtocolLyricsExecutionGateway)gateway;
        var prepared = await execution.PrepareAsync(new(context.Actor!, "fixture", target, new(Guid.NewGuid(), target),
            [ProviderAccountScope.Personal, ProviderAccountScope.Shared], context.CorrelationId, context.Deadline), default);
        if (prepared != null) await execution.ExecuteAsync(prepared, LyricsCompletionMode.OwnedAutomatic, default);
        Assert.Empty(served);
        Assert.Null(prepared);
        Assert.NotNull(await gateway.GetLyricsAsync(context, "fixture", "exact"));
        Assert.Single(served);
    }

    [Fact]
    public async Task HouseholdAutomaticUsesGlobalPolicyAndSharedAccountsWhenPersonalSourceIsOff()
    {
        var context = Context();
        var global = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "lossless", 0.07);
        var personal = global with { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("fixture", ProviderUseMode.Off) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(global);
        policies.Setup(item => item.ResolveForUserAsync(context.Principal!.UserId, It.IsAny<CancellationToken>())).ReturnsAsync(personal);
        var shared = new ProviderAccountContext(Guid.NewGuid(), "fixture", ProviderAccountScope.Shared, 3);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderRouteAccountRequest request, CancellationToken _) =>
            {
                Assert.Equal(context.Actor, request.Actor);
                Assert.Equal(new[] { ProviderAccountScope.Shared }, request.AllowedScopes);
                return new ProviderRouteAccountResolution(shared, shared.Revision);
            });
        var served = new List<Guid?>();
        var capability = Capability(served);
        var gateway = Gateway(ProviderAccountRequirement.Required, capability.Object, accounts.Object, policies: policies.Object);
        var target = new ProviderExternalResourceId("fixture", ProviderResourceKind.Track, "exact");
        var execution = (IProtocolLyricsExecutionGateway)gateway;
        var prepared = await execution.PrepareAsync(new(context.Actor!, "fixture", target,
            new(Guid.NewGuid(), target), [ProviderAccountScope.Shared], context.CorrelationId, context.Deadline, HouseholdOnly: true), default);
        Assert.NotNull(prepared);
        Assert.False(prepared.MaySpeculate);
        Assert.NotNull(await execution.ExecuteAsync(prepared, LyricsCompletionMode.RequestBound, default));
        Assert.Equal(new Guid?[] { shared.AccountId }, served);
        Assert.Null(await execution.PrepareAsync(new(context.Actor!, "fixture", target,
            new(Guid.NewGuid(), target), [ProviderAccountScope.Personal, ProviderAccountScope.Shared], context.CorrelationId, context.Deadline), default));
        var personalReads = policies.Invocations.Count(item => item.Method.Name == nameof(IEffectiveProviderPolicyResolver.ResolveForUserAsync));
        var callbacks = 0;
        var resolver = new ProtocolLyricsResolver(gateway, NullLogger<ProtocolLyricsResolver>.Instance, effectivePolicies: policies.Object);
        var answer = await resolver.FindAsync(context, new Song
        {
            Title = "Fixture",
            Artist = "Artist",
            Artists = ["Artist"],
            Album = "Album",
            ExternalProvider = "fixture",
            ExternalId = "exact"
        }, "recording", new ProtocolLyricsSources(
                Backend: _ => { callbacks++; throw new InvalidOperationException("Unexpected personal callback."); },
                Kept: _ => { callbacks++; throw new InvalidOperationException("Unexpected personal callback."); }, HouseholdOnly: true));
        Assert.Equal("fixture", answer?.Source);
        Assert.Equal(0, callbacks);
        Assert.Equal(new Guid?[] { shared.AccountId, shared.AccountId }, served);
        Assert.Equal(personalReads, policies.Invocations.Count(item => item.Method.Name == nameof(IEffectiveProviderPolicyResolver.ResolveForUserAsync)));
        await resolver.StopAsync(default);
    }

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
        IProviderLyricsCapability capability, IProviderRouteAccountResolver accounts, ITrackIdentityService? identities = null, IEffectiveProviderPolicyResolver? policies = null, ProviderUseMode defaultMode = ProviderUseMode.Automatic)
    {
        var descriptor = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Lyrics,
            ProviderCapabilitySupportState.Supported, requirement, "1.0", ["fetchLyrics"],
            requirement == ProviderAccountRequirement.None ? [] : [ProviderAccountScope.Personal, ProviderAccountScope.Shared]);
        var definition = new ProviderDescriptor("fixture", "Fixture", "Fixture", ProviderOrigin.BuiltIn, "1", "1.0", [descriptor],
            new ProviderPermissionDescriptor())
        { DefaultUseMode = defaultMode };
        var registry = new ProviderRegistry([new ProviderRegistration(definition, [capability])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unknown, false));
        var router = new ProviderRouter(registry, accounts, health.Object, Mock.Of<IProviderRouteSidecarSource>(), identities ?? Mock.Of<ITrackIdentityService>(), effectivePolicies: policies);
        return new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(), effectivePolicies: policies, audioVerifier: FixtureAudioVerifier.Instance);
    }

    private static ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "backend", "listener",
        new AllstarrPrincipal(Guid.CreateVersion7(), "jellyfin", "backend", "listener", "Listener", false),
        "household-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
}
