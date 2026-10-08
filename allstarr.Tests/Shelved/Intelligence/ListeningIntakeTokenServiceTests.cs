using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ListeningIntakeTokenServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-listening-intake", Guid.NewGuid().ToString("N"));
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private Factory _factory = null!;
    private ListeningIntakeTokenService _service = null!;
    private readonly IntelligenceScope _scope;

    public ListeningIntakeTokenServiceTests() =>
        _scope = new(_tenant, _user, "jellyfin", "main", "music");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        var now = DateTimeOffset.UtcNow;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Tenants.Add(new() { Id = _tenant, Slug = "intake", Name = "Intake", CreatedAt = now });
            db.Users.Add(new()
            {
                Id = _user,
                TenantId = _tenant,
                DisplayName = "Listener",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.BackendIdentities.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                UserId = _user,
                BackendType = "jellyfin",
                BackendInstanceId = "main",
                PrincipalId = "listener",
                CreatedAt = now,
                LastSeenAt = now
            });
            db.IntelligencePolicies.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                OwnerUserId = _user,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                LibraryScopeId = "music",
                Enabled = true,
                RetentionDays = 30,
                AllowedSignalTypesJson = "[\"complete\"]",
                EnabledProvidersJson = "[\"local-rules\"]",
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }

        var path = Path.Combine(_root, "keyring.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            activeKeyId = "key-1",
            keys = new Dictionary<string, string>
            {
                ["key-1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        }));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var options = new SecretStoreOptions { KeyRingPath = path };
        var clock = new SystemPlatformClock();
        _service = new(_factory, new(_factory, new FileSecretKeyRingProvider(options), options, clock), clock);
    }

    [Fact]
    public async Task TokenIsEncryptedExactScopedConstantTimeValidatedAndRevocable()
    {
        var created = await _service.CreateAsync(_scope, relayExternally: false);
        var grant = await _service.AuthorizeAsync(created.Token);

        Assert.NotNull(grant);
        Assert.Equal(_scope, grant.Scope);
        Assert.False(grant.RelayExternally);
        Assert.Null(await _service.AuthorizeAsync(created.Token[..^1] + (created.Token[^1] == '0' ? '1' : '0')));
        Assert.Single(await _service.ListAsync(_scope));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.ListeningIntakeTokens.SingleAsync();
            var encrypted = await db.SecretVersions.SingleAsync(item => item.SecretReferenceId == row.SecretReferenceId);
            Assert.DoesNotContain(created.Token, Convert.ToBase64String(encrypted.Ciphertext), StringComparison.Ordinal);
        }

        Assert.True(await _service.RevokeAsync(_scope, created.Id));
        Assert.Null(await _service.AuthorizeAsync(created.Token));
        Assert.Empty(await _service.ListAsync(_scope));
    }

    public async Task DisposeAsync()
    {
        if (_database != null) await _database.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
