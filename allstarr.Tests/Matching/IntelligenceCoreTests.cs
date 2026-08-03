using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
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
        await using var db = await _factory.CreateDbContextAsync();
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
    public async Task ListeningHistoryRetentionKeepsScopesExactAndAbandonsStalePlayback()
    {
        var otherScope = _scope with { LibraryScopeId = "other" };
        await _policies.SetAsync(_scope, new(true, 2, ["play"], ["local"]));
        await _policies.SetAsync(otherScope, new(true, 30, ["play"], ["local"]));
        var expiredKey = new string('a', 64);
        var recentKey = new string('b', 64);
        var staleKey = new string('c', 64);
        var activeKey = new string('d', 64);
        var otherKey = new string('e', 64);
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            setup.ListeningEvents.AddRange(
                HistoryEvent(_scope, expiredKey, _clock.UtcNow.AddDays(-3)),
                HistoryEvent(_scope, recentKey, _clock.UtcNow.AddDays(-1)),
                HistoryEvent(_scope, staleKey, _clock.UtcNow.AddHours(-9), ListeningEventState.Playing),
                HistoryEvent(_scope, activeKey, _clock.UtcNow.AddHours(-1), ListeningEventState.Playing),
                HistoryEvent(otherScope, otherKey, _clock.UtcNow.AddDays(-3)));
            setup.PlaybackDeliveryCheckpoints.AddRange(
                HistoryCheckpoint(expiredKey, new string('f', 64)),
                HistoryCheckpoint(recentKey, new string('1', 64)),
                HistoryCheckpoint(otherKey, new string('2', 64)));
            setup.ListeningSignals.AddRange(
                HistorySignal(_scope, _clock.UtcNow.AddDays(-1)),
                HistorySignal(_scope, _clock.UtcNow.AddDays(1)),
                HistorySignal(otherScope, _clock.UtcNow.AddDays(1)));
            setup.ListeningProfiles.AddRange(
                HistoryProfile(_scope, _clock.UtcNow.AddDays(-3)),
                HistoryProfile(_scope, _clock.UtcNow.AddDays(-1)),
                HistoryProfile(otherScope, _clock.UtcNow.AddDays(-3)));
            await setup.SaveChangesAsync();
        }
        await _policies.SetAsync(_scope, new(false, 2, ["play"], ["local"]));

        await new ListeningHistoryRetentionSweeper(_factory, _clock).SweepAsync();

        await using var db = await _factory.CreateDbContextAsync();
        var events = await db.ListeningEvents.AsNoTracking().ToDictionaryAsync(item => item.OccurrenceKey);
        Assert.DoesNotContain(expiredKey, events.Keys);
        Assert.Equal(ListeningEventState.Completed, events[recentKey].State);
        Assert.Equal(ListeningEventState.Abandoned, events[staleKey].State);
        Assert.Equal(1, events[staleKey].Revision);
        Assert.Equal(ListeningEventState.Playing, events[activeKey].State);
        Assert.Equal("other", events[otherKey].LibraryScopeId);
        Assert.Equal([recentKey, otherKey], await db.PlaybackDeliveryCheckpoints.AsNoTracking()
            .OrderBy(item => item.OccurrenceKey).Select(item => item.OccurrenceKey!).ToArrayAsync());
        var signals = await db.ListeningSignals.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(signals, item => item.ExpiresAt <= _clock.UtcNow);
        Assert.Contains(signals, item => item.LibraryScopeId == "music");
        Assert.Contains(signals, item => item.LibraryScopeId == "other");
        var profiles = await db.ListeningProfiles.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(profiles, item => item.LibraryScopeId == "music" &&
            item.CreatedAt == _clock.UtcNow.AddDays(-3));
        Assert.Contains(profiles, item => item.LibraryScopeId == "music");
        Assert.Contains(profiles, item => item.LibraryScopeId == "other");
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
        var progress = new List<DurableJobProgressUpdate>();
        var completion = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance)
        {
            ReportProgressAsync = (update, _) => { progress.Add(update); return Task.FromResult(true); }
        }, default);
        await _jobs.CompleteAsync(claim!, completion); Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(["recommendation.profile", "recommendation.provider", "recommendation.provider",
            "recommendation.rank", "recommendation.complete"], progress.Select(item => item.Stage));
        await using var db = await _factory.CreateDbContextAsync(); var candidate = Assert.Single(await db.RecommendationCandidates.ToListAsync());
        Assert.Equal("track-1", candidate.TrackKey); Assert.Contains("shared-genre", candidate.SignalsJson, StringComparison.Ordinal);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), candidate.CanonicalRecordingId);
        Assert.Equal("fixture:1", candidate.SourceRevision);
        Assert.Single(await db.RecommendationRuns.ToListAsync());
        db.RecommendationFeedback.Add(new()
        {
            Id = Guid.CreateVersion7(),
            CandidateId = candidate.Id,
            TenantId = _tenant,
            OwnerUserId = _user,
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            TrackKey = candidate.TrackKey,
            Kind = "dislike",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
            Revision = 1
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
    public async Task DirectPreviewCreatesOneIdempotentGeneratedSetWithoutAFakeRun()
    {
        await _policies.SetAsync(_scope, new(true, 30, ["play"], ["audiomuse-ai"]));
        var smart = new SmartPlaylistService(_factory, _clock, _jobs);
        RecommendationCandidate[] songs =
        [
            new("track-secret", 1, "audiomuse-ai", [new("audiomuse-preview", 1, "Selected preview.")],
                new(LibraryTrackId: Guid.Parse("11111111-1111-1111-1111-111111111111"), BackendItemId: "track-secret")),
            new("track-two", 1, "audiomuse-ai", [new("audiomuse-preview", 1, "Selected preview.")],
                new(LibraryTrackId: Guid.Parse("22222222-2222-2222-2222-222222222222"), BackendItemId: "track-two"))
        ];

        var first = await smart.CreateGeneratedSetAsync(_scope, "Sound preview", songs, "preview-request");
        var repeated = await smart.CreateGeneratedSetAsync(_scope, "Sound preview", songs, "preview-request");

        Assert.Equal(first, repeated);
        await using var db = await _factory.CreateDbContextAsync();
        var set = Assert.Single(await db.GeneratedSets.ToListAsync());
        Assert.Null(set.RunId);
        Assert.Equal(["track-secret", "track-two"], await db.GeneratedSetEntries
            .OrderBy(item => item.Position).Select(item => item.TrackKey).ToArrayAsync());
        Assert.Single(await db.Jobs.Where(item => item.Type == "smart-playlist.materialize").ToListAsync());
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
        var directSetId = await new SmartPlaylistService(_factory, _clock, _jobs)
            .CreateGeneratedSetAsync(_scope, "Temporary preview",
                [new("track-secret", 1, "audiomuse-ai", [new("preview", 1, "Preview.")],
                    new(LibraryTrackId: Guid.Parse("11111111-1111-1111-1111-111111111111"), BackendItemId: "track-secret"))],
                "purged-preview");
        var schedule = Schedule(policy.Id, "music");
        var childJobId = Guid.CreateVersion7();
        var otherScope = _scope with { BackendInstanceId = "other" };
        var exactEnrichment = HistoryJob(_scope, new string('a', 64));
        var otherEnrichment = HistoryJob(otherScope, new string('b', 64));
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            setup.JobSchedules.Add(schedule);
            setup.Jobs.AddRange(exactEnrichment, otherEnrichment, new()
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
            setup.ListeningEvents.AddRange(
                HistoryEvent(_scope, new string('a', 64)),
                HistoryEvent(otherScope, new string('b', 64)));
            setup.ListeningHistoryImports.AddRange(
                HistoryImport(_scope, new string('c', 64)),
                HistoryImport(otherScope, new string('d', 64)));
            setup.PlaybackDeliveryCheckpoints.AddRange(
                HistoryCheckpoint(new string('a', 64), new string('e', 64)),
                HistoryCheckpoint(new string('b', 64), new string('f', 64)));
            await setup.SaveChangesAsync();
        }
        await _policies.DisableAndPurgeAsync(_scope);
        await using var db = await _factory.CreateDbContextAsync(); var storedPolicy = Assert.Single(await db.IntelligencePolicies.ToListAsync());
        Assert.False(storedPolicy.Enabled); Assert.Empty(await db.ListeningSignals.ToListAsync()); Assert.Empty(await db.ListeningProfiles.ToListAsync());
        Assert.Empty(await db.RecommendationRuns.ToListAsync());
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(x => x.Id == pending.JobId)).State);
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(x => x.Id == childJobId)).State);
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(x =>
            x.IdempotencyKey == $"generated-set:{directSetId:N}")).State);
        Assert.Empty(await db.GeneratedSets.ToListAsync());
        Assert.Empty(await db.GeneratedSetEntries.ToListAsync());
        Assert.Equal("other", Assert.Single(await db.ListeningEvents.ToListAsync()).BackendInstanceId);
        Assert.Equal("other", Assert.Single(await db.ListeningHistoryImports.ToListAsync()).BackendInstanceId);
        Assert.Equal(new string('b', 64), Assert.Single(await db.PlaybackDeliveryCheckpoints.ToListAsync()).OccurrenceKey);
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(item => item.Id == exactEnrichment.Id)).State);
        Assert.Equal(DurableJobState.Pending, (await db.Jobs.SingleAsync(item => item.Id == otherEnrichment.Id)).State);
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

    private ListeningEventRecord HistoryEvent(IntelligenceScope scope, string occurrenceKey,
        DateTimeOffset? observedAt = null, ListeningEventState state = ListeningEventState.Completed) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            Protocol = scope.Protocol,
            BackendInstanceId = scope.BackendInstanceId,
            LibraryScopeId = scope.LibraryScopeId,
            OccurrenceKey = occurrenceKey,
            State = state,
            StartedAt = observedAt ?? _clock.UtcNow,
            ListenedAt = state == ListeningEventState.Playing ? null : observedAt ?? _clock.UtcNow,
            UpdatedAt = observedAt ?? _clock.UtcNow,
            SourceKind = "protocol",
            TrackReference = "track"
        };

    private ListeningHistoryImportRecord HistoryImport(IntelligenceScope scope, string hash) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = scope.TenantId,
        OwnerUserId = scope.OwnerUserId,
        Protocol = scope.Protocol,
        BackendInstanceId = scope.BackendInstanceId,
        LibraryScopeId = scope.LibraryScopeId,
        DisplayFileName = "history.json",
        Format = "spotify",
        ContentSha256 = hash,
        SizeBytes = 1,
        PreviewJson = "{}",
        PreviewRevision = hash,
        State = ListeningHistoryImportState.Completed,
        CreatedAt = _clock.UtcNow,
        UpdatedAt = _clock.UtcNow,
        ExpiresAt = _clock.UtcNow.AddDays(1)
    };

    private ListeningSignalRecord HistorySignal(IntelligenceScope scope, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = scope.TenantId,
        OwnerUserId = scope.OwnerUserId,
        Protocol = scope.Protocol,
        BackendInstanceId = scope.BackendInstanceId,
        LibraryScopeId = scope.LibraryScopeId,
        SignalType = "play",
        TrackKeyHash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
        TrackReference = "library:11111111111111111111111111111111",
        Value = 1,
        ObservedAt = expiresAt.AddDays(-2),
        ExpiresAt = expiresAt
    };

    private ListeningProfileRecord HistoryProfile(IntelligenceScope scope, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = scope.TenantId,
        OwnerUserId = scope.OwnerUserId,
        Protocol = scope.Protocol,
        BackendInstanceId = scope.BackendInstanceId,
        LibraryScopeId = scope.LibraryScopeId,
        ProfileJson = "{}",
        WindowStart = createdAt.AddDays(-1),
        WindowEnd = createdAt,
        CreatedAt = createdAt
    };

    private PlaybackDeliveryCheckpointEntity HistoryCheckpoint(string occurrenceKey, string signalKey) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenant,
        OwnerUserId = _user,
        OccurrenceKey = occurrenceKey,
        SignalKey = signalKey,
        TargetId = "lastfm",
        State = ScopedPlaybackScrobbleOutcome.Delivered,
        UpdatedAt = _clock.UtcNow
    };

    private DurableJobRecord HistoryJob(IntelligenceScope scope, string occurrenceKey) => new()
    {
        Id = Guid.CreateVersion7(),
        ScopeKey = $"{scope.TenantId:N}:{scope.OwnerUserId:N}",
        TenantId = scope.TenantId,
        OwnerUserId = scope.OwnerUserId,
        LibraryScopeId = scope.LibraryScopeId,
        Type = MusicBrainzListeningEnrichmentQueue.JobType,
        PayloadJson = JsonSerializer.Serialize(new MusicBrainzListeningEnrichmentPayload(scope, occurrenceKey)),
        PolicySnapshotJson = "{}",
        RequestFingerprint = occurrenceKey,
        IdempotencyKey = $"history-{occurrenceKey}",
        CorrelationId = occurrenceKey,
        State = DurableJobState.Pending,
        MaxAttempts = 3,
        AvailableAt = _clock.UtcNow,
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
    public async Task TransientProviderFailureUsesDurableRetryAndThenCompletes()
    {
        await _policies.SetAsync(_scope, new(true, 30, ["play"], ["flaky"]));
        await new RecommendationRunService(_factory, _jobs, _clock).EnqueueAsync(
            _scope, [], 10, "retry-run");
        var provider = new FailOnceProvider();
        var handler = new RecommendationRunJobHandler(_factory, [provider],
            new ListeningProfileService(_factory, _clock), _clock);
        var firstClaim = await _jobs.ClaimNextAsync("retry-worker", ["recommendation.generate"]);
        var first = await handler.ExecuteAsync(new(firstClaim!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(firstClaim!, first);
        Assert.Equal(DurableJobCompletionKind.Retry, first.Kind);
        await using (var failed = await _factory.CreateDbContextAsync())
            Assert.Equal(RecommendationRunState.Failed,
                (await failed.RecommendationRuns.SingleAsync()).State);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        var retryClaim = await _jobs.ClaimNextAsync("retry-worker", ["recommendation.generate"]);
        var retry = await handler.ExecuteAsync(new(retryClaim!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(retryClaim!, retry);
        Assert.Equal(DurableJobCompletionKind.Succeeded, retry.Kind);
        await using var completed = await _factory.CreateDbContextAsync();
        Assert.Equal(RecommendationRunState.Succeeded,
            (await completed.RecommendationRuns.SingleAsync()).State);
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

    [Fact]
    public async Task RankingIsDeterministicAndKeepsTheBestDuplicateEvidence()
    {
        await _policies.SetAsync(_scope, new(true, 30, ["play"], ["ranking"]));
        await new RecommendationRunService(_factory, _jobs, _clock).EnqueueAsync(
            _scope, [], 10, "ranking-run");
        var claim = await _jobs.ClaimNextAsync("ranking-worker", ["recommendation.generate"]);
        var result = await new RecommendationRunJobHandler(_factory, [new RankingProvider()],
            new ListeningProfileService(_factory, _clock), _clock)
            .ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);

        await using var db = await _factory.CreateDbContextAsync();
        var candidates = await db.RecommendationCandidates.OrderBy(item => item.Position).ToListAsync();
        Assert.Equal(["track-two", "track-one"], candidates.Select(item => item.TrackKey));
        Assert.Equal([.9, .7], candidates.Select(item => item.Score));
        Assert.Contains("best duplicate", candidates[0].SignalsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecommendationAccountsPreferExactUserAndNeverCrossOwnerOrLibrary()
    {
        var otherUser = Guid.CreateVersion7();
        var userAccount = Guid.CreateVersion7();
        var libraryAccount = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(new()
            {
                Id = otherUser,
                TenantId = _tenant,
                DisplayName = "Other",
                Status = PlatformUserStatus.Active,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            db.ProviderAccounts.AddRange(
                Account(userAccount, ProviderAccountScope.User, _user),
                Account(Guid.CreateVersion7(), ProviderAccountScope.User, otherUser),
                Account(libraryAccount, ProviderAccountScope.Library, null, "music"),
                Account(Guid.CreateVersion7(), ProviderAccountScope.Library, null, "other"));
            await db.SaveChangesAsync();
        }
        var accessor = new ScopedRecommendationAccountAccessor(_factory, null!);
        Assert.Equal(userAccount, (await accessor.FindAccountAsync(_scope, "fixture", default))!.AccountId);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ProviderAccounts.SingleAsync(item => item.Id == userAccount)).Enabled = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(libraryAccount, (await accessor.FindAccountAsync(_scope, "fixture", default))!.AccountId);
        Assert.Null(await accessor.FindAccountAsync(
            _scope with { OwnerUserId = Guid.CreateVersion7(), LibraryScopeId = "missing" },
            "fixture", default));
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
    private sealed class FailOnceProvider : IRecommendationProvider
    {
        private bool _failed;
        public string Id => "flaky";
        public Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request)
        {
            if (!_failed) { _failed = true; throw new HttpRequestException(); }
            return Task.FromResult(new RecommendationProviderResult(
                RecommendationProviderState.Succeeded, []));
        }
    }
    private sealed class RankingProvider : IRecommendationProvider
    {
        public string Id => "ranking";
        public Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request) =>
            Task.FromResult(new RecommendationProviderResult(RecommendationProviderState.Succeeded,
            [
                Candidate("track-one", .7,
                    Guid.Parse("11111111-1111-1111-1111-111111111111"), "tie"),
                Candidate("track-two", .7,
                    Guid.Parse("22222222-2222-2222-2222-222222222222"), "lower duplicate"),
                Candidate("track-two", .9,
                    Guid.Parse("22222222-2222-2222-2222-222222222222"), "best duplicate")
            ]));
        private static RecommendationCandidate Candidate(string key, double score, Guid id, string reason) =>
            new(key, score, "ranking", [new("score", score, reason)],
                new(LibraryTrackId: id))
            { CanonicalRecordingId = id, SourceRevision = "ranking:1" };
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
        Id = id,
        TenantId = _tenant,
        CreatedByUserId = _user,
        CreatedAt = _clock.UtcNow,
        UpdatedAt = _clock.UtcNow,
        Revision = 1
    };
    private ProviderAccountRecord Account(Guid id, ProviderAccountScope scope,
        Guid? owner, string? library = null) => new()
        {
            Id = id,
            TenantId = _tenant,
            OwnerUserId = owner,
            ProviderId = "fixture",
            DisplayName = "Fixture",
            Scope = scope,
            LibraryScopeId = library,
            Enabled = true,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
            Revision = 1
        };
}
