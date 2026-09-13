using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

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
    public async Task ExtensionConfiguration_ReturnsSafeValuesAndPreservesBlankSecretFields()
    {
        var registry = AppleExtensionRegistry();
        var controller = Controller(Session(_userId), providerRegistry: registry);
        using var secret = JsonDocument.Parse(
            """{"storefront":"ca","mediaUserToken":"fixture-private-token","lyricsTranslationLanguage":"es"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(
            new ProviderAccountsController.CreateProviderAccountRequest
            {
                ProviderId = "spotiflac-apple-music",
                DisplayName = "Apple Music",
                Scope = "User",
                Secret = secret.RootElement.Clone()
            }));
        using var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        var accountId = createdJson.RootElement.GetProperty("Id").GetGuid();

        var listed = Assert.IsType<OkObjectResult>(await controller.List());
        using var listedJson = JsonDocument.Parse(JsonSerializer.Serialize(listed.Value));
        var account = listedJson.RootElement.GetProperty("accounts")[0];
        Assert.Equal("ca", account.GetProperty("configuration").GetProperty("storefront").GetString());
        Assert.Equal("es", account.GetProperty("configuration").GetProperty("lyricsTranslationLanguage").GetString());
        Assert.Contains(account.GetProperty("configuredFields").EnumerateArray(),
            item => item.GetString() == "mediaUserToken");
        Assert.DoesNotContain("fixture-private-token", listedJson.RootElement.GetRawText(), StringComparison.Ordinal);

        using var replacement = JsonDocument.Parse(
            """{"storefront":"jp","mediaUserToken":"","lyricsTranslationLanguage":"es"}""");
        var replaced = Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(
            accountId,
            new ProviderAccountsController.ReplaceProviderSecretRequest
            {
                Secret = replacement.RootElement.Clone()
            }));
        using var replacedJson = JsonDocument.Parse(JsonSerializer.Serialize(replaced.Value));
        Assert.Equal(2, replacedJson.RootElement.GetProperty("Revision").GetInt64());

        await using var context = await _factory.CreateDbContextAsync();
        var persisted = await context.ProviderAccounts.SingleAsync(item => item.Id == accountId);
        using var lease = await _secretStore.OpenAsync(
            persisted.SecretReferenceId!.Value,
            new SecretAccessContext(_tenantId));
        using var saved = JsonDocument.Parse(lease.Value);
        Assert.Equal("jp", saved.RootElement.GetProperty("storefront").GetString());
        Assert.Equal("fixture-private-token", saved.RootElement.GetProperty("mediaUserToken").GetString());
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
        var artworkKey = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            _tenantId, _userId, account.Id, "spotify", "playlist", "private", "revision"));
        await _cache.SetStringAsync(cacheKey, "{}");
        await _cache.SetStringAsync(artworkKey, "{}");
        await Controller(Session(_userId)).SetEnabled(
            account.Id,
            new ProviderAccountsController.SetProviderAccountEnabledRequest
            {
                Enabled = false
            });
        Assert.False(await _cache.ExistsAsync(cacheKey));
        Assert.False(await _cache.ExistsAsync(artworkKey));
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.False((await verification.ProviderAccounts.SingleAsync(item => item.Id == account.Id)).Enabled);
    }

    [Fact]
    public async Task UserCannotCreateLibraryAccountOrReplaceAnotherUsersSecret()
    {
        var other = await CreateUserAccount(_otherUserId, "deezer", "Other account");
        var controller = Controller(Session(_userId));
        using var replacement = JsonDocument.Parse("""{"token":"replacement"}""");

        var library = await controller.Create(new ProviderAccountsController.CreateProviderAccountRequest
        {
            ProviderId = "deezer",
            DisplayName = "Shared",
            Scope = "Library",
            LibraryScopeId = "library"
        });
        var replace = await controller.ReplaceSecret(
            other.Id,
            new ProviderAccountsController.ReplaceProviderSecretRequest
            {
                Secret = replacement.RootElement.Clone()
            });

        Assert.IsType<BadRequestObjectResult>(library);
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
        AssertForbidden(await controller.SetEnabled(account.Id, new() { Enabled = false }));
        AssertForbidden(await controller.UpdateAudience(account.Id, new() { Scope = "Global" }));
    }

    [Theory]
    [InlineData(ProviderAccountManagementMode.UserManaged, "User")]
    [InlineData(ProviderAccountManagementMode.Hybrid, "User")]
    [InlineData(ProviderAccountManagementMode.UserManaged, "Global")]
    [InlineData(ProviderAccountManagementMode.Hybrid, "Global")]
    public async Task SelfServiceModes_UserCanListCreateReplaceAndRevokeOwnAccount(
        ProviderAccountManagementMode mode, string scope)
    {
        var controller = Controller(Session(_userId), mode);
        using var secret = JsonDocument.Parse("""{"token":"user-owned"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(
            new ProviderAccountsController.CreateProviderAccountRequest
            {
                ProviderId = "qobuz",
                DisplayName = "My account",
                Scope = scope,
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
        Assert.Equal(scope, account.GetProperty("scope").GetString());
        Assert.Equal(_userId, account.GetProperty("CreatedByUserId").GetGuid());
        Assert.True(account.GetProperty("canChangeAudience").GetBoolean());
        if (scope == "User")
        {
            Assert.Equal(_tenantId, account.GetProperty("TenantId").GetGuid());
            Assert.Equal(_userId, account.GetProperty("OwnerUserId").GetGuid());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, account.GetProperty("TenantId").ValueKind);
            Assert.Equal(JsonValueKind.Null, account.GetProperty("OwnerUserId").ValueKind);
        }

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
    public async Task UserManaged_AdministratorCannotManageAnotherUsersAccount()
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
            DisplayName = "Self-connected shared account",
            Scope = "Global"
        });
        Assert.IsType<CreatedAtActionResult>(global);

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

    [Fact]
    public async Task AdministratorCanAssignAccountToOneActiveUser()
    {
        var account = await CreateUserAccount(_userId, "deezer", "Shared connection");
        var result = Assert.IsType<OkObjectResult>(await Controller(
            Session(_userId, administrator: true)).UpdateAudience(
            account.Id,
            new ProviderAccountsController.UpdateProviderAccountAudienceRequest
            {
                Scope = "User",
                OwnerUserId = _otherUserId,
                ExpectedRevision = account.Revision
            }));

        using var response = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(_otherUserId, response.RootElement.GetProperty("OwnerUserId").GetGuid());
        Assert.Equal("User two", response.RootElement.GetProperty("ownerDisplayName").GetString());
        await using var context = await _factory.CreateDbContextAsync();
        var saved = await context.ProviderAccounts.SingleAsync(item => item.Id == account.Id);
        Assert.Equal(_otherUserId, saved.OwnerUserId);
        Assert.Equal(_userId, saved.CreatedByUserId);
    }

    [Fact]
    public async Task UpdatingAudienceRebindsEncryptedSecretToNewTenant()
    {
        using var secret = JsonDocument.Parse("""{"accessToken":"global-token"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await Controller(
            Session(_userId, administrator: true)).Create(
            new ProviderAccountsController.CreateProviderAccountRequest
            {
                ProviderId = "spotify",
                DisplayName = "Shared Spotify",
                Scope = "Global",
                Secret = secret.RootElement.Clone()
            }));
        ProviderAccountRecord account;
        await using (var context = await _factory.CreateDbContextAsync())
            account = await context.ProviderAccounts.SingleAsync();

        Assert.IsType<OkObjectResult>(await Controller(
            Session(_userId, administrator: true)).UpdateAudience(
            account.Id,
            new ProviderAccountsController.UpdateProviderAccountAudienceRequest
            {
                Scope = "User",
                OwnerUserId = _userId,
                ExpectedRevision = account.Revision
            }));

        using var lease = await _secretStore.OpenAsync(
            account.SecretReferenceId!.Value,
            new SecretAccessContext(_tenantId));
        Assert.Contains("global-token", lease.ReadUtf8(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _secretStore.OpenAsync(
            account.SecretReferenceId.Value,
            new SecretAccessContext(null, AllowGlobal: true)));
    }

    [Theory]
    [InlineData(ProviderAccountManagementMode.UserManaged)]
    [InlineData(ProviderAccountManagementMode.Hybrid)]
    public async Task UserSharing_RoundTripsWithoutSharingSecretsOrPersonalData(ProviderAccountManagementMode mode)
    {
        var account = await CreateUserAccount(_userId, "qobuz", "My connection");
        var owner = Controller(Session(_userId), mode);
        var other = Controller(Session(_otherUserId), mode);
        var resolver = new ProviderAccountResolver(_factory, new ProviderPolicyOptions());
        ProviderAccountResolutionRequest Request(Guid user, string capability, Guid? selected = null) => new(
            new AllstarrPrincipal(_tenantId, user, "Jellyfin", "fixture", user.ToString(), "Listener", false),
            "qobuz", capability, selected);

        Assert.Null(await resolver.ResolveAsync(Request(_otherUserId, "streaming")));
        Assert.IsType<OkObjectResult>(await owner.UpdateAudience(account.Id, new()
        {
            Scope = "Global",
            ExpectedRevision = account.Revision
        }));
        Assert.Equal(account.Id, (await resolver.ResolveAsync(Request(_otherUserId, "streaming")))?.Account.Id);
        Assert.Equal(account.Id, (await resolver.ResolveAsync(Request(_userId, "playlist")))?.Account.Id);
        Assert.Null(await resolver.ResolveAsync(Request(_otherUserId, "playlist")));
        Assert.Null(await resolver.ResolveAsync(Request(_otherUserId, "scrobbling")));
        Assert.Null(await resolver.ResolveAsync(Request(_otherUserId, "favorites")));
        Assert.Null(await resolver.ResolveAsync(Request(_otherUserId, "personal-library")));
        var noSharing = new ProviderAccountResolver(_factory, new ProviderPolicyOptions { AllowGlobalAccounts = false });
        Assert.Null(await noSharing.ResolveAsync(Request(_otherUserId, "streaming")));
        Assert.Null(await noSharing.ResolveAsync(Request(_userId, "playlist")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            resolver.ResolveAsync(Request(_otherUserId, "playlist", account.Id)));

        var listed = Assert.IsType<OkObjectResult>(await owner.List());
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(listed.Value));
        Assert.True(Assert.Single(payload.RootElement.GetProperty("accounts").EnumerateArray())
            .GetProperty("canChangeAudience").GetBoolean());
        Assert.DoesNotContain("\"encrypted\"", payload.RootElement.GetRawText(), StringComparison.Ordinal);
        using var otherPayload = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await other.List()).Value));
        Assert.Empty(otherPayload.RootElement.GetProperty("accounts").EnumerateArray());
        using var replacement = JsonDocument.Parse("""{"token":"not-the-owner"}""");
        Assert.IsType<NotFoundResult>(await other.ReplaceSecret(account.Id, new() { Secret = replacement.RootElement.Clone() }));
        Assert.IsType<NotFoundResult>(await other.SetEnabled(account.Id, new() { Enabled = false }));
        Assert.IsType<NotFoundResult>(await other.Revoke(account.Id));
        Assert.IsType<NotFoundResult>(await other.UpdateAudience(account.Id, new() { Scope = "User" }));
        Assert.IsType<BadRequestObjectResult>(await owner.UpdateAudience(account.Id, new() { Scope = "User", OwnerUserId = _otherUserId }));
        Assert.IsType<BadRequestObjectResult>(await owner.UpdateAudience(account.Id, new() { Scope = "Library", LibraryScopeId = "music" }));
        Assert.IsType<ConflictObjectResult>(await owner.UpdateAudience(account.Id, new() { Scope = "User", ExpectedRevision = account.Revision }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _secretStore.OpenAsync(account.SecretReferenceId!.Value, new SecretAccessContext(_tenantId)));
        using (var sharedSecret = await _secretStore.OpenAsync(account.SecretReferenceId!.Value, new SecretAccessContext(null, AllowGlobal: true)))
            Assert.Contains("secretReferenceFixture", sharedSecret.ReadUtf8(), StringComparison.Ordinal);

        Assert.IsType<OkObjectResult>(await owner.SetEnabled(account.Id, new() { Enabled = false }));
        Assert.Null(await resolver.ResolveAsync(Request(_otherUserId, "streaming")));
        Assert.IsType<OkObjectResult>(await owner.SetEnabled(account.Id, new() { Enabled = true }));

        Assert.IsType<OkObjectResult>(await owner.UpdateAudience(account.Id, new()
        {
            Scope = "User",
            ExpectedRevision = account.Revision + 3
        }));
        Assert.Null(await resolver.ResolveAsync(Request(_otherUserId, "streaming")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            resolver.ResolveAsync(Request(_otherUserId, "streaming", account.Id)));
        using var privateSecret = await _secretStore.OpenAsync(account.SecretReferenceId.Value, new SecretAccessContext(_tenantId));
        Assert.Contains("secretReferenceFixture", privateSecret.ReadUtf8(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _secretStore.OpenAsync(account.SecretReferenceId.Value, new SecretAccessContext(null, AllowGlobal: true)));
    }

    [Fact]
    public async Task SharedLastFmAccount_OnlyCreatorCanAuthenticateAndReadPersonalStatus()
    {
        var account = await CreateUserAccount(_userId, "lastfm", "Shared Last.fm");
        Assert.IsType<OkObjectResult>(await Controller(Session(_userId)).UpdateAudience(account.Id, new() { Scope = "Global" }));
        using var handler = new LastFmSessionHandler();
        using var http = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(http);
        ScrobblingAdminController Scrobbling(Guid user, ProviderAccountManagementMode mode = ProviderAccountManagementMode.Hybrid) => new(
            Options.Create(new ScrobblingSettings
            {
                LastFm = new LastFmSettings { ApiKey = "fixture-key", SharedSecret = "fixture-secret" }
            }), clients.Object, NullLogger<ScrobblingAdminController>.Instance,
            _factory, new EncryptedProviderAccountSecretAccessor(_secretStore), _secretStore,
            new ProviderAccountManagementOptions { ManagementMode = mode.ToString() })
        {
            ControllerContext = Controller(Session(user)).ControllerContext
        };
        var request = new ScrobblingAdminController.LastFmAuthenticationRequest
        {
            AccountId = account.Id,
            Username = "fixture-listener",
            Password = "fixture-password"
        };
        Assert.IsType<NotFoundObjectResult>(await Scrobbling(_otherUserId).AuthenticateLastFm(request));
        AssertForbidden(await Scrobbling(_userId, ProviderAccountManagementMode.AdminManaged).AuthenticateLastFm(request));
        Assert.Equal(0, handler.Calls);
        var owner = Scrobbling(_userId);
        var authenticated = Assert.IsType<OkObjectResult>(await owner.AuthenticateLastFm(request));
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("fixture-session", JsonSerializer.Serialize(authenticated.Value), StringComparison.Ordinal);
        using var status = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await owner.GetStatus()).Value));
        Assert.True(status.RootElement.GetProperty("LastFm").GetProperty("HasSessionKey").GetBoolean());
        using var otherStatus = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Scrobbling(_otherUserId).GetStatus()).Value));
        Assert.False(otherStatus.RootElement.GetProperty("LastFm").GetProperty("HasSessionKey").GetBoolean());
        Assert.DoesNotContain("fixture-listener", otherStatus.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class LastFmSessionHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("<lfm status='ok'><session><name>fixture-listener</name><key>fixture-session</key></session></lfm>")
            });
        }
    }

    [Fact]
    public async Task AssignedAccount_CannotBeSharedByRecipientOrReclaimedByCreator()
    {
        var account = await CreateUserAccount(_userId, "qobuz", "Assigned connection");
        Assert.IsType<OkObjectResult>(await Controller(Session(_userId, administrator: true)).UpdateAudience(account.Id,
            new() { Scope = "User", OwnerUserId = _otherUserId }));
        var recipient = Controller(Session(_otherUserId));
        AssertForbidden(await recipient.UpdateAudience(account.Id, new() { Scope = "Global" }));
        Assert.IsType<NotFoundResult>(await Controller(Session(_userId)).UpdateAudience(account.Id, new() { Scope = "Global" }));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await recipient.List()).Value));
        Assert.False(Assert.Single(payload.RootElement.GetProperty("accounts").EnumerateArray())
            .GetProperty("canChangeAudience").GetBoolean());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("invalid")]
    public async Task InvalidAudience_IsRejected(string scope)
    {
        var controller = Controller(Session(_userId));
        Assert.IsType<BadRequestObjectResult>(await controller.Create(new()
        {
            ProviderId = "qobuz",
            DisplayName = "Invalid",
            Scope = scope
        }));
    }

    private ProviderAccountsController Controller(
        AdminAuthSession session,
        ProviderAccountManagementMode mode = ProviderAccountManagementMode.Hybrid,
        IProviderRegistry? providerRegistry = null)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        return new ProviderAccountsController(
            _factory,
            _secretStore,
            _cache,
            new ProviderAccountManagementOptions { ManagementMode = mode.ToString() },
            providerRegistry)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static IProviderRegistry AppleExtensionRegistry() => new ProviderRegistry([
        new ProviderRegistration(new ProviderDescriptor(
            "spotiflac-apple-music",
            "Apple Music",
            "Apple Music metadata and lyrics",
            ProviderOrigin.Extension,
            "1",
            "1",
            [new ProviderCapabilityDescriptor(
                ProviderCapabilityKind.Metadata,
                ProviderCapabilitySupportState.ConfiguredOnly,
                ProviderAccountRequirement.None,
                "1")],
            new ProviderPermissionDescriptor(secretSettingKeys: ["mediaUserToken"]),
            [
                new ProviderSettingDescriptor(
                    "storefront", ProviderSettingValueKind.Text, ProviderSettingScope.ProviderAccount,
                    "Storefront", defaultJson: "\"us\""),
                new ProviderSettingDescriptor(
                    "mediaUserToken", ProviderSettingValueKind.Secret, ProviderSettingScope.ProviderAccount,
                    "Media User Token"),
                new ProviderSettingDescriptor(
                    "lyricsTranslationLanguage", ProviderSettingValueKind.Text,
                    ProviderSettingScope.ProviderAccount, "Lyrics Translation Language")
            ],
            entryPoint: "index.js"))
    ]);

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
