using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
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
    public async Task ClientApi_MatchChoicesArePersonalAndKeepHouseholdAndOtherListeners(bool administrator)
    {
        using var factory = ClientMatchFactory(administrator);
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        fixtures.Use(factory.Services, administrator);
        var (owner, other, snapshot) = await SeedClientMatchAsync(factory);
        const string path = "/allstarr/v1/items/ext-deezer-song-match/match";
        var initial = await ReadClientLyricsJsonAsync(client, path);
        Assert.Equal("household", initial["authority"]!.GetValue<string>());
        Assert.Equal("qobuz", initial["current"]!["provider"]!["id"]!.GetValue<string>());
        var candidate = initial["candidates"]!.AsArray().Single(item => item!["provider"]!["id"]!.GetValue<string>() == "deezer")!;
        Assert.Contains("isrc_exact", candidate["reasons"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(210000, candidate["durationMs"]!.GetValue<long>());
        Assert.True(candidate["isExternal"]!.GetValue<bool>());
        Assert.Equal("Deezer", candidate["provider"]!["name"]!.GetValue<string>());
        Assert.Equal(administrator ? "/allstarr/v1/providers/deezer/icon?variant=glyph" : null, candidate["provider"]!["glyphUrl"]?.GetValue<string>());
        using var set = await client.PutAsJsonAsync(path, new { candidateId = candidate["candidateId"]!.GetValue<string>(), expectedRevision = initial["revision"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var personal = JsonNode.Parse(await set.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", personal["authority"]!.GetValue<string>());
        Assert.Equal("deezer", personal["current"]!["provider"]!["id"]!.GetValue<string>());
        using var stale = await client.PostAsJsonAsync(path + "/reject", new { expectedRevision = initial["revision"]!.GetValue<string>() });
        await AssertClientApiError(stale, 409, "conflict");
        using var reject = await client.PostAsJsonAsync(path + "/reject", new { expectedRevision = personal["revision"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var rejected = JsonNode.Parse(await reject.Content.ReadAsStringAsync())!;
        Assert.Equal("rejected", rejected["state"]!.GetValue<string>());
        Assert.Null(rejected["current"]);
        using var staleClear = await client.DeleteAsync(path + "?expectedRevision=" + personal["revision"]!.GetValue<string>());
        await AssertClientApiError(staleClear, 409, "conflict");
        using var clear = await client.DeleteAsync(path + "?expectedRevision=" + rejected["revision"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var restored = await ReadClientLyricsJsonAsync(client, path);
        Assert.Equal("household", restored["authority"]!.GetValue<string>());
        Assert.DoesNotContain("Private choice", restored.ToJsonString());
        using var replay = await client.PutAsJsonAsync(path, new { candidateId = candidate["candidateId"]!.GetValue<string>(), expectedRevision = initial["revision"]!.GetValue<string>() });
        await AssertClientApiError(replay, 409, "conflict");
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var household = await db.ManualTrackOverrides.SingleAsync(item => item.OwnerUserId == null);
        Assert.Null(household.RevokedAt);
        Assert.Equal(1, household.Revision);
        var foreign = await db.ManualTrackOverrides.SingleAsync(item => item.OwnerUserId == other);
        Assert.Equal("Private choice", foreign.Reason);
        Assert.Null(foreign.RevokedAt);
        Assert.All(await db.ManualTrackOverrides.Where(item => item.OwnerUserId == owner).ToArrayAsync(), item => Assert.NotNull(item.RevokedAt));
        using var missing = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-unknown/match");
        await AssertClientApiError(missing, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_MatchMapsCanonicalAndNativeItemsOnlyThroughOwnedSnapshots()
    {
        var expectedRecording = Guid.NewGuid();
        using var factory = ClientMatchFactory(false, expectedRecording);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (_, _, snapshot) = await SeedClientMatchAsync(factory, expectedRecording);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var recording = await db.TrackMatches.Where(item => item.ExternalSnapshotId == snapshot).Select(item => item.CanonicalRecordingId).SingleAsync();
        var external = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-match/match");
        var canonical = await ReadClientLyricsJsonAsync(client, $"/allstarr/v1/items/ext-allstarr-song-{recording:D}/match");
        Assert.True(JsonNode.DeepEquals(external, canonical));
        var native = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/native/match");
        Assert.True(JsonNode.DeepEquals(external, native));
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        using var foreignRequest = ClientPlaybackRequest("/allstarr/v1/items/ext-deezer-song-match/match", "other");
        using var foreign = await client.SendAsync(foreignRequest);
        await AssertClientApiError(foreign, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_MatchMapsAPlaybackItemThroughItsRecordingToTheSourceSnapshot()
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMatchAsync(factory);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var recording = (await db.TrackMatches.SingleAsync()).CanonicalRecordingId!.Value;
            db.ProviderTrackIdentities.Add(new() { Id = Guid.NewGuid(), CanonicalRecordingId = recording, ProviderId = "deezer", ExternalId = "target", ExternalIdHash = ClientMatchPayloadHash("target"), ResourceKind = ProviderResourceKind.Track, Scope = ProviderIdentityScope.Catalog, Verification = ProviderIdentityVerification.Verified, DecisionVersion = 1, Revision = 1 });
            await db.SaveChangesAsync();
        }
        var source = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-match/match");
        var playback = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-target/match");
        Assert.True(JsonNode.DeepEquals(source, playback));
    }

    [Theory]
    [InlineData("household")]
    [InlineData("automatic")]
    public async Task ClientApi_MatchRejectsRevisionsAfterOtherLayersChange(string layer)
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMatchAsync(factory);
        const string path = "/allstarr/v1/items/ext-deezer-song-match/match";
        var match = await ReadClientLyricsJsonAsync(client, path);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            if (layer == "household") (await db.ManualTrackOverrides.SingleAsync(item => item.OwnerUserId == null)).Revision++;
            else (await db.TrackMatches.SingleAsync()).Revision++;
            await db.SaveChangesAsync();
        }
        using var reject = await client.PostAsJsonAsync(path + "/reject", new { expectedRevision = match["revision"]!.GetValue<string>() });
        await AssertClientApiError(reject, 409, "conflict");
    }

    [Fact]
    public async Task ClientApi_MatchPersonalLayersStaySeparateForListenersWithTheSameSource()
    {
        using var factory = ClientMatchFactory(true);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (_, other, snapshot) = await SeedClientMatchAsync(factory);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var source = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == snapshot);
            db.ExternalMetadataSnapshots.Add(new() { Id = Guid.NewGuid(), OwnerUserId = other, BackendInstanceId = "primary", BackendPrincipalId = "other", Protocol = "jellyfin", ProviderId = source.ProviderId, ResourceKind = "track", ExternalIdHash = source.ExternalIdHash, SnapshotVersion = 1, RetrievedAt = DateTimeOffset.UtcNow, PayloadJson = source.PayloadJson, PayloadSha256 = source.PayloadSha256 });
            await db.SaveChangesAsync();
        }
        const string path = "/allstarr/v1/items/ext-deezer-song-match/match";
        var initial = await ReadClientLyricsJsonAsync(client, path);
        var candidate = initial["candidates"]!.AsArray().Single(item => item!["provider"]!["id"]!.GetValue<string>() == "deezer")!;
        using var set = await client.PutAsJsonAsync(path, new { candidateId = candidate["candidateId"]!.GetValue<string>(), expectedRevision = initial["revision"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var personal = await ReadClientLyricsJsonAsync(client, path);
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        using var otherGet = ClientPlaybackRequest(path, "other");
        using var otherResponse = await client.SendAsync(otherGet);
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
        var foreign = JsonNode.Parse(await otherResponse.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", foreign["authority"]!.GetValue<string>());
        Assert.NotEqual(personal["current"]!["candidateId"]!.GetValue<string>(), foreign["current"]!["candidateId"]!.GetValue<string>());
        using var otherClear = ClientPlaybackRequest(path + "?expectedRevision=" + foreign["revision"]!.GetValue<string>(), "other");
        otherClear.Method = HttpMethod.Delete;
        using var cleared = await client.SendAsync(otherClear);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var unchanged = await ReadClientLyricsJsonAsync(client, path);
        Assert.True(JsonNode.DeepEquals(personal, unchanged));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_MatchCanChooseAnAccessibleLibraryCandidate(bool glyph)
    {
        using var factory = ClientMatchFactory(false);
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services, glyph);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, _, snapshot) = await SeedClientMatchAsync(factory);
        var localId = Guid.NewGuid();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var decision = await db.TrackMatches.SingleAsync(item => item.ExternalSnapshotId == snapshot);
            db.LibraryTracks.Add(new() { Id = localId, OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "primary", BackendLibraryId = "library", BackendItemId = "local", CanonicalRecordingId = decision.CanonicalRecordingId, Title = "Library fixture", Artist = "Artist", Revision = 1 });
            var candidates = JsonNode.Parse(decision.CandidateResultsJson)!.AsArray();
            candidates.Add(JsonSerializer.SerializeToNode(new { libraryTrackId = localId, backendItemId = "local", title = "Library fixture", artist = "Artist", isLocal = true, confidence = .96, reasons = new[] { "title_exact" } }));
            decision.CandidateResultsJson = candidates.ToJsonString();
            await db.SaveChangesAsync();
        }
        const string path = "/allstarr/v1/items/ext-deezer-song-match/match";
        var match = await ReadClientLyricsJsonAsync(client, path);
        var candidate = match["candidates"]!.AsArray().Single(item => !item!["isExternal"]!.GetValue<bool>())!;
        Assert.Equal("/allstarr/v1/providers/jellyfin/icon", candidate["provider"]!["iconUrl"]!.GetValue<string>());
        Assert.Equal(glyph ? "/allstarr/v1/providers/jellyfin/icon?variant=glyph" : null, candidate["provider"]!["glyphUrl"]?.GetValue<string>());
        using var iconResponse = await client.GetAsync("/allstarr/v1/providers/jellyfin/icon");
        Assert.Equal(HttpStatusCode.OK, iconResponse.StatusCode);
        using var response = await client.PutAsJsonAsync(path, new { candidateId = candidate["candidateId"]!.GetValue<string>(), expectedRevision = match["revision"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var chosen = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("Library fixture", chosen["current"]!["title"]!.GetValue<string>());
        Assert.Equal("personal", chosen["authority"]!.GetValue<string>());
        Assert.False(chosen["current"]!["isExternal"]!.GetValue<bool>());
        await using var saved = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        Assert.Equal(localId, (await saved.ManualTrackOverrides.SingleAsync(item => item.OwnerUserId == owner && item.RevokedAt == null)).LibraryTrackId);
    }

    [Fact]
    public async Task ClientApi_MatchKeepsSavedEvidenceWhenMetadataIsRefreshed()
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, _, snapshot) = await SeedClientMatchAsync(factory);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var source = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == snapshot);
            db.ExternalMetadataSnapshots.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, BackendInstanceId = "primary", BackendPrincipalId = "listener", Protocol = "jellyfin", ProviderId = source.ProviderId, ResourceKind = "track", ExternalIdHash = source.ExternalIdHash, SnapshotVersion = 2, RetrievedAt = source.RetrievedAt.AddMinutes(1), PayloadJson = source.PayloadJson, PayloadSha256 = source.PayloadSha256 });
            (await db.ManualTrackOverrides.SingleAsync(item => item.OwnerUserId == null)).RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var match = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-match/match");
        Assert.Equal("automatic", match["authority"]!.GetValue<string>());
        Assert.Equal("accepted", match["state"]!.GetValue<string>());
        Assert.Contains(match["candidates"]!.AsArray(), item => item!["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "isrc_exact"));
    }

    [Fact]
    public async Task ClientApi_MatchUsesAccessibleSnapshotsWhenANewerAccountIsDisabled()
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, _, snapshot) = await SeedClientMatchAsync(factory);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var source = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == snapshot);
            var accountId = Guid.NewGuid();
            db.ProviderAccounts.Add(new() { Id = accountId, ProviderId = "deezer", OwnerUserId = owner, Enabled = false, Revision = 1 });
            db.ExternalMetadataSnapshots.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderAccountId = accountId, BackendInstanceId = "primary", BackendPrincipalId = "listener", Protocol = "jellyfin", ProviderId = source.ProviderId, ResourceKind = "track", ExternalIdHash = source.ExternalIdHash, SnapshotVersion = 2, RetrievedAt = source.RetrievedAt.AddMinutes(1), PayloadJson = """{"Title":"Unavailable metadata","Artist":"Artist"}""", PayloadSha256 = ClientMatchPayloadHash("""{"Title":"Unavailable metadata","Artist":"Artist"}""") });
            await db.SaveChangesAsync();
        }
        var match = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-match/match");
        Assert.Equal("household", match["authority"]!.GetValue<string>());
        Assert.DoesNotContain("Unavailable metadata", match.ToJsonString());
    }

    [Fact]
    public async Task ClientApi_MatchCandidatesSupportProviderIdsWithHyphens()
    {
        using var factory = ClientMatchFactory(false, candidateProvider: "apple-musickit");
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMatchAsync(factory);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var decision = await db.TrackMatches.SingleAsync();
            decision.CandidateResultsJson = decision.CandidateResultsJson.Replace("\"deezer\":\"match\"", "\"apple-musickit\":\"match\"", StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }
        const string path = "/allstarr/v1/items/ext-deezer-song-match/match";
        var match = await ReadClientLyricsJsonAsync(client, path);
        var candidate = match["candidates"]!.AsArray().Single(item => item!["provider"]!["id"]!.GetValue<string>() == "apple-musickit")!;
        using var response = await client.PutAsJsonAsync(path, new { candidateId = candidate["candidateId"]!.GetValue<string>(), expectedRevision = match["revision"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var chosen = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("apple-musickit", chosen["current"]!["provider"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApi_MatchRejectsProviderEvidenceWithoutUsingItsScoringIdAsALibraryTrack()
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMatchAsync(factory);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var decision = await db.TrackMatches.SingleAsync();
            decision.CandidateResultsJson = JsonSerializer.Serialize(new[] { new TrackMatchCandidateScore(Guid.NewGuid(), "ext-deezer-song-match", .97, ["isrc_exact"], [], Title: "Fixture", ProviderTrackIds: new Dictionary<string, string> { ["deezer"] = "match" }, IsLocal: false) });
            await db.SaveChangesAsync();
        }
        const string path = "/allstarr/v1/items/ext-deezer-song-match/match";
        var match = await ReadClientLyricsJsonAsync(client, path);
        using var response = await client.PostAsJsonAsync(path + "/reject", new { expectedRevision = match["revision"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rejected = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("rejected", rejected["state"]!.GetValue<string>());
        Assert.Null(rejected["current"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"candidateId\":\"x\",\"expectedRevision\":null}")]
    [InlineData("{\"candidateId\":\"x\",\"expectedRevision\":1}")]
    public async Task ClientApi_MatchRequiresAnOpaqueRevision(string body)
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PutAsync("/allstarr/v1/items/native/match", new StringContent(body, Encoding.UTF8, "application/json"));
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Theory]
    [InlineData("ext-deezer-album-match")]
    [InlineData("ext-deezer-artist-match")]
    [InlineData("ext-deezer-playlist-match")]
    public async Task ClientApi_MatchRequiresASongItem(string itemId)
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMatchAsync(factory);
        using var response = await client.GetAsync($"/allstarr/v1/items/{itemId}/match");
        await AssertClientApiError(response, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_MatchAndReportsOffSwitchBlocksAllWritesAndReads()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory, enabled: false);
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/items/native/match"), (HttpMethod.Put, "/items/native/match"), (HttpMethod.Post, "/items/native/match/reject"), (HttpMethod.Delete, "/items/native/match"), (HttpMethod.Post, "/reports") })
        {
            using var request = new HttpRequestMessage(method, "/allstarr/v1" + path) { Content = JsonContent.Create(new { }) };
            using var response = await client.SendAsync(request);
            await AssertClientApiError(response, 404, "not_found");
        }
    }

    private static string ClientMatchPayloadHash(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static ProtocolFactory ClientMatchFactory(bool administrator, Guid? recording = null, string candidateProvider = "deezer")
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([candidateProvider, "qobuz"]);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((allstarr.Core.Protocols.ProtocolExecutionContext _, string provider, string id) => new Song { Id = $"ext-{provider}-song-{id}", ExternalProvider = provider, ExternalId = id, Title = "Fixture", Artist = "Artist" });
        return ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal) ? """{"Id":"other"}""" : JsonSerializer.Serialize(new { Id = "listener", Policy = new { IsAdministrator = administrator } }))
            : request.RequestUri.AbsolutePath == "/Items" ? Json(200, """{"Items":[{"Id":"native","Type":"Audio","Name":"Fixture"}]}""") : Json(404, "{}"), services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                var policies = new Mock<IEffectiveProviderPolicyResolver>();
                var policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                    ImmutableHashSet<string>.Empty, "Lossless", .07);
                policies.Setup(service => service.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Guid user, CancellationToken _) => policy with { UserId = user });
                policies.Setup(service => service.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(policy);
                services.RemoveAll<IEffectiveProviderPolicyResolver>();
                services.AddSingleton(policies.Object);
                var access = new Mock<IBackendLibraryAccessResolver>();
                access.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BackendLibraryAccess(true, ["library"]));
                access.Setup(service => service.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Guid user, CancellationToken _) => new BackendLibraryAccessContext(new(ProtocolKind.Jellyfin, "primary", "listener",
                        new(user, "jellyfin", "primary", "listener", "Listener", false), "fixture", DateTimeOffset.UtcNow.AddMinutes(5), default), new(true, ["library"])));
                services.RemoveAll<IBackendLibraryAccessResolver>();
                services.AddSingleton(access.Object);
                if (recording.HasValue)
                {
                    var library = new Mock<ILibraryIndexService>();
                    library.Setup(service => service.FindByBackendItemAsync(It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), "native", It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new IndexedLibraryTrack(Guid.NewGuid(), "native", "/fixture", "Fixture", "Artist", "", null, 210000, null, null, null, null, recording.Value, new Dictionary<string, string>(), default, default, 1));
                    services.RemoveAll<ILibraryIndexService>();
                    services.AddSingleton(library.Object);
                }
            });
    }

    private static async Task<(Guid Owner, Guid Other, Guid Snapshot)> SeedClientMatchAsync(ProtocolFactory factory, Guid? knownRecording = null)
    {
        var resolver = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var owner = (await resolver.ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var other = (await resolver.ResolveAsync(new("Jellyfin", "other")))!.UserId;
        var snapshot = Guid.NewGuid();
        var recording = knownRecording ?? Guid.NewGuid();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("match")));
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner, PublicId = CanonicalRecordingRecord.DefaultPublicId(recording), Title = "Fixture", Revision = 1 });
        db.ProviderTrackIdentities.Add(new() { Id = Guid.NewGuid(), CanonicalRecordingId = recording, ProviderId = "deezer", ExternalId = "match", ExternalIdHash = hash, ResourceKind = ProviderResourceKind.Track, Scope = ProviderIdentityScope.Catalog, Verification = ProviderIdentityVerification.Verified, DecisionVersion = 1, Revision = 1 });
        db.ExternalMetadataSnapshots.Add(new() { Id = snapshot, OwnerUserId = owner, BackendInstanceId = "primary", BackendPrincipalId = "listener", Protocol = "jellyfin", ProviderId = "deezer", ResourceKind = "track", ExternalIdHash = hash, SnapshotVersion = 1, RetrievedAt = DateTimeOffset.UtcNow, PayloadJson = """{"Title":"Fixture","Artist":"Artist","DurationMilliseconds":210000}""", PayloadSha256 = ClientMatchPayloadHash("""{"Title":"Fixture","Artist":"Artist","DurationMilliseconds":210000}""") });
        db.TrackMatches.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, ExternalSnapshotId = snapshot, CanonicalRecordingId = recording, State = TrackMatchState.Accepted, Confidence = .97, DecisionVersion = 1, Revision = 1, CandidateResultsJson = """[{"libraryTrackId":"00000000-0000-0000-0000-000000000001","backendItemId":"ext-deezer-song-match","title":"Fixture","artist":"Artist","album":"Album","durationMilliseconds":210000,"candidateIsrc":"US1234567890","confidence":0.97,"isLocal":false,"providerTrackIds":{"deezer":"match"},"reasons":["isrc_exact"]}]""" });
        db.ManualTrackOverrides.AddRange(new ManualTrackOverrideRecord { Id = Guid.NewGuid(), ExternalSnapshotId = snapshot, SourceProviderId = "deezer", SourceExternalIdHash = hash, TargetProviderId = "qobuz", TargetExternalId = "household", Decision = ManualOverrideDecision.Pin, Reason = "Shared choice", DecisionVersion = 1, Revision = 1 }, new ManualTrackOverrideRecord { Id = Guid.NewGuid(), OwnerUserId = other, ExternalSnapshotId = snapshot, SourceProviderId = "deezer", SourceExternalIdHash = hash, TargetProviderId = "qobuz", TargetExternalId = "private", Decision = ManualOverrideDecision.Pin, Reason = "Private choice", DecisionVersion = 1, Revision = 1 });
        await db.SaveChangesAsync();
        return (owner, other, snapshot);
    }
}
