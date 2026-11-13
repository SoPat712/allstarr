using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_TrackFactsPreserveTheItemDetailsSourceFacts()
    {
        var gateway = V2BatchGateway();
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[]}"""), services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var recording = await SeedV2DetailsAsync(factory, "listener", "facts", personal: false);
        var read = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks/" + V2Ids.Track(recording));
        var track = read["track"]!;
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-facts" } });
        response.EnsureSuccessStatusCode();
        var original = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["items"]![0]!;
        foreach (var name in new[] { "isExternal", "canonicalRecordingId", "explicit", "saved", "keep", "match", "lyrics", "playCount", "lastPlayedAt" })
            Assert.True(JsonNode.DeepEquals(original[name], track[name]), name);
        // The same provider, with its icons served by the v2 icon route.
        foreach (var name in new[] { "id", "name" })
            Assert.True(JsonNode.DeepEquals(original["provider"]![name], track["provider"]![name]), "provider." + name);
        Assert.StartsWith("/allstarr/v2/providers/", track["provider"]!["iconUrl"]!.GetValue<string>());
        Assert.Equal(original["isrc"]!.GetValue<string>(), track["externalIds"]!["isrc"]!.GetValue<string>());
        foreach (var name in new[] { "codec", "bitDepth", "sampleRate", "bitrate" })
            Assert.True(JsonNode.DeepEquals(original["audio"]![name], track["format"]![name]), name);
    }

    [Fact]
    public async Task ClientApiV2_TrackFactsDoNotExposeAnotherListenersStoredTrack()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(404, "{}"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var recording = await SeedV2DetailsAsync(factory, "other", "foreign", personal: false);
        var id = V2Ids.Track(recording);
        using var single = await client.GetAsync("/allstarr/v2/tracks/" + id);
        await AssertClientApiError(single, 404, "not_found");
        Assert.Equal(404, (await ReadV2Batch(client, "/allstarr/v2/tracks/" + id))["status"]!.GetValue<int>());
        var list = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks?ids=" + id);
        Assert.Empty(list["tracks"]!.AsArray());
        Assert.Equal(id, Assert.Single(list["missing"]!.AsArray())!.GetValue<string>());
        await AssertV2ResourceRead(client, "/allstarr/v2/tracks?ids=" + id, list);
    }

    [Fact]
    public async Task ClientApiV2_OneHundredLibraryTracksUseTwoBulkItemReads()
    {
        var itemReads = 0;
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (request.RequestUri.AbsolutePath == "/Users/listener/Items")
            {
                itemReads++;
                var ids = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["ids"].ToString().Split(',');
                Assert.Equal(100, ids.Length);
                return Json(200, JsonSerializer.Serialize(new { Items = ids.Select(id => new { Id = id, Type = "Audio", Name = "Library song" }) }));
            }
            throw new InvalidOperationException("Unexpected upstream request");
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var result = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks?ids=" + string.Join(',', Enumerable.Range(0, 100).Select(index => "native" + index)));
        Assert.Equal(100, result["tracks"]!.AsArray().Count);
        Assert.Equal(2, itemReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_OneHundredTrackFactsUseBatchQueriesWithoutPerTrackLookups(bool canonical)
    {
        var reads = new ItemDetailsReads();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["deezer"]);
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[]}"""), services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
                services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                    new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>())
                        .AddInterceptors(reads).Options));
            });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var ids = new List<string>();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            for (var index = 0; index < 100; index++)
            {
                var recording = Guid.NewGuid();
                var externalId = "bulk-" + index;
                var identityId = Guid.NewGuid();
                var hash = CanonicalCatalogKeys.Hash(externalId);
                ids.Add(canonical ? V2Ids.Track(recording) : "ext-deezer-song-" + externalId);
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner, PublicId = CanonicalRecordingRecord.DefaultPublicId(recording), Title = "Stored song", Revision = 1 });
                db.ProviderTrackIdentities.Add(new()
                {
                    Id = identityId,
                    CanonicalRecordingId = recording,
                    ProviderId = "deezer",
                    ExternalId = externalId,
                    ExternalIdHash = hash,
                    ResourceKind = ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Catalog,
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "isrc",
                    DecisionVersion = 1,
                    Revision = 1
                });
                var payload = JsonSerializer.Serialize(V2BatchSong(externalId));
                db.ExternalMetadataSnapshots.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    BackendPrincipalId = "listener",
                    ProviderId = "deezer",
                    ProviderTrackIdentityId = identityId,
                    ResourceKind = "track",
                    ExternalIdHash = hash,
                    SnapshotVersion = 1,
                    PayloadJson = payload,
                    PayloadSha256 = CanonicalCatalogKeys.Hash(payload),
                    RetrievedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks?ids=" + ids[0]);
        reads.Count = 0;
        var single = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks?ids=" + ids[0]);
        var oneReads = reads.Count;
        reads.Count = 0;
        var result = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks?ids=" + string.Join(',', ids));
        var coldReads = reads.Count;
        reads.Count = 0;
        var warm = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks?ids=" + string.Join(',', ids));
        var hundredReads = reads.Count;
        Assert.True(JsonNode.DeepEquals(result, warm));
        Assert.Equal(100, result["tracks"]!.AsArray().Count);
        Assert.Empty(result["missing"]!.AsArray());
        var track = result["tracks"]![0]!;
        Assert.True(JsonNode.DeepEquals(single["tracks"]![0], track));
        Assert.Equal("deezer", track["provider"]!["id"]!.GetValue<string>());
        Assert.True(Guid.TryParse(track["canonicalRecordingId"]!.GetValue<string>(), out _));
        var measurement = $"{(canonical ? "recording" : "service")} ids: one warm track {oneReads} database reads; 100 tracks cold {coldReads}, warm {hundredReads}";
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.True(hundredReads <= oneReads + 5 && coldReads <= 80, measurement);
        gateway.Verify(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        gateway.Verify(item => item.GetStreamingProviderAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static Task<Guid> SeedV2DetailsAsync(ProtocolFactory factory, string backendUser, string externalId, bool personal, Song? song = null) =>
        SeedClientDetailsAsync(factory, backendUser, externalId, personal, song ?? V2BatchSong(externalId));
}
