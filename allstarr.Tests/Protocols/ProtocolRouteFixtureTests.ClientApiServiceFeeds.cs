using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_ServiceFeedsHaveSectionSourceAndOptionalPublicationWithoutCreatingLinks(bool synced)
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var user = await db.Users.SingleAsync(item => item.Id == owner);
        var account = new ProviderAccountRecord { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = "spotify", Enabled = true, Revision = 1 };
        db.ProviderAccounts.Add(account);
        var feed = new PersonalMix(Guid.NewGuid(), "external", "Discover Weekly", "From Spotify", 1, null,
            new(true, "Open to see songs from your service."), false, [])
        { Source = "spotify", SourceLabel = "Spotify", SourcePlaylistId = "37i9dQZweekly", ProviderAccountId = account.Id, SongCount = 30 };
        var scope = Hash($"{user.Id}:{user.BackendType}:{user.BackendInstanceId}:{user.BackendPrincipalId}");
        var json = JsonSerializer.Serialize(new[] { feed });
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            ProviderId = "spotify",
            ResourceKind = "service-feeds",
            ExternalIdHash = Hash(scope + ":1"),
            SnapshotVersion = 1,
            Protocol = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = user.BackendPrincipalId,
            ProviderRevision = "1",
            PayloadJson = json,
            PayloadSha256 = Hash(json),
            CorrelationId = "fixture",
            RetrievedAt = DateTimeOffset.UtcNow
        });
        var linkedId = Guid.NewGuid();
        if (synced) db.PlaylistLinks.Add(new()
        {
            Id = linkedId,
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            SourceProviderId = "spotify",
            SourcePlaylistId = feed.SourcePlaylistId!,
            SourcePlaylistIdHash = Hash(feed.SourcePlaylistId!),
            TargetProtocol = user.BackendType,
            TargetBackendInstanceId = user.BackendInstanceId,
            Enabled = true,
            RuleVersion = "1",
            PolicyVersion = "1",
            Mode = PlaylistLinkMode.Virtual
        });
        await db.SaveChangesAsync();
        var links = await db.PlaylistLinks.CountAsync();
        var jobs = await db.Jobs.CountAsync();
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (synced)
        {
            var card = Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.TryGetProperty("source", out var source) && source.GetString() == "spotify");
            Assert.Equal("external", card.GetProperty("kind").GetString());
            Assert.Equal("yourServices", card.GetProperty("section").GetString());
            Assert.Equal("spotify", card.GetProperty("source").GetString());
            Assert.Equal("37i9dQZweekly", card.GetProperty("sourcePlaylistId").GetString());
            Assert.Equal(30, card.GetProperty("itemCount").GetInt32());
            Assert.Equal(linkedId, card.GetProperty("mixId").GetGuid());
            Assert.False(card.GetProperty("inApps").GetBoolean());
            Assert.Equal(JsonValueKind.Null, card.GetProperty("playlistId").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("appsUnavailableReason").GetString()));
        }
        else
        {
            Assert.DoesNotContain(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.TryGetProperty("source", out var source) && source.GetString() == "spotify");
        }
        Assert.Equal(links, await db.PlaylistLinks.CountAsync());
        Assert.Equal(jobs + (synced ? 2 : 1), await db.Jobs.CountAsync());

        static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    [Fact]
    public async Task ClientApi_ListenerWithAddedAndUnaddedFeedSeesOnlyAddedInMixesItemsAndCover404AndWebUiListsBoth()
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var user = await db.Users.SingleAsync(item => item.Id == owner);

        var allstarrMixId = Guid.NewGuid();
        var allstarrMix = new PersonalMix(allstarrMixId, "daily-1", "Daily Mix 1", "Your daily mix", 1, DateTimeOffset.UtcNow,
            new(true, "Ready"), false,
            [new(allstarrMixId, 1, new() { Id = "native-mix-song", Title = "Library song", Artist = "Library artist", Album = "Library album", IsLocal = true, Duration = 180 }, "library", "Favorite", 1)]);
        var scope = Hash($"{user.Id}:{user.BackendType}:{user.BackendInstanceId}:{user.BackendPrincipalId}");
        var allstarrJson = JsonSerializer.Serialize(new { AppsOptInVersion = 3, TimeZoneId = "UTC", Mixes = new Dictionary<string, PersonalMix> { ["daily-1"] = allstarrMix } });
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
            PayloadJson = allstarrJson,
            PayloadSha256 = scope,
            RetrievedAt = DateTimeOffset.UtcNow
        });
        db.LibraryTracks.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "primary", BackendItemId = "native-mix-song", BackendLibraryId = "library", Revision = 1 });

        var account = new ProviderAccountRecord { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = "spotify", Enabled = true, Revision = 1 };
        db.ProviderAccounts.Add(account);

        var addedFeedId = Guid.NewGuid();
        var unaddedFeedId = Guid.NewGuid();
        var addedFeed = new PersonalMix(addedFeedId, "external", "Discover Weekly", "From Spotify", 1, null,
            new(true, "Ready"), true, [])
        { Source = "spotify", SourceLabel = "Spotify", SourcePlaylistId = "weekly", ProviderAccountId = account.Id, SongCount = 30 };
        var unaddedFeed = new PersonalMix(unaddedFeedId, "external", "Arsh + Josh", "From Spotify", 1, null,
            new(true, "Ready"), false, [])
        { Source = "spotify", SourceLabel = "Spotify", SourcePlaylistId = "blend", ProviderAccountId = account.Id, SongCount = 50 };

        var feedJson = JsonSerializer.Serialize(new[] { addedFeed, unaddedFeed });
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            ProviderId = "spotify",
            ResourceKind = "service-feeds",
            ExternalIdHash = Hash(scope + ":1"),
            SnapshotVersion = 1,
            Protocol = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = user.BackendPrincipalId,
            ProviderRevision = "1",
            PayloadJson = feedJson,
            PayloadSha256 = Hash(feedJson),
            CorrelationId = "fixture",
            RetrievedAt = DateTimeOffset.UtcNow
        });

        db.PlaylistLinks.Add(new()
        {
            Id = addedFeedId,
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            SourceProviderId = "spotify",
            SourcePlaylistId = "weekly",
            SourcePlaylistIdHash = Hash("weekly"),
            TargetProtocol = user.BackendType,
            TargetBackendInstanceId = user.BackendInstanceId,
            Enabled = true,
            RuleVersion = "1",
            PolicyVersion = "1",
            Mode = PlaylistLinkMode.Virtual
        });
        db.PlaylistSourceSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            PlaylistLinkId = addedFeedId,
            SnapshotVersion = 1,
            SourceKind = PlaylistSourceKinds.Feed,
            Name = addedFeed.Name,
            ProviderRevision = "1",
            PayloadSha256 = Hash("published"),
            RetrievedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await factory.Services.GetRequiredService<IDurableRuntimeSettings>().ApplyBatchAsync([new(PersonalMixService.PublishKey, "true")], "fixture");

        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var mixesArray = body.RootElement.GetProperty("mixes").EnumerateArray().ToArray();

        var addedCard = Assert.Single(mixesArray, item => item.GetProperty("mixId").GetGuid() == addedFeedId);
        Assert.True(addedCard.GetProperty("inApps").GetBoolean());
        Assert.Equal("yourServices", addedCard.GetProperty("section").GetString());

        Assert.DoesNotContain(mixesArray, item => item.GetProperty("mixId").GetGuid() == unaddedFeedId);

        var allstarrCard = Assert.Single(mixesArray, item => item.GetProperty("mixId").GetGuid() == allstarrMixId);
        Assert.False(allstarrCard.GetProperty("inApps").GetBoolean());

        using var unaddedItems = await client.GetAsync($"/allstarr/v1/mixes/{unaddedFeedId}/items");
        await AssertClientApiError(unaddedItems, 404, "not_found");
        using var unaddedCover = await client.GetAsync($"/allstarr/v1/mixes/{unaddedFeedId}/cover");
        await AssertClientApiError(unaddedCover, 404, "not_found");

        using var addedItems = await client.GetAsync($"/allstarr/v1/mixes/{addedFeedId}/items");
        Assert.Equal(HttpStatusCode.OK, addedItems.StatusCode);
        using var addedCover = await client.GetAsync($"/allstarr/v1/mixes/{addedFeedId}/cover");
        Assert.Equal(HttpStatusCode.OK, addedCover.StatusCode);

        using var allstarrItems = await client.GetAsync($"/allstarr/v1/mixes/{allstarrMixId}/items");
        Assert.Equal(HttpStatusCode.OK, allstarrItems.StatusCode);
        using var allstarrCover = await client.GetAsync($"/allstarr/v1/mixes/{allstarrMixId}/cover");
        Assert.Equal(HttpStatusCode.OK, allstarrCover.StatusCode);

        var mixService = factory.Services.GetRequiredService<PersonalMixService>();
        var webUiCards = await mixService.ListCardsAsync(owner);
        Assert.Contains(webUiCards, c => c.Id == addedFeedId);
        Assert.Contains(webUiCards, c => c.Id == unaddedFeedId);
        Assert.Contains(webUiCards, c => c.Id == allstarrMixId);

        static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
