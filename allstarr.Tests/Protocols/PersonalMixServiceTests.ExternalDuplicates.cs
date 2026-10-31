using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Theory]
    [InlineData("lastfm", "loved", true, false)]
    [InlineData("lastfm", "loved", true, true)]
    [InlineData("listenbrainz", "createdfor:weekly-jams", true, false)]
    [InlineData("listenbrainz", "createdfor:weekly-jams", true, true)]
    [InlineData("spotify", "weekly", false, false)]
    [InlineData("lastfm", "ordinary-playlist", false, false)]
    public async Task LinkedPlaylistResolvedDuplicates_KeepFirstPositionAndConsistentSummaryAndPages(
        string provider, string selection, bool uniqueSongs, bool externalRoute)
    {
        protocol = "jellyfin";
        publish = true;
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).BackendType = protocol;
            await db.SaveChangesAsync();
        }
        var link = await ExternalLink(provider, selection);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid user, CancellationToken _) => Task.FromResult(new BackendLibraryAccessContext(Context(user), new(true, ["music"]))));
        await using (var db = factory.CreateDbContext())
        {
            var snapshot = await db.PlaylistSourceSnapshots.SingleAsync(item => item.PlaylistLinkId == link.Id);
            var first = new LibraryTrackRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "music",
                BackendItemId = "native-a",
                Title = "First",
                Artist = "Artist",
                DurationMilliseconds = 120000
            };
            var second = new LibraryTrackRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "music",
                BackendItemId = "native-b",
                Title = "Second",
                Artist = "Artist",
                DurationMilliseconds = 180000
            };
            if (externalRoute)
            {
                foreach (var track in new[] { first, second })
                {
                    var recording = Guid.NewGuid();
                    track.CanonicalRecordingId = recording;
                    db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
                    db.ProviderTrackIdentities.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        CanonicalRecordingId = recording,
                        ProviderId = "deezer",
                        Scope = ProviderIdentityScope.Catalog,
                        ResourceKind = ProviderResourceKind.Track,
                        ExternalId = track == first ? "101" : "102",
                        ExternalIdHash = new string(track == first ? 'd' : 'e', 64),
                        Verification = ProviderIdentityVerification.Verified,
                        VerificationMethod = "fixture",
                        DecisionVersion = 1
                    });
                }
            }
            else db.LibraryTracks.AddRange(first, second);
            for (var index = 0; index < 3; index++)
            {
                var hash = new string((char)('a' + index), 64);
                var metadata = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ProviderAccountId = link.ProviderAccountId,
                    ProviderId = provider,
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    ResourceKind = "Track",
                    ExternalIdHash = hash,
                    SnapshotVersion = 1,
                    PayloadSha256 = hash,
                    CorrelationId = "duplicate-test",
                    RetrievedAt = clock.UtcNow,
                    PayloadJson = JsonSerializer.Serialize(new PlaylistSourceMetadata("Source " + index, ["Artist"],
                        DurationMilliseconds: externalRoute ? index == 2 ? 180000 : 120000 : 90000))
                };
                if (externalRoute)
                {
                    var identity = new ProviderTrackIdentityRecord
                    {
                        Id = Guid.NewGuid(),
                        CanonicalRecordingId = (index == 2 ? second : first).CanonicalRecordingId!.Value,
                        ProviderId = provider,
                        ProviderAccountId = link.ProviderAccountId,
                        Scope = ProviderIdentityScope.Account,
                        ResourceKind = ProviderResourceKind.Track,
                        ExternalId = "source-" + index,
                        ExternalIdHash = hash,
                        Verification = ProviderIdentityVerification.Verified,
                        VerificationMethod = "fixture",
                        DecisionVersion = 1
                    };
                    db.ProviderTrackIdentities.Add(identity);
                    metadata.ProviderTrackIdentityId = identity.Id;
                }
                var match = new TrackMatchRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ExternalSnapshotId = metadata.Id,
                    LibraryTrackId = externalRoute ? null : index == 2 ? second.Id : first.Id,
                    CanonicalRecordingId = externalRoute ? index == 2 ? second.CanonicalRecordingId : first.CanonicalRecordingId : null,
                    State = TrackMatchState.Accepted,
                    DecisionVersion = 1,
                    SourceSnapshotVersion = 1,
                    Confidence = 1,
                    Threshold = .9,
                    DecidedAt = clock.UtcNow
                };
                db.ExternalMetadataSnapshots.Add(metadata);
                db.TrackMatches.Add(match);
                db.PlaylistSourceEntries.Add(new()
                {
                    Id = Guid.NewGuid(),
                    PlaylistSourceSnapshotId = snapshot.Id,
                    ExternalMetadataSnapshotId = metadata.Id,
                    SourcePosition = index,
                    SourceEntryIdHash = hash,
                    PublishedTrackMatchId = match.Id
                });
            }
            await db.SaveChangesAsync();
        }
        var virtualization = new PlaylistVirtualizationService(factory, new(factory, access.Object), access.Object,
            personalMixes: service);
        var context = Context(owner);
        var id = PlaylistVirtualizationService.CreateProtocolId(link.Id);
        var detail = Assert.IsType<VirtualPlaylistReadModel>(await virtualization.ReadAsync(context, id));
        var firstId = externalRoute ? "ext-deezer-song-101" : "native-a";
        var secondId = externalRoute ? "ext-deezer-song-102" : "native-b";
        var expectedIds = uniqueSongs ? new[] { firstId, secondId } : [firstId, firstId, secondId];
        var expectedPositions = uniqueSongs ? new[] { 0, 2 } : [0, 1, 2];
        Assert.Equal(expectedIds, detail.Tracks.Select(track => track.BackendItemId));
        Assert.Equal(expectedPositions, detail.Tracks.Select(track => track.SourcePosition));
        var count = uniqueSongs ? 2 : 3;
        var duration = uniqueSongs ? 300000L : 420000L;
        Assert.Equal(count, detail.TrackCount);
        Assert.Equal(duration, detail.DurationMilliseconds);
        var summary = Assert.Single(await virtualization.ListSummariesAsync(context, default));
        Assert.Equal(count, summary.TrackCount);
        Assert.Equal(duration, summary.DurationMilliseconds);
        if (uniqueSongs) Assert.Equal(count, detail.MixCover!.Count);
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(virtualization,
            new Mock<IJellyfinPlaylistMutationResolver>().Object);
        foreach (var item in new[] { adapter.ToItem(summary), adapter.ToItem(detail) })
        {
            Assert.Equal(count, item["ChildCount"]);
            Assert.Equal(duration * TimeSpan.TicksPerMillisecond, item["RunTimeTicks"]);
        }
        for (var start = 0; start <= count; start++)
        {
            var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            { ["StartIndex"] = start.ToString(), ["Limit"] = "1" });
            var result = Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(context, id, null, query, default));
            using var page = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal(count, page.RootElement.GetProperty("TotalRecordCount").GetInt32());
            Assert.Equal(expectedIds.Skip(start).Take(1), page.RootElement.GetProperty("Items").EnumerateArray()
                .Select(item => item.GetProperty("Id").GetString()));
        }
    }
}
