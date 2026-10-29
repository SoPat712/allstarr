using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Downloads;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AdminUiActivityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activity_QueriesJobsAuditsDownloadsAndMatchesForTheViewer(bool administrator)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var expected = new List<string>();
        var foreign = new List<string>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var user in new[] { owner, other })
            {
                var at = user == owner ? now : now.AddMinutes(1);
                var ids = user == owner ? expected : foreign;
                db.Users.Add(new UserRecord
                {
                    Id = user,
                    DisplayName = user == owner ? "Owner" : "Other",
                    Enabled = true,
                    BackendType = "jellyfin",
                    BackendInstanceId = "backend",
                    BackendPrincipalId = user.ToString("N")
                });
                var account = new ProviderAccountRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    ProviderId = "deezer",
                    DisplayName = "Fixture",
                    Enabled = true
                };
                db.ProviderAccounts.Add(account);
                var job = new DurableJobRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    ScopeKey = user.ToString("N"),
                    IdempotencyKey = "activity",
                    RequestFingerprint = new string('a', 64),
                    Type = "fixture",
                    CorrelationId = "same-correlation",
                    State = DurableJobState.Succeeded,
                    AvailableAt = at,
                    CreatedAt = at,
                    UpdatedAt = at,
                    MaxAttempts = 3,
                    MaxDeferrals = 3
                };
                db.Jobs.Add(job);
                ids.Add(job.Id.ToString("N"));
                var audit = new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = user,
                    Category = "playlist",
                    Action = "updated",
                    Outcome = "ok",
                    CorrelationId = job.CorrelationId,
                    CreatedAt = at
                };
                db.AuditEvents.Add(audit);
                ids.Add(audit.Id.ToString("N"));
                var workspace = new ProviderDownloadWorkspaceEntity
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = user.ToString("N"),
                    OwnerUserId = user,
                    DurableJobId = job.Id,
                    ProviderId = "deezer",
                    ProviderAccountId = account.Id,
                    IdempotencyKey = "activity",
                    CreatedAt = at
                };
                db.ProviderDownloadWorkspaces.Add(workspace);
                var download = new ProviderDownloadArtifactEntity
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceRecordId = workspace.Id,
                    WorkspaceId = workspace.WorkspaceId,
                    OwnerUserId = user,
                    DurableJobId = job.Id,
                    ProviderId = "deezer",
                    ProviderAccountId = account.Id,
                    SourceExternalTrackId = "source-track",
                    SourceAccountRevision = account.Revision,
                    ProviderArtifactId = "track",
                    RelativePath = "track.flac",
                    ContentSha256 = new string('b', 64),
                    Length = 128,
                    State = ProviderDownloadArtifactState.Verified,
                    CreatedAt = at,
                    VerifiedAt = at
                };
                db.ProviderDownloadArtifacts.Add(download);
                ids.Add(download.Id.ToString("N"));
                db.DownloadedSongMappings.Add(new DownloadedSongMappingEntity
                {
                    Id = Guid.CreateVersion7(),
                    ScopeKey = new DownloadedSongMappingScope(account.Id, ProviderAudioQuality.Any).Key,
                    ProviderAccountId = account.Id,
                    AudioQuality = ProviderAudioQuality.Any,
                    ProviderId = "deezer",
                    ExternalId = "source-track",
                    LocalPath = "track.flac",
                    Title = "Cached Song",
                    Artist = "Cached Artist",
                    Album = "Cached Album",
                    DownloadedAt = at,
                    Revision = 1
                });
                var snapshot = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    ProviderAccountId = account.Id,
                    ProviderId = "deezer",
                    ResourceKind = "track",
                    BackendInstanceId = "backend",
                    BackendPrincipalId = user.ToString("N"),
                    Protocol = "jellyfin",
                    ExternalIdHash = new string('c', 64),
                    SnapshotVersion = 1,
                    PayloadSha256 = new string('d', 64),
                    PayloadJson = "{}",
                    CorrelationId = "same-correlation",
                    RetrievedAt = at
                };
                db.ExternalMetadataSnapshots.Add(snapshot);
                var match = new TrackMatchRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    ExternalSnapshotId = snapshot.Id,
                    State = TrackMatchState.Unresolved,
                    DecisionVersion = 1,
                    SourceSnapshotVersion = 1,
                    PolicyVersion = "test",
                    DecidedAt = at,
                    CorrelationId = "same-correlation"
                };
                db.TrackMatches.Add(match);
                ids.Add(match.Id.ToString("N"));
            }
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory)
            .BuildServiceProvider();
        var controller = new AdminUiController(new ConfigurationBuilder().Build(),
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, null!,
            new ProviderAccountOptions(), matches)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } }
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = "owner",
            UserName = "Owner",
            IsAdministrator = administrator,
            AllstarrUserId = owner,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity()).Value));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var actual = items.Select(item => item.GetProperty("Id").GetString()).ToArray();
        Assert.Equal(4, actual.Length);
        Assert.All(expected, id => Assert.Contains(id, actual));
        Assert.All(foreign, id => Assert.DoesNotContain(id, actual));
        Assert.All(items, item => Assert.Equal("Owner", item.GetProperty("ActorDisplayName").GetString()));
        var cached = Assert.Single(items, item => item.GetProperty("Kind").GetString() == "caching");
        Assert.Equal("Track cached", cached.GetProperty("Label").GetString());
        Assert.Contains("verified", cached.GetProperty("Detail").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Cached Song", cached.GetProperty("SourceTitle").GetString());
        Assert.Equal("Cached Artist", cached.GetProperty("SourceArtist").GetString());
        Assert.Contains(cached.GetProperty("JobId").GetString(), expected);
        if (administrator)
        {
            using var household = JsonDocument.Parse(JsonSerializer.Serialize(
                Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(scope: "everyone")).Value));
            var all = household.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(8, all.Length);
            Assert.All(all, item => Assert.Contains(item.GetProperty("ActorDisplayName").GetString(), new[] { "Owner", "Other" }));
            Assert.All(foreign, id => Assert.Contains(all, item => item.GetProperty("Id").GetString() == id));
        }
        else Assert.IsType<ForbidResult>(await controller.GetDashboardActivity(scope: "everyone"));
        Assert.IsType<BadRequestObjectResult>(await controller.GetDashboardActivity(scope: "invalid"));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var scan = await db.Jobs.FirstAsync(x => x.OwnerUserId == owner);
            scan.Type = "library.index";
            db.AuditEvents.Add(new() { Id = Guid.CreateVersion7(), ActorUserId = owner, Category = "library-index", Action = "scan.completed", Outcome = "succeeded", CreatedAt = now.AddMinutes(2) });
            await db.SaveChangesAsync();
        }
        using (var scanJson = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity()).Value)))
        {
            var scans = scanJson.RootElement.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("Action").GetString() == "library.index" || item.GetProperty("Label").GetString() == "Scan Completed").ToArray();
            Assert.Equal(2, scans.Length);
            Assert.All(scans, item => Assert.Equal("Jellyfin library", item.GetProperty("Source").GetString()));
            Assert.All(scans, item => Assert.Equal("library", item.GetProperty("ProviderId").GetString()));
        }
        var page = await matches.GetActivityDataAsync(new(owner, false), limit: 1);
        Assert.Equal(owner, Assert.Single(page.Decisions).OwnerUserId);
        Assert.Equal(owner, Assert.Single(page.Snapshots).OwnerUserId);
        controller.HttpContext.Items.Clear();
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetDashboardActivity()).StatusCode);
    }

    [Fact]
    public async Task Activity_PresentsJobAndHealthOutcomesWithExactOwnershipAndRecoveryContext()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var healthJobId = Guid.CreateVersion7();
        var collidingHealthJobId = Guid.CreateVersion7();
        var backupJobId = Guid.CreateVersion7();
        var libraryJobId = Guid.CreateVersion7();
        var favoriteJobId = Guid.CreateVersion7();
        var latestFavoriteJobId = Guid.CreateVersion7();
        var wrongItemFavoriteJobId = Guid.CreateVersion7();
        var mismatchedFavoriteJobId = Guid.CreateVersion7();
        var foreignFavoriteJobId = Guid.CreateVersion7();
        var ownFavoriteEventId = Guid.CreateVersion7();
        var latestFavoriteEventId = Guid.CreateVersion7();
        var wrongItemFavoriteEventId = Guid.CreateVersion7();
        var foreignFavoriteEventId = Guid.CreateVersion7();
        var healthRunId = healthJobId;

        DurableJobRecord Job(Guid id, Guid userId, string type, string payload, DurableJobState state = DurableJobState.Succeeded) => new()
        {
            Id = id,
            OwnerUserId = userId,
            ScopeKey = userId.ToString("N"),
            IdempotencyKey = id.ToString("N"),
            RequestFingerprint = new string('a', 64),
            CorrelationId = $"activity:{id:N}",
            Type = type,
            PayloadJson = payload,
            State = state,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            MaxAttempts = 3,
            MaxDeferrals = 3
        };

        var ownFavoriteItemId = "ext-deezer-song-loved-track";
        var wrongItemEventItemId = "ext-deezer-song-event-item";
        var wrongItemStateItemId = "ext-deezer-song-state-item";
        var foreignFavoriteItemId = "ext-deezer-song-private-track";
        var healthPayload = JsonSerializer.Serialize(new { scheduleId = Guid.CreateVersion7(), configurationRevision = 7 });
        var backupPayload = JsonSerializer.Serialize(new { artifactId = Guid.CreateVersion7() });
        var libraryPayload = JsonSerializer.Serialize(new
        {
            backendLibraryId = "music",
            backendInstanceId = "backend",
            backendPrincipalId = owner.ToString("N")
        });
        var ownFavoritePayload = JsonSerializer.Serialize(new { eventId = ownFavoriteEventId });
        var mismatchedFavoritePayload = JsonSerializer.Serialize(new { eventId = foreignFavoriteEventId });
        var foreignFavoritePayload = JsonSerializer.Serialize(new { eventId = foreignFavoriteEventId });

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.AddRange(
                new UserRecord
                {
                    Id = owner,
                    DisplayName = "Owner",
                    Enabled = true,
                    IsAdmin = true,
                    BackendType = "jellyfin",
                    BackendInstanceId = "backend",
                    BackendPrincipalId = "owner"
                },
                new UserRecord
                {
                    Id = foreign,
                    DisplayName = "Other",
                    Enabled = true,
                    BackendType = "jellyfin",
                    BackendInstanceId = "backend",
                    BackendPrincipalId = "other"
                });
            db.Jobs.AddRange(
                Job(healthJobId, owner, "health.smoke", healthPayload, DurableJobState.Failed),
                Job(collidingHealthJobId, owner, "health.smoke", healthPayload, DurableJobState.Failed),
                Job(backupJobId, owner, "storage.backup", backupPayload),
                Job(libraryJobId, owner, "library.index", libraryPayload),
                Job(favoriteJobId, owner, "favorite.process", ownFavoritePayload),
                Job(latestFavoriteJobId, owner, "favorite.process", JsonSerializer.Serialize(new { eventId = latestFavoriteEventId })),
                Job(wrongItemFavoriteJobId, owner, "favorite.process", JsonSerializer.Serialize(new { eventId = wrongItemFavoriteEventId })),
                Job(mismatchedFavoriteJobId, owner, "favorite.process", mismatchedFavoritePayload),
                Job(foreignFavoriteJobId, foreign, "favorite.process", foreignFavoritePayload));

            var healthJob = db.Jobs.Local.Single(item => item.Id == healthJobId);
            var collidingHealthJob = db.Jobs.Local.Single(item => item.Id == collidingHealthJobId);
            collidingHealthJob.CorrelationId = healthJob.CorrelationId;
            healthJob.LastAttemptOutcome = "failed";
            healthJob.LastErrorCode = "health_search_failed";
            healthJob.LastErrorMessage = "http_401 token=fixture-private-value https://fixture.invalid/check?secret=fixture-query";

            db.Set<FavoriteEventRecord>().AddRange(
                FavoriteEvent(ownFavoriteEventId, owner, favoriteJobId, ownFavoriteItemId),
                FavoriteEvent(latestFavoriteEventId, owner, latestFavoriteJobId, ownFavoriteItemId),
                FavoriteEvent(wrongItemFavoriteEventId, owner, wrongItemFavoriteJobId, wrongItemEventItemId),
                FavoriteEvent(foreignFavoriteEventId, foreign, foreignFavoriteJobId, foreignFavoriteItemId));
            db.Set<FavoriteStateRecord>().AddRange(
                FavoriteState(owner, latestFavoriteEventId, ownFavoriteItemId,
                    new FavoriteDisplaySnapshot(1, ownFavoriteItemId, "song", "Loved Song", ["Loved Artist"])),
                FavoriteState(owner, wrongItemFavoriteEventId, wrongItemStateItemId,
                    new FavoriteDisplaySnapshot(1, wrongItemStateItemId, "song", "Wrong Item", ["Wrong Artist"])),
                FavoriteState(foreign, foreignFavoriteEventId, foreignFavoriteItemId,
                    new FavoriteDisplaySnapshot(1, foreignFavoriteItemId, "song", "Private Foreign Song", ["Private Artist"])));

            db.HealthSmokeRuns.Add(new HealthSmokeRunRecord
            {
                Id = healthRunId,
                OwnerUserId = owner,
                BindingJson = "{}",
                ArtifactsJson = "{}",
                CompletedJson = JsonSerializer.Serialize(new
                {
                    protocol = "Jellyfin",
                    startedAt = now.AddSeconds(-20),
                    completedAt = now,
                    configurationRevision = 7,
                    steps = new[]
                    {
                        new { name = "backend", status = "passed", latencyMilliseconds = 20d, reason = (string?)null },
                        new { name = "search", status = "failed", latencyMilliseconds = 15000d, reason = (string?)"http_401" },
                        new { name = "lyrics", status = "skipped", latencyMilliseconds = 0d, reason = (string?)"not_available" }
                    },
                    openAlertSteps = new[] { "search" },
                    alertStatus = "pending",
                    mode = "server-api-key"
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                AlertStatus = "pending",
                CreatedAt = now.AddSeconds(-20),
                CompletedAt = now
            });
            db.AuditEvents.AddRange(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "health-check",
                Action = "completed",
                Outcome = "failed",
                CorrelationId = "health-audit-correlation",
                SubjectType = "health-run",
                SubjectId = healthRunId.ToString("N"),
                DetailsJson = "{}",
                CreatedAt = now
            }, new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "health-check",
                Action = "completed",
                Outcome = "failed",
                CorrelationId = healthJob.CorrelationId,
                SubjectType = "health-run",
                SubjectId = healthRunId.ToString("N"),
                DetailsJson = "{}",
                CreatedAt = now.AddSeconds(1)
            });
            await db.SaveChangesAsync();
        }

        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var controller = Controller(factory, matches, owner);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity()).Value));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();

        var health = FindJobOrActivity(items, healthJobId);
        var healthDetail = RequiredText(health, "Detail", "detail");
        Assert.Contains("failed", healthDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("passed", healthDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not checked", healthDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("search", healthDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authentication", healthDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server-api-key", healthDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http_401", healthDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Setup", RequiredText(health, "Recovery", "recovery"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"#/activity?view=jobs&job={healthJobId:N}", RequiredText(health, "ActionHref", "actionHref"));
        Assert.False(string.IsNullOrWhiteSpace(RequiredText(health, "ActionLabel", "actionLabel")));
        Assert.DoesNotContain("Loved Song", RequiredText(health, "Detail"), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-private-value", JsonSerializer.Serialize(health), StringComparison.Ordinal);
        var healthAudits = items.Where(item => Text(item, "Kind") == "health").ToArray();
        Assert.NotEmpty(healthAudits);
        Assert.All(healthAudits, item => Assert.Equal(healthJobId, JobId(item)));
        var healthAudit = healthAudits[0];
        var healthTechnical = JsonSerializer.Serialize(healthAudit);
        Assert.Contains("server-api-key", healthTechnical, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http_401", healthTechnical, StringComparison.OrdinalIgnoreCase);
        var colliding = FindJobOrActivity(items, collidingHealthJobId);
        Assert.DoesNotContain("passed", JsonSerializer.Serialize(colliding), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server-api-key", JsonSerializer.Serialize(colliding), StringComparison.OrdinalIgnoreCase);

        foreach (var incomplete in new[] { "{\"steps\":[]}", "not-json" })
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                var run = await db.HealthSmokeRuns.SingleAsync(item => item.Id == healthRunId);
                run.CompletedJson = incomplete;
                await db.SaveChangesAsync();
            }
            using var incompleteJson = JsonDocument.Parse(JsonSerializer.Serialize(
                Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity()).Value));
            var incompleteHealth = FindJobOrActivity(
                incompleteJson.RootElement.GetProperty("items").EnumerateArray(), healthJobId);
            var incompleteDetail = RequiredText(incompleteHealth, "Detail", "detail");
            Assert.DoesNotContain("0 passed", incompleteDetail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0 failed", incompleteDetail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("available", incompleteDetail, StringComparison.OrdinalIgnoreCase);
        }

        var backup = FindJobOrActivity(items, backupJobId);
        Assert.Contains("backup", RequiredText(backup, "Label"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage.backup", RequiredText(backup, "Label"), StringComparison.OrdinalIgnoreCase);
        var library = FindJobOrActivity(items, libraryJobId);
        Assert.Contains("library", RequiredText(library, "Label"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("library.index", RequiredText(library, "Label"), StringComparison.OrdinalIgnoreCase);

        var favorite = FindJobOrActivity(items, favoriteJobId);
        Assert.Contains("Loved Song", RequiredText(favorite, "Detail"), StringComparison.Ordinal);
        Assert.Contains("Loved Artist", RequiredText(favorite, "Detail"), StringComparison.Ordinal);
        var latestFavorite = FindJobOrActivity(items, latestFavoriteJobId);
        Assert.Contains("Loved Song", RequiredText(latestFavorite, "Detail"), StringComparison.Ordinal);
        var wrongItemFavorite = FindJobOrActivity(items, wrongItemFavoriteJobId);
        Assert.DoesNotContain("Wrong Item", JsonSerializer.Serialize(wrongItemFavorite), StringComparison.Ordinal);
        var mismatched = FindJobOrActivity(items, mismatchedFavoriteJobId);
        Assert.DoesNotContain("Private Foreign Song", JsonSerializer.Serialize(mismatched), StringComparison.Ordinal);
        Assert.DoesNotContain("Private Artist", JsonSerializer.Serialize(mismatched), StringComparison.Ordinal);
        Assert.DoesNotContain(items, item => JobId(item) == foreignFavoriteJobId);
    }

    [Fact]
    public async Task ActivityNamesMaintenanceAndDoesNotLinkUnrelatedOrNonexistentHealthJobs()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var foreignJob = Guid.CreateVersion7();
        var configured = Guid.CreateVersion7();
        var mismatch = Guid.CreateVersion7();
        var maintenance = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.AddRange(new UserRecord { Id = owner, DisplayName = "Owner", Enabled = true, IsAdmin = true, BackendType = "jellyfin", BackendInstanceId = "backend", BackendPrincipalId = "owner" },
                new UserRecord { Id = other, DisplayName = "Other", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "backend", BackendPrincipalId = "other" });
            db.Jobs.Add(new() { Id = foreignJob, OwnerUserId = other, Type = "health.smoke", ScopeKey = other.ToString("N"), IdempotencyKey = "foreign", RequestFingerprint = new string('a', 64), State = DurableJobState.Succeeded, UpdatedAt = now });
            db.AuditEvents.AddRange(
                new() { Id = configured, ActorUserId = owner, Category = "health-check", Action = "configured", Outcome = "success", SubjectType = "health-run", SubjectId = Guid.CreateVersion7().ToString("N"), CreatedAt = now },
                new() { Id = mismatch, ActorUserId = owner, Category = "health-check", Action = "completed", Outcome = "failed", SubjectType = "health-run", SubjectId = foreignJob.ToString("N"), CreatedAt = now },
                new() { Id = maintenance, ActorUserId = owner, Category = "background-task", Action = "run-completed", Outcome = "failed", SubjectType = "background-task", SubjectId = "cache-cleanup", DetailsJson = "{\"name\":\"Clean expired cache\",\"nextRunAt\":\"2026-10-28T10:00:00Z\"}", CreatedAt = now });
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var controller = Controller(factory, matches, owner);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(scope: "everyone")).Value));
        var items = result.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var configuration = Assert.Single(items, item => item.GetProperty("Id").GetString() == configured.ToString("N"));
        Assert.Null(JobId(configuration));
        Assert.Equal("#/settings/health", RequiredText(configuration, "ActionHref", "actionHref"));
        var wrong = Assert.Single(items, item => item.GetProperty("Id").GetString() == mismatch.ToString("N"));
        Assert.Null(JobId(wrong));
        var task = Assert.Single(items, item => item.GetProperty("Id").GetString() == maintenance.ToString("N"));
        Assert.Contains("Clean expired cache", RequiredText(task, "Label"));
        Assert.Equal("#/activity?view=tasks", RequiredText(task, "ActionHref", "actionHref"));
        Assert.Contains("failed", RequiredText(task, "Detail"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Activity_RedactsLegacyExtensionLogMessagesWithoutRewritingStorage()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var userId = Guid.CreateVersion7();
        var packageId = Guid.CreateVersion7();
        var logId = Guid.CreateVersion7();
        const string rawMessage =
            "Provider failed status=403 reason=network operation=searchTracks for https://fixture-user:fixture-password@provider.invalid/media?opaque=fixture-query&signature=fixture-signature#fixture-fragment response=upstream_failure";
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = userId,
                DisplayName = "Owner",
                Enabled = true,
                IsAdmin = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "owner"
            });
            db.ExtensionPackages.Add(new ExtensionPackageRecord
            {
                Id = packageId,
                ExtensionId = "fixture-provider",
                DisplayName = "Fixture",
                Version = "1.0.0",
                SdkVersion = "1",
                Sha256 = new string('a', 64),
                ContentSha256 = new string('b', 64),
                PackagePath = "/tmp/fixture-provider",
                ManifestJson = "{}",
                State = ExtensionPackageState.Active,
                StagedAt = DateTimeOffset.UtcNow
            });
            db.ExtensionLogs.Add(new ExtensionLogRecord
            {
                Id = logId,
                ExtensionPackageId = packageId,
                ExtensionId = "fixture-provider",
                Level = "error",
                EventCode = "runtime.log",
                Message = rawMessage,
                CorrelationId = "legacy",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        await using var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory)
            .BuildServiceProvider();
        var controller = new AdminUiController(new ConfigurationBuilder().Build(),
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, null!,
            new ProviderAccountOptions(), matches)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services }
            }
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = "owner",
            UserName = "Owner",
            IsAdministrator = true,
            AllstarrUserId = userId,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(scope: "everyone")).Value));
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        var message = item.GetProperty("Detail").GetString()!;
        Assert.Contains("403", message, StringComparison.Ordinal);
        Assert.Contains("network", message, StringComparison.Ordinal);
        Assert.Contains("provider.invalid", message, StringComparison.Ordinal);
        Assert.Contains("/media", message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-user", message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-query", message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-signature", message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-fragment", message, StringComparison.Ordinal);

        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal(rawMessage, (await check.ExtensionLogs.SingleAsync(item => item.Id == logId)).Message);
    }

    private static FavoriteEventRecord FavoriteEvent(Guid id, Guid owner, Guid jobId, string itemId) => new()
    {
        Id = id,
        OwnerUserId = owner,
        Protocol = "jellyfin",
        BackendInstanceId = "backend",
        BackendPrincipalId = owner.ToString("N"),
        ItemId = itemId,
        Operation = FavoriteOperation.Favorite,
        SourceRevision = "fixture",
        EventKey = id.ToString("N"),
        CorrelationId = $"favorite:{id:N}",
        PolicySnapshotJson = "{}",
        JobId = jobId,
        State = FavoriteEventState.Succeeded,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        Revision = 1
    };

    private static FavoriteStateRecord FavoriteState(Guid owner, Guid eventId, string itemId, FavoriteDisplaySnapshot display) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = owner,
        Protocol = "jellyfin",
        BackendInstanceId = "backend",
        ItemId = itemId,
        DisplayMetadataJson = JsonSerializer.Serialize(display, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        IsFavorite = true,
        LastEventId = eventId,
        UpdatedAt = DateTimeOffset.UtcNow,
        Revision = 1
    };

    private static AdminUiController Controller(Factory factory, TrackMatchCommandService matches, Guid userId)
    {
        var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory)
            .BuildServiceProvider();
        var controller = new AdminUiController(new ConfigurationBuilder().Build(),
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, null!,
            new ProviderAccountOptions(), matches)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services }
            }
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = userId.ToString("N"),
            UserName = "Owner",
            IsAdministrator = true,
            AllstarrUserId = userId,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return controller;
    }

    [Fact]
    public async Task PlaylistActivityRetainsHistoricalRunsWithoutUsingAnotherOwnersMetadata()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var linkId = Guid.CreateVersion7();
        var ownRun = Guid.CreateVersion7();
        var otherRun = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var user in new[] { owner, other }) db.Users.Add(new() { Id = user, DisplayName = "Listener", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = user.ToString("N") });
            db.PlaylistLinks.Add(new() { Id = linkId, OwnerUserId = owner, SourceProviderId = "fixture", SourcePlaylistId = "mix", SourcePlaylistIdHash = new string('a', 64), TargetProtocol = "jellyfin", TargetBackendInstanceId = "fixture", RuleVersion = "rules", PolicyVersion = "policy" });
            var ownSnapshot = Guid.CreateVersion7();
            var foreignSnapshot = Guid.CreateVersion7();
            db.PlaylistSourceSnapshots.AddRange(
                new() { Id = ownSnapshot, OwnerUserId = owner, PlaylistLinkId = linkId, SnapshotVersion = 1, Name = "Owner mix", ArtworkReferenceKey = "art", PayloadSha256 = new string('b', 64), RetrievedAt = now, PublishedAt = now },
                new() { Id = foreignSnapshot, OwnerUserId = other, PlaylistLinkId = linkId, SnapshotVersion = 2, Name = "Foreign mix", ArtworkReferenceKey = "private-art", PayloadSha256 = new string('c', 64), RetrievedAt = now, PublishedAt = now });
            db.PlaylistSyncRuns.AddRange(
                new() { Id = ownRun, OwnerUserId = owner, PlaylistLinkId = linkId, PlaylistSourceSnapshotId = ownSnapshot, IdempotencyKey = "own", Generation = 1, State = PlaylistSyncState.Succeeded, StartedAt = now, CompletedAt = now },
                new() { Id = otherRun, OwnerUserId = other, PlaylistLinkId = linkId, PlaylistSourceSnapshotId = foreignSnapshot, IdempotencyKey = "other", Generation = 1, State = PlaylistSyncState.Failed, StartedAt = now });
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var controller = Controller(factory, matches, owner);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(scope: "everyone")).Value));
        var rows = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var own = Assert.Single(rows, row => Text(row, "Id") == ownRun.ToString("N"));
        Assert.Equal("Owner mix", Text(own, "PlaylistName"));
        Assert.NotNull(Text(own, "ArtworkUrl"));
        var foreign = Assert.Single(rows, row => Text(row, "Id") == otherRun.ToString("N"));
        Assert.Equal("Playlist", Text(foreign, "PlaylistName"));
        Assert.Null(Text(foreign, "ArtworkUrl"));
    }

    [Fact]
    public async Task AttemptHistoryUsesItsRealJobContextWithoutRewritingAnEarlierFailure()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        Guid auditId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            var job = new DurableJobRecord { Id = jobId, OwnerUserId = owner, ScopeKey = owner.ToString("N"), IdempotencyKey = "attempt", RequestFingerprint = new string('a', 64), Type = "library.index", CorrelationId = "attempt", State = DurableJobState.Failed, AvailableAt = now, CreatedAt = now, UpdatedAt = now, MaxAttempts = 3, MaxDeferrals = 3, AttemptCount = 1, LastAttemptOutcome = "failed", LastAttemptStartedAt = now.AddSeconds(-1), LastAttemptCompletedAt = now, LastErrorMessage = "Server unavailable token=fixture-private-attempt" };
            db.Jobs.Add(job);
            DurableJobHistory.Append(db, job, now);
            auditId = Assert.Single(db.AuditEvents.Local).Id;
            job.State = DurableJobState.Succeeded; job.LastAttemptOutcome = "succeeded"; job.UpdatedAt = now.AddSeconds(1); job.LastErrorMessage = null;
            db.AuditEvents.Add(new() { Id = Guid.CreateVersion7(), ActorUserId = owner, Category = "library-index", Action = "scan.completed", Outcome = "succeeded", CorrelationId = "attempt", CreatedAt = now, DetailsJson = JsonSerializer.Serialize(new { Seen = 12, Indexed = 9, SkippedPathless = 2, SkippedMalformed = 1, Pages = 2 }) });
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Controller(factory, matches, owner).GetDashboardActivity()).Value));
        var rows = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var attempt = Assert.Single(rows, row => Text(row, "Id") == auditId.ToString("N"));
        Assert.Equal("Refresh library index", Text(attempt, "Label"));
        Assert.Equal("Jellyfin library", Text(attempt, "Source"));
        Assert.Equal("job", Text(attempt, "Kind"));
        Assert.Equal(jobId, JobId(attempt));
        Assert.Contains("Library", RequiredText(attempt, "Detail"));
        Assert.Contains("failed", RequiredText(attempt, "Detail"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed", RequiredText(attempt, "Detail"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-private-attempt", JsonSerializer.Serialize(attempt));
        var scan = Assert.Single(rows, row => Text(row, "Action") == "scan.completed");
        Assert.Contains("9 indexed", RequiredText(scan, "Detail"));
        Assert.Contains("12 seen", RequiredText(scan, "Detail"));
        Assert.Contains("3 skipped", RequiredText(scan, "Detail"));
    }

    [Theory]
    [InlineData("recording.tags.refresh")]
    [InlineData("recording.musicbrainz.enrich")]
    [InlineData("recording.odesli.enrich")]
    [InlineData("listening.musicbrainz.enrich")]
    [InlineData("favorite.display-backfill")]
    [InlineData("health.cleanup")]
    [InlineData("recording.kept-cleanup")]
    public async Task Activity_HidesRoutineWorkAndItsNonfailureAuditsWithoutRemovingRecords(string type)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var states = new[]
        {
            (DurableJobState.Pending, (string?)null),
            (DurableJobState.Running, (string?)null),
            (DurableJobState.Succeeded, "succeeded"),
            (DurableJobState.Cancelled, "cancelled"),
            (DurableJobState.RetryScheduled, "deferred")
        };
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            foreach (var (state, outcome) in states)
            {
                var job = HistoryJob(owner, type, state, outcome, now);
                db.Jobs.Add(job);
                if (outcome != null) DurableJobHistory.Append(db, job, now);
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "job-progress",
                    Action = "refresh",
                    SubjectType = "job",
                    SubjectId = job.Id.ToString("N"),
                    Outcome = "running",
                    CreatedAt = now,
                    DetailsJson = JsonSerializer.Serialize(new { jobId = job.Id, completed = 1, total = 2 })
                });
            }
            var completed = db.Jobs.Local.First(item => item.State == DurableJobState.Succeeded);
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "job-attempt",
                Action = "attempt-completed",
                Outcome = "success",
                CreatedAt = now,
                DetailsJson = JsonSerializer.Serialize(new { jobId = completed.Id })
            });
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var controller = Controller(factory, matches, owner);
        foreach (var scope in new[] { "mine", "everyone" })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(scope: scope)).Value));
            Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        }
        await using var retained = await factory.CreateDbContextAsync();
        Assert.Equal(states.Length, await retained.Jobs.CountAsync());
        Assert.Equal(9, await retained.AuditEvents.CountAsync());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("retry_scheduled")]
    [InlineData("interrupted")]
    public async Task Activity_RetainsEarlierRoutineFailuresAfterTheCurrentJobRecovers(string outcome)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        Guid jobId;
        Guid auditId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            var job = HistoryJob(owner, "recording.tags.refresh", DurableJobState.RetryScheduled, outcome, now);
            jobId = job.Id;
            db.Jobs.Add(job);
            DurableJobHistory.Append(db, job, now);
            auditId = Assert.Single(db.AuditEvents.Local).Id;
            job.State = DurableJobState.Succeeded;
            job.LastAttemptOutcome = "succeeded";
            job.UpdatedAt = now.AddSeconds(1);
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Controller(factory, matches, owner).GetDashboardActivity()).Value));
        var row = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(auditId.ToString("N"), Text(row, "Id"));
        Assert.Equal(jobId, JobId(row));
        Assert.Equal("Song details updated", Text(row, "Label"));
        Assert.Equal(outcome, Text(row, "State"));
        Assert.Contains(outcome.Replace('_', ' '), RequiredText(row, "Detail"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DurableJobState.Failed, "failed")]
    [InlineData(DurableJobState.RetryScheduled, "retry_scheduled")]
    [InlineData(DurableJobState.RetryScheduled, null)]
    [InlineData(DurableJobState.Pending, "interrupted")]
    [InlineData(DurableJobState.Running, "retry_scheduled")]
    public async Task Activity_RetainsCurrentRoutineFailures(DurableJobState state, string? outcome)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        Guid jobId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            var job = HistoryJob(owner, "recording.tags.refresh", state, outcome, DateTimeOffset.UtcNow);
            jobId = job.Id;
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Controller(factory, matches, owner).GetDashboardActivity()).Value));
        var row = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(jobId, JobId(row));
        Assert.Equal("Song details updated", Text(row, "Label"));
    }

    [Fact]
    public async Task Activity_RoutineWorkCannotFillTheScanBeforeAnOlderVisibleEvent()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        Guid visibleId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            for (var index = 0; index < 8; index++)
            {
                var job = HistoryJob(owner, "recording.tags.refresh", DurableJobState.Succeeded, "succeeded", now.AddMinutes(index));
                db.Jobs.Add(job);
                DurableJobHistory.Append(db, job, job.UpdatedAt);
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "job-progress",
                    Action = "refresh",
                    Outcome = "running",
                    SubjectType = "job",
                    SubjectId = job.Id.ToString("N"),
                    CreatedAt = job.UpdatedAt
                });
            }
            var visible = HistoryJob(owner, "favorite.process", DurableJobState.Succeeded, "succeeded", now.AddMinutes(-1));
            visibleId = visible.Id;
            db.Jobs.Add(visible);
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Controller(factory, matches, owner).GetDashboardActivity(limit: 1)).Value));
        Assert.Equal(visibleId, JobId(Assert.Single(json.RootElement.GetProperty("items").EnumerateArray())));
        Assert.False(json.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Activity_RoutinePolicyRequiresTheExactJobOwnerAndKeepsRequestedWork()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        Guid requestedId;
        Guid mismatchedAuditId;
        Guid unboundAuditId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var user in new[] { owner, other })
                db.Users.Add(new() { Id = user, DisplayName = "Listener", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = user.ToString("N") });
            var routine = HistoryJob(other, "recording.tags.refresh", DurableJobState.Succeeded, "succeeded", now);
            var requested = HistoryJob(owner, "recording.keep", DurableJobState.Succeeded, "succeeded", now);
            requestedId = requested.Id;
            requested.CorrelationId = routine.CorrelationId;
            db.Jobs.AddRange(routine, requested);
            var mismatched = new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "job-progress",
                Action = "copy",
                Outcome = "running",
                SubjectType = "job",
                SubjectId = routine.Id.ToString("D"),
                CorrelationId = routine.CorrelationId,
                CreatedAt = now
            };
            var unbound = new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "job-attempt",
                Action = "attempt-completed",
                Outcome = "succeeded",
                CorrelationId = routine.CorrelationId,
                DetailsJson = JsonSerializer.Serialize(new { jobType = routine.Type }),
                CreatedAt = now
            };
            mismatchedAuditId = mismatched.Id;
            unboundAuditId = unbound.Id;
            db.AuditEvents.AddRange(mismatched, unbound);
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Controller(factory, matches, owner).GetDashboardActivity()).Value));
        var rows = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Single(rows, row => JobId(row) == requestedId);
        Assert.Null(JobId(Assert.Single(rows, row => Text(row, "Id") == mismatchedAuditId.ToString("N"))));
        Assert.Null(JobId(Assert.Single(rows, row => Text(row, "Id") == unboundAuditId.ToString("N"))));
    }

    private static DurableJobRecord HistoryJob(Guid owner, string type, DurableJobState state, string? outcome, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7();
        return new()
        {
            Id = id,
            OwnerUserId = owner,
            ScopeKey = owner.ToString("N"),
            IdempotencyKey = id.ToString("N"),
            RequestFingerprint = new string('a', 64),
            Type = type,
            CorrelationId = id.ToString("N"),
            State = state,
            AvailableAt = at,
            CreatedAt = at,
            UpdatedAt = at,
            MaxAttempts = 3,
            MaxDeferrals = 3,
            AttemptCount = 1,
            LastAttemptOutcome = outcome,
            LastAttemptStartedAt = at.AddSeconds(-1),
            LastAttemptCompletedAt = at
        };
    }

    private static JsonElement FindJobOrActivity(IEnumerable<JsonElement> items, Guid jobId)
    {
        foreach (var item in items)
            if (JobId(item) == jobId) return item;
        throw new Xunit.Sdk.XunitException($"Activity did not contain JobId {jobId:N}");
    }

    private static Guid? JobId(JsonElement item)
    {
        foreach (var name in new[] { "JobId", "jobId" })
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RequiredText(JsonElement row, params string[] names)
    {
        foreach (var name in names)
            if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
        throw new Xunit.Sdk.XunitException($"Expected one of [{string.Join(", ", names)}] in {row}");
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
