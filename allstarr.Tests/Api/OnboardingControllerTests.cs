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
    private readonly Guid _firstUserId = Guid.CreateVersion7();
    private readonly Guid _secondUserId = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private DurableRuntimeSettingsService _settings = null!;
    private LegacyEnvMigrationService _migration = null!;
    private OnboardingStateService _onboarding = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestFactory(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        var now = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        db.Users.AddRange(
            User(_firstUserId, "first-admin", "First admin", enabled: false, now: now),
            User(_secondUserId, "second-admin", "Second admin", enabled: true, now: now));
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
    public async Task Complete_IsDurableIdempotentAndUserScoped()
    {
        var first = Controller(Session(_firstUserId));

        var initial = Payload(Assert.IsType<OkObjectResult>(await first.GetStatus()).Value);
        Assert.False(initial.GetProperty("completed").GetBoolean());
        Assert.True(initial.GetProperty("shouldRedirectToSetup").GetBoolean());

        var blocked = Assert.IsType<ConflictObjectResult>(await first.Complete());
        Assert.Equal(
            "backend_identity_required",
            Payload(blocked.Value).GetProperty("code").GetString());
        await EnableIdentity(_firstUserId);

        var completed = Payload(Assert.IsType<OkObjectResult>(await first.Complete()).Value);
        Assert.True(completed.GetProperty("completed").GetBoolean());
        Assert.False(completed.GetProperty("alreadyCompleted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("completedAt").ValueKind);
        Assert.False(completed.GetProperty("shouldRedirectToSetup").GetBoolean());

        var repeated = Payload(Assert.IsType<OkObjectResult>(await first.Complete()).Value);
        Assert.True(repeated.GetProperty("alreadyCompleted").GetBoolean());

        var second = Payload(Assert.IsType<OkObjectResult>(
            await Controller(Session(_secondUserId)).GetStatus()).Value);
        Assert.False(second.GetProperty("completed").GetBoolean());

        await using var db = await _factory.CreateDbContextAsync();
        var state = await db.OnboardingStates.SingleAsync();
        Assert.Equal(_firstUserId, state.UserId);
        Assert.Equal(OnboardingStateService.SchemaVersion, state.SchemaVersion);
        Assert.Equal(1, await db.AuditEvents.CountAsync(item => item.Action == "onboarding.complete"));

        var restarted = new OnboardingStateService(_factory, new FixedClock(
            DateTimeOffset.Parse("2026-07-14T13:00:00Z")));
        var afterRestart = await restarted.GetAsync(_firstUserId);
        Assert.True(afterRestart.Completed);
    }

    [Fact]
    public async Task Status_UsesDeploymentMigrationReceiptAsAuthority()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var auditId = Guid.CreateVersion7();
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = auditId,
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
                ActorUserId = _firstUserId,
                AuditEventId = auditId,
                SourceSha256 = new string('a', 64),
                ResultJson = "{}",
                AppliedAt = DateTimeOffset.Parse("2026-07-14T12:30:00Z")
            });
            await db.SaveChangesAsync();
        }

        var first = Payload(Assert.IsType<OkObjectResult>(
            await Controller(Session(_firstUserId)).GetStatus()).Value);
        Assert.True(first.GetProperty("migration").GetProperty("Completed").GetBoolean());
        Assert.False(first.GetProperty("migration").GetProperty("FirstRun").GetBoolean());

        var second = Payload(Assert.IsType<OkObjectResult>(
            await Controller(Session(_secondUserId)).GetStatus()).Value);
        Assert.True(second.GetProperty("migration").GetProperty("Completed").GetBoolean());
        Assert.False(second.GetProperty("migration").GetProperty("FirstRun").GetBoolean());
    }

    [Fact]
    public async Task Endpoints_RequireAdministratorWithLinkedUser()
    {
        Assert.IsType<UnauthorizedObjectResult>(await Controller(null).GetStatus());

        var user = Session(_firstUserId, administrator: false);
        var forbidden = Assert.IsType<ObjectResult>(await Controller(user).Complete());
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var unlinked = Session(null);
        Assert.IsType<ConflictObjectResult>(await Controller(unlinked).GetStatus());
    }

    [Fact]
    public async Task Reopen_IsExplicitAndDoesNotConfuseRuntimeRecoveryWithFirstSetup()
    {
        await EnableIdentity(_firstUserId);
        var controller = Controller(Session(_firstUserId));
        Assert.IsType<OkObjectResult>(await controller.Complete());

        var reopened = Payload(Assert.IsType<OkObjectResult>(await controller.Reopen()).Value);
        Assert.False(reopened.GetProperty("completed").GetBoolean());
        Assert.True(reopened.GetProperty("setupOpen").GetBoolean());
        Assert.False(reopened.GetProperty("shouldRedirectToSetup").GetBoolean());

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var user = await db.Users.SingleAsync(item => item.Id == _firstUserId);
            user.Enabled = false;
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
        await EnableIdentity(_firstUserId);

        var states = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            _onboarding.CompleteAsync(
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

    [Fact]
    public async Task Steps_ReviewSkipResetAndResumeAcrossRestartsAndUsers()
    {
        await EnableIdentity(_firstUserId);
        var first = await _onboarding.SetStepAsync(_firstUserId, "sources", "review", 0, "review-sources");
        var skipped = await _onboarding.SetStepAsync(_firstUserId, "apple", "skip", first.Revision, "skip-apple");
        Assert.Contains("apple", skipped.SkippedSteps);
        Assert.Equal("reviewed", skipped.Steps.Single(item => item.Id == "sources").State);
        Assert.False(skipped.Completed);
        var restarted = new OnboardingStateService(_factory, new FixedClock(DateTimeOffset.UtcNow));
        var resumed = await restarted.GetAsync(_firstUserId);
        Assert.Equal(skipped.Revision, resumed.Revision);
        Assert.Equal("skipped", resumed.Steps.Single(item => item.Id == "apple").State);
        Assert.All((await restarted.GetAsync(_secondUserId)).Steps, step => Assert.Equal("pending", step.State));
        var reset = await restarted.SetStepAsync(_firstUserId, "apple", "reset", resumed.Revision, "reset-apple");
        Assert.Empty(reset.SkippedSteps);
        Assert.Equal("pending", reset.Steps.Single(item => item.Id == "apple").State);
        var completed = await restarted.CompleteAsync(_firstUserId, "finish");
        Assert.True(completed.Completed);
        Assert.Contains("apple", completed.SkippedSteps);
        Assert.DoesNotContain("sources", completed.SkippedSteps);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Steps_RejectStaleTabsAndRevokedAdministratorsWithoutChangingProgress()
    {
        await EnableIdentity(_firstUserId);
        var saved = await _onboarding.SetStepAsync(_firstUserId, "sources", "review", 0, "first-tab");
        var stale = await Assert.ThrowsAsync<OnboardingStateException>(() =>
            _onboarding.SetStepAsync(_firstUserId, "apple", "skip", 0, "stale-tab"));
        Assert.Equal("setup_changed", stale.Code);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var user = await db.Users.SingleAsync(item => item.Id == _firstUserId);
            user.IsAdmin = false;
            await db.SaveChangesAsync();
        }
        var denied = await Assert.ThrowsAsync<OnboardingStateException>(() =>
            _onboarding.SetStepAsync(_firstUserId, "apple", "skip", saved.Revision, "revoked-tab"));
        Assert.Equal("backend_identity_required", denied.Code);
        Assert.Equal(saved.Revision, (await _onboarding.GetAsync(_firstUserId)).Revision);
        Assert.IsType<ConflictObjectResult>(await Controller(Session(_firstUserId)).Complete());
        Assert.IsType<ConflictObjectResult>(await Controller(Session(_firstUserId)).Reopen());
    }

    [Theory]
    [InlineData("backend-identity", "skip", "required_step")]
    [InlineData("backend-identity", "reset", "required_step")]
    [InlineData("unknown", "review", "unknown_step")]
    [InlineData("sources", "execute", "invalid_step_action")]
    public async Task Steps_RejectUnsupportedOrRequiredChanges(string step, string action, string code)
    {
        await EnableIdentity(_firstUserId);
        var error = await Assert.ThrowsAsync<OnboardingStateException>(() =>
            _onboarding.SetStepAsync(_firstUserId, step, action, 0, "invalid-step"));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, (await _onboarding.GetAsync(_firstUserId)).Revision);
    }

    [Fact]
    public async Task StepEndpoint_ReturnsSharedGuideAndRequiresTheAdministratorScope()
    {
        var request = new OnboardingController.SetupStepRequest("apple", "skip", 0);
        Assert.IsType<UnauthorizedObjectResult>(await Controller(null).SetStep(request));
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Controller(Session(_firstUserId, false)).SetStep(request)).StatusCode);
        await EnableIdentity(_firstUserId);
        var result = Assert.IsType<OkObjectResult>(await Controller(Session(_firstUserId)).SetStep(request));
        var state = Payload(result.Value);
        Assert.Contains(state.GetProperty("skippedSteps").EnumerateArray(), item => item.GetString() == "apple");
        Assert.Equal(SetupGuide.Steps.Count, state.GetProperty("steps").GetArrayLength());
        var stale = Assert.IsType<ConflictObjectResult>(await Controller(Session(_firstUserId)).SetStep(request));
        Assert.Equal("setup_changed", Payload(stale.Value).GetProperty("code").GetString());
    }

    private AdminAuthSession Session(
        Guid? userId,
        bool administrator = true) => new()
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserId = userId == _secondUserId ? "second-admin" : "first-admin",
            UserName = "Admin",
            IsAdministrator = administrator,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
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

    private async Task EnableIdentity(Guid userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.Users.SingleAsync(item => item.Id == userId);
        user.Enabled = true;
        await db.SaveChangesAsync();
    }

    private static UserRecord User(
        Guid id,
        string backendPrincipalId,
        string name,
        bool enabled,
        DateTimeOffset now) => new()
        {
            Id = id,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = backendPrincipalId,
            DisplayName = name,
            IsAdmin = true,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
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
