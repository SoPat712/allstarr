using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ServiceFeedMembership_SpotifyAliasOnlyHidesTheVirtualRowWhenItsNativeRowIsPresent(bool nativePresent, bool published)
    {
        using var factory = ClientMixFactory(new(), nativeResponse: request =>
            request.RequestUri!.AbsolutePath == "/Users/listener/Items" && request.RequestUri.Query.Contains("IncludeItemTypes=Playlist", StringComparison.OrdinalIgnoreCase)
                ? Json(200, nativePresent ? """{"Items":[{"Id":"older-playlist","Name":"Discover Weekly","Type":"Playlist","ChildCount":1}],"TotalRecordCount":1}"""
                    : """{"Items":[],"TotalRecordCount":0}""") : null,
            configure: services =>
            {
                services.Configure<SpotifyImportSettings>(settings =>
                {
                    settings.Enabled = true;
                    settings.Playlists = [new() { Id = "37i9dQZweekly", Name = "Discover Weekly", JellyfinId = "older-playlist", UserId = "listener" }];
                });
                UseFixturePlaylistTarget(services);
            });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var link = await SeedServiceFeedMembershipAsync(factory, published);
        var expectedId = nativePresent ? "older-playlist" : PlaylistVirtualizationService.CreateProtocolId(link.Id);
        Assert.Equal(expectedId, Assert.Single(await factory.Services.GetRequiredService<PersonalMixService>().ListCardsAsync(link.OwnerUserId),
            item => item.Id == link.Id).PlaylistId);
        var card = await ServiceFeedCardAsync(client, link.Id);
        Assert.True(card.GetProperty("inApps").GetBoolean());
        Assert.Equal(expectedId, card.GetProperty("playlistId").GetString());
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var user = await db.Users.SingleAsync(item => item.Id == link.OwnerUserId);
        var admin = ActivatorUtilities.CreateInstance<MixesController>(factory.Services);
        admin.ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = factory.Services } };
        admin.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = user.BackendPrincipalId,
            UserName = "Listener",
            AllstarrUserId = user.Id,
            BackendType = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            IsAdministrator = user.IsAdmin,
            JellyfinAccessToken = "listener-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var dashboard = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await admin.List()).Value)
            .EnumerateArray().Single(item => item.GetProperty("Id").GetGuid() == link.Id);
        Assert.Equal(expectedId, dashboard.GetProperty("playlistId").GetString());
        await AssertServiceFeedListedAsync(client, link.Id, !nativePresent);
        if (nativePresent)
        {
            using var response = await client.GetAsync("/Users/listener/Items?IncludeItemTypes=Playlist&Recursive=true");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray(), item => item.GetProperty("Id").GetString() == "older-playlist");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceFeedMembership_ReadyAndOlderLinksAppearInBothListsAndRemovalClearsBoth(bool olderLink)
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var link = await SeedServiceFeedMembershipAsync(factory, published: true, olderLink);
        if (!olderLink)
        {
            var mixes = factory.Services.GetRequiredService<PersonalMixService>();
            await mixes.SetInAppsAsync(link.OwnerUserId, link.Id, false, default);
            await AssertServiceFeedListedAsync(client, link.Id, false);
            await mixes.SetInAppsAsync(link.OwnerUserId, link.Id, true, default);
        }
        await AssertServiceFeedListedAsync(client, link.Id, true);
        var card = await ServiceFeedCardAsync(client, link.Id);
        Assert.True(card.GetProperty("inApps").GetBoolean());
        Assert.Equal(PlaylistVirtualizationService.CreateProtocolId(link.Id), card.GetProperty("playlistId").GetString());
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var repaired = await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id);
        Assert.Equal(PlaylistLinkMode.Virtual, repaired.Mode);
        var revision = repaired.Revision;
        await AssertServiceFeedListedAsync(client, link.Id, true);
        await db.Entry(repaired).ReloadAsync();
        Assert.Equal(revision, repaired.Revision);
        await factory.Services.GetRequiredService<PersonalMixService>().SetInAppsAsync(link.OwnerUserId, link.Id, false, default);
        await AssertServiceFeedListedAsync(client, link.Id, false);
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("mixId").GetGuid() == link.Id);
    }

    [Fact]
    public async Task ServiceFeedMembership_DisabledAccountIsHiddenFromPlaylistsAndReturnsAReason()
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var link = await SeedServiceFeedMembershipAsync(factory, published: true);
        Assert.True((await ServiceFeedCardAsync(client, link.Id)).GetProperty("inApps").GetBoolean());
        await AssertServiceFeedListedAsync(client, link.Id, true);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var account = await db.ProviderAccounts.SingleAsync(item => item.Id == link.ProviderAccountId);
        account.Enabled = false;
        await db.SaveChangesAsync();
        await AssertServiceFeedListedAsync(client, link.Id, false);
        var card = await ServiceFeedCardAsync(client, link.Id);
        Assert.False(card.GetProperty("inApps").GetBoolean());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("playlistId").ValueKind);
        Assert.Contains("service", card.GetProperty("appsUnavailableReason").GetString(), StringComparison.OrdinalIgnoreCase);
        await factory.Services.GetRequiredService<PersonalMixService>().SetInAppsAsync(link.OwnerUserId, link.Id, false, default);
        await AssertServiceFeedListedAsync(client, link.Id, false);
    }

    [Fact]
    public async Task ServiceFeedMembership_UnreadableDestinationReturnsAReason()
    {
        using var factory = ClientMixFactory(new(), nativeResponse: request =>
            request.RequestUri!.AbsolutePath == "/Users/listener/Items" && request.RequestUri.Query.Contains("IncludeItemTypes=Playlist", StringComparison.OrdinalIgnoreCase)
                ? Json(200, """{"Items":[],"TotalRecordCount":0}""")
                : request.RequestUri.AbsolutePath.EndsWith("/missing-feed", StringComparison.Ordinal) ? Json(404, "{}") : null,
            configure: UseFixturePlaylistTarget);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var link = await SeedServiceFeedMembershipAsync(factory, published: true);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var stored = await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id);
        stored.Mode = PlaylistLinkMode.Materialized;
        stored.TargetPlaylistId = "missing-feed";
        await db.SaveChangesAsync();
        await AssertServiceFeedListedAsync(client, link.Id, false);
        var card = await ServiceFeedCardAsync(client, link.Id);
        Assert.False(card.GetProperty("inApps").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("appsUnavailableReason").GetString()));
    }

    [Fact]
    public async Task ServiceFeedMembership_MissingPublicationQueuesOneRepairAndAppearsAfterTheExistingRefresh()
    {
        var source = new Mock<IProviderPlaylistSourceGateway>(MockBehavior.Strict);
        using var factory = ClientMixFactory(new(), configure: services =>
        {
            services.RemoveAll<IProviderPlaylistSourceGateway>();
            services.AddSingleton(source.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var link = await SeedServiceFeedMembershipAsync(factory, published: false);
        var card = await ServiceFeedCardAsync(client, link.Id);
        Assert.False(card.GetProperty("inApps").GetBoolean());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("playlistId").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("appsUnavailableReason").GetString()));
        await AssertServiceFeedListedAsync(client, link.Id, false);
        await ServiceFeedCardAsync(client, link.Id);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var job = Assert.Single(await db.Jobs.Where(item => item.Type == "playlist.materialize").ToArrayAsync());
        Assert.Equal(link.Id, JsonSerializer.Deserialize<PlaylistMaterializationJobPayload>(job.PayloadJson)!.PlaylistLinkId);
        source.Setup(item => item.CollectAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<PlaylistLinkRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectedPlaylistSourceSnapshot("spotify", link.ProviderAccountId, link.SourcePlaylistIdHash, "ready", null,
                "Discover Weekly", null, null,
                [new(0, ServiceFeedHash("entry"), ServiceFeedHash("song"), null, "Library song", ["Library artist"], "Library album", 180000, null, false)])
            { SourceKind = PlaylistSourceKinds.Feed, SourceOwnerId = "spotify" });
        var access = await factory.Services.GetRequiredService<IBackendLibraryAccessResolver>().ResolveUserAsync(link.OwnerUserId, default);
        await factory.Services.GetRequiredService<IPlaylistOrchestrationService>().RefreshAsync(access.Context! with { IsReadOnly = false }, link.Id);
        await AssertServiceFeedListedAsync(client, link.Id, true);
        Assert.True((await ServiceFeedCardAsync(client, link.Id)).GetProperty("inApps").GetBoolean());
        source.VerifyAll();
    }

    private static void UseFixturePlaylistTarget(IServiceCollection services)
    {
        var authentication = new Mock<IBackendPlaylistAuthenticationResolver>();
        authentication.Setup(item => item.ResolveAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendPlaylistAuthentication(new Dictionary<string, string> { ["X-Emby-Token"] = "listener-token" }, []));
        services.RemoveAll<IBackendPlaylistTargetResolver>();
        services.AddSingleton<IBackendPlaylistTargetResolver>(provider => new BackendPlaylistTargetResolver(
            [new JellyfinPlaylistTarget(provider.GetRequiredService<IHttpClientFactory>().CreateClient("fixture"),
                new Uri("http://fixture/"), authentication.Object)]));
    }

    private static async Task<PlaylistLinkRecord> SeedServiceFeedMembershipAsync(ProtocolFactory factory, bool published, bool olderLink = false)
    {
        await SeedClientMixAsync(factory);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var user = await db.Users.SingleAsync(item => item.Id == owner);
        var account = new ProviderAccountRecord { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = "spotify", Enabled = true, Revision = 1 };
        db.ProviderAccounts.Add(account);
        var link = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            SourceProviderId = "spotify",
            SourcePlaylistId = "37i9dQZweekly",
            SourcePlaylistIdHash = ServiceFeedHash("37i9dQZweekly"),
            TargetProtocol = user.BackendType,
            TargetBackendInstanceId = user.BackendInstanceId,
            Mode = olderLink ? PlaylistLinkMode.Materialized : PlaylistLinkMode.Virtual,
            RuleVersion = "1",
            PolicyVersion = "1",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PlaylistLinks.Add(link);
        var scope = ServiceFeedHash($"{user.Id}:{user.BackendType}:{user.BackendInstanceId}:{user.BackendPrincipalId}");
        var feed = new PersonalMix(Guid.NewGuid(), "external", "Discover Weekly", "From Spotify", 1, null, new(true, "Ready"), false, [])
        { Source = "spotify", SourcePlaylistId = link.SourcePlaylistId, ProviderAccountId = account.Id, SongCount = 1 };
        var json = JsonSerializer.Serialize(new[] { feed });
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            ProviderId = "spotify",
            ResourceKind = "service-feeds",
            ExternalIdHash = ServiceFeedHash(scope + ":1"),
            SnapshotVersion = 1,
            ProviderRevision = "1:artwork-v1",
            PayloadJson = json,
            PayloadSha256 = ServiceFeedHash(json),
            Protocol = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = user.BackendPrincipalId,
            RetrievedAt = DateTimeOffset.UtcNow
        });
        if (published) db.PlaylistSourceSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            PlaylistLinkId = link.Id,
            SnapshotVersion = 1,
            SourceKind = PlaylistSourceKinds.Feed,
            Name = "Discover Weekly",
            ProviderRevision = "1",
            PayloadSha256 = ServiceFeedHash("published"),
            RetrievedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return link;
    }

    private static async Task<JsonElement> ServiceFeedCardAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, json);
        using var body = JsonDocument.Parse(json);
        return Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("mixId").GetGuid() == id).Clone();
    }

    private static async Task AssertServiceFeedListedAsync(HttpClient client, Guid id, bool expected)
    {
        using var response = await client.GetAsync("/Users/listener/Items?IncludeItemTypes=Playlist&Recursive=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("Items").EnumerateArray().Any(item =>
            item.GetProperty("Id").GetString() == PlaylistVirtualizationService.CreateProtocolId(id)));
    }

    private static string ServiceFeedHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
