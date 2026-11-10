using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_StatsUsesVerifiedStoredServiceCreditsAndTheirOwnCover()
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(service => service.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendLibraryAccessContext.Unavailable);
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/own-cover" => ArtistImage(222),
            _ => throw new InvalidOperationException("Unexpected artist lookup")
        }, services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
        using var client = CreateClientApiClient(factory);
        var recording = await SeedClientDetailsAsync(factory, "listener", "credits", personal: false);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var snapshot = await db.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "track");
            var identity = await db.ProviderTrackIdentities.SingleAsync();
            var payload = """{"Title":"Song","Artist":"Wrong combined display","Artists":[{"Name":"Tyler, The Creator","ArtistId":"100"},{"Name":"FLO","ArtistId":"200"}],"CoverArtUrl":"https://art.example.test/own-cover"}""";
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = snapshot.OwnerUserId,
                Protocol = snapshot.Protocol,
                BackendInstanceId = snapshot.BackendInstanceId,
                BackendPrincipalId = snapshot.BackendPrincipalId,
                ProviderId = snapshot.ProviderId,
                ResourceKind = "track",
                ExternalIdHash = snapshot.ExternalIdHash,
                SnapshotVersion = snapshot.SnapshotVersion + 1,
                ProviderTrackIdentityId = identity.Id,
                PayloadJson = payload,
                PayloadSha256 = CanonicalCatalogKeys.Hash(payload),
                RetrievedAt = DateTimeOffset.UtcNow
            });
            db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = snapshot.OwnerUserId,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                OccurrenceKey = "service-credit",
                TrackReference = "ext-deezer-song-credits",
                State = ListeningEventState.Completed,
                SourceKind = "import",
                Artist = "Wrong combined display",
                CanonicalRecordingId = recording,
                ProviderTrackIdentityId = identity.Id,
                ListenedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/me/stats?period=all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var artists = body.RootElement.GetProperty("topArtists").EnumerateArray().ToArray();
        Assert.Equal(2, artists.Length);
        Assert.Equal("ext-deezer-artist-200", artists.Single(item => item.GetProperty("name").GetString() == "FLO").GetProperty("id").GetString());
        Assert.Contains(artists, item => item.GetProperty("name").GetString() == "Tyler, The Creator");
        using var artwork = await client.GetAsync("/allstarr/v1/me/stats/art?artist=FLO");
        Assert.Equal(HttpStatusCode.OK, artwork.StatusCode);
        Assert.Equal(222, (await artwork.Content.ReadAsByteArrayAsync())[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientApi_StatsSplitsStructuredAndFallbackCreditsAndKeepsKnownSingleArtists(bool structured)
    {
        using var factory = ArtistStatsFactory(true, structured);
        using var client = CreateClientApiClient(factory);
        await SeedArtistStatsAsync(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/me/stats?period=all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var artists = body.RootElement.GetProperty("topArtists").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
        foreach (var name in new[] { "Calvin Harris", "Pharrell Williams", "FLO", "Tyler, The Creator" })
            Assert.Equal(2, artists[name].GetProperty("playCount").GetInt64());
        if (structured)
        {
            Assert.Equal("artist-calvin", artists["Calvin Harris"].GetProperty("id").GetString());
            Assert.Equal("artist-tyler", artists["Tyler, The Creator"].GetProperty("id").GetString());
        }
        else Assert.All(artists.Values, item => Assert.False(item.TryGetProperty("id", out _)));
        Assert.False(artists["Unknown"].TryGetProperty("id", out _));
        Assert.DoesNotContain("Tyler", artists.Keys);
        Assert.DoesNotContain("The Creator", artists.Keys);
    }

    [Theory]
    [InlineData(true, 111)]
    [InlineData(false, 222)]
    public async Task ClientApi_ArtistArtworkUsesThePlayedLibraryIdentityBeforeOwnCovers(bool portrait, byte expected)
    {
        using var factory = ArtistStatsFactory(portrait);
        using var client = CreateClientApiClient(factory);
        await SeedArtistStatsAsync(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/me/stats/art?artist=FLO");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, (await response.Content.ReadAsByteArrayAsync())[0]);
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "other-token");
        using var other = await client.GetAsync("/allstarr/v1/me/stats/art?artist=FLO");
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    private static ProtocolFactory ArtistStatsFactory(bool portrait, bool structured = true)
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(service => service.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid owner, CancellationToken token) => new BackendLibraryAccessContext(
                new(ProtocolKind.Jellyfin, "primary", "listener", new(owner, "jellyfin", "primary", "listener", "Listener", false),
                    "fixture", DateTimeOffset.UtcNow.AddMinutes(1), token), new(true, ["music"])));
        return ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal)
                ? """{"Id":"other"}""" : """{"Id":"listener"}"""),
            "/Users/listener/Items" => structured ? Json(200, """{"Items":[{"Id":"credit","ArtistItems":[{"Id":"artist-calvin","Name":"Calvin Harris"},{"Id":"artist-pharrell","Name":"Pharrell Williams"}]},{"Id":"single","ArtistItems":[{"Id":"artist-tyler","Name":"Tyler, The Creator"}]},{"Id":"flo-song","AlbumId":"own-cover","ArtistItems":[{"Id":"artist-flo","Name":"FLO"}]}]}""") : throw new HttpRequestException("Metadata unavailable"),
            "/Users/listener/Items/artist-flo" => Json(200, """{"Id":"artist-flo","Type":"MusicArtist","ImageTags":{"Primary":"portrait-tag"}}"""),
            "/Items/artist-flo/Images/Primary" => portrait ? ArtistImage(111) : Json(404, "{}"),
            "/Items/own-cover/Images/Primary" => ArtistImage(222),
            _ => throw new InvalidOperationException("Unexpected artwork or metadata request: " + request.RequestUri.AbsolutePath)
        }, services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
    }

    private static HttpResponseMessage ArtistImage(byte value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Enumerable.Repeat(value, 2048).ToArray()) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private static async Task SeedArtistStatsAsync(ProtocolFactory factory)
    {
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        foreach (var item in new[] { (Id: "credit", Artist: "Calvin Harris, Pharrell Williams"), (Id: "single", Artist: "Tyler, The Creator"),
            (Id: "flo-song", Artist: "FLO"), (Id: "imported", Artist: "Calvin Harris feat. Pharrell Williams & FLO x Tyler, The Creator"),
            (Id: "unknown", Artist: "Unknown ft. Another") })
        {
            var track = Guid.NewGuid();
            if (item.Id is not ("imported" or "unknown")) db.LibraryTracks.Add(new()
            {
                Id = track,
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendLibraryId = "music",
                BackendItemId = item.Id,
                Title = "Song",
                Artist = item.Artist
            });
            db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                OccurrenceKey = item.Id,
                TrackReference = item.Id,
                LibraryTrackId = item.Id is "imported" or "unknown" ? null : track,
                State = ListeningEventState.Completed,
                SourceKind = "import",
                Title = "Song",
                Artist = item.Artist,
                ListenedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }
}
