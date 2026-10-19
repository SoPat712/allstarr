using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderDiagnosticExecutionFactoryTests
{
    private const string Provider = "fixture";
    private static readonly DateTimeOffset Deadline = new(2026, 10, 19, 12, 1, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false, 400, "account_required")]
    [InlineData(false, true, 400, "account_required")]
    [InlineData(true, false, 409, "sign_in_needed")]
    [InlineData(true, true, 409, "sign_in_needed")]
    public async Task Required_account_must_be_selected_before_resolution(bool signed, bool emptyId,
        int status, string reason)
    {
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        var factory = Factory(ProviderAccountRequirement.Required, accounts, signed);

        var result = await Create(factory, emptyId ? Guid.Empty : null);

        AssertFailure(result, status, reason);
        accounts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Account_free_capability_rejects_a_selected_account()
    {
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        var factory = Factory(ProviderAccountRequirement.None, accounts);

        var result = await Create(factory, Guid.NewGuid());

        AssertFailure(result, 400, "account_not_used");
        accounts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Optional_account_can_remain_unresolved()
    {
        var actor = Actor();
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(It.Is<ProviderRouteAccountRequest>(request =>
                request.Actor == actor && request.ProviderId == Provider &&
                request.Capability == ProviderCapabilityKind.Streaming && request.RequestedAccountId == null &&
                !request.AllowManagedDiagnosticAccount), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderRouteAccountResolution?)null);
        var factory = Factory(ProviderAccountRequirement.Optional, accounts);

        var result = await Create(factory, actor: actor);

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.ReasonCode);
        Assert.Null(Assert.IsType<ProviderExecutionContext>(result.Context).Account);
        accounts.VerifyAll();
    }

    [Fact]
    public async Task Signed_provider_requires_a_signed_in_session_for_the_selected_account()
    {
        var selected = Guid.NewGuid();
        var accounts = Resolved(Shared(selected));
        var sessions = new Mock<IProviderCheckSessions>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        sessions.Setup(item => item.IsSignedInAsync(Provider, selected, cancellation.Token)).ReturnsAsync(false);
        var factory = Factory(ProviderAccountRequirement.Required, accounts, true, sessions.Object);

        var result = await Create(factory, selected, token: cancellation.Token);

        AssertFailure(result, 409, "sign_in_needed");
        sessions.VerifyAll();
    }

    [Fact]
    public async Task Selected_account_cannot_be_substituted_by_the_resolver()
    {
        var selected = Guid.NewGuid();
        var accounts = Resolved(Shared(Guid.NewGuid()));
        var factory = Factory(ProviderAccountRequirement.Required, accounts);

        var result = await Create(factory, selected);

        AssertFailure(result, 404, "account_unavailable");
        accounts.Verify(item => item.ResolveAsync(It.Is<ProviderRouteAccountRequest>(request =>
            request.RequestedAccountId == selected && request.AllowManagedDiagnosticAccount),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Required_selected_account_must_resolve()
    {
        var accounts = Resolved(null);
        var factory = Factory(ProviderAccountRequirement.Required, accounts);

        var result = await Create(factory, Guid.NewGuid());

        AssertFailure(result, 404, "account_unavailable");
    }

    [Fact]
    public async Task Account_audience_denial_becomes_forbidden()
    {
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("Unavailable account"));
        var factory = Factory(ProviderAccountRequirement.Required, accounts);

        var result = await Create(factory, Guid.NewGuid());

        AssertFailure(result, 403, "account_forbidden");
    }

    [Fact]
    public async Task Administrator_keeps_audit_identity_while_executing_as_personal_account_owner()
    {
        var actor = Actor();
        var owner = Guid.NewGuid();
        var account = new ProviderAccountContext(Guid.NewGuid(), Provider, ProviderAccountScope.Personal,
            7, ownerUserId: owner);
        var accounts = Resolved(account);
        var factory = Factory(ProviderAccountRequirement.Required, accounts);

        var result = await Create(factory, account.AccountId, actor);

        Assert.Equal(200, result.StatusCode);
        var context = Assert.IsType<ProviderExecutionContext>(result.Context);
        Assert.Same(account, context.Account);
        Assert.Equal(actor.Kind, context.Actor.Kind);
        Assert.Equal(actor.UserId, context.Actor.UserId);
        Assert.Same(actor.BackendPrincipal, context.Actor.BackendPrincipal);
        Assert.Equal(actor.DurableJobId, context.Actor.DurableJobId);
        Assert.Equal(owner, context.Actor.ActingForUserId);
        Assert.Equal(owner, context.Actor.EffectiveUserId);
        accounts.Verify(item => item.ResolveAsync(It.Is<ProviderRouteAccountRequest>(request =>
            request.Actor == actor && request.RequestedAccountId == account.AccountId &&
            request.AllowManagedDiagnosticAccount), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execution_is_restricted_to_the_selected_provider_and_original_deadline()
    {
        var actor = Actor();
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        var factory = Factory(ProviderAccountRequirement.None, accounts);
        using var cancellation = new CancellationTokenSource();

        var result = await Create(factory, actor: actor, token: cancellation.Token);

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.ReasonCode);
        var context = Assert.IsType<ProviderExecutionContext>(result.Context);
        Assert.Same(actor, context.Actor);
        Assert.Equal(Provider, context.ProviderId);
        Assert.Null(context.Account);
        Assert.False(context.Policy.AllowFallback);
        Assert.Equal(Provider, Assert.Single(context.Policy.AllowedProviderIds));
        Assert.True(context.Policy.AllowsProvider(Provider));
        Assert.False(context.Policy.AllowsProvider("other"));
        Assert.True(context.Policy.AllowSharedAccount);
        Assert.True(context.Policy.AllowManagedDownloads);
        Assert.Equal(Deadline, context.Deadline);
        Assert.Equal(cancellation.Token, context.CancellationToken);
        Assert.Equal("diagnostic-request", context.CorrelationId);
        Assert.Equal("provider-diagnostic", context.OperationId);
        accounts.VerifyNoOtherCalls();
    }

    private static Task<ProviderDiagnosticExecution> Create(ProviderDiagnosticExecutionFactory factory,
        Guid? accountId = null, ProviderActorContext? actor = null, CancellationToken token = default) =>
        factory.CreateAsync(actor ?? Actor(), Provider, ProviderCapabilityKind.Streaming, accountId,
            "diagnostic-request", Deadline, token);

    private static ProviderDiagnosticExecutionFactory Factory(ProviderAccountRequirement requirement,
        Mock<IProviderRouteAccountResolver> accounts, bool signed = false, IProviderCheckSessions? sessions = null)
    {
        var descriptor = new ProviderDescriptor(Provider, "Fixture", "Fixture", ProviderOrigin.Extension, "1", "1",
            [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, requirement, "1", ["getStreamLease"],
                requirement == ProviderAccountRequirement.None ? [] : [ProviderAccountScope.Shared, ProviderAccountScope.Personal])], new())
        { UsesSignedSession = signed };
        var registry = new Mock<IProviderRegistry>(MockBehavior.Strict);
        registry.Setup(item => item.GetRequired(Provider)).Returns(descriptor);
        return new ProviderDiagnosticExecutionFactory(registry.Object, accounts.Object, sessions);
    }

    private static Mock<IProviderRouteAccountResolver> Resolved(ProviderAccountContext? account)
    {
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account == null ? null : new ProviderRouteAccountResolution(account, account.Revision));
        return accounts;
    }

    private static ProviderAccountContext Shared(Guid accountId) => new(accountId, Provider,
        ProviderAccountScope.Shared, 3);

    private static ProviderActorContext Actor() => new(ProviderActorKind.Administrator, Guid.NewGuid(),
        new ProviderBackendPrincipal("jellyfin", "fixture", "administrator"));

    private static void AssertFailure(ProviderDiagnosticExecution result, int status, string reason)
    {
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Null(result.Context);
    }
}
