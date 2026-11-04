using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Favorites;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using System.Collections.Immutable;
using Moq;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ClientApi_ItemDetailsRejectsInvalidBatchSizes(int count)
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = Enumerable.Repeat("unknown", count) });
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApi_ItemDetailsIsAuthenticatedAndOptIn()
    {
        var requests = new List<string>();
        using var factory = ClientDetailsFactory(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Json(200, """{"Id":"listener"}""");
        });
        using var client = CreateClientApiClient(factory, enabled: false);
        using var disabled = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native" } });
        await AssertClientApiError(disabled, 404, "not_found");
        Assert.Empty(requests);
        await SetClientApiEnabledAsync(factory, true);
        using var anonymous = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native" } });
        await AssertClientApiError(anonymous, 401, "unauthorized");
    }

    [Fact]
    public async Task ClientApi_ItemDetailsBatchesNativeVisibilityAndPreservesNullFields()
    {
        var requests = new List<string>();
        using var factory = ClientDetailsFactory(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            Assert.Equal("/Users/listener/Items", request.RequestUri.AbsolutePath);
            Assert.Contains("Token=\"listener-token\"", request.Headers.GetValues("Authorization").Single());
            Assert.Contains("native", request.RequestUri.Query);
            return Json(200, """{"Items":[{"Id":"native","Type":"Audio","UserData":{"IsFavorite":true}}]}""");
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native", "foreign", "ext-deezer-song-unknown" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("native", item.GetProperty("id").GetString());
        Assert.False(item.GetProperty("isExternal").GetBoolean());
        Assert.True(item.GetProperty("saved").GetBoolean());
        foreach (var field in new[] { "canonicalRecordingId", "isrc", "provider", "audio", "explicit", "match" })
            Assert.Equal(JsonValueKind.Null, item.GetProperty(field).ValueKind);
        Assert.Equal("none", item.GetProperty("keep").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("keep").GetProperty("progress").ValueKind);
        Assert.False(item.GetProperty("lyrics").GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("lyrics").GetProperty("format").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("lyrics").GetProperty("source").ValueKind);
        Assert.Equal(["foreign", "ext-deezer-song-unknown"], json.RootElement.GetProperty("missing").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_ItemDetailsUsesStoredPersonalMatchAndMeasuredAudio(bool glyph)
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"));
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services, glyph);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "known", personal: true);
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-known" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(recording.ToString("D"), item.GetProperty("canonicalRecordingId").GetString());
        Assert.Equal("USABC1200001", item.GetProperty("isrc").GetString());
        Assert.True(item.GetProperty("isExternal").GetBoolean());
        Assert.True(item.GetProperty("explicit").GetBoolean());
        Assert.Equal("deezer", item.GetProperty("provider").GetProperty("id").GetString());
        Assert.Equal("/allstarr/v1/providers/deezer/icon", item.GetProperty("provider").GetProperty("iconUrl").GetString());
        Assert.Equal(glyph ? "/allstarr/v1/providers/deezer/icon?variant=glyph" : null, item.GetProperty("provider").GetProperty("glyphUrl").GetString());
        Assert.Equal("flac", item.GetProperty("audio").GetProperty("codec").GetString());
        Assert.Equal(24, item.GetProperty("audio").GetProperty("bitDepth").GetInt32());
        Assert.True(item.GetProperty("saved").GetBoolean());
        Assert.Equal("kept", item.GetProperty("keep").GetProperty("state").GetString());
        Assert.True(item.GetProperty("lyrics").GetProperty("available").GetBoolean());
        Assert.Equal("word", item.GetProperty("lyrics").GetProperty("format").GetString());
        Assert.Equal("kept", item.GetProperty("lyrics").GetProperty("source").GetString());
        Assert.Equal("pinned", item.GetProperty("match").GetProperty("state").GetString());
        Assert.Equal("personal", item.GetProperty("match").GetProperty("authority").GetString());
        Assert.Equal(0.95, item.GetProperty("match").GetProperty("confidence").GetDouble());
        Assert.Empty(json.RootElement.GetProperty("missing").EnumerateArray());
    }

    [Theory]
    [InlineData(FavoriteActionState.Pending, "inProgress")]
    [InlineData(FavoriteActionState.Running, "inProgress")]
    [InlineData(FavoriteActionState.Failed, "failed")]
    [InlineData(FavoriteActionState.Cancelled, "none")]
    public async Task ClientApi_ItemDetailsKeepUsesCurrentListenersDownloadAction(FavoriteActionState actionState, string expected)
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientDetailsAsync(factory, "listener", "pending", personal: true);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var action = await db.FavoriteActions.SingleAsync();
            action.State = actionState;
            var reference = await db.ManagedFileReferences.SingleAsync();
            reference.ReleasedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-pending" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keep = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("keep");
        Assert.Equal(expected, keep.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, keep.GetProperty("progress").ValueKind);
    }

    [Theory]
    [InlineData("lrclib")]
    [InlineData("lyricsplus")]
    [InlineData("musixmatch")]
    [InlineData("unison")]
    public async Task ClientApi_ItemDetailsReadsCachedLyricsAndHonorsPersonalPinnedChoice(string source)
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "cached", personal: false);
        var cache = factory.Services.GetRequiredService<IApplicationCache>();
        var key = "lrclib:automatic:v2:" + CacheKeyBuilder.BuildLyricsKey("Stored artist", "Stored song", "", 0);
        if (source == "lrclib") await cache.SetStringAsync(key,
            JsonSerializer.Serialize(new allstarr.Models.Lyrics.LyricsInfo { Source = source, PlainLyrics = "Stored words" }, ClientApiContract.JsonOptions));
        else
        {
            var request = new ProviderLyricsRequest(recording, new(source, ProviderResourceKind.Track, CanonicalCatalogKeys.Hash(recording.ToString("N"))),
                preferredFormat: ProviderLyricsFormat.LineTimed, trackTitle: "Stored song", artistNames: ["Stored artist"], isrc: "USABC1200001");
            key = source switch
            {
                "lyricsplus" => LyricsPlusCapabilityAdapter.StoredCacheKey((await factory.Services.GetRequiredService<IDurableRuntimeSettings>().GetAsync("LyricsPlus:BaseUrl")).NormalizedValue, request),
                "musixmatch" => MusixmatchLyricsCapabilityAdapter.BuildCacheKey(null, request),
                _ => "lyrics:v3:" + source + ":" + CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
                { request.Isrc, request.TrackTitle, request.ArtistNames, request.AlbumTitle, request.DurationSeconds }))
            };
            await cache.SetAsync(key, new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, source, ProviderLyricsFormat.PlainText, "Stored words"));
        }
        using var initial = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-cached" } });
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var initialBody = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
        var cached = Assert.Single(initialBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(cached.GetProperty("lyrics").GetProperty("available").GetBoolean());
        Assert.Equal("plain", cached.GetProperty("lyrics").GetProperty("format").GetString());
        Assert.Equal(source, cached.GetProperty("lyrics").GetProperty("source").GetString());
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.RecordingLyricsChoices.Add(new() { Id = Guid.NewGuid(), CanonicalRecordingId = recording, OwnerUserId = owner, SourceId = "genius", Revision = 1 });
            await db.SaveChangesAsync();
        }
        using var pinned = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-cached" } });
        using var pinnedBody = JsonDocument.Parse(await pinned.Content.ReadAsStringAsync());
        var lyrics = Assert.Single(pinnedBody.RootElement.GetProperty("items").EnumerateArray()).GetProperty("lyrics");
        Assert.False(lyrics.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, lyrics.GetProperty("format").ValueKind);
        Assert.Equal("genius", lyrics.GetProperty("source").GetString());
    }

    [Fact]
    public async Task ClientApi_ItemDetailsAcceptsOneHundredIdsAndCanonicalIdsWithoutLosingRequestedIds()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "canonical", personal: false);
        var id = CanonicalRecordingRecord.DefaultPublicId(recording);
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = Enumerable.Repeat(id, 100) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(100, items.Length);
        Assert.All(items, item => Assert.Equal(id, item.GetProperty("id").GetString()));
        Assert.Empty(json.RootElement.GetProperty("missing").EnumerateArray());
    }

    [Fact]
    public async Task ClientApi_ItemDetailsCachedAccountLyricsAreBoundToTheListener()
    {
        const string spotifyId = "1234567890123456789012";
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "spicy", personal: false);
        var resolver = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var principal = (await resolver.ResolveAsync(new("Jellyfin", "listener")))!;
        var other = (await resolver.ResolveAsync(new("Jellyfin", "other")))!;
        var accountId = Guid.NewGuid();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recording,
                ProviderId = "spotify",
                ExternalId = spotifyId,
                ExternalIdHash = CanonicalCatalogKeys.Hash(spotifyId),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "isrc",
                DecisionVersion = 1,
                Revision = 1
            });
            db.ProviderAccounts.Add(new() { Id = accountId, ProviderId = "spicylyrics", DisplayName = "Shared lyrics", Enabled = true, Revision = 1 });
            db.RecordingLyricsChoices.Add(new() { Id = Guid.NewGuid(), CanonicalRecordingId = recording, OwnerUserId = principal.UserId, SourceId = "spicylyrics", Revision = 1 });
            await db.SaveChangesAsync();
        }
        var account = new ProviderAccountContext(accountId, "spicylyrics", ProviderAccountScope.Shared, 1);
        var foreignActor = new ProviderActorContext(ProviderActorKind.User, other.UserId, new("jellyfin", "primary", "other"));
        var ownActor = new ProviderActorContext(ProviderActorKind.User, principal.UserId, new("jellyfin", "primary", "listener"));
        var cache = factory.Services.GetRequiredService<IApplicationCache>();
        var result = new SpicyLyricsCapabilityAdapter.CachedLyrics(
            new(ProviderLyricsAvailabilityState.Available, "spicylyrics", ProviderLyricsFormat.LineTimed, "[00:01.00]Stored words"),
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        await cache.SetAsync(SpicyLyricsCapabilityAdapter.StoredCacheKey(foreignActor, account, "spicylyrics", spotifyId), result);
        using var foreign = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-spicy" } });
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        using var foreignBody = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync());
        Assert.False(Assert.Single(foreignBody.RootElement.GetProperty("items").EnumerateArray()).GetProperty("lyrics").GetProperty("available").GetBoolean());
        await cache.SetAsync(SpicyLyricsCapabilityAdapter.StoredCacheKey(ownActor, account, "spicylyrics", spotifyId), result);
        using var own = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-spicy" } });
        using var ownBody = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
        var lyrics = Assert.Single(ownBody.RootElement.GetProperty("items").EnumerateArray()).GetProperty("lyrics");
        Assert.True(lyrics.GetProperty("available").GetBoolean());
        Assert.Equal("line", lyrics.GetProperty("format").GetString());
    }

    [Fact]
    public async Task ClientApi_ItemDetailsReadsOnlyTheListenersStoredMetadataCache()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var resolver = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var principal = (await resolver.ResolveAsync(new("Jellyfin", "listener")))!;
        var other = (await resolver.ResolveAsync(new("Jellyfin", "other")))!;
        var ownActor = new ProviderActorContext(ProviderActorKind.User, principal.UserId, new("jellyfin", "primary", "listener"));
        var otherActor = new ProviderActorContext(ProviderActorKind.User, other.UserId, new("jellyfin", "primary", "other"));
        var cache = factory.Services.GetRequiredService<IApplicationCache>();
        var payload = """{"Title":"Cached song","Artist":"Stored artist","ExternalProvider":"deezer","ExternalId":"cached-only","Codec":"advertised","BitDepth":32}""";
        await cache.SetStringAsync(ProtocolProviderGateway.MetadataCacheKey(ownActor, "deezer", ProviderResourceKind.Track, "cached-only"), payload);
        await cache.SetStringAsync(ProtocolProviderGateway.MetadataCacheKey(otherActor, "deezer", ProviderResourceKind.Track, "foreign-cache"), payload);
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-cached-only", "ext-deezer-song-foreign-cache" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("ext-deezer-song-cached-only", item.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("audio").ValueKind);
        Assert.Equal("ext-deezer-song-foreign-cache", Assert.Single(json.RootElement.GetProperty("missing").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task ClientApi_ItemDetailsKeepsNativeResultsWhenUnknownIdsAreMalformed()
    {
        var native = Guid.NewGuid();
        var requests = 0;
        var errors = new Mock<Microsoft.Extensions.Logging.ILogger<allstarr.Middleware.GlobalExceptionHandler>>();
        using var factory = ClientDetailsFactory(request =>
        {
            requests++;
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            return request.RequestUri.Query.Contains("malformed", StringComparison.Ordinal)
                ? Json(400, "{}") : Json(200, $$"""{"Items":[{"Id":"{{native:N}}"}]}""");
        }, services => services.AddSingleton(errors.Object));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { native.ToString("D"), "malformed" } });
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            errors.Invocations.FirstOrDefault(item => item.Method.Name == "Log")?.Arguments[3]?.ToString() ?? await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(native.ToString("D"), Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("malformed", Assert.Single(json.RootElement.GetProperty("missing").EnumerateArray()).GetString());
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":null}")]
    [InlineData("{\"ids\":[null]}")]
    [InlineData("{\"ids\":\"wrong\"}")]
    public async Task ClientApi_ItemDetailsInvalidBodiesUseTheContract(string body)
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PostAsync("/allstarr/v1/items/details", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApi_ItemDetailsNeverUsesAnotherListenersStoredState()
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var foreign = await SeedClientDetailsAsync(factory, "other-listener", "foreign", personal: true);
        await SeedClientDetailsAsync(factory, "listener", "shared", personal: false);
        await SeedClientDetailsAsync(factory, "other-listener", "shared", personal: true);
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-foreign", CanonicalRecordingRecord.DefaultPublicId(foreign), "ext-deezer-song-shared" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["ext-deezer-song-foreign", CanonicalRecordingRecord.DefaultPublicId(foreign)], json.RootElement.GetProperty("missing").EnumerateArray().Select(item => item.GetString()!).ToArray());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(item.GetProperty("saved").GetBoolean());
        Assert.Equal("none", item.GetProperty("keep").GetProperty("state").GetString());
        Assert.Equal("automatic", item.GetProperty("match").GetProperty("authority").GetString());
        Assert.Equal("accepted", item.GetProperty("match").GetProperty("state").GetString());
        Assert.Equal("aac", item.GetProperty("audio").GetProperty("codec").GetString());
    }

    private static async Task<Guid> SeedClientDetailsAsync(ProtocolFactory factory, string backendUser, string externalId, bool personal)
    {
        var principal = await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new BackendIdentityDescriptor("Jellyfin", backendUser));
        var owner = principal!.UserId;
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        var existing = await db.ProviderTrackIdentities.FirstOrDefaultAsync(item => item.ProviderId == "deezer" && item.ExternalId == externalId);
        var recording = existing?.CanonicalRecordingId ?? Guid.NewGuid();
        if (existing == null)
        {
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner, PublicId = CanonicalRecordingRecord.DefaultPublicId(recording), Title = "Stored song", Revision = 1 });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recording,
                ProviderId = "deezer",
                ExternalId = externalId,
                ExternalIdHash = CanonicalCatalogKeys.Hash(externalId),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "isrc",
                DecisionVersion = 1,
                Revision = 1
            });
        }
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = backendUser,
            ProviderId = "deezer",
            ResourceKind = "track",
            ExternalIdHash = CanonicalCatalogKeys.Hash(externalId),
            SnapshotVersion = 1,
            PayloadJson = """{"Title":"Stored song","Artist":"Stored artist","Isrc":"USABC1200001","ExplicitContentLyrics":1,"Codec":"advertised","BitDepth":32}""",
            RetrievedAt = DateTimeOffset.UtcNow
        };
        db.ExternalMetadataSnapshots.Add(snapshot);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = backendUser,
            ProviderId = "deezer",
            ResourceKind = "track-media",
            ExternalIdHash = CanonicalCatalogKeys.Hash(externalId),
            SnapshotVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new TrackMediaFacts(personal ? new("audio/flac", "flac", "flac", 900000, 96000, 24, 2) : new("audio/mp4", "m4a", "aac", 256000, 44100, null, 2))),
            RetrievedAt = DateTimeOffset.UtcNow
        });
        db.TrackMatches.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, ExternalSnapshotId = snapshot.Id, CanonicalRecordingId = recording, State = TrackMatchState.Accepted, Confidence = 0.95, DecisionVersion = 1, Revision = 1 });
        if (personal) db.ManualTrackOverrides.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ExternalSnapshotId = snapshot.Id,
            SourceProviderId = "deezer",
            SourceExternalIdHash = snapshot.ExternalIdHash,
            Decision = ManualOverrideDecision.Pin,
            TargetProviderId = "deezer",
            TargetExternalId = externalId,
            DecisionVersion = 1,
            Revision = 1
        });
        foreach (var entry in db.ChangeTracker.Entries<ExternalMetadataSnapshotRecord>().Where(item => item.State == EntityState.Added))
            entry.Entity.PayloadSha256 = CanonicalCatalogKeys.Hash(entry.Entity.PayloadJson);
        await db.SaveChangesAsync();
        if (personal) await SeedClientDetailsKeepAsync(factory, owner, backendUser, externalId, recording);
        return recording;
    }

    [Fact]
    public async Task ClientApi_ItemDetailsAppliesHouseholdAuthorityToMatchedNativeRecordings()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[{"Id":"native"}]}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "household", personal: false);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var snapshot = await db.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "track");
            db.LibraryTracks.Add(new() { Id = Guid.NewGuid(), OwnerUserId = snapshot.OwnerUserId, Protocol = "jellyfin", BackendInstanceId = "primary", BackendItemId = "native", CanonicalRecordingId = recording, Revision = 1 });
            db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.NewGuid(),
                ExternalSnapshotId = snapshot.Id,
                SourceProviderId = "deezer",
                SourceExternalIdHash = snapshot.ExternalIdHash,
                Decision = ManualOverrideDecision.Reject,
                DecisionVersion = 1,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native", "ext-deezer-song-household" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.All(json.RootElement.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("household", item.GetProperty("match").GetProperty("authority").GetString());
            Assert.Equal("rejected", item.GetProperty("match").GetProperty("state").GetString());
        });
    }

    [Fact]
    public async Task ClientApi_ItemDetailsDoesNotTreatCatalogAudioAsMeasuredFacts()
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientDetailsAsync(factory, "listener", "unmeasured", personal: false);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
            await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "track-media").ExecuteDeleteAsync();
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-unmeasured" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("audio").ValueKind);
    }

    private static ProtocolFactory ClientDetailsFactory(Func<HttpRequestMessage, HttpResponseMessage> responder, Action<IServiceCollection>? configure = null)
    {
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(service => service.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics, ["kept", "lrclib", "lyricsplus", "musixmatch", "unison", "spicylyrics"]),
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { UserId = user });
        return ClientApiFactory(responder, services =>
        {
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            configure?.Invoke(services);
        });
    }

    private static async Task SeedClientDetailsKeepAsync(ProtocolFactory factory, Guid owner, string backendUser, string externalId, Guid recording)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var key = Guid.NewGuid().ToString("N");
        var jobId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var stateId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var path = Path.Combine(factory.Services.GetRequiredService<IConfiguration>()["Storage:DataDirectory"]!, key + ".flac");
        await File.WriteAllTextAsync(path, "retained audio");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), "[00:01.00]<00:01.00>Stored <00:01.40>words");
        Assert.True(new PhysicalManagedFileOperations().TryGetFileIdentity(path, out var fileIdentity));
        db.Set<DurableJobRecord>().Add(new() { Id = jobId, OwnerUserId = owner, Type = "favorite.process", ScopeKey = key, IdempotencyKey = key, RequestFingerprint = key, CorrelationId = key, Revision = 1 });
        db.FavoriteEvents.Add(new() { Id = eventId, OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "primary", BackendPrincipalId = backendUser, ItemId = "ext-deezer-song-" + externalId, Operation = FavoriteOperation.Favorite, EventKey = key, JobId = jobId, Revision = 1 });
        db.FavoriteStates.Add(new() { Id = stateId, OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "primary", ItemId = "ext-deezer-song-" + externalId, IsFavorite = true, LastEventId = eventId, Revision = 1 });
        db.FavoriteActions.Add(new() { Id = Guid.NewGuid(), EventId = eventId, OwnerUserId = owner, ActionType = "download", State = FavoriteActionState.Succeeded, IdempotencyKey = key, Revision = 1 });
        db.ManagedFiles.Add(new()
        {
            Id = fileId,
            RootId = rootId,
            TargetRootPath = Path.GetDirectoryName(path)!,
            CanonicalPath = path,
            ContentSha256 = new string('a', 64),
            Length = new FileInfo(path).Length,
            IsManaged = true,
            ScopeKey = ManagedFileScopeKey.Create(null, rootId),
            ReferenceCount = 1,
            Revision = 1,
            FileSystemDeviceId = fileIdentity.DeviceId,
            FileSystemFileId = fileIdentity.FileId,
            FileSystemLinkCount = fileIdentity.LinkCount
        });
        db.ManagedFileReferences.Add(new() { Id = Guid.NewGuid(), ManagedFileId = fileId, ReferenceKey = $"heart:{stateId:N}", ScopeKey = ManagedFileScopeKey.Create(null, rootId), Revision = 1 });
        db.ProviderDownloadWorkspaces.Add(new() { Id = workspaceId, OwnerUserId = owner, WorkspaceId = key, DurableJobId = jobId, ProviderId = "deezer", IdempotencyKey = key, Revision = 1 });
        db.ProviderDownloadArtifacts.Add(new() { Id = artifactId, WorkspaceRecordId = workspaceId, OwnerUserId = owner, WorkspaceId = key, DurableJobId = jobId, ProviderId = "deezer", ProviderArtifactId = key, RelativePath = key, ContentSha256 = new string('a', 64), Length = new FileInfo(path).Length, SourceExternalTrackId = externalId, State = ProviderDownloadArtifactState.Verified, VerifiedAt = DateTimeOffset.UtcNow.AddDays(-1), Revision = 1 });
        db.Set<RecordingKeptRouteEntity>().Add(new() { CanonicalRecordingId = recording, ManagedFileId = fileId, PublishedByUserId = owner, PublicationJobId = jobId, SourceArtifactId = artifactId, Revision = 1 });
        await db.SaveChangesAsync();
    }
}
