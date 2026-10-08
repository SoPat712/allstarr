using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class EncryptedSecretStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private string _keyRingPath = string.Empty;
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await SqliteTestDatabase.CreateAsync();
        _keyRingPath = Path.Combine(_root, "keyring.json");
        WriteKeyRing("key-1", new Dictionary<string, byte[]>
        {
            ["key-1"] = RandomNumberGenerator.GetBytes(32)
        });
        _factory = new TestDbContextFactory(_database.Options);
        await using var context = await _factory.CreateDbContextAsync();
        context.Tenants.Add(new TenantRecord
        {
            Id = _tenantId,
            Slug = "fixture",
            Name = "Fixture tenant",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountLease_RequiresCurrentOwnerRevisionAndExactPurpose(bool shared)
    {
        var store = CreateStore();
        var accountId = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();
        var otherOwner = Guid.CreateVersion7();
        Guid? accountTenant = shared ? null : _tenantId;
        Guid? accountOwner = shared ? null : owner;
        var secret = await store.StoreAsync(accountTenant,
            $"provider-account:fixture:{accountId:N}", Encoding.UTF8.GetBytes("owned-fixture"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            foreach (var id in new[] { owner, otherOwner })
                db.Users.Add(new()
                {
                    Id = id,
                    TenantId = _tenantId,
                    DisplayName = "Listener",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            db.ProviderAccounts.Add(new()
            {
                Id = accountId,
                TenantId = accountTenant,
                OwnerUserId = accountOwner,
                ProviderId = "fixture",
                DisplayName = "Fixture",
                Enabled = true,
                Revision = 1,
                SecretReferenceId = secret.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        ProviderAccountContext Snapshot(long revision = 1, Guid? secretId = null,
            string provider = "fixture", Guid? ownerOverride = null) => new(
                accountId, provider, shared ? ProviderAccountScope.Shared : ProviderAccountScope.Personal,
                revision, tenantId: accountTenant, ownerUserId: ownerOverride ?? accountOwner,
                secretReferenceId: secretId ?? secret.Id);
        using (var lease = await store.OpenProviderAccountAsync(Snapshot()))
            Assert.Equal("owned-fixture", lease.ReadUtf8());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenProviderAccountAsync(Snapshot(revision: 0)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenProviderAccountAsync(Snapshot(provider: "other")));
        if (!shared)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenProviderAccountAsync(Snapshot(ownerOverride: otherOwner)));
        var foreignSecret = await store.StoreAsync(accountTenant,
            $"provider-account:fixture:{Guid.CreateVersion7():N}", Encoding.UTF8.GetBytes("foreign-fixture"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenProviderAccountAsync(Snapshot(secretId: foreignSecret.Id)));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync();
            account.SecretReferenceId = foreignSecret.Id;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenProviderAccountAsync(Snapshot(secretId: foreignSecret.Id)));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync();
            account.SecretReferenceId = secret.Id;
            account.Revision++;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenProviderAccountAsync(Snapshot()));
        using (var lease = await store.OpenProviderAccountAsync(Snapshot(revision: 2)))
            Assert.Equal("owned-fixture", lease.ReadUtf8());
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync();
            account.Enabled = false;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenProviderAccountAsync(Snapshot(revision: 2)));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ProviderAccounts.SingleAsync()).Enabled = true;
            await db.SaveChangesAsync();
        }
        await store.RevokeAsync(secret.Id, new SecretAccessContext(accountTenant, AllowGlobal: shared));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenProviderAccountAsync(Snapshot(revision: 2)));
    }

    [Fact]
    public async Task StoreAndOpen_EncryptsAtRestAndReturnsOnlyReferenceMetadata()
    {
        var store = CreateStore();
        var plaintext = "provider-token-fixture-should-never-be-in-db";

        var info = await store.StoreAsync(
            _tenantId,
            "deezer.account-token",
            Encoding.UTF8.GetBytes(plaintext));

        Assert.Equal(1, info.ActiveVersion);
        Assert.Equal("key-1", info.KeyId);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var version = await context.SecretVersions.SingleAsync();
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            Assert.NotEqual(plaintextBytes, version.Ciphertext);
            Assert.True(version.Ciphertext.AsSpan().IndexOf(plaintextBytes) < 0);
            Assert.Equal(12, version.Nonce.Length);
            Assert.Equal(16, version.AuthenticationTag.Length);
        }

        using var lease = await store.OpenAsync(info.Id, new SecretAccessContext(_tenantId));
        Assert.Equal(plaintext, lease.ReadUtf8());
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("backend")]
    [InlineData("tenant")]
    [InlineData("purpose")]
    [InlineData("revoked")]
    [InlineData("disabled")]
    [InlineData("unbound")]
    [InlineData("owner")]
    [InlineData("global")]
    public async Task SubsonicPlaylistWrite_RechecksExactListenerGrantBeforeEveryExecution(string mismatch)
    {
        var owner = await PlaylistPrincipalAsync("listener-a");
        var other = await PlaylistPrincipalAsync("listener-b");
        var store = CreateStore();
        Assert.Null(await store.GetSubsonicPlaylistGrantAsync(owner));
        var grant = await store.StoreSubsonicPlaylistGrantAsync(owner, "playlist-password");
        var resolver = new EncryptedSubsonicPlaylistAuthenticationResolver(store, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        var target = new BackendPlaylistTargetContext("primary", "listener-a", grant.ReferenceId.ToString(), _tenantId);
        var authentication = await resolver.ResolveAsync(target, default);
        Assert.Contains(authentication.FormParameters, item => item is { Key: "u", Value: "listener-a" });
        Assert.Contains(authentication.FormParameters, item => item is { Key: "p", Value: "playlist-password" });
        if (mismatch == "principal") target = new("primary", "listener-b", grant.ReferenceId.ToString(), _tenantId);
        if (mismatch == "backend") target = new("other", "listener-a", grant.ReferenceId.ToString(), _tenantId);
        if (mismatch == "tenant") target = new("primary", "listener-a", grant.ReferenceId.ToString(), Guid.CreateVersion7());
        if (mismatch == "revoked") await store.RevokeSubsonicPlaylistGrantAsync(owner);
        if (mismatch is "purpose" or "disabled" or "unbound" or "owner" or "global")
        {
            await using var db = await _factory.CreateDbContextAsync();
            var reference = await db.SecretReferences.SingleAsync(item => item.Id == grant.ReferenceId);
            if (mismatch == "purpose") reference.Purpose = "admin-oidc:fixture";
            if (mismatch == "disabled") (await db.Users.SingleAsync(item => item.Id == owner.UserId)).Status = PlatformUserStatus.Disabled;
            if (mismatch == "unbound") reference.BackendIdentityId = null;
            if (mismatch == "owner") reference.BackendIdentityId = await db.BackendIdentities.Where(item => item.UserId == other.UserId).Select(item => item.Id).SingleAsync();
            if (mismatch == "global") reference.TenantId = null;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await resolver.ResolveAsync(target, default));
    }

    [Fact]
    public async Task PlaylistConsent_RevokeIsPersonalAndReplacementDoesNotReviveQueuedReference()
    {
        var a = await PlaylistPrincipalAsync("listener-a");
        var b = await PlaylistPrincipalAsync("listener-b");
        var store = CreateStore();
        var first = await store.StoreSubsonicPlaylistGrantAsync(a, "a-password");
        await store.StoreSubsonicPlaylistGrantAsync(b, "b-password");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.StoreSubsonicPlaylistGrantAsync(
            a with { UserId = b.UserId }, "foreign-password"));
        await store.RevokeSubsonicPlaylistGrantAsync(a);
        var restarted = CreateStore();
        Assert.Null(await restarted.GetSubsonicPlaylistGrantAsync(a));
        Assert.NotNull(await restarted.GetSubsonicPlaylistGrantAsync(b));
        using (var lease = await restarted.OpenSubsonicPlaylistCredentialAsync(_tenantId, "primary", "listener-b", null))
            Assert.Contains("b-password", lease.ReadUtf8(), StringComparison.Ordinal);
        var replacement = await restarted.StoreSubsonicPlaylistGrantAsync(a, "replacement-password");
        Assert.NotEqual(first.ReferenceId, replacement.ReferenceId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.OpenSubsonicPlaylistCredentialAsync(
            _tenantId, "primary", "listener-a", first.ReferenceId));
        using var current = await restarted.OpenSubsonicPlaylistCredentialAsync(_tenantId, "primary", "listener-a", replacement.ReferenceId);
        Assert.Contains("replacement-password", current.ReadUtf8(), StringComparison.Ordinal);
    }

    private async Task<AllstarrPrincipal> PlaylistPrincipalAsync(string name)
    {
        var userId = Guid.CreateVersion7();
        await using var db = await _factory.CreateDbContextAsync();
        db.Users.Add(new()
        {
            Id = userId,
            TenantId = _tenantId,
            DisplayName = name,
            Status = PlatformUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.BackendIdentities.Add(new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            UserId = userId,
            BackendType = "subsonic",
            BackendInstanceId = "primary",
            PrincipalId = name,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new(_tenantId, userId, "subsonic", "primary", name, name, false);
    }

    [Fact]
    public async Task TenantBoundary_DeniesAnotherTenant()
    {
        var store = CreateStore();
        var info = await store.StoreAsync(
            _tenantId,
            "qobuz.token",
            Encoding.UTF8.GetBytes("fixture-secret"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            store.OpenAsync(info.Id, new SecretAccessContext(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task Rotation_UsesNewActiveExternalKeyAndRetainsDecryptability()
    {
        var store = CreateStore();
        var info = await store.StoreAsync(
            _tenantId,
            "apple.download-session",
            Encoding.UTF8.GetBytes("rotatable-fixture-secret"));
        var key1 = ReadKey("key-1");
        WriteKeyRing("key-2", new Dictionary<string, byte[]>
        {
            ["key-1"] = key1,
            ["key-2"] = RandomNumberGenerator.GetBytes(32)
        });

        var rotated = await store.RotateEncryptionAsync(
            info.Id,
            new SecretAccessContext(_tenantId));

        Assert.Equal(2, rotated.ActiveVersion);
        Assert.Equal("key-2", rotated.KeyId);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var versions = await context.SecretVersions
                .OrderBy(item => item.Version)
                .ToListAsync();
            Assert.Equal(2, versions.Count);
            Assert.NotNull(versions[0].RetiredAt);
            Assert.Null(versions[1].RetiredAt);
        }

        using var lease = await store.OpenAsync(info.Id, new SecretAccessContext(_tenantId));
        Assert.Equal("rotatable-fixture-secret", lease.ReadUtf8());
    }

    [Fact]
    public async Task RotateAll_ReencryptsEveryActiveReferenceAndIsIdempotent()
    {
        var store = CreateStore();
        var first = await store.StoreAsync(
            _tenantId,
            "deezer.account",
            Encoding.UTF8.GetBytes("first-fixture-secret"));
        var second = await store.StoreAsync(
            _tenantId,
            "qobuz.account",
            Encoding.UTF8.GetBytes("second-fixture-secret"));
        var revoked = await store.StoreAsync(
            _tenantId,
            "retired.account",
            Encoding.UTF8.GetBytes("revoked-fixture-secret"));
        await store.RevokeAsync(revoked.Id, new SecretAccessContext(_tenantId));
        var key1 = ReadKey("key-1");
        WriteKeyRing("key-2", new Dictionary<string, byte[]>
        {
            ["key-1"] = key1,
            ["key-2"] = RandomNumberGenerator.GetBytes(32)
        });

        var rotated = await store.RotateAllEncryptionAsync();
        var repeated = await store.RotateAllEncryptionAsync();

        Assert.Equal("key-2", rotated.ActiveKeyId);
        Assert.Equal(2, rotated.Examined);
        Assert.Equal(2, rotated.Rotated);
        Assert.Equal(0, rotated.AlreadyActive);
        Assert.Equal(2, repeated.Examined);
        Assert.Equal(0, repeated.Rotated);
        Assert.Equal(2, repeated.AlreadyActive);
        await using var context = await _factory.CreateDbContextAsync();
        var activeVersions = await context.SecretReferences.AsNoTracking()
            .Where(item => item.Id == first.Id || item.Id == second.Id)
            .Join(
                context.SecretVersions.AsNoTracking(),
                reference => new { ReferenceId = reference.Id, Version = reference.ActiveVersion },
                version => new { ReferenceId = version.SecretReferenceId, version.Version },
                (_, version) => version)
            .ToListAsync();
        Assert.All(activeVersions, version => Assert.Equal("key-2", version.KeyId));
    }

    [Fact]
    public async Task Revocation_PreventsFutureReadsAndReplacement()
    {
        var store = CreateStore();
        var info = await store.StoreAsync(
            _tenantId,
            "lastfm.session",
            Encoding.UTF8.GetBytes("revoked-fixture-secret"));

        await store.RevokeAsync(info.Id, new SecretAccessContext(_tenantId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.OpenAsync(info.Id, new SecretAccessContext(_tenantId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(
            _tenantId,
            info.Purpose,
            Encoding.UTF8.GetBytes("replacement"),
            info.Id));
    }

    [Fact]
    public async Task KeyRing_WithBroadPermissions_IsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            _keyRingPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var provider = new FileSecretKeyRingProvider(new SecretStoreOptions
        {
            KeyRingPath = _keyRingPath
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoadAsync());

        Assert.Contains("group/other", exception.Message, StringComparison.Ordinal);
    }

    private EncryptedSecretStore CreateStore()
    {
        var options = new SecretStoreOptions { KeyRingPath = _keyRingPath };
        return new EncryptedSecretStore(
            _factory,
            new FileSecretKeyRingProvider(options),
            options,
            new SystemPlatformClock());
    }

    [Fact]
    public async Task StartupWithoutSecrets_CreatesPrivateKeyRingAndPreservesItOnRestart()
    {
        File.Delete(_keyRingPath);
        var (initializer, _) = CreateInitializer();
        await initializer.StartAsync(default);
        Assert.True(File.Exists(_keyRingPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_keyRingPath));
        var saved = await File.ReadAllBytesAsync(_keyRingPath);
        var reference = await CreateStore().StoreAsync(_tenantId, "startup.fixture", Encoding.UTF8.GetBytes("fixture"));
        await initializer.StartAsync(default);
        Assert.Equal(saved, await File.ReadAllBytesAsync(_keyRingPath));
        using var secret = await CreateStore().OpenAsync(reference.Id, new SecretAccessContext(_tenantId));
        Assert.Equal("fixture", secret.ReadUtf8());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task MissingKeyRingWithSecrets_IsNeverRegeneratedAndStorageRemainsReady()
    {
        var reference = await CreateStore().StoreAsync(_tenantId, "startup.fixture", Encoding.UTF8.GetBytes("fixture"));
        var saved = await File.ReadAllBytesAsync(_keyRingPath);
        File.Delete(_keyRingPath);
        var (initializer, storage) = CreateInitializer();
        await initializer.StartAsync(default);

        Assert.False(File.Exists(_keyRingPath));
        Assert.Equal(DurableStorageReadiness.Ready, storage.GetSnapshot().Readiness);
        await Assert.ThrowsAsync<FileNotFoundException>(() => CreateStore().OpenAsync(reference.Id, new SecretAccessContext(_tenantId)));
        await File.WriteAllBytesAsync(_keyRingPath, saved);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_keyRingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var restored = await CreateStore().OpenAsync(reference.Id, new SecretAccessContext(_tenantId));
        Assert.Equal("fixture", restored.ReadUtf8());
    }

    [Fact]
    public async Task ConcurrentCreation_PublishesOneCompleteRingWithoutTemporaryFiles()
    {
        File.Delete(_keyRingPath);
        var provider = new FileSecretKeyRingProvider(new SecretStoreOptions { KeyRingPath = _keyRingPath });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.CreateIfMissingAsync(false)));
        Assert.Single(results, created => created);
        var ring = await provider.LoadAsync();
        Assert.Equal(32, ring.GetActiveKey().Length);
        foreach (var key in ring.Keys.Values) CryptographicOperations.ZeroMemory(key);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task InvalidExistingRing_IsPreservedAndUnavailableStorageDoesNotCreateOne()
    {
        await File.WriteAllTextAsync(_keyRingPath, "{invalid");
        var (initializer, storage) = CreateInitializer();
        await initializer.StartAsync(default);
        Assert.Equal("{invalid", await File.ReadAllTextAsync(_keyRingPath));
        File.Delete(_keyRingPath);
        storage.Set(DurableStorageReadiness.Unavailable);
        await initializer.StartAsync(default);
        Assert.False(File.Exists(_keyRingPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_UsesDataDirectoryUnlessKeyRingPathIsExplicit(bool overridden)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataDirectory"] = _root,
            ["Secrets:KeyRingPath"] = overridden ? Path.Combine(_root, "external.json") : null
        }).Build();
        var services = new ServiceCollection();
        services.AddEncryptedSecretStore(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(Path.Combine(_root, overridden ? "external.json" : "keyring.json"),
            provider.GetRequiredService<SecretStoreOptions>().KeyRingPath);
    }

    private (SecretStoreInitializer Initializer, DurableStorageState Storage) CreateInitializer()
    {
        var options = new SecretStoreOptions { KeyRingPath = _keyRingPath };
        var storage = new DurableStorageState(new StorageOptions { DataDirectory = _root });
        storage.Set(DurableStorageReadiness.Ready);
        return (new SecretStoreInitializer(_factory, storage, new FileSecretKeyRingProvider(options), options,
            NullLogger<SecretStoreInitializer>.Instance), storage);
    }

    private void WriteKeyRing(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        var document = JsonSerializer.Serialize(new
        {
            activeKeyId,
            keys = keys.ToDictionary(item => item.Key, item => Convert.ToBase64String(item.Value))
        });
        File.WriteAllText(_keyRingPath, document);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _keyRingPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private byte[] ReadKey(string keyId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_keyRingPath));
        return Convert.FromBase64String(
            document.RootElement.GetProperty("keys").GetProperty(keyId).GetString()!);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        if (_database is not null) await _database.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
