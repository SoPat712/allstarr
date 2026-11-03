using System.Net;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Items?IncludeItemTypes=Playlist", true)]
    [InlineData("/Items?IncludeItemTypes=Playlist&Recursive=true", true)]
    [InlineData("/Users/listener/Items?IncludeItemTypes=Playlist", true)]
    [InlineData("/Items?IncludeItemTypes=Playlist", false)]
    [InlineData("/Users/listener/Items?IncludeItemTypes=Playlist", false)]
    public async Task JellyfinPlaylistBrowse_SkipsIneligibleMixesAndKeepsLinkedPlaylists(string path, bool synthetic)
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""")
            : Json(200, """{"Items":[],"TotalRecordCount":0}"""), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(new Mock<IProtocolProviderGateway>().Object);
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton<IBackendLibraryAccessResolver>(provider => new TestBackendLibraryAccess(
                provider.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(), "library"));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        await factory.Services.GetRequiredService<IDurableRuntimeSettings>().ApplyBatchAsync([new(PersonalMixService.PublishKey, "true")], "fixture");
        var linked = Guid.NewGuid();
        var external = Guid.NewGuid();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var user = await db.Users.SingleAsync(item => item.BackendPrincipalId == "listener");
            user.IsSynthetic = synthetic;
            foreach (var id in new[] { linked, external })
            {
                var account = Guid.NewGuid();
                var provider = id == external ? "lastfm" : "spotify";
                db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = user.Id, ProviderId = provider, Enabled = true });
                db.PlaylistLinks.Add(new()
                {
                    Id = id,
                    OwnerUserId = user.Id,
                    ProviderAccountId = account,
                    SourceProviderId = provider,
                    SourcePlaylistId = "loved",
                    SourcePlaylistIdHash = id.ToString("N").PadRight(64, 'a'),
                    TargetProtocol = "jellyfin",
                    TargetBackendInstanceId = "primary",
                    Mode = PlaylistLinkMode.Virtual,
                    Enabled = true
                });
                db.PlaylistSourceSnapshots.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = user.Id,
                    PlaylistLinkId = id,
                    ProviderAccountId = account,
                    Name = "Linked playlist",
                    SnapshotVersion = 1,
                    ProviderRevision = "1",
                    PayloadSha256 = new('a', 64),
                    RetrievedAt = DateTimeOffset.UtcNow,
                    PublishedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetString()).ToArray();
        Assert.Contains(PlaylistVirtualizationService.CreateProtocolId(linked), ids);
        Assert.Equal(!synthetic, ids.Contains(PlaylistVirtualizationService.CreateProtocolId(mix.Id)));
        if (synthetic)
        {
            Assert.DoesNotContain(PlaylistVirtualizationService.CreateProtocolId(external), ids);
            foreach (var id in new[] { mix.Id, external })
            {
                using var single = await client.GetAsync($"/Items/{PlaylistVirtualizationService.CreateProtocolId(id)}");
                Assert.Equal(HttpStatusCode.NotFound, single.StatusCode);
            }
        }
    }

    [Theory]
    [InlineData("Jellyfin", "/Items?IncludeItemTypes=Playlist&api_key=fixture-key&fail=true", 403)]
    [InlineData("Subsonic", "/rest/getPlaylists?u=fixture&p=pass&v=1.16.1&c=fixture&f=json&fail=true", 401)]
    public async Task GlobalException_ProtocolPermissionFailureUsesNativeError(string backend, string path, int status)
    {
        var permissionFailed = false;
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1","playlists":{"playlist":[]}}}"""),
            services => services.Configure<MvcOptions>(options => options.Filters.Add(new PermissionFailureResultFilter(() => permissionFailed = true))));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.True(permissionFailed);
        Assert.Equal(status, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (backend == "Jellyfin")
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(403, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Forbidden", body.RootElement.GetProperty("title").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("type").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
            Assert.False(body.RootElement.TryGetProperty("subsonic-response", out _));
        }
        else
            Assert.Equal(40, body.RootElement.GetProperty("subsonic-response").GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinPlaylistAuthenticationFailure_RemainsUnauthorized(bool rejectedToken)
    {
        using var factory = new ProtocolFactory("Jellyfin", _ => Json(401, "{}"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?IncludeItemTypes=Playlist" + (rejectedToken ? "&api_key=invalid" : ""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("subsonic-response", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task LinkedPlaylistBrowse_SkipsDeniedTargetAndKeepsOtherPlaylists(ProtocolKind protocol)
    {
        using var factory = new ProtocolFactory(protocol.ToString(), _ => Json(200, "{}"));
        using var client = factory.CreateClient();
        var backend = protocol.ToString().ToLowerInvariant();
        await SeedMixedPlaylistUserAsync(factory, backend, "user-1");
        var owner = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd");
        var context = new ProtocolExecutionContext(protocol, "primary", "user-1",
            new(owner, backend, "primary", "user-1", "Owner", false), "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        var denied = Guid.NewGuid();
        var visible = Guid.NewGuid();
        await using (var db = await contexts.CreateDbContextAsync())
        {
            foreach (var id in new[] { denied, visible })
            {
                db.PlaylistLinks.Add(new()
                {
                    Id = id,
                    OwnerUserId = owner,
                    SourceProviderId = "spotify",
                    SourcePlaylistId = id.ToString(),
                    SourcePlaylistIdHash = id.ToString("N").PadRight(64, 'a'),
                    TargetProtocol = backend,
                    TargetBackendInstanceId = "primary",
                    TargetPlaylistId = id == denied ? "denied-target" : null,
                    Mode = PlaylistLinkMode.Virtual,
                    Enabled = true
                });
                db.PlaylistSourceSnapshots.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    PlaylistLinkId = id,
                    Name = "Linked playlist",
                    SnapshotVersion = 1,
                    ProviderRevision = "1",
                    PayloadSha256 = new('a', 64),
                    RetrievedAt = DateTimeOffset.UtcNow,
                    PublishedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(context, new(true, [])));
        var target = new Mock<IBackendPlaylistTarget>();
        target.Setup(item => item.ReadAsync(It.IsAny<BackendPlaylistTargetContext>(), "denied-target", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());
        var targets = new Mock<IBackendPlaylistTargetResolver>();
        targets.Setup(item => item.Resolve(backend)).Returns(target.Object);
        var playlists = new PlaylistVirtualizationService(contexts, new(contexts, access.Object), access.Object, targets.Object);
        var listed = await playlists.ListAsync(context);
        Assert.Equal(PlaylistVirtualizationService.CreateProtocolId(visible), Assert.Single(listed).ProtocolId);
        Assert.Null(await playlists.ReadAsync(context, PlaylistVirtualizationService.CreateProtocolId(denied)));
        target.Verify(item => item.ReadAsync(It.IsAny<BackendPlaylistTargetContext>(), "denied-target", It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    private sealed class PermissionFailureResultFilter(Action onFailure) : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.HttpContext.Request.Query.ContainsKey("fail"))
            {
                onFailure();
                throw new UnauthorizedAccessException("Private permission detail");
            }
        }

        public void OnResultExecuted(ResultExecutedContext context) { }
    }
}
