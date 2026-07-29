using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class DurableScheduleEngineTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _account = Guid.CreateVersion7();
    private readonly Guid _schedule = Guid.CreateVersion7();
    private readonly Guid _link = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private FakeClock _clock = null!;
    private DurableScheduleEngine _engine = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestFactory(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        db.Tenants.Add(new TenantRecord { Id = _tenant, Slug = "scheduler", Name = "Scheduler", CreatedAt = now });
        db.Users.Add(new PlatformUserRecord { Id = _user, TenantId = _tenant, DisplayName = "Owner", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now });
        db.BackendIdentities.Add(new BackendIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenant,
            UserId = _user,
            BackendType = "subsonic",
            BackendInstanceId = "navidrome-main",
            PrincipalId = "scheduled-owner",
            CreatedAt = now,
            LastSeenAt = now
        });
        db.ProviderAccounts.Add(new ProviderAccountRecord { Id = _account, TenantId = _tenant, OwnerUserId = _user, ProviderId = "spotify", DisplayName = "Source", Scope = ProviderAccountScope.User, Enabled = true, CreatedAt = now, UpdatedAt = now });
        db.JobSchedules.Add(NewSchedule(now));
        db.PlaylistLinks.Add(new PlaylistLinkRecord
        {
            Id = _link,
            TenantId = _tenant,
            OwnerUserId = _user,
            ProviderAccountId = _account,
            ScheduleId = _schedule,
            LibraryScopeId = "music",
            SourceProviderId = "spotify",
            SourcePlaylistId = "stable-playlist-id",
            SourcePlaylistIdHash = new string('a', 64),
            TargetProtocol = "subsonic",
            TargetBackendInstanceId = "navidrome-main",
            Mode = PlaylistLinkMode.Materialized,
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            RuleVersion = "rules-v1",
            PolicyVersion = "policy-v1",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        _clock = new FakeClock(now);
        var jobOptions = new DurableJobOptions();
        var queue = new DurableJobQueue(_factory, jobOptions, new JobPayloadPolicy(jobOptions), _clock);
        _engine = new DurableScheduleEngine(_factory, queue, _clock);
    }

    [Fact]
    public void NextOccurrence_IsTimezoneAwareAcrossSpringDstGap()
    {
        var next = DurableScheduleEngine.GetNextOccurrence(
            "30 2 * * *", "America/New_York", new DateTimeOffset(2026, 3, 7, 8, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("not cron", "UTC")]
    [InlineData("0 * * * *", "Mars/Olympus_Mons")]
    public void Validation_RejectsInvalidCronOrTimezone(string cron, string zone) =>
        Assert.Throws<ArgumentException>(() => DurableScheduleEngine.Validate(cron, zone));

    [Fact]
    public async Task DuplicateTicksAndRestart_EnqueueExactlyOnceAndAdvanceAtomically()
    {
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        var first = await _engine.TickAsync();
        var restarted = NewEngine();
        var duplicate = await restarted.TickAsync();

        Assert.Equal(1, first.Enqueued);
        Assert.Equal(0, duplicate.Enqueued);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.ToListAsync());
        Assert.True((await db.JobSchedules.SingleAsync()).NextRunAt > _clock.UtcNow);
    }

    [Fact]
    public async Task ConcurrentTicks_CreateOneJob()
    {
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Task.WhenAll(_engine.TickAsync(), NewEngine().TickAsync());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.ToListAsync());
        Assert.Single(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DisabledSchedule_IsIgnored()
    {
        await SetSchedule(item => item.Enabled = false);
        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        Assert.Equal(0, (await _engine.TickAsync()).Claimed);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task MisfirePolicies_SkipOrRunOnlyOnce()
    {
        _clock.UtcNow = _clock.UtcNow.AddHours(4);
        Assert.Equal(1, (await _engine.TickAsync()).SkippedMisfire);
        await using (var db = await _factory.CreateDbContextAsync()) Assert.Empty(await db.Jobs.ToListAsync());

        await SetSchedule(item => { item.NextRunAt = _clock.UtcNow.AddHours(-3); item.MisfirePolicy = ScheduleMisfirePolicy.RunOnce; });
        Assert.Equal(1, (await _engine.TickAsync()).Enqueued);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Single(await verify.Jobs.ToListAsync());
    }

    [Fact]
    public async Task SkipOverlap_DoesNotQueueBehindActiveScheduledJob()
    {
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await _engine.TickAsync();
        await SetSchedule(item => item.NextRunAt = _clock.UtcNow);
        var result = await _engine.TickAsync();
        Assert.Equal(1, result.SkippedOverlap);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task QueueOverlap_EnqueuesDistinctOccurrence()
    {
        await SetSchedule(item => item.OverlapPolicy = ScheduleOverlapPolicy.Queue);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await _engine.TickAsync();
        await SetSchedule(item => item.NextRunAt = _clock.UtcNow.AddMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await _engine.TickAsync();
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task PayloadContainsStableIdsAndPolicyReferencesWithoutSecrets()
    {
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await _engine.TickAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var job = await db.Jobs.SingleAsync();
        var payload = JsonDocument.Parse(job.PayloadJson).RootElement;
        Assert.Equal(_schedule, payload.GetProperty("ScheduleId").GetGuid());
        Assert.Equal(_link, payload.GetProperty("PlaylistLinkId").GetGuid());
        Assert.Contains("job-schedule:", payload.GetProperty("RetryPolicyReference").GetString());
        Assert.Contains("durable-job:schedule:", payload.GetProperty("CancellationPolicyReference").GetString());
        Assert.DoesNotContain("token", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_tenant, job.TenantId);
        Assert.Equal(_user, job.OwnerUserId);
        Assert.Equal(_account, job.ProviderAccountId);
        Assert.Equal("music", job.LibraryScopeId);
    }

    [Fact]
    public async Task ScheduledPayload_IsAcceptedByMaterializationHandlerAndUsesScheduledOccurrenceAsGeneration()
    {
        var scheduledFor = _clock.UtcNow.AddMinutes(1);
        _clock.UtcNow = scheduledFor;
        await _engine.TickAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var job = await db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal("playlist.materialize", job.Type);
        var payload = JsonDocument.Parse(job.PayloadJson).RootElement.Clone();
        var orchestration = new CapturingOrchestration();
        var handler = new PlaylistMaterializationJobHandler(_factory, orchestration, _clock);
        var claim = new DurableJobClaim(job.Id, Guid.CreateVersion7(), 1, job.Type, payload,
            job.TenantId, job.OwnerUserId, job.ProviderAccountId, job.LibraryScopeId,
            job.ProviderCapability, JsonDocument.Parse(job.PolicySnapshotJson).RootElement.Clone(),
            job.CorrelationId, "test-worker", _clock.UtcNow.AddMinutes(1));
        var progress = new List<DurableJobProgressUpdate>();

        var result = await handler.ExecuteAsync(new DurableJobExecutionContext(claim,
            new EmptyServiceProvider())
        {
            ReportProgressAsync = (update, _) =>
            {
                progress.Add(update);
                return Task.FromResult(true);
            }
        }, CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.NotNull(orchestration.Request);
        Assert.Equal(_link, orchestration.Request!.PlaylistLinkId);
        Assert.Equal(scheduledFor.UtcTicks, orchestration.Request.Generation);
        Assert.Equal(job.Id, orchestration.Request.JobId);
        Assert.Equal(_schedule, orchestration.Request.ScheduleId);
        Assert.Equal(["playlist.prepare", "playlist.match", "playlist.complete"],
            progress.Select(item => item.Stage));
        Assert.All(progress, item => Assert.NotNull(item.Playlist));
        Assert.Equal("spotify", progress[0].Provider);
        Assert.Equal("subsonic", progress[^1].Provider);
        Assert.Null(progress[^1].Completed);
        Assert.Null(progress[^1].Total);
    }

    [Fact]
    public async Task RecommendationOccurrence_CreatesScopedRunJobAndOutboxAtomically()
    {
        var policyId = await ConfigureRecommendationSchedule();
        var scheduledFor = _clock.UtcNow.AddMinutes(1);
        _clock.UtcNow = scheduledFor;

        var result = await _engine.TickAsync();

        Assert.Equal(1, result.Enqueued);
        await using var db = await _factory.CreateDbContextAsync();
        var job = await db.Jobs.SingleAsync();
        var run = await db.RecommendationRuns.SingleAsync();
        Assert.Equal(DurableScheduleEngine.RecommendationJobType, job.Type);
        Assert.Equal(_schedule, run.ScheduleId);
        Assert.Equal(scheduledFor, run.ScheduledFor);
        Assert.Equal(job.Id, run.JobId);
        Assert.Equal("[]", run.SeedTrackKeysJson);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        var snapshot = JsonSerializer.Deserialize<RecommendationPolicySnapshot>(run.PolicySnapshotJson)!;
        Assert.Equal(policyId, JsonSerializer.Deserialize<RecommendationScheduleTemplate>(
            (await db.JobSchedules.SingleAsync()).PayloadTemplateJson)!.IntelligencePolicyId);
        Assert.Equal(_schedule, snapshot.Automation!.ScheduleId);
        Assert.Equal(scheduledFor, snapshot.Automation.ScheduledFor);
        Assert.Equal("Daily discovery", snapshot.Automation.GeneratedSetName);
        Assert.DoesNotContain("credential", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecommendationSkipOverlap_AlsoSeesActiveMaterializationChild()
    {
        await ConfigureRecommendationSchedule();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Jobs.Add(new DurableJobRecord
            {
                Id = Guid.CreateVersion7(),
                ScopeKey = $"{_tenant:N}:{_user:N}",
                TenantId = _tenant,
                OwnerUserId = _user,
                Type = "smart-playlist.materialize",
                PayloadJson = "{}",
                PolicySnapshotJson = "{}",
                RequestFingerprint = new string('f', 64),
                IdempotencyKey = $"schedule:{_schedule:N}:materialize:{Guid.CreateVersion7():N}",
                CorrelationId = "active-materialization",
                State = DurableJobState.Running,
                MaxAttempts = 3,
                AvailableAt = _clock.UtcNow,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);

        var result = await _engine.TickAsync();

        Assert.Equal(1, result.SkippedOverlap);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Empty(await verify.RecommendationRuns.ToListAsync());
    }

    private JobScheduleRecord NewSchedule(DateTimeOffset now) => new()
    {
        Id = _schedule,
        TenantId = _tenant,
        OwnerUserId = _user,
        LibraryScopeId = "music",
        JobType = DurableScheduleEngine.PlaylistSyncJobType,
        CronExpression = "* * * * *",
        TimeZoneId = "UTC",
        OverlapPolicy = ScheduleOverlapPolicy.Skip,
        MisfirePolicy = ScheduleMisfirePolicy.Skip,
        RetryPolicyJson = "{\"policy\":\"standard\"}",
        NextRunAt = now.AddMinutes(1),
        Enabled = true,
        CreatedAt = now,
        UpdatedAt = now
    };

    private DurableScheduleEngine NewEngine()
    {
        var options = new DurableJobOptions();
        return new DurableScheduleEngine(_factory, new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock), _clock);
    }

    private async Task<Guid> ConfigureRecommendationSchedule()
    {
        var policyId = Guid.CreateVersion7();
        await using var db = await _factory.CreateDbContextAsync();
        db.BackendIdentities.Add(new BackendIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenant,
            UserId = _user,
            BackendType = "jellyfin",
            BackendInstanceId = "jellyfin-main",
            PrincipalId = "scheduled-owner",
            CreatedAt = _clock.UtcNow,
            LastSeenAt = _clock.UtcNow
        });
        db.IntelligencePolicies.Add(new IntelligencePolicyRecord
        {
            Id = policyId,
            TenantId = _tenant,
            OwnerUserId = _user,
            Protocol = "jellyfin",
            BackendInstanceId = "jellyfin-main",
            LibraryScopeId = "music",
            Enabled = true,
            RetentionDays = 30,
            AllowedSignalTypesJson = "[\"play\"]",
            EnabledProvidersJson = "[\"fixture\"]",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
            Revision = 1
        });
        var schedule = await db.JobSchedules.SingleAsync();
        schedule.JobType = DurableScheduleEngine.RecommendationJobType;
        schedule.PayloadTemplateJson = JsonSerializer.Serialize(
            new RecommendationScheduleTemplate(1, policyId, 25, "Daily discovery"));
        db.PlaylistLinks.Remove(await db.PlaylistLinks.SingleAsync());
        await db.SaveChangesAsync();
        return policyId;
    }

    private async Task SetSchedule(Action<JobScheduleRecord> mutate)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var schedule = await db.JobSchedules.SingleAsync();
        mutate(schedule); schedule.Revision++; schedule.UpdatedAt = _clock.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();
    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = now; }
    private sealed class EmptyServiceProvider : IServiceProvider { public object? GetService(Type serviceType) => null; }
    private sealed class CapturingOrchestration : IPlaylistOrchestrationService
    {
        public PlaylistOrchestrationRequest? Request { get; private set; }
        public Task<PlaylistOrchestrationResult> RunAsync(ProtocolExecutionContext execution,
            PlaylistOrchestrationRequest request, CancellationToken cancellationToken = default)
        { Request = request; return Task.FromResult(new PlaylistOrchestrationResult(null!, null, PlaylistSyncState.Succeeded, false, false)); }
        public Task<PlaylistRefreshResult> RefreshAsync(ProtocolExecutionContext execution, Guid playlistLinkId,
            Guid? jobId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
