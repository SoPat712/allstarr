using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinExternalPlaylistCover_WithoutToken_KeepsPublicArtworkRoute()
    {
        byte[] bytes = [0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9];
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPublicArtworkUriAsync("applemusic", ProviderResourceKind.Playlist,
            "playlist-42", It.IsAny<CancellationToken>())).ReturnsAsync(new Uri("https://is1-ssl.mzstatic.com/cover.jpg"));
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.Host == "is1-ssl.mzstatic.com"
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } }
            : throw new InvalidOperationException("Unexpected playlist image relay."), services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items/ext-applemusic-playlist-playlist-42/Images/Primary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinPublishedPlaylistCovers_AllowTokenlessImageLoadersWithoutWriting(bool storedLink)
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var publish = true;
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(service => service.GetAsync(PersonalMixService.PublishKey, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new EffectiveRuntimeSetting(PersonalMixService.PublishKey,
                RuntimeSettingValueType.Boolean, publish, publish.ToString(), RuntimeSettingOrigin.Durable, 1, null, null, null)));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var mix = new PersonalMix(id, "on-repeat", "Heavy Rotation", "Made for you", 2, null,
            new(true, "Ready"), true, []);
        using var factory = new ProtocolFactory("Jellyfin",
            _ => throw new InvalidOperationException("Anonymous stored covers must not call upstream services."), services =>
            {
                services.RemoveAll<IDurableRuntimeSettings>();
                services.AddSingleton(settings.Object);
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        var runtimeRows = 0;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, Enabled = true, BackendType = "jellyfin", BackendInstanceId = "primary", BackendPrincipalId = "cover-owner" });
            if (storedLink)
            {
                db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = owner, ProviderId = "lastfm", Enabled = true });
                db.PlaylistLinks.Add(new()
                {
                    Id = id,
                    OwnerUserId = owner,
                    ProviderAccountId = account,
                    Enabled = true,
                    SourceProviderId = "lastfm",
                    SourcePlaylistId = "loved",
                    SourcePlaylistIdHash = new('a', 64),
                    TargetProtocol = "jellyfin",
                    TargetBackendInstanceId = "primary",
                    Mode = PlaylistLinkMode.Virtual,
                    RuleVersion = "1",
                    PolicyVersion = "1"
                });
                db.PlaylistSourceSnapshots.Add(new()
                {
                    Id = snapshotId,
                    OwnerUserId = owner,
                    PlaylistLinkId = id,
                    ProviderAccountId = account,
                    SnapshotVersion = 2,
                    ProviderRevision = "2",
                    Name = "Loved tracks",
                    PayloadSha256 = new('b', 64),
                    CorrelationId = "cover-fixture",
                    PublishedAt = DateTimeOffset.UtcNow
                });
            }
            // A stored link takes precedence even if a document happens to reuse its ID.
            db.ExternalMetadataSnapshots.Add(PersonalCoverSnapshot(Guid.NewGuid(), owner, mix, 1));
            await db.SaveChangesAsync();
            runtimeRows = await db.RuntimeSettings.CountAsync();
        }

        var path = $"/Items/{PlaylistVirtualizationService.CreateProtocolId(id)}/Images/Primary";
        var input = storedLink ? new MixCoverInput(id.ToString(), 2, "external", Source: "lastfm",
            SourcePlaylistId: "loved", Name: "Loved tracks") : MixCoverInput.From(mix);
        using var first = await client.GetAsync(path + "?tag=old-tag&maxHeight=300");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal('"' + MixCoverImageService.GetTag(input) + '"', first.Headers.ETag?.Tag);
        Assert.True(first.Headers.CacheControl?.Private);
        using var png = SKBitmap.Decode(await first.Content.ReadAsByteArrayAsync());
        Assert.Equal(600, png.Width);
        using var head = await client.SendAsync(new(HttpMethod.Head, path + "/0/old-tag/png/300/300/0/0"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("image/png", head.Content.Headers.ContentType?.MediaType);
        Assert.Equal(first.Headers.ETag, head.Headers.ETag);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        foreach (var suffix in new[] { "Backdrop", "Primary/1" })
        {
            using var other = await client.GetAsync(path.Replace("Primary", suffix));
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }
        using var metadata = await client.GetAsync($"/Items/{PlaylistVirtualizationService.CreateProtocolId(id)}");
        Assert.Equal(HttpStatusCode.Unauthorized, metadata.StatusCode);
        publish = false;
        using var unpublished = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);
        publish = true;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await db.ExternalMetadataSnapshots.CountAsync());
            Assert.Equal(storedLink ? 1 : 0, await db.PlaylistSourceSnapshots.CountAsync());
            Assert.Equal(runtimeRows, await db.RuntimeSettings.CountAsync());
            if (storedLink) (await db.PlaylistLinks.SingleAsync(item => item.Id == id)).Enabled = false;
            else db.ExternalMetadataSnapshots.Add(PersonalCoverSnapshot(Guid.NewGuid(), owner, mix with { Added = false }, 2));
            await db.SaveChangesAsync();
        }
        using var removed = new HttpRequestMessage(HttpMethod.Get, path);
        removed.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var absent = await client.SendAsync(removed);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        gateway.VerifyNoOtherCalls();
    }

    private static ExternalMetadataSnapshotRecord PersonalCoverSnapshot(Guid snapshotId, Guid owner, PersonalMix mix, int version) => new()
    {
        Id = snapshotId,
        OwnerUserId = owner,
        Protocol = "jellyfin",
        BackendInstanceId = "primary",
        BackendPrincipalId = "cover-owner",
        ProviderId = "allstarr",
        ResourceKind = "personal-mixes",
        ExternalIdHash = new('a', 64),
        SnapshotVersion = version,
        ProviderRevision = version.ToString(),
        PayloadSha256 = new('b', 64),
        CorrelationId = "cover-fixture",
        PayloadJson = JsonSerializer.Serialize(new { AppsOptInVersion = 3, Mixes = new Dictionary<string, PersonalMix> { [mix.Kind] = mix } })
    };

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task PersonalMixCoverRoutesReturnPngAndRevalidateEachGeneration(string backend)
    {
        var model = CoverPlaylist() with { MixCover = new("mix-1", 1, "daily-1", Name: "Daily Mix", Count: 0) };
        var readable = true;
        var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        playlists.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), model.ProtocolId,
            It.IsAny<CancellationToken>())).ReturnsAsync(() => readable ? model : null);
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/Users/user-1/Items" => Json(200, """{"Items":[{"Id":"native-playlist","Type":"Playlist","ParentId":"playlists-folder"}],"TotalRecordCount":1}"""),
            "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
            _ => throw new InvalidOperationException("Unexpected cover relay")
        }, services =>
        {
            if (backend == "Jellyfin") AddArtworkPrincipal(services, "user-1");
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(playlists.Object);
        });
        using var client = factory.CreateClient();
        var auth = backend == "Jellyfin" ? "api_key=fixture-key" : "u=fixture&p=fixture&v=1.16.1&c=Tempo&f=json";
        var detailPath = backend == "Jellyfin" ? $"/Items/{model.ProtocolId}?{auth}" :
            $"/rest/getPlaylist?id={model.ProtocolId}&{auth}";
        using var detail = await client.GetAsync(detailPath);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var metadata = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var coverId = backend == "Jellyfin" ? model.ProtocolId : metadata.RootElement
            .GetProperty("subsonic-response").GetProperty("playlist").GetProperty("coverArt").GetString()!;
        if (backend == "Jellyfin")
            Assert.False(string.IsNullOrEmpty(metadata.RootElement.GetProperty("ImageTags").GetProperty("Primary").GetString()));
        var imagePath = backend == "Jellyfin" ? $"/Items/{coverId}/Images/Primary?{auth}" :
            $"/rest/getCoverArt.view?id={coverId}&{auth}";
        using var first = await client.GetAsync(imagePath);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(first.Headers.ETag);
        Assert.True(first.Headers.CacheControl?.Private);
        Assert.True(first.Headers.CacheControl?.MustRevalidate);
        Assert.Equal(TimeSpan.Zero, first.Headers.CacheControl?.MaxAge);
        var bytes = await first.Content.ReadAsByteArrayAsync();
        using var png = SKBitmap.Decode(bytes);
        Assert.Equal(600, png.Width);
        Assert.Equal(600, png.Height);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, imagePath);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());

        model = model with { MixCover = model.MixCover! with { Generation = 2 } };
        using var stale = new HttpRequestMessage(HttpMethod.Get, imagePath);
        stale.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var refreshed = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotEqual(first.Headers.ETag, refreshed.Headers.ETag);
        if (backend == "Jellyfin")
        {
            using var other = await client.GetAsync($"/Items/{model.ProtocolId}/Images/Backdrop?{auth}");
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        }
        else
        {
            using var plain = await client.GetAsync($"/rest/getCoverArt?id={model.ProtocolId}&{auth}");
            Assert.Equal(refreshed.Headers.ETag, plain.Headers.ETag);
            using var updatedDetail = await client.GetAsync(detailPath);
            using var updatedMetadata = JsonDocument.Parse(await updatedDetail.Content.ReadAsStringAsync());
            Assert.NotEqual(coverId, updatedMetadata.RootElement.GetProperty("subsonic-response")
                .GetProperty("playlist").GetProperty("coverArt").GetString());
        }
        readable = false;
        using var denied = new HttpRequestMessage(HttpMethod.Get, imagePath);
        denied.Headers.IfNoneMatch.Add(refreshed.Headers.ETag!);
        using var inaccessible = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
    }

    [Theory]
    [InlineData("Jellyfin", false)]
    [InlineData("Jellyfin", true)]
    [InlineData("Subsonic", false)]
    [InlineData("Subsonic", true)]
    public async Task OrdinaryPlaylistCoversKeepTheirExistingRoute(string backend, bool virtualPlaylist)
    {
        var model = CoverPlaylist() with { ArtworkReferenceKey = "existing-art", TargetPlaylistId = "native-playlist" };
        var id = virtualPlaylist ? model.ProtocolId : "native-playlist";
        var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        playlists.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), model.ProtocolId,
            It.IsAny<CancellationToken>())).ReturnsAsync(model);
        byte[] expected = [1, 2, 3, 4];
        var relays = 0;
        using var factory = new ProtocolFactory(backend, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (path == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            if (backend == "Jellyfin" && IsItemLookup(request, "native-playlist"))
                return ItemLookup("""{"Id":"native-playlist","Type":"Playlist"}""");
            Assert.Equal(backend == "Jellyfin" ? "/Items/native-playlist/Images/Primary" : "/rest/getCoverArt", path);
            if (backend == "Subsonic") Assert.Contains($"id={id}", request.RequestUri.Query);
            relays++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) { Headers = { ContentType = new("image/jpeg") } } };
        }, services =>
        {
            if (backend == "Jellyfin")
            {
                AddArtworkPrincipal(services, "user-1");
                var access = new Mock<IBackendLibraryAccessResolver>();
                access.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new BackendLibraryAccess(true, []));
                services.RemoveAll<IBackendLibraryAccessResolver>();
                services.AddSingleton(access.Object);
            }
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(playlists.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin" ? $"/Items/{id}/Images/Primary?api_key=fixture-key" :
            $"/rest/getCoverArt?id={id}&u=fixture&p=fixture&v=1.16.1&c=Tempo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, relays);
    }

    private static VirtualPlaylistReadModel CoverPlaylist()
    {
        var linkId = Guid.Parse("88fc0616-e45e-44ac-81c3-6a884e86e15d");
        return new(PlaylistVirtualizationService.CreateProtocolId(linkId), linkId, Guid.NewGuid(), "Daily Mix", null,
            null, "personal", "mix-1", "1", PlaylistLinkMode.Virtual, []);
    }
}
