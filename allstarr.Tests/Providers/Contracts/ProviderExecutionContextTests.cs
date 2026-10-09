using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ProviderExecutionContextTests
{
    private readonly Guid _userId = Guid.CreateVersion7();

    [Fact]
    public void PublicRead_HasNoIdentityAndCannotUseAnyAccount()
    {
        var actor = new ProviderActorContext(ProviderActorKind.PublicRead, null);
        Assert.Null(actor.EffectiveUserId);
        Assert.Null(actor.BackendPrincipal);
        Assert.Null(actor.DurableJobId);
        Assert.Throws<ArgumentException>(() => new ProviderActorContext(ProviderActorKind.PublicRead, _userId));
        Assert.Throws<ArgumentException>(() => new ProviderActorContext(ProviderActorKind.PublicRead, null,
            new ProviderBackendPrincipal("jellyfin", "primary", "listener")));
        Assert.Throws<ArgumentException>(() => new ProviderActorContext(ProviderActorKind.PublicRead, null,
            durableJobId: Guid.CreateVersion7()));
        Assert.Throws<ArgumentException>(() => new ProviderActorContext(ProviderActorKind.PublicRead, null,
            actingForUserId: _userId));
        foreach (var scope in new[] { ProviderAccountScope.Personal, ProviderAccountScope.Shared })
        {
            var account = new ProviderAccountContext(Guid.CreateVersion7(), "deezer", scope, 1,
                ownerUserId: scope == ProviderAccountScope.Personal ? _userId : null);
            Assert.Throws<UnauthorizedAccessException>(() => Context(actor, account, Policy("deezer", true)));
        }
    }

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
    public void ExecutionContext_RejectsAnotherUserOrDisabledAccount()
    {
        var actor = UserActor();
        var policy = Policy("deezer");
        var anotherUser = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "deezer",
            ProviderAccountScope.Personal,
            revision: 4,
            ownerUserId: Guid.CreateVersion7());

        Assert.Throws<UnauthorizedAccessException>(() => Context(actor, anotherUser, policy));
        Assert.Throws<UnauthorizedAccessException>(() => Context(
            actor,
            new ProviderAccountContext(
                Guid.CreateVersion7(),
                "deezer",
                ProviderAccountScope.Personal,
                revision: 4,
                enabled: false,
                ownerUserId: _userId),
            policy));
    }

    [Fact]
    public void AdministratorActingForUser_CanUseOnlyThatUsersSelectedAccount()
    {
        var targetUserId = Guid.CreateVersion7();
        var administrator = new ProviderActorContext(
            ProviderActorKind.Administrator,
            _userId,
            new ProviderBackendPrincipal("jellyfin", "primary", "administrator"),
            actingForUserId: targetUserId);
        var targetAccount = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "spotify",
            ProviderAccountScope.Personal,
            revision: 3,
            ownerUserId: targetUserId);

        var context = Context(administrator, targetAccount, Policy("spotify"));

        Assert.Equal(targetUserId, context.Actor.EffectiveUserId);
        Assert.Throws<UnauthorizedAccessException>(() => Context(
            administrator,
            new ProviderAccountContext(
                Guid.CreateVersion7(),
                "spotify",
                ProviderAccountScope.Personal,
                revision: 3,
                ownerUserId: Guid.CreateVersion7()),
            Policy("spotify")));
    }

    [Fact]
    public void ExecutionContext_RequiresPolicyForGlobalAccountAndKeepsDeadlineCancellationAndIdempotency()
    {
        var global = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "qobuz",
            ProviderAccountScope.Shared,
            revision: 2);
        Assert.Throws<UnauthorizedAccessException>(() =>
            Context(UserActor(), global, Policy("qobuz", allowSharedAccount: false)));

        using var cancellation = new CancellationTokenSource();
        var now = DateTimeOffset.UtcNow;
        var context = new ProviderExecutionContext(
            UserActor(),
            "qobuz",
            global,
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
    public void Accounts_RequireOwnershipThatMatchesTheirScope()
    {
        Assert.Throws<ArgumentException>(() => new ProviderAccountContext(
            Guid.CreateVersion7(), "spotify", ProviderAccountScope.Personal, 1,
            ownerUserId: null));
        Assert.Throws<ArgumentException>(() => new ProviderAccountContext(
            Guid.CreateVersion7(), "spotify", ProviderAccountScope.Shared, 1,
            ownerUserId: _userId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderAccountContext(
            Guid.CreateVersion7(), "spotify", (ProviderAccountScope)99, 1));

        var account = new ProviderAccountContext(
            Guid.CreateVersion7(), "spotify", ProviderAccountScope.Personal, 1,
            ownerUserId: _userId);
        Assert.Same(account, Context(UserActor(), account, Policy("spotify")).Account);
    }

    [Fact]
    public void Actors_RequireVerifiedUserIdentityAndDurableSystemJobAuthority()
    {
        Assert.Throws<ArgumentException>(() => new ProviderActorContext(
            ProviderActorKind.User,
            _userId));
        Assert.Throws<ArgumentException>(() => new ProviderActorContext(
            ProviderActorKind.User,
            Guid.Empty,
            new ProviderBackendPrincipal("jellyfin", "primary", "backend-user")));
        Assert.Throws<ArgumentException>(() => new ProviderActorContext(
            ProviderActorKind.SystemJob,
            userId: null));

        var durableJobId = Guid.CreateVersion7();
        var system = new ProviderActorContext(
            ProviderActorKind.SystemJob,
            userId: null,
            durableJobId: durableJobId,
            actingForUserId: _userId);

        Assert.Equal(durableJobId, system.DurableJobId);
        Assert.Equal(_userId, system.EffectiveUserId);
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
        ProviderExecutionPolicy policy) => new(
        actor,
        account.ProviderId,
        account,
        policy,
        "operation",
        "correlation",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);
}
