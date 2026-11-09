using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
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
        var card = Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.TryGetProperty("source", out var source) && source.GetString() == "spotify");
        Assert.Equal("external", card.GetProperty("kind").GetString());
        Assert.Equal("yourServices", card.GetProperty("section").GetString());
        Assert.Equal("spotify", card.GetProperty("source").GetString());
        Assert.Equal("37i9dQZweekly", card.GetProperty("sourcePlaylistId").GetString());
        Assert.Equal(30, card.GetProperty("itemCount").GetInt32());
        Assert.Equal(synced ? linkedId : feed.Id, card.GetProperty("mixId").GetGuid());
        Assert.Equal(synced, card.GetProperty("inApps").GetBoolean());
        Assert.Equal(synced ? PlaylistVirtualizationService.CreateProtocolId(linkedId) : null, card.GetProperty("playlistId").GetString());
        Assert.Equal(links, await db.PlaylistLinks.CountAsync());
        Assert.Equal(jobs, await db.Jobs.CountAsync());

        static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
