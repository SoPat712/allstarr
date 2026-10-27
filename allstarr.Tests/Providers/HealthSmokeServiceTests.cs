using System.Text.Json;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using allstarr.Controllers;
using allstarr.Core.Configuration;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed class HealthSmokeServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private Factory _factory = null!;
    private EncryptedSecretStore _secrets = null!;
    private DurableRuntimeSettingsService _settings = null!;
    private DurableJobQueue _jobs = null!;
    private readonly Mock<IBackendConnectionVerifier> _verifier = new(MockBehavior.Strict);
    private readonly Mock<IPlatformClock> _clock = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly Guid _listener = Guid.NewGuid();
    private readonly Guid _administrator = Guid.NewGuid();
    private BackendType _backendType = BackendType.Subsonic;
    private HttpClient _client = null!;
    private IOptions<JellyfinSettings> _jellyfinSettings = null!;
    private HealthSmokeServerProbe _serverProbe = null!;
    private HealthSmokeProtocolProbeTests.Fixture _upstream = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        _clock.SetupGet(item => item.UtcNow).Returns(() => _now);
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(_database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        _secrets = new(_factory, ring, options, _clock.Object);
        _settings = new(_factory, new ConfigurationManager
        {
            ["Backend:Type"] = "Subsonic",
            ["Backend:Url"] = "http://backend-fixture"
        }, _clock.Object, new RuntimeSettingsChangeSignal());
        var jobs = new DurableJobOptions();
        _jobs = new(_factory, jobs, new JobPayloadPolicy(jobs), _clock.Object);
        _upstream = new("Subsonic");
        _client = new(_upstream) { BaseAddress = new("http://fixture/") };
        _jellyfinSettings = Options.Create(new JellyfinSettings
        {
            Url = "http://fixture",
            ApiKey = "fixture-api-key",
            ServiceCredentialKind = "api-key",
            UserId = "fixture-administrator"
        });
        _serverProbe = new(_client, _jellyfinSettings, TimeProvider.System);
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var (id, admin, principal) in new[] { (_listener, false, "fixture-principal"), (_administrator, true, "fixture-administrator") })
            db.Users.Add(new()
            {
                Id = id,
                IsAdmin = admin,
                Enabled = true,
                BackendType = "subsonic",
                BackendInstanceId = "fixture",
                BackendPrincipalId = principal,
                DisplayName = admin ? "Administrator" : "Check listener",
                CreatedAt = _now,
                UpdatedAt = _now,
                LastSeenAt = _now
            });
        await db.SaveChangesAsync();
    }

    private HealthSmokeService Service(Factory? factory = null) => new(factory ?? _factory, _secrets, _settings, _verifier.Object,
        new(_backendType, _backendType.ToString(), "fixture", false, false, null), new() { BackendInstanceId = "fixture" },
        _clock.Object, _jobs, new EffectiveProviderPolicyResolver(_settings), new(_client, TimeProvider.System), _jellyfinSettings, _serverProbe);

    private HealthSmokeSaveRequest Request(bool enabled = true, int interval = 30, long revision = 0) =>
        new(enabled, _listener, IntervalMinutes: interval, SearchQuery: "Fixture song", ExpectedRevision: revision,
            Username: "fixture-listener", Password: "fixture-password");

    private void Accept(string user = "fixture-principal", bool admin = false) => _verifier.Setup(item => item.VerifyAsync(_backendType,
        "http://backend-fixture", "fixture-listener", "fixture-password", It.IsAny<CancellationToken>()))
        .ReturnsAsync(new AdminOidcBackendUser(user, "Check listener", admin, "fixture-session"));

    private async Task UseJellyfinAsync()
    {
        _backendType = BackendType.Jellyfin;
        _client.Dispose();
        _upstream = new("Jellyfin");
        _client = new(_upstream) { BaseAddress = new("http://fixture/") };
        _serverProbe = new(_client, _jellyfinSettings, TimeProvider.System);
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var user in db.Users)
            user.BackendType = "jellyfin";
        await db.SaveChangesAsync();
    }

    private async Task<(Guid ScheduleId, Guid ReferenceId)> SeedLegacyJellyfinAsync()
    {
        var reference = await _secrets.StoreAsync(_listener, HealthSmokeService.ListenerPurpose,
            JsonSerializer.SerializeToUtf8Bytes(new HealthSmokeCredentials
            {
                UserName = "fixture-listener",
                Password = "fixture-password",
                AccessToken = "fixture-session"
            }));
        var scheduleId = Guid.NewGuid();
        await using var db = await _factory.CreateDbContextAsync();
        db.Users.Single(item => item.Id == _listener).IsSynthetic = true;
        db.JobSchedules.Add(new()
        {
            Id = scheduleId,
            OwnerUserId = _listener,
            JobType = HealthSmokeService.JobType,
            CronExpression = "* * * * *",
            TimeZoneId = "UTC",
            OverlapPolicy = ScheduleOverlapPolicy.Skip,
            MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
            PayloadTemplateJson = JsonSerializer.Serialize(new HealthSmokeSchedule(1, 0, 30, "Fixture song", 15,
                reference.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Enabled = true,
            CreatedAt = _now,
            UpdatedAt = _now,
            NextRunAt = _now.AddMinutes(30)
        });
        await db.SaveChangesAsync();
        return (scheduleId, reference.Id);
    }

    [Fact]
    public async Task UnconfiguredHealthChecksAreOffAndCreateNoWork()
    {
        var state = JsonSerializer.SerializeToElement(await Service().ReadAsync(default));
        Assert.False(state.GetProperty("configuration").GetProperty("enabled").GetBoolean());
        Assert.Equal(30, state.GetProperty("configuration").GetProperty("intervalMinutes").GetInt32());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.JobSchedules.ToArrayAsync());
        Assert.Empty(await db.Jobs.ToArrayAsync());
        Assert.False((await db.Users.SingleAsync(item => item.Id == _listener)).IsSynthetic);
        Assert.DoesNotContain(_administrator.ToString(), state.ToString());
    }

    [Theory]
    [InlineData("other-principal", false)]
    [InlineData("fixture-principal", true)]
    public async Task CredentialsMustVerifyTheExactNonAdministratorListener(string user, bool administrator)
    {
        Accept(user, administrator);
        await Assert.ThrowsAsync<ArgumentException>(() => Service().ConfigureAsync(Request(), _administrator, default));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.SecretReferences.ToArrayAsync());
        Assert.Empty(await db.JobSchedules.ToArrayAsync());
        Assert.False((await db.Users.SingleAsync(item => item.Id == _listener)).IsSynthetic);
    }

    [Fact]
    public async Task ConfigurationEncryptsSignInAndWebhookAndKeepsDesignationAfterDisable()
    {
        Accept();
        await Service().ConfigureAsync(Request() with { WebhookUrl = "https://webhook-fixture/notify?key=fixture-private" }, _administrator, default);
        await using var db = await _factory.CreateDbContextAsync();
        var schedule = await db.JobSchedules.AsNoTracking().SingleAsync();
        var config = JsonSerializer.Deserialize<HealthSmokeSchedule>(schedule.PayloadTemplateJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True((await db.Users.SingleAsync(item => item.Id == _listener)).IsSynthetic);
        Assert.Equal(_now.AddMinutes(30), schedule.NextRunAt);
        using var stored = await _secrets.OpenAsync(config.CredentialReferenceId, new(_listener, HealthSmokeService.ListenerPurpose));
        var decrypted = stored.ReadUtf8();
        Assert.Contains("fixture-session", decrypted);
        Assert.Contains("fixture-password", decrypted);
        Assert.DoesNotContain("fixture-session", schedule.PayloadTemplateJson);
        Assert.DoesNotContain("fixture-password", schedule.PayloadTemplateJson);
        var ciphertext = Convert.ToBase64String(await db.SecretVersions.AsNoTracking()
            .Where(item => item.SecretReferenceId == config.CredentialReferenceId)
            .Select(item => item.Ciphertext).SingleAsync());
        Assert.DoesNotContain("fixture-session", ciphertext);
        Assert.DoesNotContain("fixture-password", ciphertext);
        var projection = JsonSerializer.Serialize(await Service().ReadAsync(default));
        Assert.DoesNotContain("fixture-private", projection);
        Assert.DoesNotContain("fixture-session", projection);
        Assert.DoesNotContain("fixture-password", projection);
        await Service().ConfigureAsync(Request(enabled: false, revision: 1) with { Username = null, Password = null }, _administrator, default);
        db.ChangeTracker.Clear();
        Assert.True((await db.Users.SingleAsync(item => item.Id == _listener)).IsSynthetic);
        Assert.False((await db.JobSchedules.SingleAsync()).Enabled);
    }

    [Fact]
    public async Task ManualAndScheduledChecksDoNotOverlapAndUseTheConfiguredInterval()
    {
        Accept();
        await Service().ConfigureAsync(Request(interval: 47), _administrator, default);
        await Service().EnqueueAsync(default);
        await Assert.ThrowsAsync<HealthSmokeConfigurationException>(() => Service().EnqueueAsync(default));
        _now = _now.AddMinutes(47);
        var engine = new DurableScheduleEngine(_factory, _jobs, _clock.Object, ReleaseComposition.Core);
        Assert.Equal(1, (await engine.TickAsync()).SkippedOverlap);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.ToArrayAsync());
        Assert.Equal(_now.AddMinutes(47), (await db.JobSchedules.SingleAsync()).NextRunAt);
    }

    [Fact]
    public async Task ManualAdmissionHoldsTheWriteTransactionAgainstADueSchedule()
    {
        Accept();
        await Service().ConfigureAsync(Request(), _administrator, default);
        _now = _now.AddMinutes(30);
        var admission = new AdmissionReadInterceptor();
        var scheduler = new ScheduleTransactionInterceptor();
        var manualFactory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(admission).Options);
        var scheduleFactory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(scheduler).Options);
        var manual = Task.Run(() => Service(manualFactory).EnqueueAsync(default));
        await admission.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var tick = Task.Run(() => new DurableScheduleEngine(scheduleFactory, _jobs, _clock.Object,
            ReleaseComposition.Core).TickAsync());
        try
        {
            await scheduler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(admission.InTransaction);
        }
        finally { admission.Release.TrySetResult(true); }
        await manual.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await tick.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.SkippedOverlap);
        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.Jobs.Where(item => item.Type == HealthSmokeService.JobType).ToArrayAsync());
    }

    [Fact]
    public async Task ConfigurationRechecksAdmissionAfterListenerVerification()
    {
        Accept();
        var service = Service();
        await service.ConfigureAsync(Request(), _administrator, default);
        var verifying = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _verifier.Setup(item => item.VerifyAsync(BackendType.Subsonic, "http://backend-fixture", "fixture-listener",
            "fixture-password", It.IsAny<CancellationToken>())).Returns(async () =>
            {
                verifying.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return new AdminOidcBackendUser("fixture-principal", "Check listener", false, "fixture-session");
            });
        var saving = service.ConfigureAsync(Request(interval: 47, revision: 1), _administrator, default);
        await verifying.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _now = _now.AddMinutes(30);
        try
        {
            var result = await new DurableScheduleEngine(_factory, _jobs, _clock.Object, ReleaseComposition.Core).TickAsync();
            Assert.Equal(1, result.Enqueued);
        }
        finally { release.TrySetResult(true); }
        var conflict = await Assert.ThrowsAsync<HealthSmokeConfigurationException>(() => saving);
        Assert.Equal("check_running", conflict.Code);
        await using var db = _factory.CreateDbContext();
        var schedule = await db.JobSchedules.SingleAsync();
        var configuration = JsonSerializer.Deserialize<HealthSmokeSchedule>(schedule.PayloadTemplateJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, configuration.Revision);
        Assert.Equal(30, configuration.IntervalMinutes);
        Assert.Single(await db.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task OnDemandRunPersistsEveryStepAndNoTemporaryArtifacts()
    {
        Accept();
        var service = Service();
        await service.ConfigureAsync(Request(), _administrator, default);
        var id = await service.EnqueueAsync(default);
        var claim = await _jobs.ClaimNextAsync("check-worker", [HealthSmokeService.JobType]);
        Assert.NotNull(claim);
        using var services = new ServiceCollection().BuildServiceProvider();
        var completion = await service.ExecuteAsync(new(claim!, services), default);
        await _jobs.CompleteAsync(claim!, completion);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await using var db = await _factory.CreateDbContextAsync();
        var run = await db.HealthSmokeRuns.SingleAsync();
        Assert.Equal(id, run.Id);
        Assert.NotNull(run.CompletedJson);
        Assert.DoesNotContain("fixture-session", run.CompletedJson);
        var artifact = JsonSerializer.Deserialize<HealthSmokeArtifacts>(run.ArtifactsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new HealthSmokeArtifacts(), artifact);
        Assert.False(_upstream.PlaylistExists);
        Assert.False(_upstream.Hearted);
        Assert.Equal(HealthSmokeProtocolProbe.StepNames.Length,
            JsonSerializer.SerializeToElement(await service.ReadAsync(default)).GetProperty("runs")[0].GetProperty("Steps").GetArrayLength());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task InvalidIntervalsCannotEnableWork(int interval)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().ConfigureAsync(Request(interval: interval), _administrator, default));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.JobSchedules.ToArrayAsync());
    }

    [Fact]
    public async Task AListenerCannotReadConfigureOrStartTheAdministratorCheck()
    {
        var controller = new HealthController(Service()) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Read(default)).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Configure(Request(), default)).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Run(default)).StatusCode);
        _verifier.VerifyNoOtherCalls();
        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.JobSchedules.ToArrayAsync());
        Assert.Empty(await db.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task JellyfinServerModeNeedsNoListenerCredentialsOrSyntheticUser()
    {
        await UseJellyfinAsync();
        var request = Request() with { ListenerUserId = Guid.Empty, Username = null, Password = null };

        await Service().ConfigureAsync(request, _administrator, default);

        await using var db = await _factory.CreateDbContextAsync();
        var schedule = await db.JobSchedules.AsNoTracking().SingleAsync();
        var configuration = JsonSerializer.Deserialize<HealthSmokeSchedule>(schedule.PayloadTemplateJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, configuration.Version);
        Assert.Equal(Guid.Empty, configuration.CredentialReferenceId);
        Assert.Equal(_administrator, schedule.OwnerUserId);
        Assert.False((await db.Users.SingleAsync(item => item.Id == _administrator)).IsSynthetic);
        Assert.Empty(await db.SecretReferences.Where(item => item.Purpose == HealthSmokeService.ListenerPurpose).ToArrayAsync());
        _verifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisabledJellyfinServerModeCanBeSavedWithoutAnApiKey()
    {
        await UseJellyfinAsync();
        _jellyfinSettings.Value.ApiKey = null;
        var request = Request(enabled: false) with { ListenerUserId = Guid.Empty, Username = null, Password = null };

        await Service().ConfigureAsync(request, _administrator, default);

        await using var db = await _factory.CreateDbContextAsync();
        var schedule = await db.JobSchedules.SingleAsync();
        Assert.False(schedule.Enabled);
        Assert.Equal(_administrator, schedule.OwnerUserId);
        Assert.Empty(await db.SecretReferences.Where(item => item.Purpose == HealthSmokeService.ListenerPurpose).ToArrayAsync());
    }

    [Fact]
    public async Task SwitchingJellyfinToServerModeRevokesOnlyThePreviousHealthListenerSecret()
    {
        await UseJellyfinAsync();
        var legacyReference = await _secrets.StoreAsync(_listener, HealthSmokeService.ListenerPurpose,
            JsonSerializer.SerializeToUtf8Bytes(new HealthSmokeCredentials { AccessToken = "fixture-legacy-token" }));
        var unrelated = await _secrets.StoreAsync(_listener, HealthSmokeService.ListenerPurpose,
            "unrelated-health-secret"u8.ToArray());
        await using (var seed = await _factory.CreateDbContextAsync())
        {
            seed.Users.Single(item => item.Id == _listener).IsSynthetic = true;
            seed.JobSchedules.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _listener,
                JobType = HealthSmokeService.JobType,
                CronExpression = "* * * * *",
                TimeZoneId = "UTC",
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                PayloadTemplateJson = JsonSerializer.Serialize(new HealthSmokeSchedule(1, 0, 30, "Fixture song", 15,
                    legacyReference.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Enabled = false,
                CreatedAt = _now,
                UpdatedAt = _now
            });
            await seed.SaveChangesAsync();
        }

        await Service().ConfigureAsync(Request(revision: 0) with { ListenerUserId = Guid.Empty, Username = null, Password = null },
            _administrator, default);

        await using var afterDb = await _factory.CreateDbContextAsync();
        Assert.NotNull((await afterDb.SecretReferences.SingleAsync(item => item.Id == legacyReference.Id)).RevokedAt);
        Assert.Null((await afterDb.SecretReferences.SingleAsync(item => item.Id == unrelated.Id)).RevokedAt);
    }

    [Fact]
    public async Task ConsecutiveFailuresQueueAnAlertAndRecoveryWithASafeWebhookBody()
    {
        Accept();
        var service = Service();
        await service.ConfigureAsync(Request() with { WebhookUrl = "http://webhook-fixture/notify?key=fixture-private" }, _administrator, default);
        using var services = new ServiceCollection().BuildServiceProvider();
        async Task RunAsync(string? failure)
        {
            _upstream.FailNextRunAt(failure);
            _now = _now.AddMinutes(1);
            await service.EnqueueAsync(default);
            var claim = await _jobs.ClaimNextAsync("check-worker", [HealthSmokeService.JobType]);
            var completion = await service.ExecuteAsync(new(claim!, services), default);
            await _jobs.CompleteAsync(claim!, completion);
            Assert.Equal(failure == null ? DurableJobCompletionKind.Succeeded : DurableJobCompletionKind.Failed, completion.Kind);
        }
        await RunAsync("lyrics");
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Jobs.CountAsync(item => item.Type == HealthSmokeService.AlertJobType));
        await RunAsync("lyrics");
        Assert.Equal(1, await db.Jobs.CountAsync(item => item.Type == HealthSmokeService.AlertJobType));
        await RunAsync(null);
        Assert.Equal(2, await db.Jobs.CountAsync(item => item.Type == HealthSmokeService.AlertJobType));
        var states = new List<string>();
        using var webhook = new WebhookHandler(async request =>
        {
            Assert.Equal("webhook-fixture", request.RequestUri!.Host);
            Assert.True(request.Headers.Contains("Idempotency-Key"));
            var body = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("fixture-private", body);
            Assert.DoesNotContain("fixture-session", body);
            Assert.DoesNotContain("fixture-listener", body);
            Assert.DoesNotContain(_listener.ToString(), body);
            var changes = JsonDocument.Parse(body).RootElement.GetProperty("changes").EnumerateArray().ToArray();
            states.Add(Assert.Single(changes).GetProperty("state").GetString()!);
            return new(HttpStatusCode.NoContent);
        });
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("health-webhook")).Returns(() => new HttpClient(webhook, disposeHandler: false));
        var handler = new HealthSmokeAlertJobHandler(_factory, _secrets, clients.Object, service);
        while (await _jobs.ClaimNextAsync("alert-worker", [HealthSmokeService.AlertJobType]) is { } claim)
        {
            var completion = await handler.ExecuteAsync(new(claim, services), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
            await _jobs.CompleteAsync(claim, completion);
        }
        Assert.Equal(["failed", "recovered"], states);
    }

    [Theory]
    [InlineData("cleared", 0)]
    [InlineData("replaced", 0)]
    [InlineData("unchanged", 1)]
    public async Task QueuedAlertsUseOnlyTheCurrentConfiguredDestination(string change, int expectedCalls)
    {
        Accept();
        var service = Service();
        await service.ConfigureAsync(Request() with { WebhookUrl = "http://webhook-fixture/original" }, _administrator, default);
        using var services = new ServiceCollection().BuildServiceProvider();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _upstream.FailNextRunAt("lyrics");
            _now = _now.AddMinutes(1);
            await service.EnqueueAsync(default);
            var check = await _jobs.ClaimNextAsync("check-worker", [HealthSmokeService.JobType]);
            await _jobs.CompleteAsync(check!, await service.ExecuteAsync(new(check!, services), default));
        }
        await using var db = _factory.CreateDbContext();
        var before = JsonSerializer.Deserialize<HealthSmokeSchedule>((await db.JobSchedules.SingleAsync()).PayloadTemplateJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(await db.Jobs.Where(item => item.Type == HealthSmokeService.AlertJobType).ToArrayAsync());
        await service.ConfigureAsync(Request(interval: 47, revision: 1) with
        {
            Username = null,
            Password = null,
            ClearWebhook = change == "cleared",
            WebhookUrl = change == "replaced" ? "http://webhook-fixture/replacement" : null
        }, _administrator, default);
        if (change != "unchanged")
        {
            await db.SecretReferences.Where(item => item.Id == before.WebhookReferenceId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.RevokedAt, _now));
        }
        var calls = 0;
        using var webhook = new WebhookHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("health-webhook")).Returns(() => new HttpClient(webhook, disposeHandler: false));
        var claim = await _jobs.ClaimNextAsync("alert-worker", [HealthSmokeService.AlertJobType]);
        var completion = await new HealthSmokeAlertJobHandler(_factory, _secrets, clients.Object, service)
            .ExecuteAsync(new(claim!, services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls == 0 ? "none" : "delivered", await db.HealthSmokeRuns.OrderByDescending(item => item.CreatedAt)
            .Select(item => item.AlertStatus).FirstAsync());
    }

    [Fact]
    public async Task ConfigurationChangesStartANewFailureStreak()
    {
        Accept();
        var service = Service();
        await service.ConfigureAsync(Request() with { WebhookUrl = "http://webhook-fixture/notify" }, _administrator, default);
        using var services = new ServiceCollection().BuildServiceProvider();
        async Task RunFailureAsync()
        {
            _upstream.FailNextRunAt("lyrics");
            _now = _now.AddMinutes(1);
            await service.EnqueueAsync(default);
            var claim = await _jobs.ClaimNextAsync("check-worker", [HealthSmokeService.JobType]);
            var completion = await service.ExecuteAsync(new(claim!, services), default);
            Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
            await _jobs.CompleteAsync(claim!, completion);
        }
        await RunFailureAsync();
        await service.ConfigureAsync(Request(revision: 1) with { Username = null, Password = null }, _administrator, default);
        await RunFailureAsync();
        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.Jobs.Where(item => item.Type == HealthSmokeService.AlertJobType).ToArrayAsync());
        await RunFailureAsync();
        Assert.Single(await db.Jobs.Where(item => item.Type == HealthSmokeService.AlertJobType).ToArrayAsync());
    }

    [Fact]
    public async Task FailedFinallyCleanupQueuesDurableRecoveryWithoutAnotherRun()
    {
        Accept();
        var service = Service();
        await service.ConfigureAsync(Request(), _administrator, default);
        _upstream.FailCleanup = true;
        await service.EnqueueAsync(default);
        var claim = await _jobs.ClaimNextAsync("check-worker", [HealthSmokeService.JobType]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var completion = await service.ExecuteAsync(new(claim!, services), default);
        await _jobs.CompleteAsync(claim!, completion);
        Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.Jobs.Where(item => item.Type == HealthSmokeService.CleanupJobType).ToArrayAsync());
        var saved = await db.HealthSmokeRuns.SingleAsync();
        Assert.Contains("cleanup", saved.CompletedJson);
        Assert.NotEqual(new HealthSmokeArtifacts(), JsonSerializer.Deserialize<HealthSmokeArtifacts>(saved.ArtifactsJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        _upstream.FailCleanup = false;
        var recovery = await _jobs.ClaimNextAsync("cleanup-worker", [HealthSmokeService.CleanupJobType]);
        Assert.NotNull(recovery);
        var recovered = await new HealthSmokeCleanupJobHandler(service).ExecuteAsync(new(recovery!, services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, recovered.Kind);
        db.ChangeTracker.Clear();
        Assert.Equal(new HealthSmokeArtifacts(), JsonSerializer.Deserialize<HealthSmokeArtifacts>(
            (await db.HealthSmokeRuns.SingleAsync()).ArtifactsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData("Subsonic", "fixture")]
    [InlineData("Jellyfin", "other")]
    public async Task RecoveryNeverSendsSavedCredentialsToADifferentDeployment(string protocol, string instance)
    {
        await UseJellyfinAsync();
        var seeded = await SeedLegacyJellyfinAsync();
        var id = Guid.NewGuid();
        await using (var db = _factory.CreateDbContext())
        {
            var listener = await db.Users.SingleAsync(item => item.Id == _listener);
            listener.BackendType = protocol.ToLowerInvariant();
            listener.BackendInstanceId = instance;
            var schedule = await db.JobSchedules.SingleAsync(item => item.Id == seeded.ScheduleId);
            var config = JsonSerializer.Deserialize<HealthSmokeSchedule>(schedule.PayloadTemplateJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            db.HealthSmokeRuns.Add(new()
            {
                Id = id,
                OwnerUserId = _listener,
                CreatedAt = _now,
                BindingJson = JsonSerializer.Serialize(new HealthSmokeRunBinding(schedule.Id, _listener, protocol, instance,
                    "fixture-principal", config, _now), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ArtifactsJson = JsonSerializer.Serialize(new HealthSmokeArtifacts("ext-deezer-song-fixture", false),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
            await db.SaveChangesAsync();
        }
        var service = Service();
        var completion = await service.CleanupRunAsync(id, _listener, default);
        Assert.Equal(DurableJobCompletionKind.Retry, completion.Kind);
        Assert.Empty(_upstream.Requests);
        await using var verify = _factory.CreateDbContext();
        Assert.Null((await verify.HealthSmokeRuns.SingleAsync()).CompletedJson);
    }

    [Fact]
    public async Task LegacyJellyfinCleanupUsesTheSavedCredentialWithoutLoginOrPlayback()
    {
        await UseJellyfinAsync();
        var seeded = await SeedLegacyJellyfinAsync();
        var id = Guid.NewGuid();
        var playlistName = $"[allstarr-test] Health {id:N}";
        using var created = await _client.PostAsJsonAsync("/Playlists", new { Name = playlistName });
        Assert.True(_upstream.PlaylistExists);
        await using (var db = _factory.CreateDbContext())
        {
            var schedule = await db.JobSchedules.SingleAsync(item => item.Id == seeded.ScheduleId);
            var config = JsonSerializer.Deserialize<HealthSmokeSchedule>(schedule.PayloadTemplateJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            db.HealthSmokeRuns.Add(new()
            {
                Id = id,
                OwnerUserId = _listener,
                CreatedAt = _now,
                BindingJson = JsonSerializer.Serialize(new HealthSmokeRunBinding(schedule.Id, _listener, "Jellyfin", "fixture",
                    "fixture-principal", config, _now), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ArtifactsJson = JsonSerializer.Serialize(new HealthSmokeArtifacts(PlaylistName: playlistName),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
            await db.SaveChangesAsync();
        }

        var completion = await Service().CleanupRunAsync(id, _listener, default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.False(_upstream.PlaylistExists);
        Assert.DoesNotContain(_upstream.Requests, path => path.Contains("/Audio/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_upstream.Requests, path => path.Contains("AuthenticateByName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecoverPausesLegacyJellyfinScheduleAndRevokesOnlyItsHealthCredential()
    {
        await UseJellyfinAsync();
        var seeded = await SeedLegacyJellyfinAsync();
        var unrelated = await _secrets.StoreAsync(_listener, HealthSmokeService.ListenerPurpose,
            "unrelated-health-secret"u8.ToArray());

        await Service().RecoverAsync(default);
        await Service().RecoverAsync(default);

        await using var db = await _factory.CreateDbContextAsync();
        var schedule = await db.JobSchedules.SingleAsync(item => item.Id == seeded.ScheduleId);
        Assert.False(schedule.Enabled);
        Assert.Null(schedule.NextRunAt);
        Assert.NotNull((await db.SecretReferences.SingleAsync(item => item.Id == seeded.ReferenceId)).RevokedAt);
        Assert.Null((await db.SecretReferences.SingleAsync(item => item.Id == unrelated.Id)).RevokedAt);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task MultipleFailingStepsShareOneRetriedAlertDelivery()
    {
        Accept();
        var service = Service();
        await service.ConfigureAsync(Request() with { WebhookUrl = "http://webhook-fixture/notify" }, _administrator, default);
        _upstream.HtmlAudio = true;
        using var services = new ServiceCollection().BuildServiceProvider();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _now = _now.AddMinutes(1);
            await service.EnqueueAsync(default);
            var check = await _jobs.ClaimNextAsync("check-worker", [HealthSmokeService.JobType]);
            await _jobs.CompleteAsync(check!, await service.ExecuteAsync(new(check!, services), default));
        }
        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.Jobs.Where(item => item.Type == HealthSmokeService.AlertJobType).ToArrayAsync());
        var deliveries = 0;
        using var webhook = new WebhookHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var changes = body.RootElement.GetProperty("changes").EnumerateArray().ToArray();
            Assert.Equal(4, changes.Length);
            Assert.All(changes, change => Assert.Equal("failed", change.GetProperty("state").GetString()));
            return new(++deliveries == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
        });
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("health-webhook")).Returns(() => new HttpClient(webhook, disposeHandler: false));
        var handler = new HealthSmokeAlertJobHandler(_factory, _secrets, clients.Object, service);
        var claim = await _jobs.ClaimNextAsync("alert-worker", [HealthSmokeService.AlertJobType]);
        var completion = await handler.ExecuteAsync(new(claim!, services), default);
        Assert.Equal(DurableJobCompletionKind.Retry, completion.Kind);
        await _jobs.CompleteAsync(claim!, completion);
        Assert.Equal("failed", await db.HealthSmokeRuns.OrderByDescending(item => item.CreatedAt).Select(item => item.AlertStatus).FirstAsync());
        _now = _now.AddMinutes(2);
        claim = await _jobs.ClaimNextAsync("alert-worker", [HealthSmokeService.AlertJobType]);
        completion = await handler.ExecuteAsync(new(claim!, services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await _jobs.CompleteAsync(claim!, completion);
        Assert.Equal("delivered", await db.HealthSmokeRuns.OrderByDescending(item => item.CreatedAt).Select(item => item.AlertStatus).FirstAsync());
        Assert.Equal(2, deliveries);
    }

    [Fact]
    public async Task RestartQueuesCleanupOnceWithoutRepeatingPlayback()
    {
        Accept();
        var service = Service();
        await service.ConfigureAsync(Request(), _administrator, default);
        var id = Guid.NewGuid();
        using var created = await _client.PostAsJsonAsync("/Playlists", new { Name = "[allstarr-test] Health " + id.ToString("N") });
        Assert.True(_upstream.PlaylistExists);
        _upstream.Hearted = true;
        await using (var db = _factory.CreateDbContext())
        {
            var schedule = await db.JobSchedules.SingleAsync();
            var config = JsonSerializer.Deserialize<HealthSmokeSchedule>(schedule.PayloadTemplateJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            db.HealthSmokeRuns.Add(new()
            {
                Id = id,
                OwnerUserId = _listener,
                CreatedAt = _now,
                BindingJson = JsonSerializer.Serialize(new HealthSmokeRunBinding(schedule.Id, _listener, "Subsonic", "fixture", "fixture-principal", config, _now), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ArtifactsJson = JsonSerializer.Serialize(new HealthSmokeArtifacts("ext-deezer-song-fixture", false, "fixture-playlist", "[allstarr-test] Health " + id.ToString("N")), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
            await db.SaveChangesAsync();
        }
        await Service().RecoverAsync(default);
        await Service().RecoverAsync(default);
        await using (var db = _factory.CreateDbContext())
            Assert.Single(await db.Jobs.Where(item => item.Type == HealthSmokeService.CleanupJobType).ToArrayAsync());
        var claim = await _jobs.ClaimNextAsync("cleanup-worker", [HealthSmokeService.CleanupJobType]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var completion = await new HealthSmokeCleanupJobHandler(Service()).ExecuteAsync(new(claim!, services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.False(_upstream.PlaylistExists);
        Assert.False(_upstream.Hearted);
        Assert.DoesNotContain(_upstream.Requests, path => path.Contains("/Audio/", StringComparison.Ordinal));
        await using var verify = _factory.CreateDbContext();
        Assert.Equal(new HealthSmokeArtifacts(), JsonSerializer.Deserialize<HealthSmokeArtifacts>((await verify.HealthSmokeRuns.SingleAsync()).ArtifactsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task JellyfinServerRunRejectsAChangedAdministratorBeforeAnyBackendRequest()
    {
        await UseJellyfinAsync();
        var service = Service();
        await service.ConfigureAsync(Request() with { ListenerUserId = Guid.Empty, Username = null, Password = null }, _administrator, default);
        await service.EnqueueAsync(default);
        var claim = await _jobs.ClaimNextAsync("check-worker", [HealthSmokeService.JobType]);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Single(item => item.Id == _administrator).Enabled = false;
            await db.SaveChangesAsync();
        }

        using var services = new ServiceCollection().BuildServiceProvider();
        var completion = await service.ExecuteAsync(new(claim!, services), default);

        Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
        Assert.Equal("health_scope_changed", completion.ErrorCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task JellyfinServerRunRejectsAChangedConfigurationRevisionBeforeAnyBackendRequest()
    {
        await UseJellyfinAsync();
        var service = Service();
        await service.ConfigureAsync(Request() with { ListenerUserId = Guid.Empty, Username = null, Password = null }, _administrator, default);
        await service.EnqueueAsync(default);
        var claim = await _jobs.ClaimNextAsync("check-worker", [HealthSmokeService.JobType]);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var schedule = await db.JobSchedules.SingleAsync();
            var config = JsonSerializer.Deserialize<HealthSmokeSchedule>(schedule.PayloadTemplateJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            schedule.PayloadTemplateJson = JsonSerializer.Serialize(config with { Revision = config.Revision + 1 }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.SaveChangesAsync();
        }

        using var services = new ServiceCollection().BuildServiceProvider();
        var completion = await service.ExecuteAsync(new(claim!, services), default);

        Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
        Assert.Equal("health_scope_changed", completion.ErrorCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task JellyfinServerRunRejectsAnExplicitLegacySchedule()
    {
        await UseJellyfinAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.JobSchedules.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _listener,
                JobType = HealthSmokeService.JobType,
                CronExpression = "* * * * *",
                TimeZoneId = "UTC",
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                PayloadTemplateJson = JsonSerializer.Serialize(new HealthSmokeSchedule(1, 1, 30, "Fixture song", 15,
                    Guid.NewGuid()), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Enabled = true,
                CreatedAt = _now,
                UpdatedAt = _now
            });
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<HealthSmokeConfigurationException>(() => Service().EnqueueAsync(default));

        Assert.Equal("legacy_disabled", error.Code);
        Assert.Empty(_upstream.Requests);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _database.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken token = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class AdmissionReadInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool InTransaction { get; private set; }
        private int _observed;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT EXISTS", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"durable_jobs\"", StringComparison.Ordinal) && Interlocked.Exchange(ref _observed, 1) == 0)
            {
                InTransaction = eventData.Context!.Database.CurrentTransaction != null;
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ScheduleTransactionInterceptor : DbTransactionInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class WebhookHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
}
