using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using allstarr.Controllers;
using allstarr.Core.Configuration;
using allstarr.Core.Health;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ProblemAlertTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private Factory _factory = null!;
    private EncryptedSecretStore _secrets = null!;
    private DurableJobQueue _jobs = null!;
    private ProblemAlertService _service = null!;
    private readonly Mock<IPlatformClock> _clock = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        _clock.SetupGet(item => item.UtcNow).Returns(() => _now);
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(_database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        _secrets = new(_factory, ring, options, _clock.Object);
        var jobs = new DurableJobOptions();
        _jobs = new(_factory, jobs, new JobPayloadPolicy(jobs), _clock.Object);
        _service = Service();
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var id in new[] { _owner, _other, _admin })
            db.Users.Add(new()
            {
                Id = id,
                IsAdmin = id == _admin,
                BackendType = "subsonic",
                BackendInstanceId = "fixture",
                BackendPrincipalId = id.ToString(),
                DisplayName = "Listener",
                Enabled = true
            });
        await db.SaveChangesAsync();
        await _service.SaveNetworkAsync(Session(_admin), ["notification-fixture"], 0, default);
    }

    private ProblemAlertService Service() => new(_factory, _secrets, _jobs, _clock.Object,
        new(BackendType.Subsonic, "Subsonic", "fixture", false, false, null), new() { BackendInstanceId = "fixture" });

    private AdminAuthSession Session(Guid id) => new()
    {
        SessionId = "fixture",
        UserId = id.ToString(),
        UserName = "Listener",
        AllstarrUserId = id,
        IsAdministrator = id == _admin,
        BackendType = "Subsonic",
        BackendInstanceId = "fixture",
        JellyfinAccessToken = "",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private Task<NotificationChannelView> Channel(Guid owner, bool household = false, string kind = "ntfy") =>
        _service.SaveChannelAsync(Session(owner), null, new("Push", kind, true, household,
            "http://notification-fixture/topic-private", ExpectedRevision: 0), default);

    private ProblemAlertEvidence Evidence(bool active = true, string severity = "warning", int open = 1, int recover = 1) =>
        new("provider-login", "source-fixture", _owner, active, severity, _now, open, recover);

    [Fact]
    public async Task RepeatedEvidenceAndRestartsCreateOneOpeningAndOneRecovery()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        for (var i = 0; i < 12; i++)
        {
            _now = _now.AddSeconds(30);
            await Service().ObserveAsync(Evidence(), default);
        }
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.NotificationDeliveries.ToArrayAsync());
        _now = _now.AddSeconds(30);
        await _service.ObserveAsync(Evidence(false), default);
        await _service.ObserveAsync(Evidence(false), default);
        Assert.Equal(2, await db.NotificationDeliveries.CountAsync());
        Assert.Equal("resolved", (await db.ProblemIncidents.SingleAsync()).State);
        _now = _now.AddMinutes(1);
        await _service.ObserveAsync(Evidence(), default);
        Assert.Equal(2, await db.NotificationDeliveries.CountAsync());
        Assert.Equal("open", (await db.ProblemIncidents.AsNoTracking().SingleAsync()).State);
        _now = _now.AddMinutes(31);
        await _service.ObserveAsync(Evidence(), default);
        Assert.Equal(3, await db.NotificationDeliveries.CountAsync());
    }

    [Fact]
    public async Task RecoveryDuringReopeningCooldownUpdatesStateWithoutAnExtraMessage()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(severity: "critical"), default);
        _now = _now.AddMinutes(1);
        await _service.ObserveAsync(Evidence(false), default);
        _now = _now.AddMinutes(1);
        await _service.ObserveAsync(Evidence(), default);
        _now = _now.AddMinutes(1);
        await _service.ObserveAsync(Evidence(false), default);
        await using var db = await _factory.CreateDbContextAsync();
        var incident = await db.ProblemIncidents.SingleAsync();
        Assert.Equal("resolved", incident.State);
        Assert.Equal("warning", incident.Severity);
        Assert.Equal(2, await db.NotificationDeliveries.CountAsync());
        _now = _now.AddMinutes(31);
        await _service.ObserveAsync(Evidence(), default);
        Assert.Equal(3, await db.NotificationDeliveries.CountAsync());
    }

    [Fact]
    public async Task FailuresAreWindowedAndRecoveryRequiresDistinctStableSamples()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(open: 3, recover: 2), default);
        _now = _now.AddMinutes(11);
        await _service.ObserveAsync(Evidence(open: 3, recover: 2), default);
        _now = _now.AddMinutes(1);
        await _service.ObserveAsync(Evidence(open: 3, recover: 2), default);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.NotificationDeliveries.ToArrayAsync());
        _now = _now.AddMinutes(1);
        await _service.ObserveAsync(Evidence(open: 3, recover: 2), default);
        Assert.Single(await db.NotificationDeliveries.ToArrayAsync());
        _now = _now.AddMinutes(1);
        await _service.ObserveAsync(Evidence(false, recover: 2), default);
        await _service.ObserveAsync(Evidence(false, recover: 2), default);
        Assert.Single(await db.NotificationDeliveries.ToArrayAsync());
        _now = _now.AddMinutes(1);
        await _service.ObserveAsync(Evidence(false, recover: 2), default);
        Assert.Equal(2, await db.NotificationDeliveries.CountAsync());
    }

    [Fact]
    public async Task PersonalIncidentsNeverRouteToAnotherListenerOrHouseholdChannel()
    {
        var personal = await Channel(_owner);
        await Channel(_other);
        var household = await Channel(_admin, true);
        await _service.ObserveAsync(Evidence(), default);
        await _service.ObserveAsync(new("disk-space", "volume-fixture", null, true, "critical", _now), default);
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.NotificationDeliveries.ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, item => item.ChannelId == personal.Id);
        Assert.Contains(rows, item => item.ChannelId == household.Id);
        Assert.Empty(await _service.ReadIncidentsAsync(Session(_other), default));
        Assert.Single(await _service.ReadIncidentsAsync(Session(_owner), default));
        Assert.Single(await _service.ReadIncidentsAsync(Session(_admin), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Channel(_owner, true));
    }

    [Fact]
    public async Task ChannelDestinationIsEncryptedAndAbsentFromResponsesAndJobs()
    {
        var channel = await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        var response = JsonSerializer.Serialize(await _service.ReadAsync(Session(_owner), default));
        Assert.DoesNotContain("topic-private", response);
        Assert.DoesNotContain("notification-fixture", response);
        Assert.True(channel.HasDestination);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.DoesNotContain("topic-private", JsonSerializer.Serialize(await db.NotificationChannels.ToArrayAsync()));
        Assert.DoesNotContain("topic-private", JsonSerializer.Serialize(await db.Jobs.ToArrayAsync()));
        Assert.Single(await db.SecretVersions.ToArrayAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SaveChannelAsync(Session(_other), channel.Id,
            new("Push", "ntfy", true, false, ExpectedRevision: 1), default));
    }

    [Fact]
    public async Task EscalationIsPromptAndRepeatedCriticalSamplesAreDeduplicated()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        _now = _now.AddSeconds(1);
        await _service.ObserveAsync(Evidence(severity: "critical"), default);
        _now = _now.AddSeconds(1);
        await _service.ObserveAsync(Evidence(severity: "critical"), default);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.NotificationDeliveries.CountAsync());
    }

    [Theory]
    [InlineData("rate-limited")]
    [InlineData("local-capacity-unavailable")]
    [InlineData("account-needs-configuration")]
    [InlineData("not-found")]
    public void NeutralProviderEvidenceNeverOpensAnIncident(string reason)
    {
        var status = new ProviderHealthObservation(1, ProviderHealthState.Degraded, _now, null, reason, 10, _now.AddMinutes(1));
        Assert.Null(ProblemAlertMonitor.ProviderEvidence("fixture", _owner, status));
    }

    [Fact]
    public void ExplicitAuthenticationEvidenceOpensImmediatelyAndTimeoutNeedsThreeSamples()
    {
        var auth = new ProviderHealthObservation(1, ProviderHealthState.Degraded, _now, null, "account-needs-reauthentication", 1, null);
        Assert.Equal("provider-login", ProblemAlertMonitor.ProviderEvidence("fixture", _owner, auth)!.Kind);
        var timeout = auth with { ReasonCode = "timeout" };
        Assert.Equal(3, ProblemAlertMonitor.ProviderEvidence("fixture", _owner, timeout)!.OpenAfter);
    }

    [Fact]
    public void AuthenticationKindRemainsActionableWhenReasonTextIsRedacted()
    {
        var sample = new ProviderHealthObservation(1, ProviderHealthState.Degraded, _now, null, "probe_failed", 1, null)
        { ReauthenticationRequired = true };
        Assert.Equal("provider-login", ProblemAlertMonitor.ProviderEvidence("fixture", _owner, sample)!.Kind);
    }

    [Fact]
    public void ReadinessUsesExistingResultsAndAvoidsDuplicateDiskConditions()
    {
        var failed = new PlatformReadinessSnapshot(false, "not_ready",
            [new("sidecar:download", "unreachable", true, "sidecar_unreachable")], _now);
        Assert.True(ProblemAlertMonitor.ReadinessEvidence(failed, _now)!.Active);
        var lowSpace = failed with { Components = [new("directory:0", "unavailable", true, "required_directory_space_low")] };
        Assert.Null(ProblemAlertMonitor.ReadinessEvidence(lowSpace, _now));
        Assert.False(ProblemAlertMonitor.ReadinessEvidence(failed with { Ready = true, Components = [] }, _now)!.Active);
    }

    [Fact]
    public void SustainedAutomaticPauseOpensWithoutAddingAProbe()
    {
        var sample = new ProviderHealthObservation(1, ProviderHealthState.Degraded, _now.AddMinutes(-11), null,
            "timeout", 1, _now.AddMinutes(1))
        { PausedAt = _now.AddMinutes(-11) };
        var evidence = ProblemAlertMonitor.ProviderEvidence("fixture", _owner, sample, _now)!;
        Assert.Equal(1, evidence.OpenAfter);
        Assert.Equal(_now, evidence.ObservedAt);
    }

    [Fact]
    public void DiskUnknownIsNotHealthyAndRecoveryUsesHysteresis()
    {
        var settings = new ProblemAlertThresholds();
        Assert.Null(ProblemAlertMonitor.DiskEvidence("disk", null, 100L << 30, settings, _now, false));
        Assert.Equal("critical", ProblemAlertMonitor.DiskEvidence("disk", 500L << 20, 100L << 30, settings, _now, false)!.Severity);
        Assert.True(ProblemAlertMonitor.DiskEvidence("disk", 5L << 30, 100L << 30, settings, _now, true)!.Active);
        Assert.False(ProblemAlertMonitor.DiskEvidence("disk", 8L << 30, 100L << 30, settings, _now, true)!.Active);
    }

    [Theory]
    [InlineData("ntfy")]
    [InlineData("discord")]
    [InlineData("webhook")]
    public async Task DeliveryUsesSafePayloadAndStableIdempotencyWithBoundedRetries(string kind)
    {
        await Channel(_owner, kind: kind);
        await _service.ObserveAsync(Evidence(), default);
        var transport = new Transport();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("problem-alerts")).Returns(() => new HttpClient(transport, false));
        var handler = new ProblemAlertDeliveryHandler(_factory, _secrets, clients.Object, _service, _clock.Object);
        using var services = new ServiceCollection().BuildServiceProvider();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var claim = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
            Assert.NotNull(claim);
            var result = await handler.ExecuteAsync(new(claim!, services), default);
            Assert.InRange(result.RetryDelay!.Value.TotalSeconds, 30, 3600);
            Assert.DoesNotContain("topic-private", result.SafeMessage!);
            await _jobs.CompleteAsync(claim!, result);
            _now = _now.AddHours(2);
        }
        Assert.Null(await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]));
        Assert.Single(transport.Keys.Distinct());
        Assert.All(transport.Bodies, body => Assert.DoesNotContain("source-fixture", body));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("failed", (await db.NotificationDeliveries.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledChannelAndRevokedAdministratorAreRecheckedBeforeDelivery(bool disabled)
    {
        var channel = await Channel(_admin, true);
        await _service.ObserveAsync(new("disk-space", "disk", null, true, "critical", _now), default);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            if (disabled) (await db.NotificationChannels.SingleAsync(item => item.Id == channel.Id)).Enabled = false;
            else (await db.Users.SingleAsync(item => item.Id == _admin)).IsAdmin = false;
            await db.SaveChangesAsync();
        }
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var handler = new ProblemAlertDeliveryHandler(_factory, _secrets, clients.Object, _service, _clock.Object);
        var claim = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(new(claim!, services), default)).Kind);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeliveryHistoryReflectsWorkRejectedBeforeTheHandlerRuns()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        var claim = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        await _jobs.CompleteAsync(claim!, DurableJobCompletion.Failure("scope_unavailable", "The saved scope is unavailable."));
        var state = JsonSerializer.SerializeToElement(await _service.ReadAsync(Session(_owner), default), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("failed", state.GetProperty("deliveries")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task OlderOpeningCannotArriveAfterItsRecovery()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        _now = _now.AddSeconds(1);
        await _service.ObserveAsync(Evidence(false), default);
        var transport = new Transport { Status = HttpStatusCode.NoContent };
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("problem-alerts")).Returns(() => new HttpClient(transport, false));
        var handler = new ProblemAlertDeliveryHandler(_factory, _secrets, clients.Object, _service, _clock.Object);
        using var services = new ServiceCollection().BuildServiceProvider();
        var opening = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        await _jobs.CompleteAsync(opening!, await handler.ExecuteAsync(new(opening!, services), default));
        Assert.Empty(transport.Keys);
        var recovery = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        await handler.ExecuteAsync(new(recovery!, services), default);
        Assert.Single(transport.Keys);
        Assert.Contains("resolved", transport.Bodies[0]);
    }

    [Fact]
    public async Task PermanentFailureReplayDoesNotSendAgain()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        var transport = new Transport { Status = HttpStatusCode.BadRequest };
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("problem-alerts")).Returns(() => new HttpClient(transport, false));
        var handler = new ProblemAlertDeliveryHandler(_factory, _secrets, clients.Object, _service, _clock.Object);
        var claim = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(DurableJobCompletionKind.Failed, (await handler.ExecuteAsync(new(claim!, services), default)).Kind);
        await handler.ExecuteAsync(new(claim!, services), default);
        Assert.Single(transport.Keys);
    }

    [Fact]
    public async Task BackoffGrowsAndSuccessfulReplayDoesNotSendAgain()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        var transport = new Transport { RetryAfter = null };
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("problem-alerts")).Returns(() => new HttpClient(transport, false));
        var handler = new ProblemAlertDeliveryHandler(_factory, _secrets, clients.Object, _service, _clock.Object);
        using var services = new ServiceCollection().BuildServiceProvider();
        foreach (var expectedSeconds in new[] { 30, 60, 120 })
        {
            var claim = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
            var result = await handler.ExecuteAsync(new(claim!, services), default);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.RetryDelay);
            await _jobs.CompleteAsync(claim!, result);
            _now = _now.AddHours(1);
        }
        transport.Status = HttpStatusCode.NoContent;
        var successful = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        await handler.ExecuteAsync(new(successful!, services), default);
        var calls = transport.Keys.Count;
        await handler.ExecuteAsync(new(successful!, services), default);
        Assert.Equal(calls, transport.Keys.Count);
    }

    [Fact]
    public async Task TransportErrorsCannotExposeSecretUrlsInDeliveryHistoryOrApi()
    {
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        var transport = new Transport { ThrowSensitiveError = true };
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("problem-alerts")).Returns(() => new HttpClient(transport, false));
        var handler = new ProblemAlertDeliveryHandler(_factory, _secrets, clients.Object, _service, _clock.Object);
        var claim = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var result = await handler.ExecuteAsync(new(claim!, services), default);
        await _jobs.CompleteAsync(claim!, result);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.DoesNotContain("private-topic", JsonSerializer.Serialize(await db.AuditEvents.ToArrayAsync()));
        Assert.DoesNotContain("private-topic", JsonSerializer.Serialize(await _service.ReadAsync(Session(_owner), default)));
    }

    [Fact]
    public async Task RevokedDestinationFailsLocallyWithoutRepeatedNetworkAttempts()
    {
        var channel = await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.NotificationChannels.SingleAsync(item => item.Id == channel.Id);
            (await db.SecretReferences.SingleAsync(item => item.Id == row.DestinationReferenceId)).RevokedAt = _now;
            await db.SaveChangesAsync();
        }
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var handler = new ProblemAlertDeliveryHandler(_factory, _secrets, clients.Object, _service, _clock.Object);
        var claim = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var result = await handler.ExecuteAsync(new(claim!, services), default);
        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DuplicateWebhookDestinationsShareOneLogicalDelivery()
    {
        await _service.SaveChannelAsync(Session(_admin), null, new("First", "webhook", true, true,
            "http://notification-fixture/notify"), default);
        await _service.SaveChannelAsync(Session(_admin), null, new("Second", "webhook", true, true,
            "http://notification-fixture/notify"), default);
        await _service.ObserveAsync(new("health-check", "health", null, true, "warning", _now), default);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.NotificationDeliveries.ToArrayAsync());
    }

    [Fact]
    public async Task ListenerDestinationsRequireCurrentAdministratorPermission()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveChannelAsync(Session(_owner), null,
            new("Push", "ntfy", true, false, "http://unapproved-fixture/private"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SaveNetworkAsync(Session(_owner), ["unapproved-fixture"], 1, default));
        await Channel(_owner);
        await _service.ObserveAsync(Evidence(), default);
        await _service.SaveNetworkAsync(Session(_admin), [], 1, default);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var handler = new ProblemAlertDeliveryHandler(_factory, _secrets, clients.Object, _service, _clock.Object);
        var claim = await _jobs.ClaimNextAsync("delivery", [ProblemAlertService.DeliveryJobType]);
        using var services = new ServiceCollection().BuildServiceProvider();
        await handler.ExecuteAsync(new(claim!, services), default);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SettingsApiRedactsDestinationsAndRejectsForeignSessions()
    {
        var controller = new ProblemAlertsController(_service) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(_owner);
        var result = await controller.Create(new("Push", "discord", true, false, "http://notification-fixture/private-token"), default);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(Assert.IsType<ObjectResult>(result).Value));
        var invalid = await controller.Create(new("Push", "ntfy", true, false, "file:///private-token"), default);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(invalid).Value));
        var session = Session(_owner);
        await using var db = await _factory.CreateDbContextAsync();
        (await db.Users.SingleAsync(item => item.Id == _owner)).BackendInstanceId = "different";
        await db.SaveChangesAsync();
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Read(default)).StatusCode);
    }

    [Fact]
    public async Task MonitorReadsExistingEvidenceWithoutProviderRequestsAndGroupsDiskRoots()
    {
        await Channel(_owner);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.Add(new()
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ProviderId = "deezer",
                OwnerUserId = _owner,
                Enabled = true,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var health = new ProviderRuntimeHealth(new() { CircuitOpenSeconds = 3600 }, _clock.Object);
        health.Record(ProviderRuntimeStatusKey.CreateManaged("deezer", "streaming", Guid.Parse("11111111-1111-1111-1111-111111111111")),
            1, false, null, "account-needs-reauthentication", authenticationFailure: true);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var statuses = new ProviderStatusManager(new ConfigurationManager(), clients.Object, NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()), health: health);
        var disks = new Mock<IProblemDiskSampler>();
        disks.Setup(item => item.Sample()).Returns(ProblemDiskSampler.Group([
            new("volume", ["database"], 100L << 20, 100L << 30), new("volume", ["cache"], 100L << 20, 100L << 30)]));
        var monitor = new ProblemAlertMonitor(_factory, _service, health, statuses, disks.Object, _clock.Object, NullLogger<ProblemAlertMonitor>.Instance);
        await monitor.SampleAsync(default);
        await monitor.SampleAsync(default);
        clients.VerifyNoOtherCalls();
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.ProblemIncidents.CountAsync(item => item.State == "open"));
        var disk = await verify.ProblemIncidents.SingleAsync(item => item.Kind == "disk-space");
        Assert.Contains("database", disk.DetailJson);
        Assert.Contains("cache", disk.DetailJson);
        _now = _now.AddSeconds(1);
        health.Record(ProviderRuntimeStatusKey.CreateManaged("deezer", "metadata", Guid.Parse("11111111-1111-1111-1111-111111111111")),
            1, true, 10, null);
        await monitor.SampleAsync(default);
        Assert.Equal("open", (await verify.ProblemIncidents.AsNoTracking().SingleAsync(item => item.Kind == "provider-login")).State);
        var key = ProviderRuntimeStatusKey.CreateManaged("deezer", "streaming", Guid.Parse("11111111-1111-1111-1111-111111111111"));
        for (var count = 0; count < 3; count++)
        {
            _now = _now.AddSeconds(1);
            health.Record(key, 1, false, null, "timeout");
        }
        await monitor.SampleAsync(default);
        Assert.Equal("open", (await verify.ProblemIncidents.AsNoTracking().SingleAsync(item => item.Kind == "source-failure")).State);
        _now = _now.AddMinutes(11);
        for (var count = 0; count < 2; count++)
        {
            _now = _now.AddSeconds(1);
            health.Record(key, 1, true, 10, null);
        }
        await monitor.SampleAsync(default);
        Assert.Equal("resolved", (await verify.ProblemIncidents.AsNoTracking().SingleAsync(item => item.Kind == "source-failure")).State);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TestSendIsExplicitRateLimitedAndReportsSafeDeliveryState()
    {
        var channel = await Channel(_owner);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.NotificationDeliveries.ToArrayAsync());
        var id = await _service.TestAsync(Session(_owner), channel.Id, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.TestAsync(Session(_owner), channel.Id, default));
        Assert.Equal("test", (await db.NotificationDeliveries.SingleAsync(item => item.Id == id)).Event);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.TestAsync(Session(_other), channel.Id, default));
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Transport : HttpMessageHandler
    {
        public bool ThrowSensitiveError { get; init; }
        public TimeSpan? RetryAfter { get; init; } = TimeSpan.FromDays(7);
        public HttpStatusCode Status { get; set; } = HttpStatusCode.TooManyRequests;
        public List<string> Keys { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (ThrowSensitiveError) throw new HttpRequestException("http://notification-fixture/private-topic?key=fixture-private");
            Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            var response = new HttpResponseMessage(Status);
            if (RetryAfter is { } after) response.Headers.RetryAfter = new(after);
            return response;
        }
    }
}
