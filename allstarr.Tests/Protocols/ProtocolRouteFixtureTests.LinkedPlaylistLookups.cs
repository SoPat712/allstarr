using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinLinkedPlaylistLookups_MatchListDetailAndMembers(bool spotifyAlias)
    {
        var reads = new PlaylistSummaryReads();
        using var factory = LinkedPlaylistLookupFactory(spotifyAlias, reads);
        using var client = factory.CreateClient();
        await SeedLinkedPlaylistLookupsAsync(factory, spotifyAlias);
        using var listResponse = await client.GetAsync("/Users/user-1/Items?IncludeItemTypes=Playlist&ParentId=music-library&Fields=ChildCount&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        foreach (var (id, name, count, seconds) in LinkedLookupPlaylists())
        {
            using var membersResponse = await client.GetAsync($"/Playlists/{id}/Items?api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, membersResponse.StatusCode);
            using var members = JsonDocument.Parse(await membersResponse.Content.ReadAsStringAsync());
            var rows = members.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal(count, rows.Length);
            var ticks = rows.Sum(row => row.GetProperty("RunTimeTicks").GetInt64());
            Assert.Equal(seconds * TimeSpan.TicksPerSecond, ticks);
            void Check(JsonElement row)
            {
                Assert.Equal(id, row.GetProperty("Id").GetString());
                Assert.Equal(name, row.GetProperty("Name").GetString());
                Assert.Equal(count, row.GetProperty("ChildCount").GetInt32());
                Assert.Equal(ticks, row.GetProperty("RunTimeTicks").GetInt64());
            }
            Check(list.RootElement.GetProperty("Items").EnumerateArray().Single(row => row.GetProperty("Id").GetString() == id));
            using var detailResponse = await client.GetAsync($"/Users/user-1/Items/{id}?api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
            Check(detail.RootElement);
            foreach (var path in new[] { "/Users/user-1/Items", "/Items?UserId=user-1", "/Playlists?UserId=user-1" })
                foreach (var shape in new[] { "", "&Fields=ChildCount", "&ParentId=music-library", "&ParentId=music-library&Fields=ChildCount",
                    "&ParentId=music-library&IncludeItemTypes=Playlist", "&ParentId=music-library&IncludeItemTypes=Playlist&Fields=ChildCount" })
                {
                    using var response = await client.GetAsync(path + (path.Contains('?') ? "&" : "?") + $"Ids={id}{shape}&api_key=fixture-key");
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var lookup = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Check(Assert.Single(lookup.RootElement.GetProperty("Items").EnumerateArray()));
                }
        }
        using var unlinkedResponse = await client.GetAsync("/Users/user-1/Items?Ids=unlinked&api_key=fixture-key");
        using var unlinked = JsonDocument.Parse(await unlinkedResponse.Content.ReadAsStringAsync());
        var original = Assert.Single(unlinked.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(7, original.GetProperty("ChildCount").GetInt32());
        Assert.Equal(123456789L, original.GetProperty("RunTimeTicks").GetInt64());
        Assert.Equal("Unlinked", original.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task JellyfinLinkedPlaylistLookups_ListReadsStayBulk()
    {
        var counts = new List<int>();
        var measurements = new List<string>();
        foreach (var count in new[] { 1, 24 })
        {
            var reads = new PlaylistSummaryReads();
            var requests = new List<string>();
            using var factory = LinkedPlaylistLookupFactory(false, reads, playlistCount: count, requests: requests);
            using var client = factory.CreateClient();
            await SeedLinkedPlaylistLookupsAsync(factory, false, count);
            reads.Commands.Clear();
            requests.Clear();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var response = await client.GetAsync("/Users/user-1/Items?IncludeItemTypes=Playlist&Fields=ChildCount&Limit=100&api_key=fixture-key");
            timer.Stop();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(count, body.RootElement.GetProperty("Items").GetArrayLength());
            counts.Add(reads.Commands.Count);
            var measurement = $"{count} playlists, {count * 100} members, {reads.Commands.Count} SQL commands, {timer.Elapsed.TotalMilliseconds:F1} ms";
            measurements.Add(measurement);
            Assert.DoesNotContain(requests, path => path.StartsWith("/Playlists/", StringComparison.Ordinal) ||
                path.StartsWith("/Users/user-1/Items/", StringComparison.Ordinal));
            var gateway = Mock.Get(factory.Services.GetRequiredService<IProtocolProviderGateway>());
            gateway.Verify(item => item.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            gateway.Verify(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()), Times.AtMostOnce());
            gateway.VerifyNoOtherCalls();
        }
        Assert.True(counts[0] == counts[1], string.Join("; ", measurements));
    }

    [Fact]
    public async Task JellyfinLinkedPlaylistLookups_SongResultsReadNoSummaries()
    {
        var reads = new PlaylistSummaryReads();
        using var factory = LinkedPlaylistLookupFactory(false, reads);
        using var client = factory.CreateClient();
        await SeedLinkedPlaylistLookupsAsync(factory, false);
        reads.Commands.Clear();
        using var response = await client.GetAsync("/Users/user-1/Items?Ids=native-song&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Audio", Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Type").GetString());
        Assert.DoesNotContain(reads.Commands, sql => sql.Contains("playlist_source_snapshots", StringComparison.Ordinal) ||
            sql.Contains("playlist_source_entries", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JellyfinLinkedPlaylistLookups_InaccessibleRowsAreNotSynthesized()
    {
        using var factory = LinkedPlaylistLookupFactory(false, new PlaylistSummaryReads(), denied: true);
        using var client = factory.CreateClient();
        await SeedLinkedPlaylistLookupsAsync(factory, false);
        var id = LinkedLookupPlaylists()[0].Id;
        foreach (var shape in new[] { "", "&ParentId=music-library&IncludeItemTypes=Playlist" })
        {
            using var response = await client.GetAsync($"/Users/user-1/Items?Ids={id}{shape}&api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(body.RootElement.GetProperty("Items").EnumerateArray());
        }
    }

    private static (string Id, string Name, int Count, long Seconds)[] LinkedLookupPlaylists(int playlistCount = 3) => playlistCount != 3
        ? Enumerable.Range(1, playlistCount).Select(index => (index.ToString("x32"), "Linked " + index, 100, 20000L)).ToArray()
        :
    [
        ("00112233445566778899aabbccddeef0", "Release Radar", 30, 5846),
        ("00112233445566778899aabbccddeef1", "Adrenaline", 99, 19580),
        ("00112233445566778899aabbccddeef2", "On Repeat", 30, 5486)
    ];

    private static ProtocolFactory LinkedPlaylistLookupFactory(bool spotifyAlias, PlaylistSummaryReads reads, bool denied = false,
        int playlistCount = 3, List<string>? requests = null)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        var policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
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
        var settings = new Dictionary<string, string?> { ["Jellyfin:LibraryId"] = null, ["SpotifyImport:Enabled"] = spotifyAlias.ToString() };
        foreach (var (playlist, index) in LinkedLookupPlaylists(playlistCount).Select((item, index) => (item, index)))
        {
            settings[$"SpotifyImport:Playlists:{index}:Id"] = playlist.Name;
            settings[$"SpotifyImport:Playlists:{index}:Name"] = playlist.Name;
            settings[$"SpotifyImport:Playlists:{index}:JellyfinId"] = playlist.Id;
            settings[$"SpotifyImport:Playlists:{index}:UserId"] = "user-1";
        }
        return new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests?.Add(path);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1","Name":"Owner"}""");
            if (path == "/Users/user-1/Views") return Json(200, """{"Items":[{"Id":"music-library","CollectionType":"music"},{"Id":"playlists-folder","Type":"PlaylistsFolder"}]}""");
            if (path == "/Users/user-1/Items" || path == "/Items")
            {
                var ids = query.GetValueOrDefault("Ids").ToString();
                if (ids == "native-song") return Json(200, """{"Items":[{"Id":"native-song","Type":"Audio","Name":"Song"}],"TotalRecordCount":1}""");
                if (ids == "unlinked") return Json(200, """{"Items":[{"Id":"unlinked","Name":"Unlinked","Type":"Playlist","ChildCount":7,"RunTimeTicks":123456789}],"TotalRecordCount":1}""");
                if (denied || ids.Length > 0 && query.ContainsKey("ParentId") && query.GetValueOrDefault("IncludeItemTypes") == "Playlist")
                    return Json(200, """{"Items":[],"TotalRecordCount":0}""");
                var rows = LinkedLookupPlaylists(playlistCount).Where(item => ids.Length == 0 || ids == item.Id).Select(item => new JsonObject
                {
                    ["Id"] = item.Id, ["Name"] = "Stale " + item.Name, ["Type"] = "Playlist", ["ParentId"] = "playlists-folder",
                    ["ChildCount"] = ids.Length == 0 ? item.Count : item.Name == "Adrenaline" ? 102 : item.Name == "On Repeat" ? 28 : 0,
                    ["RunTimeTicks"] = ids.Length == 0 ? item.Name == "Release Radar" ? 58604120000L : item.Name == "Adrenaline" ? 195840710000L : 54962460000L
                        : item.Name == "Adrenaline" ? 201981773730L : item.Name == "On Repeat" ? 50349724698L : 0L
                }).ToArray();
                return Json(200, new JsonObject { ["Items"] = new JsonArray(rows), ["TotalRecordCount"] = rows.Length }.ToJsonString());
            }
            if (path.StartsWith("/Users/user-1/Items/", StringComparison.Ordinal))
                return denied ? Json(404, "{}") : Json(200, """{"Type":"Playlist","Name":"Stale","ChildCount":0,"RunTimeTicks":0}""");
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IBackendPlaylistTarget>();
            services.AddSingleton<IBackendPlaylistTarget>(provider => new JellyfinPlaylistTarget(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("fixture"), new Uri("http://fixture")));
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>())
                    .AddInterceptors(reads).Options));
        }, settings);
    }

    private static async Task SeedLinkedPlaylistLookupsAsync(ProtocolFactory factory, bool spotifyAlias, int playlistCount = 3)
    {
        await SeedMixedPlaylistUserAsync(factory, "jellyfin", "user-1");
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var owner = (await db.Users.SingleAsync()).Id;
        var account = new ProviderAccountRecord { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = "spotify", Enabled = true };
        db.ProviderAccounts.Add(account);
        foreach (var (id, name, count, seconds) in LinkedLookupPlaylists(playlistCount))
        {
            var link = new PlaylistLinkRecord
            {
                Id = Guid.NewGuid(), OwnerUserId = owner, ProviderAccountId = account.Id, SourceProviderId = "spotify",
                SourcePlaylistId = name, SourcePlaylistIdHash = name.PadRight(64, 'a'), TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "primary", TargetPlaylistId = spotifyAlias ? null : id,
                Mode = spotifyAlias ? PlaylistLinkMode.Virtual : PlaylistLinkMode.Materialized, Enabled = true
            };
            db.PlaylistLinks.Add(link);
            var snapshot = new PlaylistSourceSnapshotRecord
            {
                Id = Guid.NewGuid(), OwnerUserId = owner, PlaylistLinkId = link.Id, ProviderAccountId = account.Id,
                Name = name, SnapshotVersion = 1, ProviderRevision = "1", PayloadSha256 = new string('a', 64),
                RetrievedAt = DateTimeOffset.UtcNow, PublishedAt = DateTimeOffset.UtcNow
            };
            db.PlaylistSourceSnapshots.Add(snapshot);
            for (var position = 0; position < count; position++)
            {
                var recording = Guid.NewGuid();
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
                var hash = Guid.NewGuid().ToString("N").PadRight(64, 'a');
                var identity = new ProviderTrackIdentityRecord
                {
                    Id = Guid.NewGuid(), CanonicalRecordingId = recording, ProviderId = "spotify", ProviderAccountId = account.Id, Scope = ProviderIdentityScope.Account,
                    ResourceKind = ProviderResourceKind.Track, ExternalId = hash, ExternalIdHash = hash,
                    Verification = ProviderIdentityVerification.Verified, VerificationMethod = "fixture", DecisionVersion = 1
                };
                db.ProviderTrackIdentities.Add(identity);
                db.ProviderTrackIdentities.Add(new()
                {
                    Id = Guid.NewGuid(), CanonicalRecordingId = recording, ProviderId = "deezer", Scope = ProviderIdentityScope.Catalog,
                    ResourceKind = ProviderResourceKind.Track, ExternalId = hash, ExternalIdHash = hash,
                    Verification = ProviderIdentityVerification.Verified, VerificationMethod = "fixture", DecisionVersion = 1
                });
                var duration = (position == count - 1 ? seconds - seconds / count * (count - 1) : seconds / count) * 1000 + 654;
                var external = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.NewGuid(), OwnerUserId = owner, ProviderAccountId = account.Id, ProviderTrackIdentityId = identity.Id,
                    ProviderId = "spotify", Protocol = "jellyfin", BackendInstanceId = "primary", BackendPrincipalId = "user-1",
                    ResourceKind = "Track", ExternalIdHash = hash, SnapshotVersion = 1, ProviderRevision = "1", PayloadSha256 = hash,
                    PayloadJson = JsonSerializer.Serialize(new PlaylistSourceMetadata("Song", ["Artist"], DurationMilliseconds: duration, CanonicalRecordingId: recording))
                };
                db.ExternalMetadataSnapshots.Add(external);
                db.PlaylistSourceEntries.Add(new()
                {
                    Id = Guid.NewGuid(), PlaylistSourceSnapshotId = snapshot.Id, ExternalMetadataSnapshotId = external.Id,
                    SourcePosition = position, SourceEntryIdHash = hash
                });
            }
        }
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready, "fixture");
    }
}
