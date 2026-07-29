using allstarr.Controllers;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class FavoriteActionPolicyTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private Factory _factory = null!;
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _other = Guid.CreateVersion7();
    private FavoriteActionPolicyStore _store = null!;
    private DurableFavoriteActionPolicyResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = await _factory.CreateDbContextAsync(); await db.Database.MigrateAsync();
        db.Tenants.Add(new() { Id = _tenant, Slug = "policy", Name = "Policy", CreatedAt = Clock.Now });
        db.Users.AddRange(User(_user, "Owner"), User(_other, "Other"));
        db.BackendIdentities.AddRange(Identity(_user, "principal"), Identity(_other, "other-principal"));
        await db.SaveChangesAsync();
        _store = new(_factory, new Clock(), new FavoriteActionPolicyOptions { AddToVirtualLiked = true });
        _resolver = new(_factory, new FavoriteActionPolicyOptions { AddToVirtualLiked = true });
    }

    [Fact]
    public async Task Resolver_LayersExactUserOverrideOverExactTenantBackendPolicy()
    {
        await _store.UpsertAsync(new(_tenant, null, "jellyfin", "main", "music"), FavoriteActionPolicyScope.Global,
            new(true, true, false, false, false, true), _user);
        await _store.UpsertAsync(new(_tenant, _user, "jellyfin", "main", "music"), FavoriteActionPolicyScope.User,
            new(null, null, true, null, null, false), _user);

        var effective = await _resolver.ResolveAsync(_tenant, _user, "jellyfin", "main", "music");
        var otherLibrary = await _resolver.ResolveAsync(_tenant, _user, "jellyfin", "main", "another");

        Assert.True(effective.MatchLocalLibrary);
        Assert.True(effective.AutoDownload);
        Assert.False(effective.RefreshBackendLibrary);
        Assert.StartsWith("user-backend-override", effective.Source, StringComparison.Ordinal);
        Assert.False(otherLibrary.MatchLocalLibrary);
        Assert.False(otherLibrary.AutoDownload);
    }

    [Fact]
    public async Task Store_RejectsCrossTenantActorAndNeverWritesOtherUsersOverride()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _store.UpsertAsync(
            new(Guid.CreateVersion7(), _other, "jellyfin", "main", null), FavoriteActionPolicyScope.User,
            new(true, null, null, null, null, null), _user));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.FavoriteActionPolicies.ToListAsync());
    }

    [Fact]
    public async Task Store_ValidatesDependenciesAfterGlobalInheritance()
    {
        await _store.UpsertAsync(new(_tenant, null, "jellyfin", "main", null), FavoriteActionPolicyScope.Global,
            new(true, true, true, false, true, false), _user);
        await _store.UpsertAsync(new(_tenant, _user, "jellyfin", "main", null), FavoriteActionPolicyScope.User,
            new(null, null, null, true, null, null), _user);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpsertAsync(
            new(_tenant, _other, "jellyfin", "main", null), FavoriteActionPolicyScope.User,
            new(null, null, false, null, null, null), _other));
    }

    [Fact]
    public async Task UserApi_WritesOnlyCallerScopeAndAdminManagedModeDeniesOverride()
    {
        var allowed = Controller(Session(_user, false), ProviderAccountManagementMode.Hybrid);
        var result = await allowed.PutMine(new() { Protocol = "jellyfin", BackendInstanceId = "main", AutoDownload = true }, default);
        Assert.IsType<OkObjectResult>(result);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var record = await db.FavoriteActionPolicies.SingleAsync();
            Assert.Equal(_user, record.OwnerUserId);
            Assert.True(record.AutoDownload);
        }

        var denied = Controller(Session(_other, false), ProviderAccountManagementMode.AdminManaged);
        Assert.IsType<ObjectResult>(await denied.PutMine(new() { Protocol = "jellyfin", BackendInstanceId = "main" }, default));
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Single(await verify.FavoriteActionPolicies.ToListAsync());
    }

    [Fact]
    public async Task GlobalApi_RequiresAdministratorAndUsesSessionTenant()
    {
        var nonAdmin = Controller(Session(_user, false), ProviderAccountManagementMode.Hybrid);
        var forbidden = Assert.IsType<StatusCodeResult>(await nonAdmin.PutGlobal(
            GlobalRequest(), default));
        Assert.Equal(403, forbidden.StatusCode);

        var admin = Controller(Session(_user, true), ProviderAccountManagementMode.Hybrid);
        Assert.IsType<BadRequestObjectResult>(await admin.PutGlobal(
            new() { Protocol = "jellyfin", BackendInstanceId = "main", RefreshBackendLibrary = true }, default));
        Assert.IsType<OkObjectResult>(await admin.PutGlobal(
            GlobalRequest(), default));
        await using var db = await _factory.CreateDbContextAsync();
        var record = await db.FavoriteActionPolicies.SingleAsync();
        Assert.Equal(_tenant, record.TenantId); Assert.Null(record.OwnerUserId); Assert.True(record.RefreshBackendLibrary);
    }

    [Fact]
    public async Task SubsonicRefresh_RequiresAndResolvesOnlyExactTenantCredentialReference()
    {
        var credential = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SecretReferences.Add(new SecretReferenceRecord
            {
                Id = credential,
                TenantId = _tenant,
                Purpose = "subsonic-target:main",
                ActiveVersion = 1,
                CreatedAt = Clock.Now,
                UpdatedAt = Clock.Now
            });
            await db.SaveChangesAsync();
        }
        var values = new FavoriteActionPolicyValues(true, false, false, false, false, true, credential);
        var saved = await _store.UpsertAsync(new(_tenant, null, "subsonic", "main", "music"),
            FavoriteActionPolicyScope.Global, values, _user);
        var effective = await _resolver.ResolveAsync(_tenant, _user, "subsonic", "main", "music");

        Assert.Equal(credential, saved.TargetCredentialReferenceId);
        Assert.Equal(credential, effective.TargetCredentialReferenceId);
        Assert.Null((await _resolver.ResolveAsync(_tenant, _user, "subsonic", "main", "other")).TargetCredentialReferenceId);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpsertAsync(
            new(_tenant, null, "subsonic", "missing", null), FavoriteActionPolicyScope.Global,
            new(true, false, false, false, false, true), _user));
    }

    [Fact]
    public async Task RefreshCredential_CannotCrossTenantOrAttachToJellyfin()
    {
        var foreignTenant = Guid.CreateVersion7();
        var foreignCredential = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Tenants.Add(new TenantRecord { Id = foreignTenant, Slug = "foreign-policy", Name = "Foreign", CreatedAt = Clock.Now });
            db.SecretReferences.Add(new SecretReferenceRecord
            {
                Id = foreignCredential,
                TenantId = foreignTenant,
                Purpose = "subsonic-target:foreign",
                ActiveVersion = 1,
                CreatedAt = Clock.Now,
                UpdatedAt = Clock.Now
            });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _store.UpsertAsync(
            new(_tenant, null, "subsonic", "main", null), FavoriteActionPolicyScope.Global,
            new(true, false, false, false, false, true, foreignCredential), _user));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpsertAsync(
            new(_tenant, null, "jellyfin", "main", null), FavoriteActionPolicyScope.Global,
            new(true, false, false, false, false, true, foreignCredential), _user));
    }
    private static FavoriteActionPolicyUpdateRequest GlobalRequest() => new()
    {
        Protocol = "jellyfin",
        BackendInstanceId = "main",
        AddToVirtualLiked = true,
        MatchLocalLibrary = false,
        AutoDownload = false,
        EnrichMetadata = false,
        PlaceManagedFile = false,
        RefreshBackendLibrary = true
    };

    private FavoriteActionPoliciesController Controller(AdminAuthSession session, ProviderAccountManagementMode mode)
    {
        var controller = new FavoriteActionPoliciesController(_factory, _store, _resolver,
            new ProviderAccountManagementOptions { ManagementMode = mode.ToString() });
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        return controller;
    }
    private PlatformUserRecord User(Guid id, string name) => new()
    {
        Id = id,
        TenantId = _tenant,
        DisplayName = name,
        Status = PlatformUserStatus.Active,
        CreatedAt = Clock.Now,
        UpdatedAt = Clock.Now
    };
    private BackendIdentityRecord Identity(Guid user, string principal) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenant,
        UserId = user,
        BackendType = "jellyfin",
        BackendInstanceId = "main",
        PrincipalId = principal,
        CreatedAt = Clock.Now,
        LastSeenAt = Clock.Now
    };
    private AdminAuthSession Session(Guid user, bool admin) => new()
    {
        SessionId = "session",
        UserId = "backend",
        UserName = "User",
        IsAdministrator = admin,
        TenantId = _tenant,
        AllstarrUserId = user,
        JellyfinAccessToken = "fixture",
        ExpiresAtUtc = Clock.Now.UtcDateTime.AddHours(1),
        LastSeenUtc = Clock.Now.UtcDateTime
    };
    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
    private sealed class Clock : IPlatformClock { public static DateTimeOffset Now => new(2026, 7, 12, 22, 0, 0, TimeSpan.Zero); public DateTimeOffset UtcNow => Now; }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    { public AllstarrDbContext CreateDbContext() => new(options); public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext()); }
}
