using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
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
                .Where(item => item.GetProperty("Label").GetString() is "library.index" or "Scan Completed").ToArray();
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

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
