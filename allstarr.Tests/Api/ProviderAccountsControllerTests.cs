using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Providers.AppleMusicKit;
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
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _otherUserId = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
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

        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using var context = await _factory.CreateDbContextAsync();
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
            Scope = "Personal",
            OwnerUserId = _otherUserId,
            Secret = secret.RootElement.Clone()
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var response = JsonSerializer.Serialize(created.Value);
        Assert.DoesNotContain("fixture-private-token", response, StringComparison.Ordinal);
        await using var context = await _factory.CreateDbContextAsync();
        var account = await context.ProviderAccounts.SingleAsync();
        Assert.Equal(_userId, account.OwnerUserId);
        Assert.Equal(ProviderAccountScope.Personal, account.Scope);
        Assert.True(account.Enabled);
        Assert.NotNull(account.SecretReferenceId);
        Assert.Single(await context.AuditEvents.ToListAsync());
        using var lease = await _secretStore.OpenAsync(
            account.SecretReferenceId!.Value,
            new SecretAccessContext(_userId, $"provider-account:{account.ProviderId}:{account.Id:N}"));
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
                Scope = "Personal",
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
            new SecretAccessContext(_userId, $"provider-account:{persisted.ProviderId}:{persisted.Id:N}"));
        using var saved = JsonDocument.Parse(lease.Value);
        Assert.Equal("jp", saved.RootElement.GetProperty("storefront").GetString());
        Assert.Equal("fixture-private-token", saved.RootElement.GetProperty("mediaUserToken").GetString());
        Assert.Equal("jp", ProviderAccountSettings.ReadText(persisted.SettingsJson, "storefront"));
        Assert.DoesNotContain("mediaUserToken", persisted.SettingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppleStorefrontEdit_PreservesTheExistingPersonalTokenAndAccount()
    {
        var client = AppleProviderTestFactory.Client();
        var registry = new ProviderRegistry([AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(new(client, new HttpClient()), new(client))]);
        var controller = Controller(Session(_userId), providerRegistry: registry);
        using var secret = JsonDocument.Parse("""{"MusicUserToken":"fixture-private-token","Storefront":"us","DeveloperToken":"obsolete"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        { ProviderId = "apple-musickit", DisplayName = "Personal Apple", Scope = "Personal", Secret = secret.RootElement.Clone() }));
        using var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        var id = createdJson.RootElement.GetProperty("Id").GetGuid();
        using var listed = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.List()).Value));
        var account = listed.RootElement.GetProperty("accounts")[0];
        Assert.Equal("us", account.GetProperty("configuration").GetProperty("storefront").GetString());
        Assert.Contains(account.GetProperty("configuredFields").EnumerateArray(), item => item.GetString() == "musicUserToken");
        Assert.DoesNotContain("fixture-private-token", listed.RootElement.GetRawText());
        long createdRevision;
        await using (var before = await _factory.CreateDbContextAsync())
        {
            var initial = await before.ProviderAccounts.SingleAsync(item => item.Id == id);
            Assert.Equal("""{"storefront":"us"}""", initial.SettingsJson);
            createdRevision = initial.Revision;
        }
        using var replacement = JsonDocument.Parse("""{"musicUserToken":"","storefront":"ca"}""");
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(id, new() { Secret = replacement.RootElement.Clone() }));
        await using var db = await _factory.CreateDbContextAsync();
        var saved = await db.ProviderAccounts.SingleAsync(item => item.Id == id);
        Assert.Equal("""{"storefront":"ca"}""", saved.SettingsJson);
        var reader = new ProviderAccountSettingsReader(_factory);
        Assert.Equal("ca", await reader.GetTextAsync(new(id, "apple-musickit", ProviderAccountScope.Personal,
            saved.Revision, ownerUserId: _userId), "storefront", default));
        Assert.Null(await reader.GetTextAsync(new(id, "apple-musickit", ProviderAccountScope.Personal,
            createdRevision, ownerUserId: _userId), "storefront", default));
        using var lease = await _secretStore.OpenAsync(saved.SecretReferenceId!.Value,
            new(_userId, $"provider-account:apple-musickit:{id:N}"));
        var credential = AppleMusicCredential.Read(lease.Value);
        Assert.NotNull(credential);
        Assert.Equal("fixture-private-token", credential.MusicUserToken);
        Assert.Equal("ca", credential.Storefront);
        Assert.Equal(_userId, saved.OwnerUserId);
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
            _userId, account.Id, account.Revision, "spotify", null, null, 100);
        var artworkKey = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            _userId, account.Id, "spotify", "playlist", "private", "revision"));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdministratorControlModes_CanListCreateReplaceAndRevokeGlobalAccount(
        bool allowConnections)
    {
        var controller = Controller(Session(_userId, administrator: true), allowConnections);
        using var secret = JsonDocument.Parse("""{"apiKey":"global-fixture"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(
            new ProviderAccountsController.CreateProviderAccountRequest
            {
                ProviderId = "lastfm",
                DisplayName = "Shared Last.fm",
                Scope = "Shared",
                Secret = secret.RootElement.Clone()
            }));
        using var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        var accountId = createdJson.RootElement.GetProperty("Id").GetGuid();

        var listed = Assert.IsType<OkObjectResult>(await controller.List());
        using var listedJson = JsonDocument.Parse(JsonSerializer.Serialize(listed.Value));
        Assert.Equal(allowConnections, listedJson.RootElement.GetProperty("listenersCanConnectOwnAccounts").GetBoolean());
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
    public async Task ConnectionsDisabled_BlocksCreationButPreservesExistingUseAndRemoval()
    {
        var account = await CreateUserAccount(_userId, "deezer", "Personal");
        var controller = Controller(Session(_userId), false);
        Assert.IsType<OkObjectResult>(await controller.List());
        AssertForbidden(await controller.Create(new() { ProviderId = "qobuz", DisplayName = "New account" }));
        var principal = new AllstarrPrincipal(_userId, "Jellyfin", "fixture", "a", "A", false);
        Assert.Equal(account.Id, (await new ProviderAccountResolver(_factory).ResolveAsync(new(principal, "deezer", "streaming")))!.Account.Id);
        Assert.IsType<NoContentResult>(await controller.Revoke(account.Id));
    }

    [Fact]
    public async Task Listener_CanManageOwnPersonalAccountButCannotCreateSharedOrChangeAudience()
    {
        var account = await CreateUserAccount(_userId, "qobuz", "Personal");
        var controller = Controller(Session(_userId));
        using var secret = JsonDocument.Parse("""{"token":"replacement"}""");
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(account.Id, new() { Secret = secret.RootElement.Clone() }));
        Assert.IsType<BadRequestObjectResult>(await controller.Create(new() { ProviderId = "qobuz", DisplayName = "Shared", Scope = "Shared" }));
        AssertForbidden(await controller.UpdateAudience(account.Id, new() { Scope = "Shared" }));
        using var list = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.List()).Value));
        var row = Assert.Single(list.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.Equal("Personal", row.GetProperty("scope").GetString());
        Assert.True(row.GetProperty("canManage").GetBoolean());
        Assert.False(row.GetProperty("canChangeAudience").GetBoolean());
        Assert.IsType<NoContentResult>(await controller.Revoke(account.Id));
    }

    [Fact]
    public async Task Administrator_CanManageOtherAccountsButCannotUseTheirPersonalCredentials()
    {
        var other = await CreateUserAccount(_otherUserId, "qobuz", "Other personal account");
        var controller = Controller(Session(_userId, administrator: true), false);
        Assert.IsType<OkObjectResult>(await controller.List());
        var actor = new AllstarrPrincipal(_userId, "Jellyfin", "fixture", "admin", "Admin", true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ProviderAccountResolver(_factory)
            .ResolveAsync(new(actor, "qobuz", "streaming", other.Id)));
        using var secret = JsonDocument.Parse("""{"token":"admin-managed"}""");
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(other.Id, new() { Secret = secret.RootElement.Clone() }));
        Assert.IsType<NoContentResult>(await controller.Revoke(other.Id));
    }

    [Fact]
    public async Task AdministratorCannotCreatePersonalAccountWithDisabledOwner()
    {
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var disabled = await context.Users.SingleAsync(item => item.Id == _otherUserId);
            disabled.Enabled = false;
            await context.SaveChangesAsync();
        }

        var controller = Controller(Session(_userId, administrator: true));
        var result = await controller.Create(new ProviderAccountsController.CreateProviderAccountRequest
        {
            ProviderId = "deezer",
            DisplayName = "Invalid disabled-owner account",
            Scope = "Personal",
            OwnerUserId = _otherUserId
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("unavailable", JsonSerializer.Serialize(badRequest.Value), StringComparison.OrdinalIgnoreCase);
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
                Scope = "Personal",
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
    public async Task UpdatingAudienceRebindsEncryptedSecretToNewOwner()
    {
        using var secret = JsonDocument.Parse("""{"accessToken":"global-token"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await Controller(
            Session(_userId, administrator: true)).Create(
            new ProviderAccountsController.CreateProviderAccountRequest
            {
                ProviderId = "spotify",
                DisplayName = "Shared Spotify",
                Scope = "Shared",
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
                Scope = "Personal",
                OwnerUserId = _userId,
                ExpectedRevision = account.Revision
            }));

        using var lease = await _secretStore.OpenAsync(
            account.SecretReferenceId!.Value,
            new SecretAccessContext(_userId, $"provider-account:{account.ProviderId}:{account.Id:N}"));
        Assert.Contains("global-token", lease.ReadUtf8(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _secretStore.OpenAsync(
            account.SecretReferenceId.Value,
            new SecretAccessContext(null, $"provider-account:{account.ProviderId}:{account.Id:N}", AllowShared: true)));
    }

    [Fact]
    public async Task AdministratorSharing_PreservesOwnerIsolationAndExposesOnlySafeSharedDescriptions()
    {
        var account = await CreateUserAccount(_userId, "qobuz", "Personal connection");
        var admin = Controller(Session(_userId, administrator: true));
        var listener = Controller(Session(_otherUserId));
        var resolver = new ProviderAccountResolver(_factory);
        var actor = new AllstarrPrincipal(_otherUserId, "Jellyfin", "fixture", "b", "B", false);
        Assert.Null(await resolver.ResolveAsync(new(actor, "qobuz", "streaming")));
        Assert.IsType<OkObjectResult>(await admin.UpdateAudience(account.Id, new() { Scope = "Shared", ExpectedRevision = account.Revision }));
        Assert.Equal(account.Id, (await resolver.ResolveAsync(new(actor, "qobuz", "playlist")))!.Account.Id);
        using var listed = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await listener.List()).Value));
        var row = Assert.Single(listed.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.Equal("Shared", row.GetProperty("scope").GetString());
        Assert.False(row.GetProperty("canManage").GetBoolean());
        Assert.False(row.GetProperty("canChangeAudience").GetBoolean());
        Assert.Empty(row.GetProperty("configuration").EnumerateObject());
        Assert.DoesNotContain("encrypted", row.GetRawText(), StringComparison.Ordinal);
        using var secret = JsonDocument.Parse("""{"token":"forbidden"}""");
        Assert.IsType<NotFoundResult>(await listener.ReplaceSecret(account.Id, new() { Secret = secret.RootElement.Clone() }));
        Assert.IsType<NotFoundResult>(await listener.SetEnabled(account.Id, new() { Enabled = false }));
        Assert.IsType<NotFoundResult>(await listener.Revoke(account.Id));
        AssertForbidden(await listener.UpdateAudience(account.Id, new() { Scope = "Personal" }));
        Assert.IsType<ConflictObjectResult>(await admin.UpdateAudience(account.Id, new() { Scope = "Personal", ExpectedRevision = account.Revision }));
        using (var shared = await _secretStore.OpenAsync(account.SecretReferenceId!.Value, new(null, $"provider-account:{account.ProviderId}:{account.Id:N}", AllowShared: true)))
            Assert.Contains("secretReferenceFixture", shared.ReadUtf8(), StringComparison.Ordinal);
        Assert.IsType<OkObjectResult>(await admin.UpdateAudience(account.Id, new() { Scope = "Personal", OwnerUserId = _userId, ExpectedRevision = account.Revision + 1 }));
        Assert.Null(await resolver.ResolveAsync(new(actor, "qobuz", "streaming")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.ResolveAsync(new(actor, "qobuz", "streaming", account.Id)));
    }

    [Fact]
    public async Task PersonalLastFmAccount_OnlyOwnerCanAuthenticateAndReadPersonalStatus()
    {
        var account = await CreateUserAccount(_userId, "lastfm", "Shared Last.fm");
        using var handler = new LastFmSessionHandler();
        using var http = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(http);
        ScrobblingAdminController Scrobbling(Guid user) => new(
            Options.Create(new ScrobblingSettings
            {
                LastFm = new LastFmSettings { ApiKey = "fixture-key", SharedSecret = "fixture-secret" }
            }), clients.Object, NullLogger<ScrobblingAdminController>.Instance,
            _factory, new EncryptedProviderAccountSecretAccessor(_secretStore), _secretStore,
            new ProviderAccountOptions { ListenersCanConnectOwnAccounts = false })
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
            new() { Scope = "Personal", OwnerUserId = _otherUserId }));
        var recipient = Controller(Session(_otherUserId));
        AssertForbidden(await recipient.UpdateAudience(account.Id, new() { Scope = "Shared" }));
        AssertForbidden(await Controller(Session(_userId)).UpdateAudience(account.Id, new() { Scope = "Shared" }));
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
        bool allowConnections = true,
        IProviderRegistry? providerRegistry = null)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        return new ProviderAccountsController(
            _factory,
            _secretStore,
            _cache,
            new ProviderAccountOptions { ListenersCanConnectOwnAccounts = allowConnections },
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
                Scope = "Personal",
                Secret = secret.RootElement.Clone()
            }));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var id = payload.RootElement.GetProperty("Id").GetGuid();
        await using var context = await _factory.CreateDbContextAsync();
        return await context.ProviderAccounts.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    private UserRecord User(Guid id, string name) => new()
    {
        Id = id,
        DisplayName = name,
        Enabled = true,
        BackendType = "jellyfin",
        BackendInstanceId = "fixture",
        BackendPrincipalId = id.ToString(),
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
