using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Routing;
using Xunit.Abstractions;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    private readonly ITestOutputHelper output;

    public PersonalMixServiceTests(ITestOutputHelper output) => this.output = output;

    private async Task<PlaylistLinkRecord> ExternalLink(string source = "lastfm", string selection = "loved",
        Guid? listener = null, bool schedule = true, string? artwork = null)
    {
        var user = listener ?? owner;
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult(new BackendLibraryAccessContext(Context(id), new(true, []))));
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        var account = new ProviderAccountRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user,
            ProviderId = source,
            Enabled = true,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
        var link = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user,
            ProviderAccountId = account.Id,
            SourceProviderId = source,
            SourcePlaylistId = selection,
            SourcePlaylistIdHash = new string('a', 64),
            TargetProtocol = protocol,
            TargetBackendInstanceId = "main",
            Mode = PlaylistLinkMode.Virtual,
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            RuleVersion = "1",
            PolicyVersion = "1",
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
        await using var db = factory.CreateDbContext();
        db.ProviderAccounts.Add(account);
        if (schedule)
        {
            var row = new JobScheduleRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user,
                JobType = DurableScheduleEngine.PlaylistSyncJobType,
                Enabled = true,
                CronExpression = "0 4 * * *",
                TimeZoneId = "UTC",
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            db.JobSchedules.Add(row);
            link.ScheduleId = row.Id;
        }
        db.PlaylistLinks.Add(link);
        db.PlaylistSourceSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user,
            PlaylistLinkId = link.Id,
            ProviderAccountId = account.Id,
            Name = "External favourite songs",
            ArtworkReferenceKey = artwork,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            RetrievedAt = clock.UtcNow,
            PublishedAt = clock.UtcNow,
            PayloadSha256 = new string('a', 64),
            CorrelationId = "external-test"
        });
        await db.SaveChangesAsync();
        return link;
    }

    [Fact]
    public async Task MixDetailAndCover_DoNotReadUnrelatedLinkedPlaylists()
    {
        await Seed();
        var mix = (await service.GenerateAsync(owner)).Single(item => item.Kind == "daily-1");
        await ExternalLink();
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Linked playlist unavailable."));
        var controller = Controller(owner, true);
        var response = Assert.IsType<OkObjectResult>(await controller.Get(mix.Id, default));
        var detail = JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal(mix.Id, detail.GetProperty("mix").GetProperty("Id").GetGuid());
        Assert.Equal(mix.Songs.Count, detail.GetProperty("songs").GetArrayLength());
        Assert.IsAssignableFrom<IActionResult>(await controller.Cover(mix.Id, default));
        access.Verify(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExternalVisibility_MigratesChoicesAndEnabledLinksOnce()
    {
        var enabled = await ExternalLink();
        var chosen = await ExternalLink();
        var hidden = await ExternalLink();
        await service.GenerateAsync(owner);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var id in new[] { chosen.Id, hidden.Id })
                (await db.PlaylistLinks.SingleAsync(item => item.Id == id)).Enabled = false;
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
            var payload = System.Text.Json.Nodes.JsonNode.Parse(row.PayloadJson)!;
            payload["AppsOptInVersion"] = 1;
            payload["ExternalShowInApps"] = JsonSerializer.SerializeToNode(new Dictionary<Guid, bool> { [chosen.Id] = true, [hidden.Id] = false });
            row.PayloadJson = payload.ToJsonString();
            row.Id = Guid.NewGuid();
            row.SnapshotVersion++;
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        var cards = await NewService().ListAsync(owner);
        Assert.True(cards.Single(item => item.Id == enabled.Id).Added);
        Assert.True(cards.Single(item => item.Id == chosen.Id).Added);
        Assert.False(cards.Single(item => item.Id == hidden.Id).Added);
        await service.SetInAppsAsync(owner, chosen.Id, false, default);
        await service.SetInAppsAsync(owner, chosen.Id, false, default);
        Assert.False((await NewService().ListAsync(owner)).Single(item => item.Id == chosen.Id).Added);
        await using var read = factory.CreateDbContext();
        Assert.Equal(3, await read.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "personal-mixes"));
        var saved = await read.ExternalMetadataSnapshots.OrderByDescending(item => item.SnapshotVersion).FirstAsync(item => item.ResourceKind == "personal-mixes");
        Assert.DoesNotContain("ExternalShowInApps", saved.PayloadJson);
    }

    [Fact]
    public async Task ExternalCards_UseOwnedLinkedAccountsAndCurrentBackend()
    {
        var loved = await ExternalLink();
        var imported = await ExternalLink("spotify", "provider-playlist");
        await ExternalLink("lastfm", "top:1month:50");
        await ExternalLink("lastfm", "similar");
        await ExternalLink("listenbrainz", "createdfor:weekly-jams");
        await ExternalLink(listener: other);
        var invalid = await ExternalLink();
        await using (var db = factory.CreateDbContext())
        {
            (await db.ProviderAccounts.SingleAsync(item => item.Id == invalid.ProviderAccountId)).OwnerUserId = other;
            await db.SaveChangesAsync();
        }
        var wrongBackend = await ExternalLink();
        var oneTime = await ExternalLink();
        await using (var db = factory.CreateDbContext())
        {
            (await db.PlaylistLinks.SingleAsync(item => item.Id == wrongBackend.Id)).TargetBackendInstanceId = "other";
            (await db.PlaylistLinks.SingleAsync(item => item.Id == oneTime.Id)).ImportMode = PlaylistImportMode.OneTime;
            (await db.PlaylistLinks.SingleAsync(item => item.Id == oneTime.Id)).ScheduleId = null;
            await db.SaveChangesAsync();
        }
        var cards = (await service.ListAsync(owner)).Where(item => item.Kind == "external").ToArray();
        Assert.Equal(4, cards.Length);
        Assert.DoesNotContain(cards, item => item.Id == imported.Id);
        Assert.Equal("Last.fm", cards.Single(item => item.Id == loved.Id).SourceLabel);
        Assert.Equal("Kept in sync · Loved on Last.fm", cards.Single(item => item.Id == loved.Id).Description);
        Assert.Single(cards, item => item.SourceLabel == "ListenBrainz");
        Assert.All(cards, item => Assert.Equal("yourServices", item.Section));
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await Controller(owner, true).List()).Value);
        Assert.Equal(4, json.EnumerateArray().Count(item => item.GetProperty("Kind").GetString() == "external"));
    }

    [Fact]
    public async Task ExternalInApps_UpdatesLinkAndScheduleAndRejectsOtherListeners()
    {
        var link = await ExternalLink();
        await service.SetInAppsAsync(owner, link.Id, false, default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.False((await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id)).Enabled);
            var row = await db.JobSchedules.SingleAsync(item => item.Id == link.ScheduleId);
            Assert.False(row.Enabled);
            Assert.Null(row.NextRunAt);
        }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetInAppsAsync(other, link.Id, true, default));
        await service.SetInAppsAsync(owner, link.Id, true, default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.True((await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id)).Enabled);
            Assert.NotNull((await db.JobSchedules.SingleAsync(item => item.Id == link.ScheduleId)).NextRunAt);
        }
        var unscheduled = await ExternalLink(schedule: false);
        await service.SetInAppsAsync(owner, unscheduled.Id, true, default);
        await using var read = factory.CreateDbContext();
        var scheduled = await read.PlaylistLinks.SingleAsync(item => item.Id == unscheduled.Id);
        Assert.NotNull(scheduled.ScheduleId);
        Assert.True((await read.JobSchedules.SingleAsync(item => item.Id == scheduled.ScheduleId)).Enabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExternalVisibility_PreservesEnabledLinksAndRemovesSeparateChoices(int version)
    {
        await Seed();
        await service.GenerateAsync(owner);
        var restored = await ExternalLink("listenbrainz", "createdfor:weekly-jams");
        var explicitHidden = await ExternalLink("lastfm", "similar");
        var disabled = await ExternalLink();
        await ExternalLink(listener: other);
        await using (var db = factory.CreateDbContext())
        {
            (await db.PlaylistLinks.SingleAsync(item => item.Id == disabled.Id)).Enabled = false;
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
            var document = JsonNode.Parse(row.PayloadJson)!;
            document["AppsOptInVersion"] = version;
            document["ExternalShowInApps"] = new JsonObject { [explicitHidden.Id.ToString()] = false };
            row.Id = Guid.NewGuid();
            row.SnapshotVersion++;
            row.PayloadJson = document.ToJsonString();
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        publish = true;
        Assert.True(await NewService().IsExternalVisibleAsync(Context(owner), restored, default));
        var cards = await NewService().ListAsync(owner);
        Assert.True(cards.Single(item => item.Id == restored.Id).Added);
        Assert.True(cards.Single(item => item.Id == explicitHidden.Id).Added);
        Assert.False(cards.Single(item => item.Id == disabled.Id).Added);
        Assert.False(await service.IsExternalVisibleAsync(Context(other), restored, default));
        await using var read = factory.CreateDbContext();
        var rows = await read.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "personal-mixes")
            .OrderBy(item => item.SnapshotVersion).ToArrayAsync();
        Assert.Equal(3, rows.Length);
        var saved = JsonNode.Parse(rows[^1].PayloadJson)!;
        Assert.Equal(3, saved["AppsOptInVersion"]!.GetValue<int>());
        Assert.Null(saved["ExternalShowInApps"]);
        await service.SetInAppsAsync(owner, restored.Id, false, default);
        Assert.False((await NewService().ListAsync(owner)).Single(item => item.Id == restored.Id).Added);
    }

    [Fact]
    public async Task ExternalVirtualReads_RequireHouseholdPublishingAndTheirOwnListener()
    {
        var link = await ExternalLink();
        var virtualization = new PlaylistVirtualizationService(factory, new(factory, access.Object), access.Object, personalMixes: service);
        var id = PlaylistVirtualizationService.CreateProtocolId(link.Id);
        Assert.Null(await virtualization.ReadAsync(Context(owner), id));
        Assert.Empty(await virtualization.ListAsync(Context(owner)));
        publish = true;
        Assert.NotNull(await virtualization.ReadAsync(Context(owner), id));
        Assert.True((await service.ListAsync(owner)).Single(mix => mix.Id == link.Id).Added);
        await service.SetInAppsAsync(owner, link.Id, true, default);
        await service.GenerateAsync(owner);
        Assert.True((await NewService().ListAsync(owner)).Single(mix => mix.Id == link.Id).Added);
        var visible = await virtualization.ReadAsync(Context(owner), id);
        Assert.NotNull(visible);
        Assert.Equal("lastfm", visible.MixCover!.Source);
        Assert.Equal("loved", visible.MixCover.SourcePlaylistId);
        Assert.Equal(1, visible.MixCover.Generation);
        Assert.Single(await virtualization.ListAsync(Context(owner)));
        Assert.Null(await virtualization.ReadAsync(Context(other), id));
        await service.SetInAppsAsync(owner, link.Id, false, default);
        Assert.Null(await virtualization.ReadBySourceAsync(Context(owner), "lastfm", "loved"));
    }

    [Fact]
    public async Task ExternalSongs_ApplyNeverAndCleanFiltersToCardsAndVirtualReads()
    {
        var link = await ExternalLink();
        await using (var db = factory.CreateDbContext())
        {
            var snapshot = await db.PlaylistSourceSnapshots.SingleAsync(item => item.PlaylistLinkId == link.Id);
            for (var index = 0; index < 4; index++)
            {
                var recording = Guid.NewGuid();
                var hash = new string((char)('a' + index), 64);
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
                var identity = new ProviderTrackIdentityRecord
                {
                    Id = Guid.NewGuid(),
                    CanonicalRecordingId = recording,
                    ProviderAccountId = link.ProviderAccountId,
                    ProviderId = "lastfm",
                    Scope = ProviderIdentityScope.Account,
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = "source-" + index,
                    ExternalIdHash = hash,
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "fixture",
                    DecisionVersion = 1
                };
                db.ProviderTrackIdentities.Add(identity);
                db.ProviderTrackIdentities.Add(new()
                {
                    Id = Guid.NewGuid(),
                    CanonicalRecordingId = recording,
                    ProviderId = "deezer",
                    Scope = ProviderIdentityScope.Catalog,
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = index.ToString(),
                    ExternalIdHash = hash,
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "fixture",
                    DecisionVersion = 1
                });
                var external = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ProviderAccountId = link.ProviderAccountId,
                    ProviderTrackIdentityId = identity.Id,
                    ProviderId = "lastfm",
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    BackendPrincipalId = owner.ToString(),
                    ResourceKind = "Track",
                    ExternalIdHash = hash,
                    SnapshotVersion = 1,
                    ProviderRevision = "1",
                    PayloadSha256 = hash,
                    CorrelationId = "external-test",
                    RetrievedAt = clock.UtcNow,
                    PayloadJson = JsonSerializer.Serialize(new PlaylistSourceMetadata("Song " + index, ["Artist " + index],
                        IsExplicit: index == 3 ? null : index == 1, CanonicalRecordingId: recording))
                };
                db.ExternalMetadataSnapshots.Add(external);
                db.PlaylistSourceEntries.Add(new()
                {
                    Id = Guid.NewGuid(),
                    PlaylistSourceSnapshotId = snapshot.Id,
                    ExternalMetadataSnapshotId = external.Id,
                    SourcePosition = index,
                    SourceEntryIdHash = hash
                });
            }
            await db.SaveChangesAsync();
        }
        publish = true;
        var virtualization = new PlaylistVirtualizationService(factory, new(factory, access.Object), access.Object, personalMixes: service);
        var id = PlaylistVirtualizationService.CreateProtocolId(link.Id);
        await service.SetInAppsAsync(owner, link.Id, true, default);
        Assert.Equal(4, (await virtualization.ReadAsync(Context(owner), id))!.Tracks.Count);
        Assert.Equal(4, (await service.ListAsync(owner)).Single(item => item.Id == link.Id).Songs.Count);
        await taste.PutNudgeAsync(owner, new("artist", "Artist 2", "never"));
        filter = "CleanOnly";
        Assert.Equal("Song 0", Assert.Single((await virtualization.ReadAsync(Context(owner), id))!.Tracks).Title);
        Assert.Equal("Song 0", Assert.Single((await service.ListAsync(owner)).Single(item => item.Id == link.Id).Songs).Song.Title);
    }

    [Theory]
    [InlineData("other", "loved", null)]
    [InlineData("lastfm", "top", "invalid")]
    [InlineData("lastfm", "unknown", null)]
    public async Task ExternalCreation_RejectsUnsupportedSelections(string source, string kind, string? period)
    {
        publish = true;
        Assert.IsType<BadRequestObjectResult>(await Controller(owner, true).External(new(source, kind, period), default));
    }

    [Theory]
    [InlineData("lastfm", "top", "7day", "top:7day:50", false)]
    [InlineData("lastfm", "top", "7day", "top:7day:50", true)]
    [InlineData("listenbrainz", "weekly-jams", null, "createdfor:weekly-jams", false)]
    [InlineData("listenbrainz", "weekly-jams", null, "createdfor:weekly-jams", true)]
    public async Task ExternalCreation_ReturnsWithoutProviderReadsOrOtherProjectionsAndQueuesOnce(
        string source, string kind, string? period, string selection, bool allowed)
    {
        publish = allowed;
        var accountLink = await ExternalLink(source, source == "lastfm" ? "loved" : "createdfor:weekly-exploration");
        await ExternalLink("lastfm", "similar");
        await ExternalLink("lastfm", "top:1month:50");
        await using (var db = factory.CreateDbContext())
        {
            (await db.ProviderAccounts.SingleAsync(item => item.Id == accountLink.ProviderAccountId)).CreatedAt = clock.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }
        var projectionCalls = 0;
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken token) =>
            {
                projectionCalls++;
                await Task.Delay(300, token);
                return new BackendLibraryAccessContext(Context(id), new(true, []));
            });
        var providerCalls = 0;
        var provider = new Mock<IProviderPlaylistCapability>();
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderPlaylistCapability>(source, ProviderCapabilityKind.Playlist))
            .Returns(provider.Object);
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .Returns(async (ProviderRouteRequest request) =>
            {
                providerCalls++;
                await Task.Delay(700, request.CancellationToken);
                return new ProviderRoutePlan<IProviderPlaylistCapability>(request, [],
                    new(request.CorrelationId, ProviderCapabilityKind.Playlist, source, null, []));
            });
        var persistence = new PlaylistPersistenceService(factory, new(factory), clock,
            Mock.Of<ITrackMatchRepository>(), access.Object, providerGateway: gateway.Object, registry: registry.Object);
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var linkController = new PlaylistLinksController(factory, persistence, new(factory, access.Object), null!, null!, queue,
            null!, registry.Object, router.Object, null!, null!, null!, clock, null!, new(factory, clock), null!, null!, null!, null!);
        var controller = Controller(owner, true);
        controller.HttpContext.RequestServices = new ServiceCollection().AddSingleton(linkController).BuildServiceProvider();
        var timer = Stopwatch.StartNew();
        var result = await controller.External(new(source, kind, period), default);
        timer.Stop();
        output.WriteLine($"External POST {source}: {timer.ElapsedMilliseconds} ms; provider plans {providerCalls}; other projections {projectionCalls} (300 ms per projection, 700 ms per provider plan).");
        if (!allowed)
        {
            Assert.Equal(403, Assert.IsType<StatusCodeResult>(result).StatusCode);
            return;
        }
        var response = Assert.IsType<CreatedResult>(result);
        Assert.Equal(0, providerCalls);
        Assert.Equal(0, projectionCalls);
        provider.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Never);
        registry.VerifyNoOtherCalls();
        policies.Verify(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        gateway.VerifyNoOtherCalls();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"POST took {timer.ElapsedMilliseconds} ms.");
        var card = JsonSerializer.SerializeToElement(response.Value);
        Assert.False(card.TryGetProperty("Added", out _));
        Assert.Equal(allowed, card.GetProperty("appsAllowed").GetBoolean());
        Assert.True(card.GetProperty("inApps").GetBoolean());
        Assert.False(card.GetProperty("Readiness").GetProperty("Ready").GetBoolean());
        Assert.Equal("Matching songs…", card.GetProperty("Readiness").GetProperty("Message").GetString());
        Assert.Empty(card.GetProperty("coverArtwork").EnumerateArray());
        await using var read = factory.CreateDbContext();
        var created = await read.PlaylistLinks.SingleAsync(item => item.SourcePlaylistId == selection);
        Assert.Equal(PlaylistLinkMode.Virtual, created.Mode);
        Assert.Equal(PlaylistImportMode.Linked, created.ImportMode);
        Assert.Equal(accountLink.ProviderAccountId, created.ProviderAccountId);
        Assert.True(created.Enabled);
        Assert.True((await read.JobSchedules.SingleAsync(item => item.Id == created.ScheduleId)).Enabled);
        var job = Assert.Single(await read.Jobs.ToArrayAsync());
        Assert.Equal("playlist.materialize", job.Type);
        Assert.Equal(DurableJobState.Pending, job.State);
        Assert.True(job.AvailableAt <= clock.UtcNow);
        Assert.Equal(created.Id, JsonSerializer.Deserialize<PlaylistMaterializationJobPayload>(job.PayloadJson)!.PlaylistLinkId);
        var repeated = Assert.IsType<CreatedResult>(await controller.External(new(source, kind, period), default));
        Assert.Equal(response.Location, repeated.Location);
        Assert.Equal(0, projectionCalls);
        Assert.Equal(0, providerCalls);
        Assert.Equal(job.Id, JsonSerializer.SerializeToElement(response.Value).GetProperty("initialJobId").GetGuid());
        Assert.Equal(job.Id, JsonSerializer.SerializeToElement(repeated.Value).GetProperty("initialJobId").GetGuid());
        var listed = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await linkController.List(default)).Value)
            .GetProperty("playlistLinks").EnumerateArray();
        Assert.Contains(listed, item => item.GetProperty("id").GetGuid() == created.Id);
        var status = new JobsController(factory, queue) { ControllerContext = controller.ControllerContext };
        var pending = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await status.Get(job.Id)).Value);
        Assert.Equal("Pending", pending.GetProperty("job").GetProperty("state").GetString());
        read.AuditEvents.Add(new()
        {
            Id = Guid.NewGuid(),
            ActorUserId = owner,
            Category = "job-progress",
            Action = "playlist.match",
            SubjectType = "job",
            SubjectId = job.Id.ToString("D"),
            CorrelationId = job.CorrelationId,
            Outcome = "running",
            DetailsJson = "{\"stage\":\"playlist.match\",\"completed\":2,\"total\":10}",
            CreatedAt = clock.UtcNow
        });
        await read.SaveChangesAsync();
        var progress = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await status.Get(job.Id)).Value);
        using var details = JsonDocument.Parse(Assert.Single(progress.GetProperty("progress").EnumerateArray()).GetProperty("DetailsJson").GetString()!);
        Assert.Equal(2, details.RootElement.GetProperty("completed").GetInt32());
        Assert.Equal(10, details.RootElement.GetProperty("total").GetInt32());
        Assert.Single(await read.Jobs.ToArrayAsync());
        Assert.Single(await read.PlaylistLinks.Where(item => item.SourcePlaylistId == selection).ToArrayAsync());
    }
}
