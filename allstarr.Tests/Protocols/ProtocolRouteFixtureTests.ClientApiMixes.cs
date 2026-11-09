using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
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
    [Fact]
    public async Task ClientApi_MixDescriptionsExplainStoredKindsWithoutRepeatingNames()
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
        var payload = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
        foreach (var kind in PersonalMixRules.Kinds)
        {
            var name = PersonalMixRules.Name(kind, kind.StartsWith("daily-", StringComparison.Ordinal) ? "rock" : null);
            payload.Mixes[kind] = new(Guid.NewGuid(), kind, name, "Made for you · " + name, 3, DateTimeOffset.UtcNow,
                new(true, "Ready"), true, [])
            { CoverTag = kind.StartsWith("daily-", StringComparison.Ordinal) ? "rock" : null };
        }
        row.Id = Guid.NewGuid(); row.SnapshotVersion++;
        row.PayloadJson = JsonSerializer.Serialize(payload);
        db.ExternalMetadataSnapshots.Add(row);
        await db.SaveChangesAsync();
        var documents = await db.ExternalMetadataSnapshots.CountAsync();
        var jobs = await db.Jobs.CountAsync();
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = body.RootElement.GetProperty("mixes").EnumerateArray().ToArray();
        Assert.Equal(PersonalMixRules.Kinds.Length + 1, cards.Length);
        foreach (var card in cards)
        {
            var description = card.GetProperty("description").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(description));
            Assert.DoesNotContain(card.GetProperty("name").GetString()!, description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Made for you", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\n', description);
        }
        Assert.Equal("What you're playing most right now", cards.Single(card => card.GetProperty("kind").GetString() == "on-repeat").GetProperty("description").GetString());
        Assert.Equal("Rock you play most", cards.First(card => card.GetProperty("kind").GetString() == "daily-1").GetProperty("description").GetString());
        Assert.Equal(documents, await db.ExternalMetadataSnapshots.CountAsync());
        Assert.Equal(jobs, await db.Jobs.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_MixesMatchDashboardWithoutGeneratingAndItemsUsePlayableIdsInPlaylistOrder(bool glyph)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(ClientMixSong());
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(ClientMixSong());
        using var factory = ClientMixFactory(gateway);
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services, glyph);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var dashboard = await factory.Services.GetRequiredService<PersonalMixService>().ListAsync(owner, "UTC");
        var settings = factory.Services.GetRequiredService<IDurableRuntimeSettings>();
        await settings.ApplyBatchAsync([new(PersonalMixService.PublishKey, "true")], "fixture");
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var jobs = await db.Jobs.CountAsync();
        var documents = await db.ExternalMetadataSnapshots.CountAsync();
        using var response = await client.GetAsync("/allstarr/v1/mixes?timeZoneId=UTC");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = body.RootElement.GetProperty("mixes").EnumerateArray().ToArray();
        Assert.Equal(dashboard.Select(item => item.Id.ToString()), cards.Select(item => item.GetProperty("mixId").GetString()));
        Assert.Equal(dashboard.Select(item => item.Kind), cards.Select(item => item.GetProperty("kind").GetString()));
        var card = Assert.Single(cards, item => item.GetProperty("mixId").GetString() == mix.Id.ToString());
        Assert.Equal(mix.Name, card.GetProperty("name").GetString());
        Assert.Equal(mix.Description, card.GetProperty("description").GetString());
        Assert.Equal("ready", card.GetProperty("state").GetString());
        Assert.Equal(2, card.GetProperty("itemCount").GetInt32());
        Assert.Equal(390000, card.GetProperty("durationMs").GetInt64());
        Assert.True(card.GetProperty("inApps").GetBoolean());
        var playlist = PlaylistVirtualizationService.CreateProtocolId(mix.Id);
        Assert.Equal(playlist, card.GetProperty("playlistId").GetString());
        Assert.Equal($"/allstarr/v1/mixes/{mix.Id}/cover?v={MixCoverImageService.Version(MixCoverInput.From(mix))}", card.GetProperty("coverUrl").GetString());
        using var itemsResponse = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, itemsResponse.StatusCode);
        using var itemsBody = JsonDocument.Parse(await itemsResponse.Content.ReadAsStringAsync());
        Assert.Equal(mix.Id.ToString(), itemsBody.RootElement.GetProperty("mixId").GetString());
        Assert.Equal(mix.UpdatedAt, itemsBody.RootElement.GetProperty("updatedAt").GetDateTimeOffset());
        var items = itemsBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["ext-deezer-song-mix-song", "native-mix-song"], items.Select(item => item.GetProperty("itemId").GetString()));
        Assert.Equal(["Played 12 times lately", "An old favourite"], items.Select(item => item.GetProperty("reason").GetString()));
        Assert.Equal("ext-deezer-album-mix-album", items[0].GetProperty("albumId").GetString());
        Assert.Equal("ext-deezer-artist-mix-artist", items[0].GetProperty("artistId").GetString());
        Assert.Equal("deezer", items[0].GetProperty("provider").GetProperty("id").GetString());
        Assert.Equal("/allstarr/v1/providers/deezer/icon", items[0].GetProperty("provider").GetProperty("iconUrl").GetString());
        Assert.Equal(glyph ? "/allstarr/v1/providers/deezer/icon?variant=glyph" : null, items[0].GetProperty("provider").GetProperty("glyphUrl").GetString());
        Assert.True(items[0].GetProperty("isExternal").GetBoolean());
        Assert.Equal("native-album", items[1].GetProperty("albumId").GetString());
        Assert.Equal("native-artist", items[1].GetProperty("artistId").GetString());
        Assert.False(items[1].GetProperty("isExternal").GetBoolean());
        AssertLibraryProvider(items[1].GetProperty("provider"), glyph);
        using var playlistResponse = await client.GetAsync($"/Playlists/{playlist}/Items");
        Assert.Equal(HttpStatusCode.OK, playlistResponse.StatusCode);
        using var playlistBody = JsonDocument.Parse(await playlistResponse.Content.ReadAsStringAsync());
        Assert.Equal(items.Select(item => item.GetProperty("itemId").GetString()),
            playlistBody.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetString()));
        foreach (var item in items)
        {
            using var resolved = await client.GetAsync($"/Users/listener/Items/{item.GetProperty("itemId").GetString()}");
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
            using var resolvedBody = JsonDocument.Parse(await resolved.Content.ReadAsStringAsync());
            Assert.Equal(item.GetProperty("itemId").GetString(), resolvedBody.RootElement.GetProperty("Id").GetString());
        }
        Assert.Equal(jobs, await db.Jobs.CountAsync());
        Assert.Equal(documents, await db.ExternalMetadataSnapshots.CountAsync());
        gateway.Verify(service => service.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData("items")]
    [InlineData("cover")]
    public async Task ClientApi_MixesHideOtherListeners(string suffix)
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        var mix = await SeedClientMixAsync(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "other-token");
        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/{suffix}");
        await AssertClientApiError(response, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_MixCoversMatchDashboardAndRevalidateAfterAuthorization()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "mix-song",
            It.IsAny<CancellationToken>(), false, false)).ReturnsAsync(new Uri("https://covers.example.test/Items/mix-song/Images/Primary"));
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var path = $"/allstarr/v1/mixes/{mix.Id}/cover";
        using var first = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(["service:ext-deezer-song-mix-song"], MixCoverInput.From(mix).Art!);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        Assert.True(first.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.Zero, first.Headers.CacheControl?.MaxAge);
        Assert.Equal('"' + MixCoverImageService.GetTag(MixCoverInput.From(mix)) + '"', first.Headers.ETag?.Tag);
        using var current = await client.GetAsync(path + "?v=" + MixCoverImageService.Version(MixCoverInput.From(mix)));
        Assert.Equal(TimeSpan.FromDays(365), current.Headers.CacheControl?.MaxAge);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await current.Content.ReadAsByteArrayAsync());
        using var stale = await client.GetAsync(path + "?v=0000000000000000");
        Assert.Equal(TimeSpan.Zero, stale.Headers.CacheControl?.MaxAge);
        using (var collage = SkiaSharp.SKBitmap.Decode(await first.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal((600, 600), (collage.Width, collage.Height));
            Assert.InRange(collage.GetPixel(300, 300).Red, 180, 220);
        }
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "other-token");
        using var foreign = new HttpRequestMessage(HttpMethod.Get, path);
        foreign.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var hidden = await client.SendAsync(foreign);
        await AssertClientApiError(hidden, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_MixesRespectPublicationSettingAndDashboardSyntheticRules()
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        await factory.Services.GetRequiredService<IDurableRuntimeSettings>().ApplyBatchAsync([new(PersonalMixService.PublishKey, "false")], "fixture");
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var card = Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("mixId").GetString() == mix.Id.ToString());
        Assert.False(card.GetProperty("inApps").GetBoolean());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("playlistId").ValueKind);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        (await db.Users.SingleAsync(item => item.BackendPrincipalId == "listener")).IsSynthetic = true;
        await db.SaveChangesAsync();
        using var synthetic = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, synthetic.StatusCode);
        using var syntheticBody = JsonDocument.Parse(await synthetic.Content.ReadAsStringAsync());
        Assert.Empty(syntheticBody.RootElement.GetProperty("mixes").EnumerateArray());
        foreach (var suffix in new[] { "items", "cover" })
        {
            using var specific = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/{suffix}");
            await AssertClientApiError(specific, 404, "not_found");
        }
    }

    [Fact]
    public async Task ClientApi_LinkedExternalMixesUseDashboardPublicationAndCoverInputs()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["deezer"]);
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var linkId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        db.ProviderAccounts.Add(new() { Id = accountId, OwnerUserId = owner, ProviderId = "lastfm", Enabled = true });
        db.PlaylistLinks.Add(new()
        {
            Id = linkId,
            OwnerUserId = owner,
            ProviderAccountId = accountId,
            Enabled = true,
            SourceProviderId = "lastfm",
            SourcePlaylistId = "loved",
            SourcePlaylistIdHash = new('a', 64),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "primary",
            Mode = PlaylistLinkMode.Virtual,
            ImportMode = PlaylistImportMode.Linked,
            RuleVersion = "1",
            PolicyVersion = "1"
        });
        db.PlaylistSourceSnapshots.Add(new()
        {
            Id = snapshotId,
            OwnerUserId = owner,
            PlaylistLinkId = linkId,
            ProviderAccountId = accountId,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            Name = "Loved tracks",
            PayloadSha256 = new('b', 64),
            CorrelationId = "fixture",
            PublishedAt = DateTimeOffset.UtcNow
        });
        var nativeId = (await db.LibraryTracks.SingleAsync(item => item.BackendItemId == "native-mix-song")).Id;
        for (var position = 0; position < 2; position++)
        {
            var metadataId = Guid.NewGuid();
            var matchId = Guid.NewGuid();
            var hash = new string((char)('c' + position), 64);
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = metadataId,
                OwnerUserId = owner,
                ProviderAccountId = accountId,
                ProviderId = "lastfm",
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "listener",
                ResourceKind = "track",
                ExternalIdHash = hash,
                SnapshotVersion = 1,
                PayloadSha256 = hash,
                RetrievedAt = DateTimeOffset.UtcNow,
                PayloadJson = JsonSerializer.Serialize(new PlaylistSourceMetadata("Library song", ["Library artist"], DurationMilliseconds: 180000))
            });
            db.TrackMatches.Add(new()
            {
                Id = matchId,
                OwnerUserId = owner,
                ExternalSnapshotId = metadataId,
                LibraryTrackId = nativeId,
                State = TrackMatchState.Accepted,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                Confidence = 1,
                Threshold = .9
            });
            db.PlaylistSourceEntries.Add(new()
            {
                Id = Guid.NewGuid(),
                PlaylistSourceSnapshotId = snapshotId,
                ExternalMetadataSnapshotId = metadataId,
                SourcePosition = position,
                SourceEntryIdHash = hash,
                PublishedTrackMatchId = matchId
            });
        }
        await db.SaveChangesAsync();
        await factory.Services.GetRequiredService<IDurableRuntimeSettings>().ApplyBatchAsync([new(PersonalMixService.PublishKey, "true")], "fixture");
        var dashboard = await factory.Services.GetRequiredService<PersonalMixService>().GetAsync(owner, linkId);
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var card = Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("mixId").GetString() == linkId.ToString());
        Assert.Equal("external", card.GetProperty("kind").GetString());
        Assert.Equal(dashboard.Name, card.GetProperty("name").GetString());
        Assert.True(card.GetProperty("inApps").GetBoolean());
        Assert.Equal(1, card.GetProperty("itemCount").GetInt32());
        Assert.Equal(PlaylistVirtualizationService.CreateProtocolId(linkId), card.GetProperty("playlistId").GetString());
        using var items = await client.GetAsync($"/allstarr/v1/mixes/{linkId}/items");
        Assert.Equal(HttpStatusCode.OK, items.StatusCode);
        using var itemsBody = JsonDocument.Parse(await items.Content.ReadAsStringAsync());
        var song = Assert.Single(itemsBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("native-mix-song", song.GetProperty("itemId").GetString());
        Assert.Equal("From your linked playlist", song.GetProperty("reason").GetString());
        using var playlist = await client.GetAsync($"/Playlists/{PlaylistVirtualizationService.CreateProtocolId(linkId)}/Items");
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        using var playlistBody = JsonDocument.Parse(await playlist.Content.ReadAsStringAsync());
        Assert.Equal("native-mix-song", Assert.Single(playlistBody.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        using var cover = await client.GetAsync(card.GetProperty("coverUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        var expected = MixCoverInput.From(dashboard) with { Source = "lastfm", SourcePlaylistId = "loved" };
        Assert.Equal('"' + MixCoverImageService.GetTag(expected) + '"', cover.Headers.ETag?.Tag);
        // The linked playlist's song has a picture, so the cover is made from it.
        Assert.Equal("image/jpeg", cover.Content.Headers.ContentType?.MediaType);
        using var collage = SkiaSharp.SKBitmap.Decode(await cover.Content.ReadAsByteArrayAsync());
        Assert.Equal((600, 600), (collage.Width, collage.Height));
    }

    [Theory]
    [InlineData(true, "Ready", "ready")]
    [InlineData(false, "Matching songs…", "building")]
    [InlineData(false, "Play about 12 more songs to build this mix.", "needs-listening")]
    [InlineData(false, "More listening history is needed.", "needs-listening")]
    [InlineData(false, "Refresh this mix to look for songs from your music and services.", "empty")]
    [InlineData(false, "No playable songs are available for this mix yet. Refresh after more listening or new releases.", "empty")]
    [InlineData(false, "Fewer than 25 songs remain under your current listening policy. Refresh to look for more.", "empty")]
    public async Task ClientApi_MixStatesReflectDashboardReadiness(bool ready, string message, string expected)
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory, readiness: new(ready, message));
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var card = Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("mixId").GetString() == mix.Id.ToString());
        Assert.Equal(expected, card.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ClientApi_MixesRejectInvalidTimeZones()
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/mixes?timeZoneId=missing-zone");
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApi_MixItemsPreserveJellyfinCatalogAliasesAndStoredRecordingIds()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-musickit", "mix-song"))
            .ReturnsAsync(new Song
            {
                Id = "ext-allstarr-song-recording",
                ExternalProvider = "apple-download",
                ExternalId = "mix-song",
                AlbumId = "ext-apple-download-album-album",
                ArtistId = "ext-apple-download-artist-artist"
            });
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var song = new Song { Id = "ext-apple-musickit-song-mix-song", ExternalProvider = "apple-musickit", ExternalId = "mix-song" };
        var mix = await SeedClientMixAsync(factory, serviceSong: song);
        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("ext-allstarr-song-recording", item.GetProperty("itemId").GetString());
        Assert.Equal("ext-apple-download-album-album", item.GetProperty("albumId").GetString());
        Assert.Equal("ext-apple-download-artist-artist", item.GetProperty("artistId").GetString());
        Assert.Equal("apple-download", item.GetProperty("provider").GetProperty("id").GetString());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClientApi_MixItemsOmitLibrarySongsTheCallerCanNoLongerRead()
    {
        using var factory = ClientMixFactory(new(), nativeVisible: false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ext-deezer-song-mix-song", Assert.Single(body.RootElement.GetProperty("items").EnumerateArray()).GetProperty("itemId").GetString());
    }

    [Theory]
    [InlineData("GET", "/allstarr/v1/mixes")]
    [InlineData("GET", "/allstarr/v1/mixes/11111111-1111-4111-8111-111111111111/items")]
    [InlineData("GET", "/allstarr/v1/mixes/11111111-1111-4111-8111-111111111111/cover")]
    [InlineData("GET", "/allstarr/v1/releases")]
    [InlineData("GET", "/allstarr/v1/releases/missing/cover")]
    [InlineData("PUT", "/allstarr/v1/artists/ext-deezer-artist-one/follow")]
    [InlineData("DELETE", "/allstarr/v1/artists/ext-deezer-artist-one/follow")]
    public async Task ClientApi_MixesReleasesAndFollowRespectOffSwitch(string method, string path)
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory, enabled: false);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.SendAsync(new(new HttpMethod(method), path));
        await AssertClientApiError(response, 404, "not_found");
    }

    private static ProtocolFactory ClientMixFactory(Mock<IProtocolProviderGateway> gateway, bool nativeVisible = true,
        string nativeArtwork = "\"AlbumPrimaryImageTag\":\"native-album-art\"", bool duplicateAlbum = false) => ClientDetailsFactory(request =>
    {
        var nativeItem = $$"""{"Id":"native-mix-song","Type":"Audio","Name":"Library song","AlbumId":"native-album","ArtistItems":[{"Id":"native-artist","Name":"Library artist"}],{{nativeArtwork}}}""";
        var nativeItems = nativeItem + (duplicateAlbum ? "," + nativeItem.Replace("native-mix-song", "native-second-song", StringComparison.Ordinal) : "");
        if (request.RequestUri!.AbsolutePath.StartsWith("/Users/", StringComparison.Ordinal))
            Assert.Contains(request.Headers.ToString().Contains("other-token", StringComparison.Ordinal) ? "other-token" : "listener-token", request.Headers.ToString());
        return request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal) ? """{"Id":"other"}""" : """{"Id":"listener"}"""),
            "/Users/listener/Items/native-mix-song" => nativeVisible ? Json(200, nativeItem) : Json(404, "{}"),
            "/Users/listener/Items" => nativeVisible ? Json(200, "{\"Items\":[" + nativeItems + "]}") : Json(403, "{}"),
            "/Items" => Json(200, "{\"Items\":[" + nativeItem + "],\"TotalRecordCount\":1}"),
            // The media server's album pictures, which mix covers are made from.
            var path when path.EndsWith("/Images/Primary", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MixPicture()) { Headers = { ContentType = new("image/png") } }
            },
            _ => Json(200, """{"Items":[],"TotalRecordCount":0}""")
        };
    }, services =>
    {
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway.Object);
        services.RemoveAll<IBackendLibraryAccessResolver>();
        services.AddSingleton<IBackendLibraryAccessResolver>(provider => new TestBackendLibraryAccess(provider.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(), "library"));
    });

    private static byte[] MixPicture()
    {
        using var bitmap = new SkiaSharp.SKBitmap(64, 64);
        bitmap.Erase(new SkiaSharp.SKColor(200, 40, 90));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static Song ClientMixSong() => new()
    {
        Id = "ext-deezer-song-mix-song",
        Title = "Service song",
        Artist = "Service artist",
        Album = "Service album",
        ArtistId = "ext-deezer-artist-mix-artist",
        AlbumId = "ext-deezer-album-mix-album",
        Duration = 210,
        ExternalProvider = "deezer",
        ExternalId = "mix-song"
    };

    private static async Task<PersonalMix> SeedClientMixAsync(ProtocolFactory factory, MixReadiness? readiness = null,
        Song? serviceSong = null, bool includeLibrary = false)
    {
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var id = Guid.NewGuid();
        var mix = new PersonalMix(id, "custom", "Saved favourites", "A saved mix", 2, DateTimeOffset.UtcNow,
            readiness ?? new(true, "Ready"), true,
            [new(id, 2, ClientMixSong(), "deezer", "Played 12 times lately", 1),
                new(id, 2, new() { Id = "native-mix-song", Title = "Library song", Artist = "Library artist", Album = "Library album", IsLocal = true, Duration = 180 }, "library", "An old favourite", 1),
                new(id, 2, new() { Id = "inaccessible-song", Title = "Hidden song", IsLocal = true, Duration = 50 }, "library", "Hidden", 1)]);
        if (serviceSong != null) mix = mix with { Songs = [new(id, 2, serviceSong, serviceSong.ExternalProvider!, "Picked for you", 1), .. includeLibrary ? mix.Songs.Skip(1) : []] };
        var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}:jellyfin:primary:listener")));
        var json = JsonSerializer.Serialize(new { AppsOptInVersion = 3, TimeZoneId = "UTC", Mixes = new Dictionary<string, PersonalMix> { ["custom"] = mix } });
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = "listener",
            ProviderId = "allstarr",
            ResourceKind = "personal-mixes",
            ExternalIdHash = scope,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            PayloadJson = json,
            PayloadSha256 = scope,
            RetrievedAt = DateTimeOffset.UtcNow
        });
        db.LibraryTracks.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "primary", BackendItemId = "native-mix-song", BackendLibraryId = "library", Revision = 1 });
        await db.SaveChangesAsync();
        return mix;
    }
}
