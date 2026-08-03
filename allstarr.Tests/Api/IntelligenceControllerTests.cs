using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed class IntelligenceControllerTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.CreateVersion7(); private readonly Guid _user = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private readonly CommandCounter _commands = new();
    private Factory _factory = null!; private FakePolicy _policy = null!; private FakeRuns _runs = null!; private FakeSmart _smart = null!; private FakeReadiness _readiness = null!;
    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(_commands).Options);
        await using var db = await _factory.CreateDbContextAsync();
        db.Tenants.Add(new() { Id = _tenant, Slug = "intelligence", Name = "Intelligence", CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new()
        {
            Id = _user,
            TenantId = _tenant,
            DisplayName = "Owner",
            Status = PlatformUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.BackendIdentities.Add(new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenant,
            UserId = _user,
            BackendType = "jellyfin",
            BackendInstanceId = "main",
            PrincipalId = "principal",
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(); _policy = new(); _runs = new(); _smart = new(); _readiness = new();
    }

    [Fact]
    public async Task Get_DerivesExactCallerScopeAndReturnsTruthfulEmptyOrUnauthorizedState()
    {
        var controller = Controller();
        var empty = Assert.IsType<OkObjectResult>(await controller.Get(Scope(), default));
        Assert.Contains("empty", JsonSerializer.Serialize(empty.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_tenant, _policy.LastScope!.TenantId); Assert.Equal(_user, _policy.LastScope.OwnerUserId);

        var unauthorized = Assert.IsType<OkObjectResult>(await controller.Get(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "other",
            LibraryScopeId = "music"
        }, default));
        Assert.Contains("unauthorized", JsonSerializer.Serialize(unauthorized.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_IgnoresMalformedScheduleRowsInsteadOfBreakingTheIntelligenceScreen()
    {
        var policy = Policy();
        _policy.Record = policy;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IntelligencePolicies.Add(policy);
            db.JobSchedules.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                OwnerUserId = _user,
                LibraryScopeId = "music",
                JobType = DurableScheduleEngine.RecommendationJobType,
                CronExpression = "0 8 * * *",
                TimeZoneId = "UTC",
                RetryPolicyJson = "{}",
                PayloadTemplateJson = "{broken",
                Enabled = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var result = Assert.IsType<OkObjectResult>(await Controller().Get(Scope(), default));
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("configured", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"schedules\":[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReportsExactListeningServiceAndSongDetailStatus()
    {
        _policy.Record = Policy();
        var delivered = HistoryEvent("Delivered", DateTimeOffset.UtcNow.AddMinutes(-2));
        delivered.MusicBrainzEnrichmentState = MusicBrainzEnrichmentState.Resolved;
        var pending = HistoryEvent("Pending", DateTimeOffset.UtcNow.AddMinutes(-1));
        pending.MusicBrainzEnrichmentState = MusicBrainzEnrichmentState.Pending;
        var other = HistoryEvent("Other", DateTimeOffset.UtcNow, "other");
        other.MusicBrainzEnrichmentState = MusicBrainzEnrichmentState.Failed;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ListeningEvents.AddRange(delivered, pending, other);
            db.PlaybackDeliveryCheckpoints.AddRange(
                DeliveryCheckpoint(delivered.OccurrenceKey, "lastfm", ScopedPlaybackScrobbleOutcome.Delivered),
                DeliveryCheckpoint(other.OccurrenceKey, "listenbrainz", ScopedPlaybackScrobbleOutcome.PermanentFailure));
            await db.SaveChangesAsync();
        }

        var result = Assert.IsType<OkObjectResult>(await Controller(scrobbleTargets:
            [new FakeScrobbleTarget("lastfm", true), new FakeScrobbleTarget("listenbrainz", false)])
            .Get(Scope(), default));
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("Last.fm", json, StringComparison.Ordinal);
        Assert.Contains("\"latestState\":\"delivered\"", json, StringComparison.Ordinal);
        Assert.Contains("ListenBrainz", json, StringComparison.Ordinal);
        Assert.DoesNotContain("permanentfailure", json, StringComparison.Ordinal);
        Assert.Contains("\"pending\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"resolved\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"failed\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioMuseSimilarUsesTheExactSessionScopeAndReturnsPlainSongFacts()
    {
        var audioMuse = new FakeAudioMuse();
        var result = Assert.IsType<OkObjectResult>(await Controller(audioMuse).GetAudioMuseSimilar(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            SeedTrackIds = ["seed-1"],
            Limit = 10
        }, default));
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Equal(_tenant, audioMuse.Scope!.TenantId);
        Assert.Equal(_user, audioMuse.Scope.OwnerUserId);
        Assert.Contains("A song", json, StringComparison.Ordinal);
        Assert.Contains("A clear reason", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioMuseFingerprintSendsOnlyBoundedExactScopeCompletedHistory()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ListeningEvents.AddRange(
                HistoryEvent("recent", now.AddDays(-1)),
                HistoryEvent("frequent", now.AddDays(-10)),
                HistoryEvent("frequent", now.AddDays(-9)),
                HistoryEvent("too-old", now.AddDays(-45)),
                HistoryEvent("other-library", now.AddDays(-1), "other"));
            await db.SaveChangesAsync();
        }
        var audioMuse = new FakeAudioMuse();

        var result = Assert.IsType<OkObjectResult>(await Controller(audioMuse).GetAudioMuseFingerprint(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            PeriodDays = 30,
            Limit = 10
        }, default));
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Equal(["frequent", "recent"], audioMuse.Seeds);
        Assert.Contains("\"completedListens\":3", json, StringComparison.Ordinal);
        Assert.DoesNotContain("too-old", json, StringComparison.Ordinal);
        Assert.DoesNotContain("other-library", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioMuseClustersAreReturnedAsBoundedCursorPages()
    {
        var audioMuse = new FakeAudioMuse
        {
            Clusters =
            [
                new("cluster-1", "First group", []),
                new("cluster-2", "Second group", []),
                new("cluster-3", "Third group", [])
            ]
        };
        var first = Assert.IsType<OkObjectResult>(await Controller(audioMuse).GetAudioMuseClusters(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Limit = 1
        }, default));
        var firstJson = JsonSerializer.Serialize(first.Value);
        Assert.Contains("First group", firstJson, StringComparison.Ordinal);
        Assert.Contains("\"nextCursor\":\"1\"", firstJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Second group", firstJson, StringComparison.Ordinal);
        Assert.Equal(2, audioMuse.ClusterLimit);

        var second = Assert.IsType<OkObjectResult>(await Controller(audioMuse).GetAudioMuseClusters(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Limit = 1,
            Cursor = "1"
        }, default));
        var secondJson = JsonSerializer.Serialize(second.Value);
        Assert.Contains("Second group", secondJson, StringComparison.Ordinal);
        Assert.Contains("\"nextCursor\":\"2\"", secondJson, StringComparison.Ordinal);
        Assert.Equal(3, audioMuse.ClusterLimit);
    }

    [Fact]
    public async Task AudioMusePreviewCreatesOnlyAnExactScopeGeneratedPlaylist()
    {
        _policy.Record = Policy();
        _policy.Record.EnabledProvidersJson = "[\"audiomuse-ai\"]";
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var backendIdentityId = await db.BackendIdentities.Select(item => item.Id).SingleAsync();
            db.LibraryTracks.AddRange(
                LocalTrack(backendIdentityId, "song-2", "Second"),
                LocalTrack(backendIdentityId, "song-1", "First"),
                LocalTrack(backendIdentityId, "other-song", "Other", "other"));
            await db.SaveChangesAsync();
        }

        var result = Assert.IsType<AcceptedResult>(await Controller().CreateAudioMuseGeneratedSet(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Name = "Evening sounds",
            TrackIds = ["song-1", "song-2"],
            IdempotencyKey = "sound-preview-1"
        }, default));

        Assert.Contains("creating", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        Assert.Equal("Evening sounds", _smart.PreviewName);
        Assert.Equal("sound-preview-1", _smart.IdempotencyKey);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<RecommendationCandidate>>(_smart.Candidates);
        Assert.Equal(["song-1", "song-2"], candidates.Select(item => item.TrackKey));
        Assert.All(candidates, item => Assert.Equal("audiomuse-ai", item.Source));

        var crossed = await Controller().CreateAudioMuseGeneratedSet(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Name = "Wrong library",
            TrackIds = ["other-song"],
            IdempotencyKey = "sound-preview-2"
        }, default);
        Assert.IsType<ConflictObjectResult>(crossed);
    }

    [Fact]
    public async Task HistoryRoutesPageCorrectAndDeleteOnlyTheExactScope()
    {
        var now = DateTimeOffset.UtcNow;
        var first = HistoryEvent("First", now.AddMinutes(-1));
        var second = HistoryEvent("Second", now.AddMinutes(-2));
        var third = HistoryEvent("Third", now.AddMinutes(-3));
        var otherLibrary = HistoryEvent("Other library", now.AddMinutes(-4), "other");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ListeningEvents.AddRange(first, second, third, otherLibrary);
            db.Set<allstarr.Core.Playback.PlaybackDeliveryCheckpointEntity>().Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                OwnerUserId = _user,
                OccurrenceKey = first.OccurrenceKey,
                SignalKey = new string('a', 64),
                TargetId = "lastfm",
                Kind = allstarr.Core.Playback.PlaybackScrobbleDeliveryKind.Completed,
                State = allstarr.Core.Playback.ScopedPlaybackScrobbleOutcome.Delivered,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var page = Assert.IsType<OkObjectResult>(await Controller().GetHistory(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            From = now.AddDays(-1),
            To = now.AddDays(1),
            Limit = 2
        }, default));
        var pageJson = JsonSerializer.Serialize(page.Value);
        Assert.Contains("First", pageJson, StringComparison.Ordinal);
        Assert.Contains("Second", pageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Third", pageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Other library", pageJson, StringComparison.Ordinal);
        Assert.Contains("lastfm", pageJson, StringComparison.Ordinal);
        using var pageDocument = JsonDocument.Parse(pageJson);
        var cursor = pageDocument.RootElement.GetProperty("nextCursor").GetString();
        var nextPage = Assert.IsType<OkObjectResult>(await Controller().GetHistory(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            From = now.AddDays(-1),
            To = now.AddDays(1),
            Limit = 2,
            Cursor = cursor
        }, default));
        var nextPageJson = JsonSerializer.Serialize(nextPage.Value);
        Assert.Contains("Third", nextPageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("First", nextPageJson, StringComparison.Ordinal);

        Assert.IsType<OkObjectResult>(await Controller().GetHistoryOverview(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            From = now.AddDays(-1),
            To = now.AddDays(1),
            TimeZoneId = "UTC"
        }, default));
        Assert.IsType<OkObjectResult>(await Controller().GetHistoryActivity(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            From = now.AddDays(-1),
            To = now.AddDays(1),
            TimeZoneId = "UTC"
        }, default));
        Assert.IsType<OkObjectResult>(await Controller().GetHistoryTopItems("track", new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            From = now.AddDays(-1),
            To = now.AddDays(1)
        }, default));

        var exportController = Controller();
        await using var exportBody = new MemoryStream();
        exportController.HttpContext.Response.Body = exportBody;
        var export = await exportController.ExportHistory(Scope(), default);
        await export.ExecuteResultAsync(exportController.ControllerContext);
        var exportJson = System.Text.Encoding.UTF8.GetString(exportBody.ToArray());
        Assert.Contains("allstarr-listening-history", exportJson, StringComparison.Ordinal);
        Assert.Contains("First", exportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Other library", exportJson, StringComparison.Ordinal);

        var corrected = Assert.IsType<OkObjectResult>(await Controller().CorrectHistory(first.Id, new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Title = "Corrected",
            Artist = "Artist",
            Album = "Album",
            ExpectedRevision = first.Revision
        }, default));
        Assert.Contains("Revision", JsonSerializer.Serialize(corrected.Value), StringComparison.Ordinal);

        var staleDelete = await Controller().DeleteHistory(first.Id, new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            ExpectedRevision = first.Revision,
            Confirmed = true
        }, default);
        Assert.IsType<ConflictObjectResult>(staleDelete);

        Assert.IsType<NoContentResult>(await Controller().DeleteHistory(first.Id, new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            ExpectedRevision = first.Revision + 1,
            Confirmed = true
        }, default));

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(3, await verify.ListeningEvents.CountAsync());
        Assert.Empty(await verify.Set<allstarr.Core.Playback.PlaybackDeliveryCheckpointEntity>().ToListAsync());
        Assert.Contains(await verify.AuditEvents.ToListAsync(), item =>
            item.Category == "listening-history" && item.Action == "corrected");
        Assert.Contains(await verify.AuditEvents.ToListAsync(), item =>
            item.Category == "listening-history" && item.Action == "deleted");
    }

    [Fact]
    public async Task Policy_UsesCallerIdentityAndRejectsUnavailableStaticPromise()
    {
        var controller = Controller();
        var unavailable = await controller.SetPolicy(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Enabled = true,
            RetentionDays = 30,
            EnabledProviders = ["not-registered"]
        }, default);
        Assert.IsType<BadRequestObjectResult>(unavailable);

        Assert.IsType<OkObjectResult>(await controller.SetPolicy(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Enabled = true,
            RetentionDays = 30,
            AllowedSignalTypes = ["play", "favorite"],
            EnabledProviders = ["lastfm"]
        }, default));
        Assert.Equal(_user, _policy.LastScope!.OwnerUserId); Assert.Equal(["lastfm"], _policy.LastInput!.EnabledProviders);

        _readiness.LastFmState = RecommendationProviderReadinessState.Unauthorized;
        var notReady = await controller.SetPolicy(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Enabled = true,
            RetentionDays = 30,
            EnabledProviders = ["lastfm"]
        }, default);
        Assert.IsType<ConflictObjectResult>(notReady);
    }

    [Fact]
    public async Task Get_ShowsOnlyScopedCandidatesExplanationsProfilesAndGeneratedPreviews()
    {
        _policy.Record = Policy(); var run = Guid.CreateVersion7(); var job = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Jobs.Add(new()
            {
                Id = job,
                ScopeKey = $"{_tenant:N}:{_user:N}",
                TenantId = _tenant,
                OwnerUserId = _user,
                LibraryScopeId = "music",
                Type = "recommendation.generate",
                PayloadJson = "{}",
                PolicySnapshotJson = "{}",
                RequestFingerprint = new string('a', 64),
                IdempotencyKey = "run",
                CorrelationId = "test",
                State = DurableJobState.Succeeded,
                MaxAttempts = 1,
                AvailableAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                ActorUserId = _user,
                Category = "job-progress",
                Action = "recommendation.rank",
                Outcome = "running",
                CorrelationId = "test",
                DetailsJson = """{"stage":"recommendation.rank","message":"Ranking tracks.","completed":1,"total":2}""",
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.RecommendationRuns.Add(new()
            {
                Id = run,
                TenantId = _tenant,
                OwnerUserId = _user,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                LibraryScopeId = "music",
                JobId = job,
                IdempotencyKey = "run",
                Limit = 10,
                State = RecommendationRunState.Succeeded,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            var candidateId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            db.RecommendationCandidates.Add(new()
            {
                Id = candidateId,
                RunId = run,
                TenantId = _tenant,
                OwnerUserId = _user,
                Position = 0,
                TrackKey = "local:42",
                Score = .9,
                Source = "lastfm",
                SourceRevision = "lastfm:fixture",
                SignalsJson = JsonSerializer.Serialize(new[] { new RecommendationSignal("similar", .9, "Shared listening context") }),
                IdentityJson = JsonSerializer.Serialize(new RecommendationTrackIdentity(
                    MusicBrainzRecordingId: "11111111-1111-1111-1111-111111111111",
                    Title: "Recommended song",
                    Artist: "Recommended artist",
                    LibraryTrackId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    BackendItemId: "backend-track-42")),
                CreatedAt = DateTimeOffset.UtcNow
            });
            var set = Guid.CreateVersion7(); db.GeneratedSets.Add(new()
            {
                Id = set,
                RunId = run,
                TenantId = _tenant,
                OwnerUserId = _user,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                LibraryScopeId = "music",
                Name = "Private preview",
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.GeneratedSetEntries.Add(new() { Id = Guid.CreateVersion7(), GeneratedSetId = set, TenantId = _tenant, OwnerUserId = _user, Position = 0, TrackKey = "local:42" });
            db.ListeningProfiles.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                OwnerUserId = _user,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                LibraryScopeId = "music",
                ProfileJson = JsonSerializer.Serialize(new ListeningProfile(_tenant, _user, "main", "music", 4, 1, 2,
                    new Dictionary<string, double>(), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)),
                WindowStart = DateTimeOffset.UtcNow.AddDays(-1),
                WindowEnd = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        _commands.Reset(); var elapsed = Stopwatch.StartNew();
        var result = Assert.IsType<OkObjectResult>(await Controller().Get(Scope(), default));
        elapsed.Stop(); var json = JsonSerializer.Serialize(result.Value);
        Assert.InRange(_commands.Count, 1, 12);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2),
            $"Intelligence read took {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.Contains("Shared listening context", json); Assert.Contains("Private preview", json); Assert.Contains("visualization", json);
        Assert.Contains("lastfm:fixture", json, StringComparison.Ordinal);
        Assert.Contains("Recommended song", json, StringComparison.Ordinal);
        Assert.Contains("\"latestRunState\":\"succeeded\"", json, StringComparison.Ordinal);
        Assert.Contains("Ranking tracks.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TenantId", json, StringComparison.Ordinal);
        var feedback = Assert.IsType<OkObjectResult>(await Controller().SetFeedback(
            Guid.Parse("33333333-3333-3333-3333-333333333333"), new()
            {
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                LibraryScopeId = "music",
                Kind = "dislike",
                ReasonCode = "not-my-style",
                ExpectedRevision = 0
            }, default));
        Assert.Contains("dislike", JsonSerializer.Serialize(feedback.Value), StringComparison.Ordinal);
        Assert.IsType<NotFoundResult>(await Controller().SetFeedback(
            Guid.Parse("33333333-3333-3333-3333-333333333333"), new()
            {
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                LibraryScopeId = "other",
                Kind = "dismiss",
                ExpectedRevision = 1
            }, default));
        Assert.IsType<OkObjectResult>(await Controller().GenerateSet(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            RunId = run,
            Name = "Generated mix"
        }, default));
        var generatedCandidate = Assert.Single(_smart.Candidates!);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), generatedCandidate.Identity!.LibraryTrackId);
        Assert.Equal("backend-track-42", generatedCandidate.Identity.BackendItemId);
    }

    [Fact]
    public async Task RunAndGeneratedPreview_UseExactSessionScope()
    {
        _policy.Record = Policy(); var controller = Controller();
        Assert.IsType<AcceptedResult>(await controller.Enqueue(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Limit = 20,
            IdempotencyKey = "request-1"
        }, default));
        Assert.Equal(_user, _runs.Scope!.OwnerUserId);
    }

    [Fact]
    public async Task ScheduleCrudIsExactScopeRevisionCheckedAndSoftDisables()
    {
        var policy = Policy(); _policy.Record = policy;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IntelligencePolicies.Add(policy);
            await db.SaveChangesAsync();
        }
        var controller = Controller();
        var created = Assert.IsType<CreatedResult>(await controller.CreateSchedule(new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            CronExpression = "0 3 * * *",
            TimeZoneId = "UTC",
            OverlapPolicy = "skip",
            MisfirePolicy = "runOnce",
            Enabled = true,
            Limit = 40,
            Name = "Morning discovery"
        }, default));
        var createdJson = JsonSerializer.SerializeToElement(created.Value);
        var scheduleId = createdJson.GetProperty("id").GetGuid();
        Assert.Equal(0, createdJson.GetProperty("Revision").GetInt64());

        var conflict = await controller.UpdateSchedule(scheduleId, new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            CronExpression = "0 4 * * *",
            TimeZoneId = "UTC",
            OverlapPolicy = "queue",
            MisfirePolicy = "skip",
            Enabled = true,
            Limit = 25,
            Name = "Updated discovery",
            ExpectedRevision = 99
        }, default);
        Assert.IsType<ConflictObjectResult>(conflict);

        var updated = Assert.IsType<OkObjectResult>(await controller.UpdateSchedule(scheduleId, new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            CronExpression = "0 4 * * *",
            TimeZoneId = "UTC",
            OverlapPolicy = "queue",
            MisfirePolicy = "skip",
            Enabled = true,
            Limit = 25,
            Name = "Updated discovery",
            ExpectedRevision = 0
        }, default));
        Assert.Contains("Updated discovery", JsonSerializer.Serialize(updated.Value), StringComparison.Ordinal);

        Assert.IsType<NoContentResult>(await controller.DeleteSchedule(scheduleId, new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            ExpectedRevision = 1
        }, default));
        await using var verify = await _factory.CreateDbContextAsync();
        var stored = await verify.JobSchedules.SingleAsync();
        Assert.False(stored.Enabled); Assert.Null(stored.NextRunAt); Assert.Equal(2, stored.Revision);
        var template = JsonSerializer.Deserialize<RecommendationScheduleTemplate>(stored.PayloadTemplateJson)!;
        Assert.Equal(policy.Id, template.IntelligencePolicyId);
    }

    [Fact]
    public async Task DeleteScheduleRejectsUnownedBackendBeforeReadingSchedule()
    {
        var policy = Policy();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IntelligencePolicies.Add(policy);
            db.JobSchedules.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                OwnerUserId = _user,
                LibraryScopeId = "music",
                JobType = DurableScheduleEngine.RecommendationJobType,
                CronExpression = "0 3 * * *",
                TimeZoneId = "UTC",
                RetryPolicyJson = "{}",
                PayloadTemplateJson = JsonSerializer.Serialize(new RecommendationScheduleTemplate(
                    1, policy.Id, 25, "Discovery")),
                Enabled = true,
                NextRunAt = DateTimeOffset.UtcNow.AddDays(1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await using var read = await _factory.CreateDbContextAsync();
        var id = await read.JobSchedules.Select(item => item.Id).SingleAsync();
        var result = await Controller().DeleteSchedule(id, new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "not-owned",
            LibraryScopeId = "music",
            ExpectedRevision = 0
        }, default);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateScheduleCannotTakeOverAnotherPolicySchedule()
    {
        var policy = Policy();
        var scheduleId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IntelligencePolicies.Add(policy);
            db.JobSchedules.Add(new()
            {
                Id = scheduleId,
                TenantId = _tenant,
                OwnerUserId = _user,
                LibraryScopeId = "music",
                JobType = DurableScheduleEngine.RecommendationJobType,
                CronExpression = "0 3 * * *",
                TimeZoneId = "UTC",
                RetryPolicyJson = "{}",
                PayloadTemplateJson = JsonSerializer.Serialize(new RecommendationScheduleTemplate(
                    1, Guid.CreateVersion7(), 25, "Another backend's discovery")),
                Enabled = true,
                NextRunAt = DateTimeOffset.UtcNow.AddDays(1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var result = await Controller().UpdateSchedule(scheduleId, new()
        {
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            CronExpression = "0 4 * * *",
            TimeZoneId = "UTC",
            OverlapPolicy = "skip",
            MisfirePolicy = "runOnce",
            Enabled = true,
            Limit = 25,
            Name = "Taken over",
            ExpectedRevision = 0
        }, default);

        Assert.IsType<NotFoundResult>(result);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.DoesNotContain("Taken over", (await verify.JobSchedules.SingleAsync()).PayloadTemplateJson,
            StringComparison.Ordinal);
    }

    private IntelligenceController Controller(FakeAudioMuse? audioMuse = null,
        IReadOnlyList<IExactScopePlaybackScrobbleTarget>? scrobbleTargets = null)
    {
        var value = new IntelligenceController(_factory, _policy, _runs, _smart, _readiness,
            [new FakeProvider("lastfm"), new FakeProvider("musicbrainz-local"), new FakeProvider("audiomuse-ai")],
            audioMuse ?? new FakeAudioMuse(), scrobbleTargets: scrobbleTargets);
        value.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        value.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = "backend",
            UserName = "Owner",
            IsAdministrator = false,
            JellyfinAccessToken = "token",
            TenantId = _tenant,
            AllstarrUserId = _user,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        return value;
    }
    private IntelligenceScopeRequest Scope() => new() { Protocol = "jellyfin", BackendInstanceId = "main", LibraryScopeId = "music" };
    private IntelligencePolicyRecord Policy() => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenant,
        OwnerUserId = _user,
        Protocol = "jellyfin",
        BackendInstanceId = "main",
        LibraryScopeId = "music",
        Enabled = true,
        RetentionDays = 30,
        AllowedSignalTypesJson = "[\"play\"]",
        EnabledProvidersJson = "[\"lastfm\"]",
        Revision = 1
    };
    private ListeningEventRecord HistoryEvent(string title, DateTimeOffset listenedAt, string library = "music") => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenant,
        OwnerUserId = _user,
        Protocol = "jellyfin",
        BackendInstanceId = "main",
        LibraryScopeId = library,
        OccurrenceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(),
        State = ListeningEventState.Completed,
        ListenedAt = listenedAt,
        UpdatedAt = listenedAt,
        SourceKind = "protocol",
        TrackReference = title,
        Title = title,
        Artist = "Artist",
        Revision = 1
    };
    private PlaybackDeliveryCheckpointEntity DeliveryCheckpoint(string occurrenceKey, string target,
        ScopedPlaybackScrobbleOutcome state) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenant,
            OwnerUserId = _user,
            OccurrenceKey = occurrenceKey,
            SignalKey = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
            TargetId = target,
            Kind = PlaybackScrobbleDeliveryKind.Completed,
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    private LibraryTrackRecord LocalTrack(Guid backendIdentityId, string id, string title,
        string library = "music") => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenant,
            OwnerUserId = _user,
            BackendIdentityId = backendIdentityId,
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = library,
            BackendItemId = id,
            FilePath = $"/music/{id}.flac",
            Title = title,
            Artist = "Artist",
            ProviderIdsJson = "{}",
            IndexedAt = DateTimeOffset.UtcNow,
            SourceModifiedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Revision = 1
        };
    public async Task DisposeAsync() => await _database.DisposeAsync();
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    { public AllstarrDbContext CreateDbContext() => new(options); public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext()); }
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
    private sealed class FakePolicy : IIntelligencePolicyService
    {
        public IntelligencePolicyRecord? Record { get; set; }
        public IntelligenceScope? LastScope { get; private set; }
        public IntelligencePolicyInput? LastInput { get; private set; }
        public Task<IntelligencePolicyRecord?> GetAsync(IntelligenceScope scope, CancellationToken cancellationToken = default) { LastScope = scope; return Task.FromResult(Record); }
        public Task<IntelligencePolicyRecord> SetAsync(IntelligenceScope scope, IntelligencePolicyInput input, CancellationToken cancellationToken = default) { LastScope = scope; LastInput = input; Record ??= new() { Id = Guid.CreateVersion7(), TenantId = scope.TenantId, OwnerUserId = scope.OwnerUserId, Protocol = scope.Protocol, BackendInstanceId = scope.BackendInstanceId, LibraryScopeId = scope.LibraryScopeId }; Record.Enabled = input.Enabled; Record.RetentionDays = input.RetentionDays; Record.Revision++; return Task.FromResult(Record); }
        public Task DisableAndPurgeAsync(IntelligenceScope scope, CancellationToken cancellationToken = default) { LastScope = scope; return Task.CompletedTask; }
    }
    private sealed class FakeRuns : IRecommendationRunService { public IntelligenceScope? Scope { get; private set; } public Task<RecommendationRunReceipt> EnqueueAsync(IntelligenceScope scope, IReadOnlyList<string> seeds, int limit, string idempotencyKey, CancellationToken cancellationToken = default) { Scope = scope; return Task.FromResult(new RecommendationRunReceipt(Guid.CreateVersion7(), Guid.CreateVersion7(), true, RecommendationRunState.Pending)); } }
    private sealed class FakeSmart : ISmartPlaylistService
    {
        public IReadOnlyList<RecommendationCandidate>? Candidates { get; private set; }
        public string? PreviewName { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public Task<Guid> CreateGeneratedSetAsync(IntelligenceScope scope, Guid runId, string name,
            IReadOnlyList<RecommendationCandidate> candidates, CancellationToken cancellationToken = default)
        {
            Candidates = candidates;
            return Task.FromResult(Guid.CreateVersion7());
        }
        public Task<Guid> CreateGeneratedSetAsync(IntelligenceScope scope, string name,
            IReadOnlyList<RecommendationCandidate> candidates, string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            PreviewName = name; Candidates = candidates; IdempotencyKey = idempotencyKey;
            return Task.FromResult(Guid.CreateVersion7());
        }
    }
    private sealed class FakeProvider(string id) : IRecommendationProvider { public string Id => id; public Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request) => Task.FromResult(new RecommendationProviderResult(RecommendationProviderState.Succeeded, [])); }
    private sealed class FakeScrobbleTarget(string id, bool configured) : IExactScopePlaybackScrobbleTarget
    {
        public string ProviderId => id;
        public Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(configured);
        public Task<ScopedPlaybackScrobbleResult> DeliverAsync(IntelligenceScope scope,
            PlaybackTransition transition, ScopedPlaybackTrack track, long? positionTicks,
            DateTimeOffset observedAt, string signalKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
    private sealed class FakeAudioMuse : IAudioMuseRecommendationClient
    {
        public bool IsAvailable => true;
        public IntelligenceScope? Scope { get; private set; }
        public IReadOnlyList<string>? Seeds { get; private set; }
        public IReadOnlyList<AudioMuseCluster> Clusters { get; init; } = [];
        public int ClusterLimit { get; private set; }
        public Task<bool> CheckHealthAsync(IntelligenceScope scope, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyList<RecommendationSourceItem>> RecommendAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken)
        {
            Seeds = query.SeedTrackKeys;
            return FindSimilarAsync(query.Scope, query.SeedTrackKeys, query.Limit, cancellationToken);
        }
        public Task<IReadOnlyList<RecommendationSourceItem>> FindSimilarAsync(IntelligenceScope scope,
            IReadOnlyList<string> seedTrackIds, int limit, CancellationToken cancellationToken)
        {
            Scope = scope;
            return Task.FromResult<IReadOnlyList<RecommendationSourceItem>>([
                new("song-1", .8, [new("similar", .8, "A clear reason")],
                    new("audiomuse-ai", Title: "A song", Artist: "An artist", BackendItemId: "song-1"))]);
        }
        public Task<ProviderAnalysisProgress> StartAnalysisAsync(IntelligenceScope scope, bool rebuild,
            string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderAnalysisProgress("job-1", ProviderAnalysisState.Queued, 0, 1));
        public Task<ProviderAnalysisProgress> GetAnalysisProgressAsync(IntelligenceScope scope, string jobId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderAnalysisProgress(jobId, ProviderAnalysisState.Completed, 1, 1));
        public Task<IReadOnlyList<AudioMuseCluster>> GetClustersAsync(IntelligenceScope scope, int limit,
            CancellationToken cancellationToken)
        {
            ClusterLimit = limit;
            return Task.FromResult<IReadOnlyList<AudioMuseCluster>>(Clusters.Take(limit).ToArray());
        }
        public Task<IReadOnlyList<RecommendationSourceItem>> SearchAsync(IntelligenceScope scope, string query,
            bool includeLyrics, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecommendationSourceItem>>([]);
        public Task<AudioMusePathResult> FindPathAsync(IntelligenceScope scope, string startTrackId,
            string endTrackId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new AudioMusePathResult([], 0));
        public Task<IReadOnlyList<RecommendationSourceItem>> BlendAsync(IntelligenceScope scope,
            IReadOnlyList<string> positiveSeedTrackIds, IReadOnlyList<string> negativeSeedTrackIds,
            int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecommendationSourceItem>>([]);
        public Task<AudioMuseMapPage> GetMapAsync(IntelligenceScope scope, ProviderPageRequest page,
            CancellationToken cancellationToken) => Task.FromResult(new AudioMuseMapPage([], "none"));
    }
    private sealed class FakeReadiness : IRecommendationProviderStatusService { public RecommendationProviderReadinessState LastFmState { get; set; } = RecommendationProviderReadinessState.Ready; public Task<IReadOnlyList<RecommendationProviderReadiness>> ListAsync(IntelligenceScope scope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RecommendationProviderReadiness>>([new("lastfm", LastFmState, LastFmState == RecommendationProviderReadinessState.Ready ? "fixture_ready" : "account_unauthorized"), new("musicbrainz-local", RecommendationProviderReadinessState.Ready, "fixture_ready"), new("audiomuse-ai", RecommendationProviderReadinessState.Ready, "fixture_ready")]); }
}
