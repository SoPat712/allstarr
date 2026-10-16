using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Health;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderAccountsControllerTests : IAsyncLifetime
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

    [Theory]
    [InlineData("lastfm", "Last.fm", "Personal")]
    [InlineData("listenbrainz", "ListenBrainz", "Shared")]
    [InlineData("multi-scrobbler", "multi-scrobbler", "Personal")]
    [InlineData("koito", "Koito", "Personal")]
    [InlineData("spotify", "Spotify", "Personal")]
    [InlineData("deezer", "Deezer", "Shared")]
    [InlineData("apple-musickit", "Apple Music", "Personal")]
    public async Task Create_BlankLabelUsesProviderName(string providerId, string expected, string scope)
    {
        var result = await Controller(Session(_userId, administrator: true)).Create(new()
        {
            ProviderId = providerId,
            DisplayName = "  ",
            Scope = scope
        });
        var created = Assert.IsType<CreatedAtActionResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        Assert.Equal(expected, json.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("customLabel").ValueKind);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(expected, (await context.ProviderAccounts.SingleAsync()).DisplayName);
    }

    [Theory]
    [InlineData("multi-scrobbler")]
    [InlineData("koito")]
    public async Task CompatibleScrobbleAccount_EncryptsConfigurationAndRetainsTokenOnUrlEdit(string providerId)
    {
        var controller = Controller(Session(_userId));
        using var initial = JsonDocument.Parse("{\"token\":\"private-fixture-token\",\"baseUrl\":\"http://127.0.0.1:9078/1/\"}");
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(new() { ProviderId = providerId, Secret = initial.RootElement }));
        using var receipt = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        var id = receipt.RootElement.GetProperty("Id").GetGuid();
        using var replacement = JsonDocument.Parse("{\"baseUrl\":\"http://192.168.20.10:9078/1/\"}");
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(id, new() { Secret = replacement.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.SingleAsync();
        Assert.Equal("{}", account.SettingsJson);
        using var lease = await _secretStore.OpenAsync(account.SecretReferenceId!.Value,
            new SecretAccessContext(_userId, $"provider-account:{providerId}:{id:N}"));
        using var saved = JsonDocument.Parse(lease.Value);
        Assert.Equal("private-fixture-token", saved.RootElement.GetProperty("token").GetString());
        Assert.Equal("http://192.168.20.10:9078/1/", saved.RootElement.GetProperty("baseUrl").GetString());
        var listed = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.List()).Value);
        Assert.DoesNotContain("private-fixture-token", listed);
        Assert.Contains("192.168.20.10", listed);
        Assert.DoesNotContain("private-fixture-token", System.Text.Encoding.UTF8.GetString((await db.SecretVersions.FirstAsync()).Ciphertext));
    }

    [Theory]
    [InlineData("http://8.8.8.8/1/")]
    [InlineData("http://scrobbler.example/1/")]
    [InlineData("http://[::]/1/")]
    [InlineData("https://user:private-marker@example.org/1/")]
    [InlineData("https://example.org/1/?token=private-marker")]
    [InlineData("")]
    public async Task CompatibleScrobbleAccount_RejectsUnsafeAddressBeforeStoringAnything(string address)
    {
        using var secret = JsonDocument.Parse(JsonSerializer.Serialize(new { token = "private-marker", baseUrl = address }));
        var result = Assert.IsType<BadRequestObjectResult>(await Controller(Session(_userId)).Create(new()
        { ProviderId = "multi-scrobbler", Secret = secret.RootElement }));
        Assert.DoesNotContain("private-marker", JsonSerializer.Serialize(result.Value));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ProviderAccounts.ToArrayAsync());
        Assert.Empty(await db.SecretReferences.ToArrayAsync());
    }

    [Fact]
    public async Task Create_DefaultUsesExtensionDefinitionAndPreservesCustomLabel()
    {
        var controller = Controller(Session(_userId), providerRegistry: AppleExtensionRegistry());
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        {
            ProviderId = "spotiflac-apple-music"
        }));
        using var defaultJson = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        Assert.Equal("Apple Music", defaultJson.RootElement.GetProperty("displayName").GetString());
        created = Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        {
            ProviderId = "spotiflac-apple-music",
            DisplayName = "Road trip"
        }));
        using var customJson = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        Assert.Equal("Road trip", customJson.RootElement.GetProperty("displayName").GetString());
        Assert.Equal("Road trip", customJson.RootElement.GetProperty("customLabel").GetString());
    }

    [Fact]
    public async Task StartupRename_PreservesPersonalAccountAndEncryptedSecretBytes()
    {
        var original = await CreateUserAccount(_userId, "deezer", "My deezer");
        await using var context = await _factory.CreateDbContextAsync();
        var references = JsonSerializer.Serialize(await context.SecretReferences.AsNoTracking().ToListAsync());
        var ciphertext = JsonSerializer.Serialize(await context.SecretVersions.AsNoTracking().ToListAsync());
        var keyring = await File.ReadAllBytesAsync(Path.Combine(_root, "keyring.json"));
        var state = new DurableStorageState(new StorageOptions());
        state.Set(DurableStorageReadiness.Ready);
        await new ProviderAccountNameNormalizer(_factory, new ProviderRegistry([]), state,
            NullLogger<ProviderAccountNameNormalizer>.Instance).StartAsync(CancellationToken.None);
        var renamed = await context.ProviderAccounts.AsNoTracking().SingleAsync();
        Assert.Equal("Deezer", renamed.DisplayName);
        Assert.Equal(original.Id, renamed.Id);
        Assert.Equal(original.OwnerUserId, renamed.OwnerUserId);
        Assert.Equal(original.CreatedByUserId, renamed.CreatedByUserId);
        Assert.Equal(original.SecretReferenceId, renamed.SecretReferenceId);
        Assert.Equal(original.SettingsJson, renamed.SettingsJson);
        Assert.Equal(original.Enabled, renamed.Enabled);
        Assert.Equal(references, JsonSerializer.Serialize(await context.SecretReferences.AsNoTracking().ToListAsync()));
        Assert.Equal(ciphertext, JsonSerializer.Serialize(await context.SecretVersions.AsNoTracking().ToListAsync()));
        Assert.Equal(keyring, await File.ReadAllBytesAsync(Path.Combine(_root, "keyring.json")));
    }

    [Fact]
    public async Task HomeSummary_SeparatesAudiencesAndCustomLabelsWithoutExposingAccountData()
    {
        await CreateUserAccount(_userId, "deezer", "My deezer");
        Assert.IsType<CreatedAtActionResult>(await Controller(Session(_userId, administrator: true)).Create(new()
        {
            ProviderId = "deezer",
            DisplayName = "Road trip",
            Scope = "Shared"
        }));
        await using var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<AllstarrDbContext>>(_factory)
            .AddSingleton(new ProviderRuntimeHealth()).BuildServiceProvider();
        var controller = new AdminUiController(new ConfigurationBuilder().Build(),
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, null!,
            new ProviderAccountOptions(), null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } }
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId, administrator: true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.GetProviderSummaries()).Value));
        var summary = Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
        var accounts = summary.GetProperty("connectedAccounts").EnumerateArray().ToArray();
        Assert.Equal(2, accounts.Length);
        Assert.Contains(accounts, item => item.GetProperty("scope").GetString() == "Personal" &&
            item.GetProperty("customLabel").ValueKind == JsonValueKind.Null);
        Assert.Contains(accounts, item => item.GetProperty("scope").GetString() == "Shared" &&
            item.GetProperty("customLabel").GetString() == "Road trip");
        Assert.All(accounts, item => Assert.Equal(2, item.EnumerateObject().Count()));
        Assert.Equal(0, summary.GetProperty("capabilityTotal").GetInt32());
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId);
        AssertForbidden(await controller.GetProviderSummaries());
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

        Guid? secretId;
        await using (var before = await _factory.CreateDbContextAsync())
            secretId = (await before.ProviderAccounts.SingleAsync(item => item.Id == accountId)).SecretReferenceId;

        var revoked = await controller.Revoke(accountId);

        Assert.IsType<NoContentResult>(revoked);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.False(await context.ProviderAccounts.AnyAsync(item => item.Id == accountId));
        var reference = await context.SecretReferences.SingleAsync(item => item.Id == secretId);
        Assert.NotNull(reference.RevokedAt);
        using var afterJson = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.List()).Value));
        Assert.Empty(afterJson.RootElement.GetProperty("accounts").EnumerateArray());
    }

    [Fact]
    public async Task RemovingAnAccountWithScrobbleHistoryArchivesItAndHidesIt()
    {
        var account = await CreateUserAccount(_userId, "lastfm", "Personal");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PlaybackDeliveryCheckpoints.Add(new allstarr.Core.Playback.PlaybackDeliveryCheckpointEntity
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _userId,
                DestinationAccountId = account.Id,
                SignalKey = "signal",
                TargetId = "lastfm",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var controller = Controller(Session(_userId, administrator: true));

        Assert.IsType<NoContentResult>(await controller.Revoke(account.Id));

        await using var context = await _factory.CreateDbContextAsync();
        var archived = await context.ProviderAccounts.SingleAsync(item => item.Id == account.Id);
        Assert.False(archived.Enabled);
        Assert.True(ProviderAccountsController.IsRemoved(archived));
        using var list = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.List()).Value));
        Assert.Empty(list.RootElement.GetProperty("accounts").EnumerateArray());
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

    [Theory]
    [InlineData("apple-musickit")]
    [InlineData("spotify")]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    [InlineData("musixmatch")]
    [InlineData("lastfm")]
    [InlineData("listenbrainz")]
    [InlineData("multi-scrobbler")]
    [InlineData("koito")]
    [InlineData("spotiflac-apple-music")]
    public async Task AudienceRoundTripKeepsSelectedCredentialsUsable(string provider)
    {
        var account = await CreateUserAccount(_userId, provider, "Connection");
        var admin = Controller(Session(_userId, administrator: true));
        var resolver = new ProviderAccountResolver(_factory);
        var secrets = new EncryptedProviderAccountSecretAccessor(_secretStore);
        var owner = new AllstarrPrincipal(_userId, "Jellyfin", "fixture", "a", "A", false);
        var listener = new AllstarrPrincipal(_otherUserId, "Jellyfin", "fixture", "b", "B", false);
        foreach (var scope in new[] { "Personal", "Shared", "Personal" })
        {
            Assert.IsType<OkObjectResult>(await admin.UpdateAudience(account.Id,
                new() { Scope = scope, OwnerUserId = scope == "Personal" ? _userId : null }));
            foreach (var actor in new[] { owner, listener })
            {
                var resolved = await resolver.ResolveAsync(new(actor, provider, "streaming"));
                if (scope == "Personal" && actor.UserId != _userId) { Assert.Null(resolved); continue; }
                Assert.NotNull(resolved);
                var selected = resolved.Account;
                var value = await secrets.UseAsync(new(selected.Id, provider, selected.Scope, selected.Revision,
                    selected.Enabled, selected.OwnerUserId, "selected-account", selected.SecretReferenceId),
                    bytes => Task.FromResult(System.Text.Encoding.UTF8.GetString(bytes.Span)), default);
                Assert.Contains("secretReferenceFixture", value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task RouteResolutionDistinguishesPrivateAccountFromMissingSignIn()
    {
        var health = new Mock<allstarr.Core.Routing.IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new allstarr.Core.Routing.ProviderRouteHealthSnapshot(allstarr.Core.Routing.ProviderRouteHealthState.Unknown, false));
        var resolver = new allstarr.Core.Routing.DurableProviderRouteAccountResolver(new(_factory), health.Object);
        var actor = new ProviderActorContext(ProviderActorKind.User, _otherUserId, new("jellyfin", "fixture", "b"));
        var request = new allstarr.Core.Routing.ProviderRouteAccountRequest(actor, "apple-musickit", ProviderCapabilityKind.Streaming, null);
        Assert.Null(await resolver.ResolveAsync(request));
        var account = await CreateUserAccount(_userId, "apple-musickit", "Connection");
        await Assert.ThrowsAsync<allstarr.Core.Routing.ProviderAccountAudienceDeniedException>(() => resolver.ResolveAsync(request));
        await Assert.ThrowsAsync<allstarr.Core.Routing.ProviderAccountAudienceDeniedException>(() =>
            resolver.ResolveAsync(request with { RequestedAccountId = account.Id }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            resolver.ResolveAsync(request with { RequestedAccountId = Guid.CreateVersion7() }));
        Assert.IsType<OkObjectResult>(await Controller(Session(_userId, administrator: true))
            .UpdateAudience(account.Id, new() { Scope = "Shared" }));
        Assert.Equal(account.Id, (await resolver.ResolveAsync(request))!.Account.AccountId);
    }

    [Fact]
    public async Task PersonalRouteAccountTakesPriorityOverHealthySharedAccount()
    {
        var shared = await CreateUserAccount(_userId, "spotify", "Shared connection");
        await Controller(Session(_userId, administrator: true)).UpdateAudience(shared.Id, new() { Scope = "Shared" });
        var personal = await CreateUserAccount(_userId, "spotify", "Personal connection");
        var health = new Mock<allstarr.Core.Routing.IProviderRouteHealthSource>();
        health.Setup(item => item.Get("spotify", It.IsAny<Guid?>(), ProviderCapabilityKind.Playlist))
            .Returns((string _, Guid? id, ProviderCapabilityKind _) => new allstarr.Core.Routing.ProviderRouteHealthSnapshot(
                id == personal.Id ? allstarr.Core.Routing.ProviderRouteHealthState.Degraded : allstarr.Core.Routing.ProviderRouteHealthState.Healthy, false));
        var resolver = new allstarr.Core.Routing.DurableProviderRouteAccountResolver(new(_factory), health.Object);
        var actor = new ProviderActorContext(ProviderActorKind.User, _userId, new("jellyfin", "fixture", "a"));
        Assert.Equal(personal.Id, (await resolver.ResolveAsync(new(actor, "spotify", ProviderCapabilityKind.Playlist, null)))!.Account.AccountId);
    }

    [Fact]
    public async Task UnsupportedAudienceIsRejectedBeforeChangingCredentials()
    {
        var account = await CreateUserAccount(_userId, "limited", "Connection");
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(
            "limited", "Limited", "Limited", ProviderOrigin.BuiltIn, "1", "1",
            [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming,
                ProviderCapabilitySupportState.ConfiguredOnly, ProviderAccountRequirement.Required, "1",
                [], [ProviderAccountScope.Personal])], new ProviderPermissionDescriptor()))]);
        var controller = Controller(Session(_userId, administrator: true), providerRegistry: registry);
        var result = Assert.IsType<BadRequestObjectResult>(await controller.UpdateAudience(account.Id, new() { Scope = "Shared" }));
        Assert.Contains("does not support Shared access", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(_userId, (await db.ProviderAccounts.SingleAsync()).OwnerUserId);
        using var listed = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.List()).Value));
        Assert.Equal("Personal", Assert.Single(Assert.Single(listed.RootElement.GetProperty("accounts").EnumerateArray())
            .GetProperty("supportedAudiences").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task SignedConnectionCanBeReenabledWithoutAStoredCredential()
    {
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(
            "signed-audio", "Signed Audio", "Signed Audio", ProviderOrigin.Extension, "1", "1",
            [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata,
                ProviderCapabilitySupportState.ConfiguredOnly, ProviderAccountRequirement.Required, "1",
                [], [ProviderAccountScope.Personal, ProviderAccountScope.Shared])], new ProviderPermissionDescriptor(),
            entryPoint: "index.js") { UsesSignedSession = true })]);
        var controller = Controller(Session(_userId, administrator: true), providerRegistry: registry);
        Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        { ProviderId = "signed-audio", Scope = "Personal", Enabled = true }));
        await using var db = await _factory.CreateDbContextAsync();
        var id = (await db.ProviderAccounts.SingleAsync()).Id;
        Assert.IsType<OkObjectResult>(await controller.SetEnabled(id, new() { Enabled = false }));
        Assert.IsType<OkObjectResult>(await controller.SetEnabled(id, new() { Enabled = true }));
        Assert.Null((await db.ProviderAccounts.AsNoTracking().SingleAsync()).SecretReferenceId);
        Assert.IsType<NoContentResult>(await controller.Revoke(id));
        Assert.IsType<NotFoundResult>(await controller.SetEnabled(id, new() { Enabled = true }));
    }

    [Fact]
    public async Task SignedConnectionAllowsOnePersonalConnectionPerPerson()
    {
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(
            "signed-audio", "Signed Audio", "Signed Audio", ProviderOrigin.Extension, "1", "1",
            [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata,
                ProviderCapabilitySupportState.ConfiguredOnly, ProviderAccountRequirement.Required, "1",
                [], [ProviderAccountScope.Personal, ProviderAccountScope.Shared])], new ProviderPermissionDescriptor(),
            entryPoint: "index.js") { UsesSignedSession = true })]);
        var controller = Controller(Session(_userId, administrator: true), providerRegistry: registry);
        Assert.IsType<CreatedAtActionResult>(await controller.Create(new() { ProviderId = "signed-audio", Scope = "Personal", Enabled = true }));
        Assert.IsType<ConflictObjectResult>(await controller.Create(new() { ProviderId = "signed-audio", Scope = "Personal", Enabled = true }));
        await using var db = await _factory.CreateDbContextAsync();
        var id = (await db.ProviderAccounts.SingleAsync()).Id;
        Assert.IsType<NoContentResult>(await controller.Revoke(id));
        Assert.IsType<CreatedAtActionResult>(await controller.Create(new() { ProviderId = "signed-audio", Scope = "Personal", Enabled = true }));
    }

    [Fact]
    public async Task ReplacingCredentialsPreservesTheSignedConnectionBinding()
    {
        var account = await CreateUserAccount(_userId, "spotiflac-apple-music", "Connection");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ProviderAccounts.SingleAsync()).SettingsJson = """{"__extensionSessionSource":"local"}""";
            await db.SaveChangesAsync();
        }
        using var secret = JsonDocument.Parse("""{"token":"replacement"}""");
        Assert.IsType<OkObjectResult>(await Controller(Session(_userId)).ReplaceSecret(account.Id,
            new() { Secret = secret.RootElement.Clone() }));
        await using var verify = await _factory.CreateDbContextAsync();
        using var settings = JsonDocument.Parse((await verify.ProviderAccounts.SingleAsync()).SettingsJson);
        Assert.Equal("local", settings.RootElement.GetProperty("__extensionSessionSource").GetString());
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
        using var secret = JsonDocument.Parse("""{"secretReferenceFixture":"encrypted","baseUrl":"https://listen.example.test"}""");
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
