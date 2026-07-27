using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class IntelligenceCoreTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.CreateVersion7(); private readonly Guid _user = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private Factory _factory = null!; private Clock _clock = null!; private IntelligenceScope _scope = null!;
    private IntelligencePolicyService _policies = null!; private DurableJobQueue _jobs = null!;
    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        _clock = new(new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)); _scope = new(_tenant, _user, "jellyfin", "main", "music");
        await using var db = await _factory.CreateDbContextAsync(); await db.Database.MigrateAsync();
        db.Tenants.Add(new() { Id = _tenant, Slug = "intel", Name = "Intelligence", CreatedAt = _clock.UtcNow });
        db.Users.Add(new() { Id = _user, TenantId = _tenant, DisplayName = "Listener", Status = PlatformUserStatus.Active, CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
        var identityId = Guid.CreateVersion7();
        db.BackendIdentities.Add(new() { Id = identityId, TenantId = _tenant, UserId = _user, BackendType = "jellyfin", BackendInstanceId = "main", PrincipalId = "listener", CreatedAt = _clock.UtcNow, LastSeenAt = _clock.UtcNow });
        db.CanonicalRecordings.AddRange(Canonical(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            Canonical(Guid.Parse("22222222-2222-2222-2222-222222222222")));
        db.LibraryTracks.AddRange(Track(Guid.Parse("11111111-1111-1111-1111-111111111111"), identityId, "track-secret"),
            Track(Guid.Parse("22222222-2222-2222-2222-222222222222"), identityId, "track-two"));
        await db.SaveChangesAsync(); _policies = new(_factory, _clock);
        var options = new DurableJobOptions { LeaseSeconds = 30, PollIntervalMilliseconds = 10 };
        _jobs = new(_factory, options, new JobPayloadPolicy(options), _clock);
    }

    [Fact]
    public async Task SignalsRequireExactOptInAndRetentionPrunesExpiredData()
    {
        var writer = new RecommendationSignalWriter(_factory, _clock);
        Assert.False(await writer.WriteAsync(_scope, "play", "track-secret", 1, _clock.UtcNow));
        await _policies.SetAsync(_scope, new(true, 2, ["play", "skip"], ["local"]));
        Assert.True(await writer.WriteAsync(_scope, "play", "track-secret", 1, _clock.UtcNow));
        Assert.False(await writer.WriteAsync(_scope with { LibraryScopeId = "other" }, "play", "track-secret", 1, _clock.UtcNow));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var signal = Assert.Single(await db.ListeningSignals.ToListAsync());
            Assert.DoesNotContain("track-secret", signal.TrackKeyHash, StringComparison.Ordinal);
        }
        _clock.UtcNow = _clock.UtcNow.AddDays(3);
        var profile = await new ListeningProfileService(_factory, _clock).BuildAsync(_scope);
        Assert.Equal(0, profile.PlayCount);
        await using var verified = await _factory.CreateDbContextAsync(); Assert.Empty(await verified.ListeningSignals.ToListAsync());
    }

    [Fact]
    public async Task DurableRunIsIdempotentRestartSafeAndPersistsExplanations()
    {
        await _policies.SetAsync(_scope, new(true, 30, ["play"], ["fixture"]));
        var runs = new RecommendationRunService(_factory, _jobs, _clock);
        var first = await runs.EnqueueAsync(_scope, ["seed"], 10, "same-run");
        var repeated = await runs.EnqueueAsync(_scope, ["seed"], 10, "same-run");
        Assert.True(first.Created); Assert.False(repeated.Created); Assert.Equal(first.RunId, repeated.RunId);
        await _policies.SetAsync(_scope, new(true, 1, ["play"], ["different-provider"]));
        var claim = await _jobs.ClaimNextAsync("intelligence-restart", ["recommendation.generate"]); Assert.NotNull(claim);
        var handler = new RecommendationRunJobHandler(_factory, [new FixtureProvider()],
            new ListeningProfileService(_factory, _clock), _clock);
        var completion = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(claim!, completion); Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await using var db = await _factory.CreateDbContextAsync(); var candidate = Assert.Single(await db.RecommendationCandidates.ToListAsync());
        Assert.Equal("track-1", candidate.TrackKey); Assert.Contains("shared-genre", candidate.SignalsJson, StringComparison.Ordinal);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), candidate.CanonicalRecordingId);
        Assert.Equal("fixture:1", candidate.SourceRevision);
        Assert.Single(await db.RecommendationRuns.ToListAsync());
        db.RecommendationFeedback.Add(new()
        {
            Id = Guid.CreateVersion7(), CandidateId = candidate.Id, TenantId = _tenant, OwnerUserId = _user,
            Protocol = "jellyfin", BackendInstanceId = "main", LibraryScopeId = "music",
            TrackKey = candidate.TrackKey, Kind = "dislike", CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow, Revision = 1
        });
        await db.SaveChangesAsync();
        await db.DisposeAsync();
        await _policies.SetAsync(_scope, new(true, 30, ["play"], ["fixture"]));
        var next = await runs.EnqueueAsync(_scope, ["seed"], 10, "feedback-run");
        var nextClaim = await _jobs.ClaimNextAsync("intelligence-feedback", ["recommendation.generate"]);
        var nextResult = await handler.ExecuteAsync(new(nextClaim!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(nextClaim!, nextResult);
        await using (var feedbackDb = await _factory.CreateDbContextAsync())
        {
            var excluded = await feedbackDb.RecommendationCandidates.SingleAsync(item => item.RunId == next.RunId);
            Assert.Contains("user-feedback", excluded.ExclusionsJson, StringComparison.Ordinal);
        }
        var smart = new SmartPlaylistService(_factory, _clock, _jobs);
        var setId = await smart.CreateGeneratedSetAsync(_scope, first.RunId, "Daily mix",
            [new("track-1", .9, "fixture", [new("genre", .8, "shared-genre")],
                new(LibraryTrackId: Guid.Parse("11111111-1111-1111-1111-111111111111"), BackendItemId: "track-secret"))]);
        var materialization = await _jobs.ClaimNextAsync("smart-playlist", ["smart-playlist.materialize"]);
        Assert.NotNull(materialization); var target = new RecordingMaterializer();
        var materialized = await new GeneratedSetMaterializationJobHandler(_factory, [target])
            .ExecuteAsync(new(materialization!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, materialized.Kind);
        Assert.Equal(setId, target.Request!.GeneratedSetId); Assert.Equal(["track-1"], target.Request.OrderedCandidates.Select(x => x.TrackKey));
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            target.Request.OrderedCandidates.Single().Identity!.LibraryTrackId);
        Assert.Equal("track-secret", target.Request.OrderedCandidates.Single().Identity!.BackendItemId);
        await using var verified = await _factory.CreateDbContextAsync(); var savedSet = await verified.GeneratedSets.SingleAsync(x => x.Id == setId);
        Assert.Equal(GeneratedSetMaterializationState.Succeeded, savedSet.MaterializationState);
        Assert.Equal("backend-playlist-1", savedSet.BackendPlaylistId);
        Assert.Equal("revision-1", savedSet.TargetRevision);
    }

    [Fact]
    public async Task ScheduledRunAutomaticallyCreatesOneRepairableGeneratedSetJob()
    {
        var policy = await _policies.SetAsync(_scope, new(true, 30, ["play"], ["fixture"]));
        var scheduleId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.JobSchedules.Add(new()
            {
                Id = scheduleId,
                TenantId = _tenant,
                OwnerUserId = _user,
                LibraryScopeId = "music",
                JobType = DurableScheduleEngine.RecommendationJobType,
                CronExpression = "* * * * *",
                TimeZoneId = "UTC",
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                RetryPolicyJson = "{}",
                PayloadTemplateJson = JsonSerializer.Serialize(
                    new RecommendationScheduleTemplate(1, policy.Id, 25, "Daily discovery")),
                NextRunAt = _clock.UtcNow.AddMinutes(1),
                Enabled = true,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        Assert.Equal(1, (await new DurableScheduleEngine(_factory, _jobs, _clock).TickAsync()).Enqueued);
        var claim = await _jobs.ClaimNextAsync("scheduled-recommendation", ["recommendation.generate"]);
        Assert.NotNull(claim);
        var smart = new SmartPlaylistService(_factory, _clock, _jobs);
        var handler = new RecommendationRunJobHandler(_factory, [new FixtureProvider()],
            new ListeningProfileService(_factory, _clock), _clock, smart);

        var completion = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(claim!, completion);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await using var verify = await _factory.CreateDbContextAsync();
        var run = await verify.RecommendationRuns.SingleAsync();
        var set = await verify.GeneratedSets.SingleAsync();
        Assert.Equal(scheduleId, run.ScheduleId);
        Assert.Equal(scheduleId, set.ScheduleId);
        Assert.Equal("Daily discovery", set.Name);
        var child = await verify.Jobs.SingleAsync(item => item.Type == "smart-playlist.materialize");
        Assert.StartsWith($"schedule:{scheduleId:N}:materialize:", child.IdempotencyKey, StringComparison.Ordinal);

        var replay = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, replay.Kind);
        Assert.Equal(1, await verify.GeneratedSets.CountAsync());
        Assert.Equal(1, await verify.Jobs.CountAsync(item => item.Type == "smart-playlist.materialize"));
    }

    [Fact]
    public async Task DisablingPolicyAlsoDisablesItsExactScopeSchedules()
    {
        var policy = await _policies.SetAsync(_scope, new(true, 30, ["play"], ["fixture"]));
        var otherPolicy = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.JobSchedules.AddRange(Schedule(policy.Id, "music"), Schedule(otherPolicy, "music"),
                Schedule(policy.Id, "other"));
            await db.SaveChangesAsync();
        }

        await _policies.SetAsync(_scope, new(false, 30, ["play"], ["fixture"]));

        await using var verify = await _factory.CreateDbContextAsync();
        var schedules = await verify.JobSchedules.OrderBy(item => item.LibraryScopeId)
            .ThenBy(item => item.PayloadTemplateJson).ToListAsync();
        Assert.False(schedules.Single(item => item.LibraryScopeId == "music" &&
            item.PayloadTemplateJson.Contains(policy.Id.ToString(), StringComparison.OrdinalIgnoreCase)).Enabled);
        Assert.Null(schedules.Single(item => item.LibraryScopeId == "music" &&
            item.PayloadTemplateJson.Contains(policy.Id.ToString(), StringComparison.OrdinalIgnoreCase)).NextRunAt);
        Assert.True(schedules.Single(item => item.PayloadTemplateJson.Contains(otherPolicy.ToString(),
            StringComparison.OrdinalIgnoreCase)).Enabled);
        Assert.True(schedules.Single(item => item.LibraryScopeId == "other").Enabled);
    }

    [Fact]
    public async Task DisableAndPurgeRemovesOnlyExactScopeIntelligenceData()
    {
        var policy = await _policies.SetAsync(_scope, new(true, 30, ["play"], ["fixture"]));
        await new RecommendationSignalWriter(_factory, _clock).WriteAsync(_scope, "play", "track", 1, _clock.UtcNow);
        await new ListeningProfileService(_factory, _clock).BuildAsync(_scope);
        var pending = await new RecommendationRunService(_factory, _jobs, _clock).EnqueueAsync(_scope, [], 10, "purged-run");
        var schedule = Schedule(policy.Id, "music");
        var childJobId = Guid.CreateVersion7();
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            setup.JobSchedules.Add(schedule);
            setup.Jobs.Add(new()
            {
                Id = childJobId,
                ScopeKey = $"{_tenant:N}:{_user:N}",
                TenantId = _tenant,
                OwnerUserId = _user,
                LibraryScopeId = "music",
                Type = "smart-playlist.materialize",
                PayloadJson = "{}",
                PolicySnapshotJson = "{}",
                RequestFingerprint = new string('d', 64),
                IdempotencyKey = $"schedule:{schedule.Id:N}:materialize:{Guid.CreateVersion7():N}",
                CorrelationId = "purge-child",
                State = DurableJobState.Pending,
                MaxAttempts = 3,
                AvailableAt = _clock.UtcNow,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await setup.SaveChangesAsync();
        }
        await _policies.DisableAndPurgeAsync(_scope);
        await using var db = await _factory.CreateDbContextAsync(); var storedPolicy = Assert.Single(await db.IntelligencePolicies.ToListAsync());
        Assert.False(storedPolicy.Enabled); Assert.Empty(await db.ListeningSignals.ToListAsync()); Assert.Empty(await db.ListeningProfiles.ToListAsync());
        Assert.Empty(await db.RecommendationRuns.ToListAsync());
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(x => x.Id == pending.JobId)).State);
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(x => x.Id == childJobId)).State);
        Assert.False((await db.JobSchedules.SingleAsync()).Enabled);
        Assert.Null(await _jobs.ClaimNextAsync("purge-restart", ["recommendation.generate"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecommendationRunService(_factory, _jobs, _clock).EnqueueAsync(_scope, [], 10, "disabled"));
    }

    private JobScheduleRecord Schedule(Guid policyId, string libraryScopeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenant,
        OwnerUserId = _user,
        LibraryScopeId = libraryScopeId,
        JobType = DurableScheduleEngine.RecommendationJobType,
        CronExpression = "* * * * *",
        TimeZoneId = "UTC",
        OverlapPolicy = ScheduleOverlapPolicy.Skip,
        MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
        RetryPolicyJson = "{}",
        PayloadTemplateJson = JsonSerializer.Serialize(new RecommendationScheduleTemplate(
            1, policyId, 25, "Daily discovery")),
        Enabled = true,
        NextRunAt = _clock.UtcNow.AddMinutes(1),
        CreatedAt = _clock.UtcNow,
        UpdatedAt = _clock.UtcNow
    };

    [Fact]
    public async Task ProviderCancellationIsTerminalCancellationNotRetry()
    {
        await _policies.SetAsync(_scope, new(true, 30, ["play"], ["cancel"]));
        await new RecommendationRunService(_factory, _jobs, _clock).EnqueueAsync(_scope, [], 10, "cancel-run");
        var claim = await _jobs.ClaimNextAsync("cancel-worker", ["recommendation.generate"]); Assert.NotNull(claim);
        var result = await new RecommendationRunJobHandler(_factory, [new CancellingProvider()],
            new ListeningProfileService(_factory, _clock), _clock).ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Cancelled, result.Kind);
        await using var db = await _factory.CreateDbContextAsync();
        var run = await db.RecommendationRuns.SingleAsync();
        Assert.Equal(RecommendationRunState.Cancelled, run.State);
        Assert.Equal("recommendation_cancelled", run.ErrorCode);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public async Task EmptySeedRunUsesWeightedExactScopeListeningHabits()
    {
        await _policies.SetAsync(_scope, new(true, 30, ["favorite", "skip"], ["capture"]));
        var writer = new RecommendationSignalWriter(_factory, _clock);
        await writer.WriteAsync(_scope, "favorite", "track-secret", 1, _clock.UtcNow);
        await writer.WriteAsync(_scope, "skip", "track-two", 1, _clock.UtcNow);
        await new RecommendationRunService(_factory, _jobs, _clock).EnqueueAsync(_scope, [], 10, "habit-run");
        var claim = await _jobs.ClaimNextAsync("habit-worker", ["recommendation.generate"]); var provider = new CapturingProvider();
        var result = await new RecommendationRunJobHandler(_factory, [provider], new ListeningProfileService(_factory, _clock), _clock)
            .ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(["library:11111111111111111111111111111111"], provider.Seeds);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();
    private sealed class FixtureProvider : IRecommendationProvider
    {
        public string Id => "fixture";
        public Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request)
        {
            Assert.True(request.ExplicitlyOptedIn);
            return Task.FromResult(new RecommendationProviderResult(RecommendationProviderState.Succeeded,
                [new RecommendationCandidate("track-1", .9, Id, [new("genre", .8, "shared-genre")],
                    new(LibraryTrackId: Guid.Parse("11111111-1111-1111-1111-111111111111")))
                    { SourceRevision = "fixture:1" }]));
        }
    }
    private sealed class RecordingMaterializer : IGeneratedSetMaterializer
    {
        public string Protocol => "jellyfin"; public GeneratedSetMaterializationRequest? Request { get; private set; }
        public Task<GeneratedSetMaterializationResult> MaterializeAsync(GeneratedSetMaterializationRequest request, CancellationToken cancellationToken)
        { Request = request; return Task.FromResult(new GeneratedSetMaterializationResult(true, BackendPlaylistId: "backend-playlist-1", TargetRevision: "revision-1")); }
    }
    private sealed class CancellingProvider : IRecommendationProvider
    {
        public string Id => "cancel";
        public Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request) =>
            throw new OperationCanceledException();
    }
    private sealed class CapturingProvider : IRecommendationProvider
    {
        public string Id => "capture"; public IReadOnlyList<string> Seeds { get; private set; } = [];
        public Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request)
        { Seeds = request.SeedTrackKeys; return Task.FromResult(new RecommendationProviderResult(RecommendationProviderState.Succeeded, [])); }
    }
    private sealed class Clock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = now; }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    { public AllstarrDbContext CreateDbContext() => new(options); public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext()); }
    private sealed class EmptyServices : IServiceProvider { public static readonly EmptyServices Instance = new(); public object? GetService(Type serviceType) => null; }
    private LibraryTrackRecord Track(Guid id, Guid identityId, string backendId) => new()
    {
        Id = id,
        TenantId = _tenant,
        OwnerUserId = _user,
        BackendIdentityId = identityId,
        CanonicalRecordingId = id,
        LibraryScopeId = "music",
        Protocol = "jellyfin",
        BackendInstanceId = "main",
        BackendItemId = backendId,
        FilePath = $"/library/{backendId}.flac",
        Title = backendId,
        Artist = "Fixture",
        DurationMilliseconds = 180000,
        ProviderIdsJson = "{}",
        IndexedAt = _clock.UtcNow,
        SourceModifiedAt = _clock.UtcNow,
        UpdatedAt = _clock.UtcNow
    };

    private CanonicalRecordingRecord Canonical(Guid id) => new()
    {
        Id = id, TenantId = _tenant, CreatedByUserId = _user,
        CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow, Revision = 1
    };
}
