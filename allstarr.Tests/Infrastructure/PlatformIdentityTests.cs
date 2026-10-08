using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class PlatformIdentityTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableStorageState _state = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        var storage = new StorageOptions
        {
            DataDirectory = _database.StorageOptions.DataDirectory,
            DatabaseFileName = _database.StorageOptions.DatabaseFileName
        };
        _factory = new TestDbContextFactory(_database.Options);
        _state = new DurableStorageState(storage);
        _state.Set(DurableStorageReadiness.Ready, "fixture");
    }

    [Fact]
    public async Task HybridMode_MapsStableBackendIdentitiesToTenantScopedUsers()
    {
        var options = Options(MultiUserMode.Hybrid);
        var resolver = Resolver(options);

        var first = await resolver.ResolveAsync(new BackendIdentityDescriptor(
            "Jellyfin",
            "backend-user-1",
            "Listener One"));
        var repeated = await resolver.ResolveAsync(new BackendIdentityDescriptor(
            "Jellyfin",
            "backend-user-1",
            "Listener One Updated"));
        var second = await resolver.ResolveAsync(new BackendIdentityDescriptor(
            "Subsonic",
            "listener-two",
            "Listener Two"));

        Assert.NotNull(first);
        Assert.NotNull(repeated);
        Assert.NotNull(second);
        Assert.Equal(first.UserId, repeated.UserId);
        Assert.Equal(first.TenantId, second.TenantId);
        Assert.NotEqual(first.UserId, second.UserId);
        Assert.Equal("Listener One Updated", repeated.DisplayName);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await context.BackendIdentities.CountAsync());
        Assert.Equal(2, await context.Users.CountAsync());
    }

    [Fact]
    public async Task StrictMode_DoesNotAutoProvisionUnknownBackendIdentity()
    {
        var resolver = Resolver(Options(MultiUserMode.Strict));

        var principal = await resolver.ResolveAsync(new BackendIdentityDescriptor(
            "Jellyfin",
            "unknown-user"));

        Assert.Null(principal);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Empty(await context.Users.ToListAsync());
    }

    [Fact]
    public async Task DisabledMappedUser_IsDenied()
    {
        var resolver = Resolver(Options(MultiUserMode.Hybrid));
        var principal = await resolver.ResolveAsync(new BackendIdentityDescriptor(
            "Jellyfin",
            "disabled-user"));
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var user = await context.Users.SingleAsync(item => item.Id == principal!.UserId);
            user.Status = PlatformUserStatus.Disabled;
            await context.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            resolver.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "disabled-user")));
    }

    [Fact]
    public async Task AccountResolution_UsesPersonalBeforeSharedAndRejectsAnotherUsersAccount()
    {
        var resolver = Resolver(Options(MultiUserMode.Hybrid));
        var first = (await resolver.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "user-1")))!;
        var second = (await resolver.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "user-2")))!;
        var firstAccount = Account("applemusic", ProviderAccountScope.Personal, first.TenantId, first.UserId);
        var secondAccount = Account("applemusic", ProviderAccountScope.Personal, second.TenantId, second.UserId);
        var global = Account("applemusic", ProviderAccountScope.Shared, null, null);
        await AddAccounts(firstAccount, secondAccount, global);
        var accountResolver = new ProviderAccountResolver(_factory);

        var resolved = await accountResolver.ResolveAsync(new ProviderAccountResolutionRequest(
            first,
            "applemusic",
            "personal-library"));

        Assert.NotNull(resolved);
        Assert.Equal(firstAccount.Id, resolved.Account.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accountResolver.ResolveAsync(
            new ProviderAccountResolutionRequest(
                first,
                "applemusic",
                "personal-library",
                secondAccount.Id)));
    }

    [Fact]
    public async Task SharedAccounts_AreAvailableForEveryUserAndCapability()
    {
        var identities = Resolver(Options(MultiUserMode.Hybrid));
        var user = (await identities.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "listener")))!;
        var shared = Account("spotify", ProviderAccountScope.Shared, null, null);
        await AddAccounts(shared);
        var resolver = new ProviderAccountResolver(_factory);
        foreach (var capability in new[] { "streaming", "download", "playlist", "scrobbling", "favorites", "personal-library" })
        {
            var resolved = await resolver.ResolveAsync(new(user, "spotify", capability));
            Assert.Equal(shared.Id, resolved!.Account.Id);
            Assert.Equal("shared_account", resolved.Reason);
            Assert.Equal(shared.Id, (await resolver.ResolveAsync(new(user, "spotify", capability, shared.Id)))!.Account.Id);
        }
    }

    [Fact]
    public async Task PersonalAccount_PrecedesSharedForDownloadsAndFallsBackWhenDisabled()
    {
        var identities = Resolver(Options(MultiUserMode.Hybrid));
        var user = (await identities.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "listener")))!;
        var personal = Account("qobuz", ProviderAccountScope.Personal, user.TenantId, user.UserId);
        var shared = Account("qobuz", ProviderAccountScope.Shared, null, null);
        await AddAccounts(shared, personal);
        var resolver = new ProviderAccountResolver(_factory);
        foreach (var capability in new[] { "download", "playlist" })
            Assert.Equal(personal.Id, (await resolver.ResolveAsync(new(user, "qobuz", capability)))!.Account.Id);
        await using var db = await _factory.CreateDbContextAsync();
        var current = await db.ProviderAccounts.SingleAsync(item => item.Id == personal.Id);
        current.Enabled = false;
        await db.SaveChangesAsync();
        Assert.Equal(shared.Id, (await resolver.ResolveAsync(new(user, "qobuz", "download")))!.Account.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.ResolveAsync(new(user, "qobuz", "download", personal.Id)));
    }

    [Fact]
    public async Task AccountResolution_FiltersUnsupportedScopesAndRevokedCredentialsBeforeFallback()
    {
        var identities = Resolver(Options(MultiUserMode.Hybrid));
        var user = (await identities.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "listener")))!;
        var personal = Account("fixture-extension", ProviderAccountScope.Personal, user.TenantId, user.UserId);
        var shared = Account("fixture-extension", ProviderAccountScope.Shared, null, null);
        await AddAccounts(personal, shared);
        var resolver = new ProviderAccountResolver(_factory);
        Assert.Equal(shared.Id, (await resolver.ResolveAsync(new(user, "fixture-extension", "streaming",
            AllowedScopes: [ProviderAccountScope.Shared])))!.Account.Id);
        Assert.Null(await resolver.ResolveAsync(new(user, "fixture-extension", "streaming",
            AllowedScopes: [ProviderAccountScope.Shared], AllowSharedAccount: false)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.ResolveAsync(new(user,
            "fixture-extension", "streaming", personal.Id, AllowedScopes: [ProviderAccountScope.Shared])));
        await using var db = await _factory.CreateDbContextAsync();
        var secretId = Guid.CreateVersion7();
        db.SecretReferences.Add(new()
        {
            Id = secretId,
            TenantId = user.TenantId,
            Purpose = $"provider-account:fixture-extension:{personal.Id:N}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            RevokedAt = DateTimeOffset.UtcNow
        });
        (await db.ProviderAccounts.SingleAsync(item => item.Id == personal.Id)).SecretReferenceId = secretId;
        await db.SaveChangesAsync();
        Assert.Equal(shared.Id, (await resolver.ResolveAsync(new(user, "fixture-extension", "streaming")))!.Account.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.ResolveAsync(new(user,
            "fixture-extension", "streaming", personal.Id)));
    }

    [Theory]
    [InlineData("deezer", ProviderRouteHealthState.Unauthorized, false)]
    [InlineData("fixture-extension", ProviderRouteHealthState.Degraded, false)]
    [InlineData("fixture-extension", ProviderRouteHealthState.Healthy, true)]
    public async Task Routing_FallsBackFromUnusablePersonalAccountButHonorsExplicitSelection(
        string provider, ProviderRouteHealthState personalHealth, bool circuitOpen)
    {
        var identities = Resolver(Options(MultiUserMode.Hybrid));
        var user = (await identities.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "listener")))!;
        var personal = Account(provider, ProviderAccountScope.Personal, user.TenantId, user.UserId);
        var shared = Account(provider, ProviderAccountScope.Shared, null, null);
        await AddAccounts(personal, shared);
        var routes = new DurableProviderRouteAccountResolver(new ProviderAccountResolver(_factory),
            new AccountHealth(personal.Id, personalHealth, circuitOpen));
        var actor = new ProviderActorContext(user.TenantId, ProviderActorKind.User, user.UserId,
            new ProviderBackendPrincipal("jellyfin", "fixture", "listener"));
        var request = new ProviderRouteAccountRequest(actor, provider, ProviderCapabilityKind.Streaming,
            null, null, [ProviderAccountScope.Personal, ProviderAccountScope.Shared]);
        Assert.Equal(shared.Id, (await routes.ResolveAsync(request))!.Account.AccountId);
        Assert.Equal(personal.Id, (await routes.ResolveAsync(request with { RequestedAccountId = personal.Id }))!.Account.AccountId);
    }

    private sealed class AccountHealth(Guid personalId, ProviderRouteHealthState state, bool circuitOpen)
        : IProviderRouteHealthSource
    {
        public ProviderRouteHealthSnapshot Get(string providerId, Guid? providerAccountId, ProviderCapabilityKind capability) =>
            providerAccountId == personalId ? new(state, circuitOpen) : new(ProviderRouteHealthState.Healthy, false);
    }

    [Fact]
    public async Task AccountOwnership_DoesNotChangeWithLibraryAndAdministratorCannotImpersonateOwner()
    {
        var identities = Resolver(Options(MultiUserMode.Hybrid));
        var a = (await identities.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "a")))!;
        var b = (await identities.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "b")))!;
        var personal = Account("deezer", ProviderAccountScope.Personal, a.TenantId, a.UserId);
        await AddAccounts(personal);
        var resolver = new ProviderAccountResolver(_factory);
        Assert.Equal(personal.Id, (await resolver.ResolveAsync(new(a, "deezer", "metadata", LibraryScopeId: "other-library")))!.Account.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.ResolveAsync(
            new(b with { IsAdministrator = true }, "deezer", "metadata", personal.Id)));
    }

    private BackendIdentityResolver Resolver(IdentityOptions options) => new(
        _factory,
        _state,
        options,
        new SystemPlatformClock());

    private static IdentityOptions Options(MultiUserMode mode) => new()
    {
        Mode = mode.ToString(),
        DefaultTenantId = Guid.CreateVersion7().ToString(),
        SingleUserId = Guid.CreateVersion7().ToString(),
        DefaultTenantSlug = $"tenant-{Guid.NewGuid():N}",
        DefaultTenantName = "Fixture tenant",
        BackendInstanceId = "fixture-backend"
    };

    private static ProviderAccountRecord Account(
        string provider,
        ProviderAccountScope scope,
        Guid? tenantId,
        Guid? ownerId) => new()
        {
            Id = Guid.CreateVersion7(),
            ProviderId = provider,
            DisplayName = $"{provider} fixture",
            TenantId = tenantId,
            OwnerUserId = ownerId,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private async Task AddAccounts(params ProviderAccountRecord[] accounts)
    {
        await using var context = await _factory.CreateDbContextAsync();
        context.ProviderAccounts.AddRange(accounts);
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
