using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
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
    private async Task<PlaylistLinkRecord> ExternalLink(string source = "lastfm", string selection = "loved",
        Guid? listener = null, bool schedule = true)
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
    public async Task ExternalCards_UseOwnedLinkedAccountsAndCurrentBackend()
    {
        var loved = await ExternalLink();
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

    [Fact]
    public async Task ExternalVirtualReads_RequireHouseholdPublishingAndTheirOwnListener()
    {
        var link = await ExternalLink();
        var virtualization = new PlaylistVirtualizationService(factory, new(factory, access.Object), access.Object, personalMixes: service);
        var id = PlaylistVirtualizationService.CreateProtocolId(link.Id);
        Assert.Null(await virtualization.ReadAsync(Context(owner), id));
        Assert.Empty(await virtualization.ListAsync(Context(owner)));
        publish = true;
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
        Assert.IsType<BadRequestObjectResult>(await Controller(owner, true).External(new(source, kind, period), default));
    }

    [Fact]
    public async Task ExternalCreation_ReusesLinkedPlaylistCreationWithVirtualDefaults()
    {
        var accountLink = await ExternalLink();
        var persistence = new Mock<IPlaylistPersistenceService>();
        PlaylistLinkInput? captured = null;
        persistence.Setup(item => item.CreateLinkAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<PlaylistLinkInput>(), It.IsAny<CancellationToken>()))
            .Returns<ProtocolExecutionContext, PlaylistLinkInput, CancellationToken>(async (_, input, token) =>
            {
                captured = input;
                var row = new PlaylistLinkRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ProviderAccountId = input.ProviderAccountId,
                    SourceProviderId = input.SourceProviderId,
                    SourcePlaylistId = input.SourcePlaylistId,
                    SourcePlaylistIdHash = input.SourcePlaylistIdHash,
                    TargetProtocol = input.TargetProtocol,
                    TargetBackendInstanceId = input.TargetBackendInstanceId,
                    Mode = input.Mode,
                    MaterializationMode = input.MaterializationMode,
                    ImportMode = input.ImportMode,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow
                };
                await using var db = factory.CreateDbContext();
                db.PlaylistLinks.Add(row);
                await db.SaveChangesAsync(token);
                return row;
            });
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var linkController = new PlaylistLinksController(factory, persistence.Object, null!, null!, null!, queue,
            null!, null!, null!, null!, null!, null!, clock, null!, new(factory, clock), null!, null!, null!, null!);
        var controller = Controller(owner, true);
        controller.HttpContext.RequestServices = new ServiceCollection().AddSingleton(linkController).BuildServiceProvider();
        var response = Assert.IsType<CreatedResult>(await controller.External(new("lastfm", "top", "7day"), default));
        Assert.NotNull(response.Value);
        Assert.Equal(PlaylistLinkMode.Virtual, captured!.Mode);
        Assert.Equal(PlaylistImportMode.Linked, captured.ImportMode);
        Assert.Equal("top:7day:50", captured.SourcePlaylistId);
        Assert.Equal(accountLink.ProviderAccountId, captured.ProviderAccountId);
        await using var read = factory.CreateDbContext();
        var created = await read.PlaylistLinks.SingleAsync(item => item.SourcePlaylistId == "top:7day:50");
        Assert.True((await read.JobSchedules.SingleAsync(item => item.Id == created.ScheduleId)).Enabled);
        Assert.Single(await read.Jobs.ToArrayAsync());
    }
}
