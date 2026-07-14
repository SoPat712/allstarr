using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class PlatformIdentityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private TestDbContextFactory _factory = null!;
    private DurableStorageState _state = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var storage = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "identity.db")}"
        };
        var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(storage.ConnectionString)
            .Options;
        _factory = new TestDbContextFactory(dbOptions);
        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
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
    public async Task AccountResolution_IsolatesUsersAndDoesNotUseGlobalForPersonalData()
    {
        var resolver = Resolver(Options(MultiUserMode.Hybrid));
        var first = (await resolver.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "user-1")))!;
        var second = (await resolver.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "user-2")))!;
        var firstAccount = Account("applemusic", ProviderAccountScope.User, first.TenantId, first.UserId);
        var secondAccount = Account("applemusic", ProviderAccountScope.User, second.TenantId, second.UserId);
        var global = Account("applemusic", ProviderAccountScope.Global, null, null);
        await AddAccounts(firstAccount, secondAccount, global);
        var accountResolver = new ProviderAccountResolver(
            _factory,
            new ProviderPolicyOptions
            {
                AllowGlobalAccounts = true,
                AllowGlobalPersonalAccounts = false
            });

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
    public async Task SharedDownloaderPolicy_AppliesOnlyToDownloadLane()
    {
        var identityResolver = Resolver(Options(MultiUserMode.Hybrid));
        var principal = (await identityResolver.ResolveAsync(
            new BackendIdentityDescriptor("Subsonic", "listener")))!;
        var userAccount = Account("qobuz", ProviderAccountScope.User, principal.TenantId, principal.UserId);
        var shared = Account("qobuz", ProviderAccountScope.Global, null, null);
        await AddAccounts(userAccount, shared);
        var resolver = new ProviderAccountResolver(
            _factory,
            new ProviderPolicyOptions
            {
                AllowGlobalAccounts = true,
                AllowGlobalPersonalAccounts = false,
                SharedDownloaderAccountId = shared.Id
            });

        var download = await resolver.ResolveAsync(new ProviderAccountResolutionRequest(
            principal,
            "qobuz",
            "download"));
        var playlist = await resolver.ResolveAsync(new ProviderAccountResolutionRequest(
            principal,
            "qobuz",
            "playlist"));

        Assert.Equal(shared.Id, download!.Account.Id);
        Assert.Equal("policy_shared_downloader", download.Reason);
        Assert.Equal(userAccount.Id, playlist!.Account.Id);
        Assert.Equal("user_account", playlist.Reason);
    }

    [Fact]
    public async Task LibraryAccount_RequiresExactTenantAndLibraryScope()
    {
        var identityResolver = Resolver(Options(MultiUserMode.Hybrid));
        var principal = (await identityResolver.ResolveAsync(
            new BackendIdentityDescriptor("Jellyfin", "library-user")))!;
        var account = Account("deezer", ProviderAccountScope.Library, principal.TenantId, null);
        account.LibraryScopeId = "music-main";
        await AddAccounts(account);
        var resolver = new ProviderAccountResolver(_factory, new ProviderPolicyOptions());

        var missingScope = await resolver.ResolveAsync(new ProviderAccountResolutionRequest(
            principal,
            "deezer",
            "metadata"));
        var correctScope = await resolver.ResolveAsync(new ProviderAccountResolutionRequest(
            principal,
            "deezer",
            "metadata",
            LibraryScopeId: "music-main"));

        Assert.Null(missingScope);
        Assert.Equal(account.Id, correctScope!.Account.Id);
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
            Scope = scope,
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

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
