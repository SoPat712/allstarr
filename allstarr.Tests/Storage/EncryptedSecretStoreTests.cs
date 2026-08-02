using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class EncryptedSecretStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private string _keyRingPath = string.Empty;
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await PostgresTestDatabase.CreateAsync();
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

    [Fact]
    public async Task SubsonicPlaylistAuthentication_ResolvesTenantSecretOnlyAtExecutionTime()
    {
        var store = CreateStore();
        var secret = await store.StoreAsync(
            _tenantId,
            "backend-playlist:subsonic:primary",
            Encoding.UTF8.GetBytes("{\"username\":\"playlist-user\",\"password\":\"playlist-password\"}"));
        var resolver = new EncryptedSubsonicPlaylistAuthenticationResolver(
            store,
            Options.Create(new SubsonicSettings()));

        var authentication = await resolver.ResolveAsync(
            new BackendPlaylistTargetContext(
                "primary",
                "backend-user",
                secret.Id.ToString(),
                _tenantId),
            default);

        Assert.Contains(authentication.FormParameters, item => item is { Key: "u", Value: "playlist-user" });
        Assert.Contains(authentication.FormParameters, item => item is { Key: "p", Value: "playlist-password" });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await resolver.ResolveAsync(
                new BackendPlaylistTargetContext(
                    "primary",
                    "other-user",
                    secret.Id.ToString(),
                    Guid.CreateVersion7()),
                default));
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
