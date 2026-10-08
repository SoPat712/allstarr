using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ProviderExecutionContextTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    [Fact]
    public void ExternalResourceId_IsTypedImmutableAndPreservesOpaqueValue()
    {
        var id = new ProviderExternalResourceId(
            "apple-musickit",
            ProviderResourceKind.Track,
            "MixedCase/opaque+value==",
            "us.catalog");

        Assert.Equal("apple-musickit", id.ProviderId);
        Assert.Equal(ProviderResourceKind.Track, id.ResourceKind);
        Assert.Equal("MixedCase/opaque+value==", id.Value);
        Assert.Equal("us.catalog", id.Catalog);
        Assert.Throws<ArgumentException>(() => new ProviderExternalResourceId(
            "AppleMusicKit",
            ProviderResourceKind.Track,
            "track-id"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderExternalResourceId(
            "apple-musickit",
            ProviderResourceKind.Unknown,
            "track-id"));
        Assert.Throws<ArgumentException>(() => id.RequireOwner(
            "apple-musickit",
            ProviderResourceKind.Playlist));
    }

    [Fact]
    public void ExternalResourceId_UsesTheDurableIdentityLengthBoundary()
    {
        var maximum = new ProviderExternalResourceId(
            "qobuz",
            ProviderResourceKind.Track,
            new string('x', 500));

        Assert.Equal(500, maximum.Value.Length);
        Assert.Throws<ArgumentException>(() => new ProviderExternalResourceId(
            "qobuz",
            ProviderResourceKind.Track,
            new string('x', 501)));
    }

    [Fact]
    public void ExecutionContext_RejectsAnotherTenantOrUserAccount()
    {
        var actor = UserActor();
        var policy = Policy("deezer");
        var anotherTenant = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "deezer",
            ProviderAccountScope.User,
            revision: 4,
            tenantId: Guid.CreateVersion7(),
            ownerUserId: _userId);
        var anotherUser = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "deezer",
            ProviderAccountScope.User,
            revision: 4,
            tenantId: _tenantId,
            ownerUserId: Guid.CreateVersion7());

        Assert.Throws<UnauthorizedAccessException>(() => Context(actor, anotherTenant, policy));
        Assert.Throws<UnauthorizedAccessException>(() => Context(actor, anotherUser, policy));
        Assert.Throws<UnauthorizedAccessException>(() => Context(
            actor,
            new ProviderAccountContext(
                Guid.CreateVersion7(),
                "deezer",
                ProviderAccountScope.User,
                revision: 4,
                enabled: false,
                tenantId: _tenantId,
                ownerUserId: _userId),
            policy));
    }

    [Fact]
    public void AdministratorActingForUser_CanUseOnlyThatUsersSelectedAccount()
    {
        var targetUserId = Guid.CreateVersion7();
        var administrator = new ProviderActorContext(
            _tenantId,
            ProviderActorKind.Administrator,
            _userId,
            new ProviderBackendPrincipal("jellyfin", "primary", "administrator"),
            actingForUserId: targetUserId);
        var targetAccount = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "spotify",
            ProviderAccountScope.User,
            revision: 3,
            tenantId: _tenantId,
            ownerUserId: targetUserId);

        var context = Context(administrator, targetAccount, Policy("spotify"));

        Assert.Equal(targetUserId, context.Actor.EffectiveUserId);
        Assert.Throws<UnauthorizedAccessException>(() => Context(
            administrator,
            new ProviderAccountContext(
                Guid.CreateVersion7(),
                "spotify",
                ProviderAccountScope.User,
                revision: 3,
                tenantId: _tenantId,
                ownerUserId: Guid.CreateVersion7()),
            Policy("spotify")));
    }

    [Fact]
    public void ExecutionContext_RequiresPolicyForGlobalAccountAndKeepsDeadlineCancellationAndIdempotency()
    {
        var global = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "qobuz",
            ProviderAccountScope.Global,
            revision: 2);
        Assert.Throws<UnauthorizedAccessException>(() =>
            Context(UserActor(), global, Policy("qobuz", allowSharedAccount: false)));

        using var cancellation = new CancellationTokenSource();
        var now = DateTimeOffset.UtcNow;
        var context = new ProviderExecutionContext(
            UserActor(),
            "qobuz",
            global,
            library: null,
            Policy("qobuz", allowSharedAccount: true),
            operationId: "operation-17",
            correlationId: "correlation-17",
            deadline: now.AddSeconds(30),
            cancellation.Token,
            idempotencyKey: "download:user:track");

        Assert.Equal("download:user:track", context.RequireIdempotencyKey());
        Assert.Equal(TimeSpan.FromSeconds(30), context.Remaining(now));
        Assert.False(context.IsExpired(now));
        Assert.Equal(cancellation.Token, context.CancellationToken);
    }

    [Fact]
    public void LibraryAccount_RequiresTheExactLibraryContext()
    {
        var account = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "spotify",
            ProviderAccountScope.Library,
            revision: 1,
            tenantId: _tenantId,
            libraryScopeId: "library-a");
        var policy = Policy("spotify");

        Assert.Throws<ArgumentException>(() => Context(UserActor(), account, policy));
        Assert.Throws<UnauthorizedAccessException>(() => Context(
            UserActor(),
            account,
            policy,
            new ProviderLibraryContext(_tenantId, "library-b")));

        var valid = Context(
            UserActor(),
            account,
            policy,
            new ProviderLibraryContext(_tenantId, "library-a"));
        Assert.Equal("library-a", valid.Library!.ScopeId);
    }

    [Fact]
    public void ProviderOutcome_HasOneTypedSafeFailureAndRateLimitTiming()
    {
        var success = ProviderOutcome<string>.Success("value");
        var failure = ProviderOutcome<string>.Failure(new ProviderError(
            ProviderErrorKind.IncompatibleMedia));

        Assert.True(success.IsSuccess);
        Assert.Equal("value", success.RequireValue());
        Assert.False(failure.IsSuccess);
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, failure.Error!.Kind);
        Assert.Throws<InvalidOperationException>(() => failure.RequireValue());
        Assert.Throws<ArgumentException>(() => new ProviderError(
            ProviderErrorKind.RateLimited));

        var rateLimited = new ProviderError(
            ProviderErrorKind.RateLimited,
            TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(15), rateLimited.RetryAfter);
        Assert.Equal("rate-limited", rateLimited.Code);
        Assert.Equal("The provider rate limit was reached.", rateLimited.SafeMessage);
        Assert.DoesNotContain(
            typeof(ProviderError).GetConstructors().SelectMany(item => item.GetParameters()),
            parameter => parameter.ParameterType == typeof(string));
    }

    private ProviderActorContext UserActor() => new(
        _tenantId,
        ProviderActorKind.User,
        _userId,
        new ProviderBackendPrincipal("jellyfin", "primary", "backend-user"));

    private static ProviderExecutionPolicy Policy(
        string providerId,
        bool allowSharedAccount = false) => new(
        new ProviderQualityPolicy(
            ProviderAudioQuality.Any,
            ProviderAudioQuality.HighResolution,
            allowTranscode: true),
        ProviderExplicitContentPolicy.Allow,
        allowFallback: true,
        allowSharedAccount,
        allowManagedDownloads: true,
        [providerId]);

    private static ProviderExecutionContext Context(
        ProviderActorContext actor,
        ProviderAccountContext account,
        ProviderExecutionPolicy policy,
        ProviderLibraryContext? library = null) => new(
        actor,
        account.ProviderId,
        account,
        library,
        policy,
        "operation",
        "correlation",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);
}
