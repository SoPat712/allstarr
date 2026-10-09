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
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _otherUser = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private Factory _factory = null!;
    private ListeningIntakeTokenService _service = null!;
    private readonly IntelligenceScope _scope;

    public ListeningIntakeTokenServiceTests() =>
        _scope = new(_user, "jellyfin", "main");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        var now = DateTimeOffset.UtcNow;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.AddRange(
                User(_user, "main", "listener", now),
                User(_otherUser, "other", "other-listener", now));
            db.IntelligencePolicies.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _user,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
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
        var otherScope = new IntelligenceScope(_otherUser, "jellyfin", "other");
        Assert.Empty(await _service.ListAsync(otherScope));
        Assert.False(await _service.RevokeAsync(otherScope, created.Id));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.ListeningIntakeTokens.SingleAsync();
            var reference = await db.SecretReferences.SingleAsync(item => item.Id == row.SecretReferenceId);
            Assert.Equal(_user, reference.UserId);
            Assert.Equal("listening-intake-token", reference.Purpose);
            var encrypted = await db.SecretVersions.SingleAsync(item => item.SecretReferenceId == row.SecretReferenceId);
            Assert.DoesNotContain(created.Token, Convert.ToBase64String(encrypted.Ciphertext), StringComparison.Ordinal);
        }

        Assert.True(await _service.RevokeAsync(_scope, created.Id));
        Assert.Null(await _service.AuthorizeAsync(created.Token));
        Assert.Empty(await _service.ListAsync(_scope));

        var disabled = await _service.CreateAsync(_scope, relayExternally: true);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == _user)).Enabled = false;
            await db.SaveChangesAsync();
        }
        Assert.Null(await _service.AuthorizeAsync(disabled.Token));
    }

    private static UserRecord User(Guid id, string backendInstanceId, string backendPrincipalId,
        DateTimeOffset now) => new()
        {
            Id = id,
            BackendType = "jellyfin",
            BackendInstanceId = backendInstanceId,
            BackendPrincipalId = backendPrincipalId,
            DisplayName = backendPrincipalId,
            IsAdmin = false,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
        };

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
