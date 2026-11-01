using System.Collections.Immutable;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinAddedPlaylistSummaries_MatchMemberDurationsAndReadProjectionsInBulk(bool countReads)
    {
        var reads = new PlaylistSummaryReads();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => new Song
            { Id = "ext-deezer-song-" + id, Title = id, Artist = "Artist", Duration = id == "known" ? 187 : null });
        var settings = new Mock<IDurableRuntimeSettings>();
        settings.Setup(item => item.GetAsync(PersonalMixService.PublishKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveRuntimeSetting(PersonalMixService.PublishKey, RuntimeSettingValueType.Boolean,
                true, "true", RuntimeSettingOrigin.Durable, 1, null, null, null));
        settings.Setup(item => item.GetPreferencesAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new("All"), new(), false, "1"));
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "Lossless", .07);
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new BackendLibraryAccessContext(new(ProtocolKind.Jellyfin,
                "primary", "user-1", new(id, "jellyfin", "primary", "user-1", "Owner", false), "fixture",
                DateTimeOffset.UtcNow.AddMinutes(5), default), new(true, [])));
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1","Name":"Owner"}"""),
            "/Users/user-1/Views" => Json(200, """{"Items":[{"Id":"music-library","CollectionType":"music"},{"Id":"playlists-folder","Type":"PlaylistsFolder"}]}"""),
            _ => Json(200, """{"Items":[],"TotalRecordCount":0}""")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IDurableRuntimeSettings>();
            services.AddSingleton(settings.Object);
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>())
                    .AddInterceptors(reads).Options));
        }, new Dictionary<string, string?> { ["Jellyfin:LibraryId"] = null });
        using var client = factory.CreateClient();
        await SeedMixedPlaylistUserAsync(factory, "jellyfin", "user-1");
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        var ids = new List<string>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var owner = (await db.Users.SingleAsync()).Id;
            var knownRecording = Guid.NewGuid();
            var unknownRecording = Guid.NewGuid();
            db.CanonicalRecordings.AddRange(new CanonicalRecordingRecord { Id = knownRecording, CreatedByUserId = owner },
                new CanonicalRecordingRecord { Id = unknownRecording, CreatedByUserId = owner });
            foreach (var (provider, selection) in new[] { ("lastfm", "similar"), ("lastfm", "loved"), ("listenbrainz", "createdfor:weekly-exploration") })
            {
                var account = new ProviderAccountRecord { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = provider, Enabled = true };
                db.ProviderAccounts.Add(account);
                var link = new PlaylistLinkRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ProviderAccountId = account.Id,
                    SourceProviderId = provider,
                    SourcePlaylistId = selection,
                    SourcePlaylistIdHash = selection.PadRight(64, 'a'),
                    TargetProtocol = "jellyfin",
                    TargetBackendInstanceId = "primary",
                    Mode = PlaylistLinkMode.Virtual,
                    Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.PlaylistLinks.Add(link);
                ids.Add(PlaylistVirtualizationService.CreateProtocolId(link.Id));
                var snapshot = new PlaylistSourceSnapshotRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    PlaylistLinkId = link.Id,
                    ProviderAccountId = account.Id,
                    Name = selection,
                    SnapshotVersion = 1,
                    ProviderRevision = "1",
                    PayloadSha256 = new string('a', 64),
                    RetrievedAt = DateTimeOffset.UtcNow,
                    PublishedAt = DateTimeOffset.UtcNow
                };
                db.PlaylistSourceSnapshots.Add(snapshot);
                for (var position = 0; position < 3; position++)
                {
                    var hash = new string((char)('b' + position), 64);
                    var recording = position < 2 ? knownRecording : unknownRecording;
                    var identity = new ProviderTrackIdentityRecord
                    {
                        Id = Guid.NewGuid(),
                        CanonicalRecordingId = recording,
                        ProviderAccountId = account.Id,
                        ProviderId = provider,
                        Scope = ProviderIdentityScope.Account,
                        ResourceKind = ProviderResourceKind.Track,
                        ExternalId = "source-" + position,
                        ExternalIdHash = hash,
                        Verification = ProviderIdentityVerification.Verified,
                        VerificationMethod = "fixture",
                        DecisionVersion = 1
                    };
                    db.ProviderTrackIdentities.Add(identity);
                    if (ids.Count == 1 && position != 1) db.ProviderTrackIdentities.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        CanonicalRecordingId = recording,
                        ProviderId = "deezer",
                        Scope = ProviderIdentityScope.Catalog,
                        ResourceKind = ProviderResourceKind.Track,
                        ExternalId = position == 0 ? "known" : "unknown",
                        ExternalIdHash = hash,
                        Verification = ProviderIdentityVerification.Verified,
                        VerificationMethod = "fixture",
                        DecisionVersion = 1
                    });
                    var external = new ExternalMetadataSnapshotRecord
                    {
                        Id = Guid.NewGuid(),
                        OwnerUserId = owner,
                        ProviderAccountId = account.Id,
                        ProviderTrackIdentityId = identity.Id,
                        ProviderId = provider,
                        Protocol = "jellyfin",
                        BackendInstanceId = "primary",
                        BackendPrincipalId = "user-1",
                        ResourceKind = "Track",
                        ExternalIdHash = hash,
                        SnapshotVersion = 1,
                        ProviderRevision = "1",
                        PayloadSha256 = hash,
                        PayloadJson = JsonSerializer.Serialize(new PlaylistSourceMetadata("Song", ["Artist"], CanonicalRecordingId: recording))
                    };
                    db.ExternalMetadataSnapshots.Add(external);
                    var match = new TrackMatchRecord
                    {
                        Id = Guid.NewGuid(),
                        OwnerUserId = owner,
                        ExternalSnapshotId = external.Id,
                        CanonicalRecordingId = recording,
                        State = TrackMatchState.Accepted,
                        DecisionVersion = 1,
                        SourceSnapshotVersion = 1,
                        CandidateResultsJson = position < 2 ? """[{"providerTrackIds":{"deezer":"known"},"durationMilliseconds":187654}]""" : "[]"
                    };
                    db.TrackMatches.Add(match);
                    db.PlaylistSourceEntries.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        PlaylistSourceSnapshotId = snapshot.Id,
                        ExternalMetadataSnapshotId = external.Id,
                        PublishedTrackMatchId = match.Id,
                        SourcePosition = position,
                        SourceEntryIdHash = hash
                    });
                }
            }
            await db.SaveChangesAsync();
        }
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready, "fixture");
        reads.Commands.Clear();
        using var response = await client.GetAsync("/Users/user-1/Items?IncludeItemTypes=Playlist&ParentId=music-library&MediaTypes=Audio&Fields=ChildCount&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var summaries = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var projectionReads = reads.Commands.Where(sql => sql.Contains("FROM \"playlist_source_entries\"", StringComparison.Ordinal)).ToArray();
        if (countReads)
        {
            Assert.Single(projectionReads);
            Assert.Contains(" IN ", projectionReads[0], StringComparison.Ordinal);
            return;
        }
        foreach (var id in ids)
        {
            var outside = summaries.RootElement.GetProperty("Items").EnumerateArray().Single(row => row.GetProperty("Id").GetString() == id);
            using var membersResponse = await client.GetAsync($"/Playlists/{id}/Items?api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, membersResponse.StatusCode);
            using var members = JsonDocument.Parse(await membersResponse.Content.ReadAsStringAsync());
            var rows = members.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(2, outside.GetProperty("ChildCount").GetInt32());
            Assert.Equal(187 * TimeSpan.TicksPerSecond, rows.Sum(row => row.GetProperty("RunTimeTicks").GetInt64()));
            Assert.Equal(rows.Sum(row => row.GetProperty("RunTimeTicks").GetInt64()), outside.GetProperty("RunTimeTicks").GetInt64());
            using var detailResponse = await client.GetAsync($"/Items/{id}?api_key=fixture-key");
            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
            Assert.Equal(outside.GetProperty("RunTimeTicks").GetInt64(), detail.RootElement.GetProperty("RunTimeTicks").GetInt64());
        }
    }

    private sealed class PlaylistSummaryReads : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
