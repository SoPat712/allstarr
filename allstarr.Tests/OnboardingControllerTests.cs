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
    private PostgresTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private DurableRuntimeSettingsService _settings = null!;
    private LegacyEnvMigrationService _migration = null!;
    private OnboardingStateService _onboarding = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestFactory(_database.Options);
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
        _onboarding = new OnboardingStateService(_factory, clock);
    }

    [Fact]
    public async Task Complete_IsDurableIdempotentAndTenantScoped()
    {
        var first = Controller(Session(_firstTenantId, _firstUserId));

        var initial = Payload(Assert.IsType<OkObjectResult>(await first.GetStatus()).Value);
        Assert.False(initial.GetProperty("completed").GetBoolean());
        Assert.True(initial.GetProperty("shouldRedirectToSetup").GetBoolean());

        var blocked = Assert.IsType<ConflictObjectResult>(await first.Complete());
        Assert.Equal(
            "backend_identity_required",
            Payload(blocked.Value).GetProperty("code").GetString());
        await AddIdentity(_firstTenantId, _firstUserId);

        var completed = Payload(Assert.IsType<OkObjectResult>(await first.Complete()).Value);
        Assert.True(completed.GetProperty("completed").GetBoolean());
        Assert.False(completed.GetProperty("alreadyCompleted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("completedAt").ValueKind);
        Assert.False(completed.GetProperty("shouldRedirectToSetup").GetBoolean());

        var repeated = Payload(Assert.IsType<OkObjectResult>(await first.Complete()).Value);
        Assert.True(repeated.GetProperty("alreadyCompleted").GetBoolean());

        var second = Payload(Assert.IsType<OkObjectResult>(
            await Controller(Session(_secondTenantId, _secondUserId)).GetStatus()).Value);
        Assert.False(second.GetProperty("completed").GetBoolean());

        await using var db = await _factory.CreateDbContextAsync();
        var state = await db.OnboardingStates.SingleAsync();
        Assert.Equal(_firstTenantId, state.TenantId);
        Assert.Equal(_firstUserId, state.UserId);
        Assert.Equal(OnboardingStateService.SchemaVersion, state.SchemaVersion);
        Assert.Equal(1, await db.AuditEvents.CountAsync(item => item.Action == "onboarding.complete"));

        var restarted = new OnboardingStateService(_factory, new FixedClock(
            DateTimeOffset.Parse("2026-07-14T13:00:00Z")));
        var afterRestart = await restarted.GetAsync(_firstTenantId, _firstUserId);
        Assert.True(afterRestart.Completed);
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

    [Fact]
    public async Task Reopen_IsExplicitAndDoesNotConfuseRuntimeRecoveryWithFirstSetup()
    {
        await AddIdentity(_firstTenantId, _firstUserId);
        var controller = Controller(Session(_firstTenantId, _firstUserId));
        Assert.IsType<OkObjectResult>(await controller.Complete());

        var reopened = Payload(Assert.IsType<OkObjectResult>(await controller.Reopen()).Value);
        Assert.False(reopened.GetProperty("completed").GetBoolean());
        Assert.True(reopened.GetProperty("setupOpen").GetBoolean());
        Assert.False(reopened.GetProperty("shouldRedirectToSetup").GetBoolean());

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.BackendIdentities.RemoveRange(db.BackendIdentities);
            await db.SaveChangesAsync();
        }
        var unhealthy = Payload(Assert.IsType<OkObjectResult>(await controller.GetStatus()).Value);
        Assert.False(unhealthy.GetProperty("shouldRedirectToSetup").GetBoolean());
        Assert.Contains(
            unhealthy.GetProperty("recoveryNotices").EnumerateArray(),
            item => item.GetString() == "backend_identity_missing");
    }

    [Fact]
    public async Task Complete_IsIdempotentAcrossConcurrentAdministratorTabs()
    {
        await AddIdentity(_firstTenantId, _firstUserId);

        var states = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            _onboarding.CompleteAsync(
                _firstTenantId,
                _firstUserId,
                $"tab-{index}")));

        Assert.All(states, state => Assert.True(state.Completed));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.OnboardingStates.ToListAsync());
        Assert.Single(
            await db.AuditEvents
                .Where(item => item.Action == "onboarding.complete")
                .ToListAsync());
    }

    private OnboardingController Controller(AdminAuthSession? session)
    {
        var context = new DefaultHttpContext();
        if (session != null)
        {
            context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        }

        return new OnboardingController(_onboarding, _migration)
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

    private async Task AddIdentity(Guid tenantId, Guid userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.BackendIdentities.Add(new BackendIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            BackendType = "jellyfin",
            BackendInstanceId = "primary",
            PrincipalId = "backend-user",
            CreatedAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
            LastSeenAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z")
        });
        await db.SaveChangesAsync();
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

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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
