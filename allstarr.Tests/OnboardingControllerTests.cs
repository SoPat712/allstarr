using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class OnboardingControllerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        $"onboarding-{Guid.NewGuid():N}");
    private readonly Guid _firstTenantId = Guid.CreateVersion7();
    private readonly Guid _firstUserId = Guid.CreateVersion7();
    private readonly Guid _secondTenantId = Guid.CreateVersion7();
    private readonly Guid _secondUserId = Guid.CreateVersion7();
    private TestFactory _factory = null!;
    private DurableRuntimeSettingsService _settings = null!;
    private LegacyEnvMigrationService _migration = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "onboarding.db");
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        _factory = new TestFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        db.Tenants.AddRange(
            new TenantRecord { Id = _firstTenantId, Slug = "first", Name = "First", CreatedAt = now },
            new TenantRecord { Id = _secondTenantId, Slug = "second", Name = "Second", CreatedAt = now });
        db.Users.AddRange(
            User(_firstTenantId, _firstUserId, "First admin", now),
            User(_secondTenantId, _secondUserId, "Second admin", now));
        await db.SaveChangesAsync();

        var clock = new FixedClock(now);
        _settings = new DurableRuntimeSettingsService(
            _factory,
            new ConfigurationBuilder().Build(),
            clock,
            new RuntimeSettingsChangeSignal());
        var secretOptions = new SecretStoreOptions { KeyRingPath = WriteKeyRing() };
        var secrets = new EncryptedSecretStore(
            _factory,
            new FileSecretKeyRingProvider(secretOptions),
            secretOptions,
            clock);
        _migration = new LegacyEnvMigrationService(_factory, _settings, secrets, clock);
    }

    [Fact]
    public async Task Complete_IsDurableIdempotentAndTenantScoped()
    {
        var first = Controller(Session(_firstTenantId, _firstUserId));

        var initial = Payload(Assert.IsType<OkObjectResult>(await first.GetStatus()).Value);
        Assert.False(initial.GetProperty("completed").GetBoolean());

        var completed = Payload(Assert.IsType<OkObjectResult>(await first.Complete()).Value);
        Assert.True(completed.GetProperty("completed").GetBoolean());
        Assert.False(completed.GetProperty("alreadyCompleted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("completedAt").ValueKind);

        var repeated = Payload(Assert.IsType<OkObjectResult>(await first.Complete()).Value);
        Assert.True(repeated.GetProperty("alreadyCompleted").GetBoolean());

        var second = Payload(Assert.IsType<OkObjectResult>(
            await Controller(Session(_secondTenantId, _secondUserId)).GetStatus()).Value);
        Assert.False(second.GetProperty("completed").GetBoolean());

        await using var db = await _factory.CreateDbContextAsync();
        var setting = await db.TenantRuntimeSettings.SingleAsync();
        Assert.Equal(_firstTenantId, setting.TenantId);
        Assert.Equal("WebUi:SetupCompleted", setting.Key);
        Assert.Equal(1, await db.AuditEvents.CountAsync(item => item.Action == "runtime-settings.batch-apply"));
    }

    [Fact]
    public async Task Status_UsesTenantMigrationReceiptAsAuthority()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var auditId = Guid.CreateVersion7();
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = auditId,
                TenantId = _firstTenantId,
                ActorUserId = _firstUserId,
                Category = "configuration",
                Action = "legacy-env.apply",
                Outcome = "succeeded",
                CorrelationId = "onboarding-test",
                DetailsJson = "{}",
                CreatedAt = DateTimeOffset.Parse("2026-07-14T12:30:00Z")
            });
            db.LegacyEnvImports.Add(new LegacyEnvImportRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = _firstTenantId,
                ActorUserId = _firstUserId,
                AuditEventId = auditId,
                SourceSha256 = new string('a', 64),
                ResultJson = "{}",
                AppliedAt = DateTimeOffset.Parse("2026-07-14T12:30:00Z")
            });
            await db.SaveChangesAsync();
        }

        var first = Payload(Assert.IsType<OkObjectResult>(
            await Controller(Session(_firstTenantId, _firstUserId)).GetStatus()).Value);
        Assert.True(first.GetProperty("migration").GetProperty("Completed").GetBoolean());
        Assert.False(first.GetProperty("migration").GetProperty("FirstRun").GetBoolean());

        var second = Payload(Assert.IsType<OkObjectResult>(
            await Controller(Session(_secondTenantId, _secondUserId)).GetStatus()).Value);
        Assert.False(second.GetProperty("migration").GetProperty("Completed").GetBoolean());
        Assert.True(second.GetProperty("migration").GetProperty("FirstRun").GetBoolean());
    }

    [Fact]
    public async Task Endpoints_RequireAdministratorWithLinkedTenant()
    {
        Assert.IsType<UnauthorizedObjectResult>(await Controller(null).GetStatus());

        var user = Session(_firstTenantId, _firstUserId, administrator: false);
        var forbidden = Assert.IsType<ObjectResult>(await Controller(user).Complete());
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var unlinked = Session(null, null);
        Assert.IsType<ConflictObjectResult>(await Controller(unlinked).GetStatus());
    }

    private OnboardingController Controller(AdminAuthSession? session)
    {
        var context = new DefaultHttpContext();
        if (session != null)
        {
            context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        }

        return new OnboardingController(_settings, _migration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static AdminAuthSession Session(
        Guid? tenantId,
        Guid? userId,
        bool administrator = true) => new()
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserId = "backend-user",
            UserName = "Admin",
            IsAdministrator = administrator,
            TenantId = tenantId,
            AllstarrUserId = userId,
            JellyfinAccessToken = "fixture-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };

    private string WriteKeyRing()
    {
        var path = Path.Combine(_root, "keyring.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            activeKeyId = "key-1",
            keys = new Dictionary<string, string>
            {
                ["key-1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static PlatformUserRecord User(Guid tenantId, Guid id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        DisplayName = name,
        Status = PlatformUserStatus.Active,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static JsonElement Payload(object? value) =>
        JsonSerializer.SerializeToElement(value);

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
