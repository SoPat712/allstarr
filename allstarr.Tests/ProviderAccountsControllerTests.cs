using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ProviderAccountsControllerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _otherUserId = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private EncryptedSecretStore _secretStore = null!;
    private readonly TestMemoryApplicationCache _cache = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var keyPath = Path.Combine(_root, "keyring.json");
        await File.WriteAllTextAsync(
            keyPath,
            JsonSerializer.Serialize(new
            {
                activeKeyId = "key-1",
                keys = new Dictionary<string, string>
                {
                    ["key-1"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                }
            }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
        context.Tenants.Add(new TenantRecord
        {
            Id = _tenantId,
            Slug = "fixture",
            Name = "Fixture tenant",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Users.AddRange(
            User(_userId, "User one"),
            User(_otherUserId, "User two"));
        await context.SaveChangesAsync();
        var options = new SecretStoreOptions { KeyRingPath = keyPath };
        _secretStore = new EncryptedSecretStore(
            _factory,
            new FileSecretKeyRingProvider(options),
            options,
            new SystemPlatformClock());
    }

    [Fact]
    public async Task UserCreate_OverridesSpoofedOwnershipAndNeverEchoesSecret()
    {
        var controller = Controller(Session(_userId));
        using var secret = JsonDocument.Parse("""{"accessToken":"fixture-private-token"}""");

        var result = await controller.Create(new ProviderAccountsController.CreateProviderAccountRequest
        {
            ProviderId = "qobuz",
            DisplayName = "My Qobuz",
            Scope = "User",
            TenantId = Guid.CreateVersion7(),
            OwnerUserId = _otherUserId,
            Secret = secret.RootElement.Clone()
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var response = JsonSerializer.Serialize(created.Value);
        Assert.DoesNotContain("fixture-private-token", response, StringComparison.Ordinal);
        await using var context = await _factory.CreateDbContextAsync();
        var account = await context.ProviderAccounts.SingleAsync();
        Assert.Equal(_tenantId, account.TenantId);
        Assert.Equal(_userId, account.OwnerUserId);
        Assert.Equal(ProviderAccountScope.User, account.Scope);
        Assert.True(account.Enabled);
        Assert.NotNull(account.SecretReferenceId);
        Assert.Single(await context.AuditEvents.ToListAsync());
        using var lease = await _secretStore.OpenAsync(
            account.SecretReferenceId!.Value,
            new SecretAccessContext(_tenantId));
        Assert.Contains("fixture-private-token", lease.ReadUtf8(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserList_ReturnsOnlyOwnAccountsWithSecretMetadata()
    {
        await CreateUserAccount(_userId, "deezer", "First account");
        await CreateUserAccount(_otherUserId, "qobuz", "Other account");
        var controller = Controller(Session(_userId));

        var result = await controller.List();

        var ok = Assert.IsType<OkObjectResult>(result);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var accounts = payload.RootElement.GetProperty("accounts");
        Assert.Equal(1, accounts.GetArrayLength());
        Assert.Equal("deezer", accounts[0].GetProperty("ProviderId").GetString());
        Assert.True(accounts[0].GetProperty("secret").GetProperty("configured").GetBoolean());
        Assert.False(accounts[0].GetProperty("secret").TryGetProperty("value", out _));
    }

    [Fact]
    public async Task ImportedDisabledAccount_CanBeEnabledWithoutReplacingItsCredential()
    {
        var account = await CreateUserAccount(_userId, "spotify", "Imported Spotify");
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var persisted = await context.ProviderAccounts.SingleAsync(item => item.Id == account.Id);
            persisted.Enabled = false;
            await context.SaveChangesAsync();
            account = persisted;
        }

        var result = Assert.IsType<OkObjectResult>(await Controller(Session(_userId)).SetEnabled(
            account.Id,
            new ProviderAccountsController.SetProviderAccountEnabledRequest
            {
                Enabled = true,
                ExpectedRevision = account.Revision
            }));

        Assert.DoesNotContain("secretReferenceFixture", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        var cacheKey = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            _tenantId, _userId, account.Id, account.Revision, "spotify", null, null, 100);
        await _cache.SetStringAsync(cacheKey, "{}");
        await Controller(Session(_userId)).SetEnabled(
            account.Id,
            new ProviderAccountsController.SetProviderAccountEnabledRequest
            {
                Enabled = false
            });
        Assert.False(await _cache.ExistsAsync(cacheKey));
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.False((await verification.ProviderAccounts.SingleAsync(item => item.Id == account.Id)).Enabled);
    }

    [Fact]
    public async Task UserCannotCreateGlobalAccountOrReplaceAnotherUsersSecret()
    {
        var other = await CreateUserAccount(_otherUserId, "deezer", "Other account");
        var controller = Controller(Session(_userId));
        using var replacement = JsonDocument.Parse("""{"token":"replacement"}""");

        var global = await controller.Create(new ProviderAccountsController.CreateProviderAccountRequest
        {
            ProviderId = "deezer",
            DisplayName = "Shared",
            Scope = "Global"
        });
        var replace = await controller.ReplaceSecret(
            other.Id,
            new ProviderAccountsController.ReplaceProviderSecretRequest
            {
                Secret = replacement.RootElement.Clone()
            });

        Assert.IsType<BadRequestObjectResult>(global);
        Assert.IsType<NotFoundResult>(replace);
    }

    [Theory]
    [InlineData(ProviderAccountManagementMode.AdminManaged)]
    [InlineData(ProviderAccountManagementMode.Hybrid)]
    public async Task AdministratorControlModes_CanListCreateReplaceAndRevokeGlobalAccount(
        ProviderAccountManagementMode mode)
    {
        var controller = Controller(Session(_userId, administrator: true), mode);
        using var secret = JsonDocument.Parse("""{"apiKey":"global-fixture"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(
            new ProviderAccountsController.CreateProviderAccountRequest
            {
                ProviderId = "lastfm",
                DisplayName = "Shared Last.fm",
                Scope = "Global",
                Secret = secret.RootElement.Clone()
            }));
        using var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        var accountId = createdJson.RootElement.GetProperty("Id").GetGuid();

        var listed = Assert.IsType<OkObjectResult>(await controller.List());
        using var listedJson = JsonDocument.Parse(JsonSerializer.Serialize(listed.Value));
        Assert.Equal(mode.ToString(), listedJson.RootElement.GetProperty("managementMode").GetString());
        Assert.Single(listedJson.RootElement.GetProperty("accounts").EnumerateArray());

        using var replacement = JsonDocument.Parse("""{"apiKey":"updated-global-fixture"}""");
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(
            accountId,
            new ProviderAccountsController.ReplaceProviderSecretRequest
            {
                Secret = replacement.RootElement.Clone()
            }));

        var revoked = await controller.Revoke(accountId);

        Assert.IsType<NoContentResult>(revoked);
        await using var context = await _factory.CreateDbContextAsync();
        var account = await context.ProviderAccounts.SingleAsync(item => item.Id == accountId);
        Assert.False(account.Enabled);
        var reference = await context.SecretReferences.SingleAsync(item => item.Id == account.SecretReferenceId);
        Assert.NotNull(reference.RevokedAt);
    }

    [Fact]
    public async Task AdminManaged_RejectsEveryUserAccountOperation()
    {
        var account = await CreateUserAccount(_userId, "deezer", "Managed by admin");
        var controller = Controller(Session(_userId), ProviderAccountManagementMode.AdminManaged);
        using var secret = JsonDocument.Parse("""{"token":"user-must-not-write"}""");

        AssertForbidden(await controller.List());
        AssertForbidden(await controller.Create(new ProviderAccountsController.CreateProviderAccountRequest
        {
            ProviderId = "qobuz",
            DisplayName = "Blocked self-service",
            Scope = "User",
            Secret = secret.RootElement.Clone()
        }));
        AssertForbidden(await controller.ReplaceSecret(
            account.Id,
            new ProviderAccountsController.ReplaceProviderSecretRequest
            {
                Secret = secret.RootElement.Clone()
            }));
        AssertForbidden(await controller.Revoke(account.Id));
    }

    [Theory]
    [InlineData(ProviderAccountManagementMode.UserManaged)]
    [InlineData(ProviderAccountManagementMode.Hybrid)]
    public async Task SelfServiceModes_UserCanListCreateReplaceAndRevokeOwnAccount(
        ProviderAccountManagementMode mode)
    {
        var controller = Controller(Session(_userId), mode);
        using var secret = JsonDocument.Parse("""{"token":"user-owned"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(
            new ProviderAccountsController.CreateProviderAccountRequest
            {
                ProviderId = "qobuz",
                DisplayName = "My account",
                Scope = "User",
                TenantId = Guid.CreateVersion7(),
                OwnerUserId = _otherUserId,
                Secret = secret.RootElement.Clone()
            }));
        using var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        var accountId = createdJson.RootElement.GetProperty("Id").GetGuid();

        var listed = Assert.IsType<OkObjectResult>(await controller.List());
        using var listedJson = JsonDocument.Parse(JsonSerializer.Serialize(listed.Value));
        Assert.Equal(mode.ToString(), listedJson.RootElement.GetProperty("managementMode").GetString());
        var account = Assert.Single(listedJson.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.Equal(_tenantId, account.GetProperty("TenantId").GetGuid());
        Assert.Equal(_userId, account.GetProperty("OwnerUserId").GetGuid());

        using var replacement = JsonDocument.Parse("""{"token":"user-owned-updated"}""");
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(
            accountId,
            new ProviderAccountsController.ReplaceProviderSecretRequest
            {
                Secret = replacement.RootElement.Clone()
            }));
        Assert.IsType<NoContentResult>(await controller.Revoke(accountId));
    }

    [Fact]
    public async Task UserManaged_AdministratorCannotEscalateBeyondOwnUserAccount()
    {
        var own = await CreateUserAccount(_userId, "deezer", "Administrator personal account");
        var other = await CreateUserAccount(_otherUserId, "qobuz", "Other user account");
        var controller = Controller(
            Session(_userId, administrator: true),
            ProviderAccountManagementMode.UserManaged);

        var listed = Assert.IsType<OkObjectResult>(await controller.List());
        using var listedJson = JsonDocument.Parse(JsonSerializer.Serialize(listed.Value));
        var listedAccount = Assert.Single(listedJson.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.Equal(own.Id, listedAccount.GetProperty("Id").GetGuid());

        var global = await controller.Create(new ProviderAccountsController.CreateProviderAccountRequest
        {
            ProviderId = "lastfm",
            DisplayName = "Disallowed shared account",
            Scope = "Global"
        });
        Assert.IsType<BadRequestObjectResult>(global);

        using var replacement = JsonDocument.Parse("""{"token":"must-not-cross-user-boundary"}""");
        Assert.IsType<NotFoundResult>(await controller.ReplaceSecret(
            other.Id,
            new ProviderAccountsController.ReplaceProviderSecretRequest
            {
                Secret = replacement.RootElement.Clone()
            }));
        Assert.IsType<NotFoundResult>(await controller.Revoke(other.Id));
        Assert.IsType<NoContentResult>(await controller.Revoke(own.Id));
    }

    [Fact]
    public async Task AdministratorCannotCreateUserAccountWithCrossTenantOwner()
    {
        var otherTenantId = Guid.CreateVersion7();
        var crossTenantUserId = Guid.CreateVersion7();
        await using (var context = await _factory.CreateDbContextAsync())
        {
            context.Tenants.Add(new TenantRecord
            {
                Id = otherTenantId,
                Slug = "other-tenant",
                Name = "Other tenant",
                CreatedAt = DateTimeOffset.UtcNow
            });
            context.Users.Add(new PlatformUserRecord
            {
                Id = crossTenantUserId,
                TenantId = otherTenantId,
                DisplayName = "Other tenant user",
                Status = PlatformUserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var controller = Controller(Session(_userId, administrator: true));
        var result = await controller.Create(new ProviderAccountsController.CreateProviderAccountRequest
        {
            ProviderId = "deezer",
            DisplayName = "Invalid cross-tenant account",
            Scope = "User",
            TenantId = _tenantId,
            OwnerUserId = crossTenantUserId
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(
            "another tenant",
            JsonSerializer.Serialize(badRequest.Value),
            StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.ProviderAccounts.ToListAsync());
    }

    private ProviderAccountsController Controller(
        AdminAuthSession session,
        ProviderAccountManagementMode mode = ProviderAccountManagementMode.Hybrid)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        return new ProviderAccountsController(
            _factory,
            _secretStore,
            _cache,
            new ProviderAccountManagementOptions { ManagementMode = mode.ToString() })
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private AdminAuthSession Session(Guid userId, bool administrator = false) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        UserId = userId.ToString(),
        UserName = "fixture",
        IsAdministrator = administrator,
        BackendType = "Jellyfin",
        TenantId = _tenantId,
        AllstarrUserId = userId,
        JellyfinAccessToken = "protected-in-real-session-store",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        LastSeenUtc = DateTime.UtcNow
    };

    private static void AssertForbidden(IActionResult result)
    {
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    private async Task<ProviderAccountRecord> CreateUserAccount(
        Guid userId,
        string provider,
        string name)
    {
        var controller = Controller(Session(userId));
        using var secret = JsonDocument.Parse("""{"secretReferenceFixture":"encrypted"}""");
        var result = Assert.IsType<CreatedAtActionResult>(await controller.Create(
            new ProviderAccountsController.CreateProviderAccountRequest
            {
                ProviderId = provider,
                DisplayName = name,
                Scope = "User",
                Secret = secret.RootElement.Clone()
            }));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var id = payload.RootElement.GetProperty("Id").GetGuid();
        await using var context = await _factory.CreateDbContextAsync();
        return await context.ProviderAccounts.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    private PlatformUserRecord User(Guid id, string name) => new()
    {
        Id = id,
        TenantId = _tenantId,
        DisplayName = name,
        Status = PlatformUserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
