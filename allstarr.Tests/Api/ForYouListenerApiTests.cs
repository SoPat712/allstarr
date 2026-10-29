using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Middleware;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task ListenerHttp_MixActionsUseOwnDataAndRejectOtherListenersIds()
    {
        await Seed(user: other);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", It.IsAny<int>()))
            .ReturnsAsync(songs.Values.ToList());
        var own = (await service.GenerateAsync(other)).Single(mix => mix.Kind == "on-repeat");
        var foreign = (await service.ListAsync(owner)).Single(mix => mix.Kind == "on-repeat");
        var foreignPreview = await service.StartAsync(owner, new("song", songs["0"].Id, 25));
        var foreignCustom = await service.SaveCustomAsync(owner, foreignPreview.Id, "Private mix");
        clock.UtcNow += TimeSpan.FromMinutes(16);
        using var host = await ForYouHost();
        using var client = await ListenerClient(host, other);

        var cards = await ReadSuccess(await client.GetAsync("/api/admin/mixes?timeZoneId=UTC"));
        Assert.Contains(cards.EnumerateArray(), card => card.GetProperty("id").GetGuid() == own.Id);
        Assert.DoesNotContain(cards.EnumerateArray(), card => card.GetProperty("id").GetGuid() == foreign.Id);
        await ReadSuccess(await client.GetAsync("/API/ADMIN/MIXES/"));
        await ReadSuccess(await client.GetAsync($"/api/admin/mixes/{own.Id}"));
        await ReadSuccess(await client.PostAsync($"/api/admin/mixes/{own.Id}/refresh", null));
        await ReadSuccess(await client.PutAsJsonAsync($"/api/admin/mixes/{own.Id}/in-apps", new { enabled = true }));
        Assert.True((await service.ListAsync(other)).Single(mix => mix.Id == own.Id).ShowInApps);
        var songId = (await service.ListAsync(other)).Single(mix => mix.Id == own.Id).Songs[0].Song.Id;
        await ReadSuccess(await client.PostAsJsonAsync($"/api/admin/mixes/{own.Id}/feedback", new { songId, kind = "notForMe", todayOnly = true }));
        var preview = await ReadSuccess(await client.PostAsJsonAsync("/api/admin/mixes/start", new { seedKind = "song", seedId = songs["0"].Id, length = 25 }));
        var previewId = preview.GetProperty("previewId").GetGuid();
        await ReadSuccess(await client.PostAsJsonAsync($"/api/admin/mixes/start/{previewId}/save", new { name = "My mix" }));
        await ReadSuccess(await client.DeleteAsync($"/api/admin/mixes/{previewId}"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/mixes/{foreign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/admin/mixes/{foreign.Id}/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/admin/mixes/{foreign.Id}/in-apps", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/mixes/{foreign.Id}/feedback", new { songId, kind = "notForMe" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/mixes/start/{foreignPreview.Id}/save", new { name = "Stolen" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/admin/mixes/{foreignCustom.Id}")).StatusCode);
        Assert.Contains(await service.ListAsync(owner), mix => mix.Id == foreignCustom.Id);
        Assert.False((await service.ListAsync(owner)).Single(mix => mix.Id == foreign.Id).ShowInApps);
    }

    [Fact]
    public async Task ListenerHttp_ExternalMixUsesPersonalAccountAndOwnJobsAndLinks()
    {
        var ownLink = await ExternalLink(listener: other);
        var foreignLink = await ExternalLink();
        using var host = await ForYouHost();
        using var client = await ListenerClient(host, other);
        var card = await ReadSuccess(await client.PostAsJsonAsync("/api/admin/mixes/external", new { source = "lastfm", kind = "top", period = "7day" }));
        var id = card.GetProperty("id").GetGuid();
        var jobId = card.GetProperty("initialJobId").GetGuid();
        await ReadSuccess(await client.GetAsync($"/api/admin/jobs/{jobId}?limit=25"));
        var sources = await ReadSuccess(await client.GetAsync("/api/admin/playlist-sources"));
        Assert.Equal(ownLink.ProviderAccountId, Assert.Single(sources.GetProperty("accounts").EnumerateArray()).GetProperty("id").GetGuid());
        var links = await ReadSuccess(await client.GetAsync("/api/admin/playlist-links"));
        Assert.DoesNotContain(links.GetProperty("playlistLinks").EnumerateArray(), link => link.GetProperty("id").GetGuid() == foreignLink.Id);
        await ReadSuccess(await client.GetAsync($"/api/admin/playlist-links/{ownLink.Id}"));
        await ReadSuccess(await client.GetAsync($"/api/admin/mixes/{id}"));
        await ReadSuccess(await client.PutAsJsonAsync($"/api/admin/mixes/{id}/in-apps", new { enabled = false }));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/playlist-links/{foreignLink.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/mixes/{foreignLink.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/admin/mixes/{foreignLink.Id}/in-apps", new { enabled = false })).StatusCode);
        await using var db = factory.CreateDbContext();
        var created = await db.PlaylistLinks.SingleAsync(link => link.Id == id);
        Assert.Equal(other, created.OwnerUserId);
        Assert.Equal(ownLink.ProviderAccountId, created.ProviderAccountId);
        Assert.Equal(other, (await db.Jobs.SingleAsync(job => job.Id == jobId)).OwnerUserId);
        using var foreignClient = await ListenerClient(host, owner);
        Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.GetAsync($"/api/admin/jobs/{jobId}")).StatusCode);
    }

    [Fact]
    public async Task ListenerHttp_SeedSearchAndArtworkUseListenerLibraryAccess()
    {
        await Seed(user: other);
        await using (var db = factory.CreateDbContext())
        {
            db.LibraryTracks.AddRange(new LibraryTrackRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = other,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "visible",
                BackendItemId = "my-song",
                Title = "Seed song",
                Artist = "My artist"
            }, new LibraryTrackRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "hidden",
                BackendItemId = "private-song",
                Title = "Seed private",
                Artist = "Private artist"
            });
            await db.SaveChangesAsync();
        }
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult(new BackendLibraryAccessContext(Context(id), new(true, ["visible"]))));
        gateway.Setup(item => item.GetProviderOrder(It.IsAny<ProviderCapabilityKind>())).Returns(["deezer"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Seed", 50))
            .Returns((ProtocolExecutionContext context, string _, int _) =>
            {
                Assert.Equal(other.ToString(), context.VerifiedBackendPrincipalId);
                return Task.FromResult<IReadOnlyList<Song>>(songs.Values.Take(1).ToList());
            });
        using var host = await ForYouHost();
        using var client = await ListenerClient(host, other);
        var local = await ReadSuccess(await client.GetAsync("/api/admin/track-matches/targets/local?query=Seed"));
        Assert.Equal("my-song", Assert.Single(local.GetProperty("tracks").EnumerateArray()).GetProperty("backendItemId").GetString());
        var provider = await ReadSuccess(await client.GetAsync("/api/admin/track-matches/targets/provider?query=Seed&limit=50"));
        Assert.Single(provider.GetProperty("tracks").EnumerateArray());
        (await client.GetAsync("/api/admin/downloads/artwork/my-song")).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/admin/downloads/artwork/ext-deezer-song-0")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/downloads/artwork/private-song")).StatusCode);
    }

    [Fact]
    public async Task ListenerHttp_TasteAndRecapUseTheSessionListener()
    {
        await taste.PutNudgeAsync(owner, new("artist", "Private artist", "never"));
        await Seed(user: owner);
        using var host = await ForYouHost();
        using var client = await ListenerClient(host, other);
        var profile = await ReadSuccess(await client.GetAsync("/api/admin/taste"));
        Assert.DoesNotContain("Private artist", profile.ToString());
        await ReadSuccess(await client.PutAsJsonAsync("/api/admin/taste/nudge", new { kind = "artist", name = "My artist", mode = "more" }));
        Assert.DoesNotContain((await taste.GetAsync(owner)).Nudges, nudge => nudge.Name == "My artist");
        await ReadSuccess(await client.DeleteAsync("/api/admin/taste/nudge?kind=artist&name=My%20artist"));
        await ReadSuccess(await client.PostAsync("/api/admin/taste/pause", null));
        await ReadSuccess(await client.PostAsync("/api/admin/taste/resume", null));
        await ReadSuccess(await client.PutAsJsonAsync("/api/admin/taste/imported-history", new { enabled = false }));
        await ReadSuccess(await client.PostAsJsonAsync("/api/admin/taste/reset", new { clearNudges = true }));
        Assert.Contains((await taste.GetAsync(owner)).Nudges, nudge => nudge.Name == "Private artist");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/updates/stream?scope=everyone")).StatusCode);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/updates/stream");
        using var stream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        stop.Cancel();
        foreach (var query in new[] { "period=7d", "period=30d", "period=year", "period=all", "year=2026" })
        {
            var stats = await ReadSuccess(await client.GetAsync("/api/admin/stats?" + query));
            Assert.Equal(0, stats.GetProperty("importedCount").GetInt64());
            Assert.Empty(stats.GetProperty("topSongs").EnumerateArray());
        }
    }

    [Fact]
    public async Task ListenerHttp_HistoryImportActionsRejectAnotherListenersImport()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-listener-history", Guid.NewGuid().ToString("N"));
        try
        {
            using var host = await ForYouHost(root);
            using var client = await ListenerClient(host, other);
            using var foreignClient = await ListenerClient(host, owner);
            using var upload = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new
                {
                    ts = "2026-10-20T12:00:00Z", ms_played = 180000, master_metadata_track_name = "My song",
                    master_metadata_album_artist_name = "My artist", master_metadata_album_album_name = "Album",
                    spotify_track_uri = "spotify:track:1111111111111111111111", reason_end = "trackdone"
                }
            }));
            var preview = await ReadSuccess(await client.PostAsync("/api/admin/history/imports?fileName=history.json", upload));
            var id = preview.GetProperty("importId").GetGuid();
            var revision = preview.GetProperty("revision").GetString();
            var list = await ReadSuccess(await client.GetAsync("/api/admin/history/imports"));
            Assert.Single(list.EnumerateArray());
            Assert.Empty((await ReadSuccess(await foreignClient.GetAsync("/api/admin/history/imports"))).EnumerateArray());
            await ReadSuccess(await client.GetAsync($"/api/admin/history/imports/{id}"));
            Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.GetAsync($"/api/admin/history/imports/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.PostAsJsonAsync($"/api/admin/history/imports/{id}/confirm", new { revision })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.DeleteAsync($"/api/admin/history/imports/{id}?revision={revision}")).StatusCode);
            await ReadSuccess(await client.PostAsJsonAsync($"/api/admin/history/imports/{id}/confirm", new { revision }));
            await ReadSuccess(await client.DeleteAsync($"/api/admin/history/imports/{id}?revision={revision}"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ListenerHttp_ReleasesAndFollowsUseOwnInbox()
    {
        using var host = await ForYouHost();
        var releases = host.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(owner, new("deezer", "private", "Private artist"));
        using var client = await ListenerClient(host, other);
        (await client.PutAsJsonAsync("/api/admin/releases/artists", new { providerId = "deezer", artistId = "mine", displayName = "My artist" })).EnsureSuccessStatusCode();
        var follows = await ReadSuccess(await client.GetAsync("/api/admin/releases/artists"));
        Assert.Single(follows.EnumerateArray());
        Assert.DoesNotContain("Private artist", follows.ToString());
        await ReadSuccess(await client.PostAsync("/api/admin/releases/artists/from-hearts", null));
        await releases.PollAsync(other, Guid.NewGuid(), default);
        clock.UtcNow += TimeSpan.FromSeconds(3);
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        var ownInbox = await ReadSuccess(await client.GetAsync("/api/admin/releases"));
        var entryId = Assert.Single(ownInbox.GetProperty("entries").EnumerateArray()).GetProperty("id").GetString();
        var foreignEntry = Assert.Single((await releases.ReadAsync(owner)).Entries);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/admin/releases/{foreignEntry.Id}")).StatusCode);
        (await client.DeleteAsync($"/api/admin/releases/{entryId}")).EnsureSuccessStatusCode();
        (await client.DeleteAsync("/api/admin/releases/artists/deezer/mine")).EnsureSuccessStatusCode();
        (await client.DeleteAsync("/api/admin/releases/artists/deezer/private")).EnsureSuccessStatusCode();
        Assert.Single(await releases.ListAsync(owner));
        Assert.Single((await releases.ReadAsync(owner)).Entries);
        Assert.Empty(await releases.ListAsync(other));
    }

    [Theory]
    [InlineData("GET", "/api/admin/mixes/publish-setting")]
    [InlineData("PUT", "/api/admin/mixes/publish-setting")]
    [InlineData("POST", "/api/admin/mixes/dry-run?listenerId=11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/api/admin/audiomuse/connection")]
    [InlineData("PUT", "/api/admin/audiomuse/connection")]
    [InlineData("POST", "/api/admin/audiomuse/analysis")]
    [InlineData("PUT", "/api/admin/preferences/users/11111111-1111-1111-1111-111111111111/clean-only-lock")]
    [InlineData("POST", "/api/admin/mixes/publish-setting")]
    [InlineData("GET", "/api/admin/mixes/start")]
    [InlineData("GET", "/api/admin/mixes/not-a-guid")]
    [InlineData("GET", "/api/admin/mixes-extra")]
    [InlineData("PUT", "/api/admin/mixes/11111111-1111-1111-1111-111111111111/other")]
    public async Task ListenerHttp_HouseholdSettingsAndUnlistedMixActionsStayForbidden(string method, string path)
    {
        using var host = await ForYouHost();
        using var client = await ListenerClient(host, other);
        using var response = await client.SendAsync(new(new HttpMethod(method), path) { Content = JsonContent.Create(new { enabled = true }) });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<IHost> ForYouHost(string? historyRoot = null)
    {
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(user => user.Id == owner)).IsAdmin = false;
            await db.SaveChangesAsync();
        }
        var auth = new AdminAuthSessionService(new MemoryAdminAuthSessionStore(), new EphemeralDataProtectionProvider(),
            NullLogger<AdminAuthSessionService>.Instance, factory, identityOptions: new IdentityOptions { BackendInstanceId = "main" },
            backendSessionValidator: HealthyAdminBackendSessionValidator.Instance);
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.FindByCapability(ProviderCapabilityKind.Playlist, false)).Returns([
            new("lastfm", "Last.fm", "Playlist fixture", ProviderOrigin.BuiltIn, "1", "1",
                [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                    "1", ["getUserPlaylists"], [ProviderAccountScope.Personal])], new())]);
        var router = new Mock<IProviderRouter>();
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .Returns((ProviderExecutionContext _, ProviderArtistItemsRequest request) => Task.FromResult(
                ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer", [new(new("deezer", ProviderResourceKind.Album, request.Id.Value),
                    "Release " + request.Id.Value, [new("Artist", request.Id)], releaseDate: "2026-10-20", releaseType: "album", isExplicit: false)]))));
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .Returns((ProviderRouteRequest request) =>
            {
                var descriptor = new ProviderDescriptor("deezer", "Deezer", "Catalog fixture", ProviderOrigin.BuiltIn, "1", "1",
                    [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["getArtistAlbums"])], new());
                var context = new ProviderExecutionContext(request.Actor, "deezer", null, request.Policy, request.OperationId,
                    request.CorrelationId, request.Deadline, request.CancellationToken);
                return Task.FromResult(new ProviderRoutePlan<IProviderMetadataCapability>(request,
                    [new(0, descriptor, descriptor.Capabilities.Single(), metadata.Object, context, null)],
                    new(request.CorrelationId, request.Capability, "deezer", null, [])));
            });
        var releases = new NewReleasesService(factory, clock, router.Object, settings.Object);
        var matches = new TrackMatchCommandService(factory, new(), new(factory), clock, access.Object);
        var persistence = new PlaylistPersistenceService(factory, new(factory), clock, matches,
            access.Object, providerGateway: gateway.Object, registry: registry.Object);
        var media = new Mock<IMediaAssetResolver>();
        media.Setup(item => item.ResolveAsync(It.IsAny<MediaAssetIdentity>(), It.IsAny<Func<CancellationToken, Task<MediaAssetSource?>>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((MediaAssetIdentity identity, Func<CancellationToken, Task<MediaAssetSource?>> _, int _, CancellationToken _) =>
            {
                Assert.Equal(other, identity.UserId);
                return Task.FromResult<ResolvedMediaAsset?>(new([1, 2, 3], "image/png", "fixture", null, null, true));
            });
        var artwork = new Mock<IPlaybackMetadataResolver>();
        artwork.Setup(item => item.ResolveArtworkAsync(It.IsAny<string>(), It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProtocolExecutionContext context, CancellationToken _) => Task.FromResult<PlaybackArtwork?>(
                id == "my-song" && context.VerifiedBackendPrincipalId == other.ToString() ? new([1, 2, 3], "image/png") : null));
        var importOptions = new ListeningHistoryImportOptions { RootPath = historyRoot ?? Path.Combine(Path.GetTempPath(), "unused-listener-history") };
        return await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddSingleton(auth);
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory);
            services.AddSingleton<IPlatformClock>(clock);
            services.AddSingleton(settings.Object);
            services.AddSingleton(service);
            services.AddSingleton(taste);
            services.AddSingleton(releases);
            services.AddSingleton(queue);
            services.AddSingleton(new ListeningStatsService(factory, clock));
            services.AddSingleton(importOptions);
            services.AddSingleton(new ListeningHistoryImportService(factory, new([new SpotifyListeningHistoryImporter()]),
                new(importOptions), importOptions, clock, queue));
            services.AddScoped(_ => new PlaylistLinksController(factory, persistence, new(factory, access.Object), null!, null!, queue,
                null!, registry.Object, router.Object, null!, null!, null!, clock, null!, new(factory, clock),
                Mock.Of<IPlaylistVirtualizationService>(), null!, new ConfigurationBuilder().Build(), null!));
            services.AddScoped(_ => new TrackMatchesController(matches,
                gateway.Object, new(factory, clock), factory, null!, media.Object, new(), null!));
            services.AddScoped(_ => new DownloadActivityController([], [artwork.Object], media.Object,
                NullLogger<DownloadActivityController>.Instance, access.Object));
            services.AddScoped<AdminPortFilter>();
            services.AddSingleton(new AdminUpdateFeed(factory));
            services.AddControllers().AddApplicationPart(typeof(MixesController).Assembly).AddControllersAsServices();
        }).Configure(app =>
        {
            app.Use((context, next) => { context.Connection.LocalPort = 5275; return next(context); });
            app.UseRouting();
            app.UseMiddleware<AdminAuthenticationMiddleware>();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
    }

    private async Task<HttpClient> ListenerClient(IHost host, Guid user)
    {
        var session = await host.Services.GetRequiredService<AdminAuthSessionService>().CreateSessionAsync(user.ToString(), "Listener",
            false, "fixture-token", "fixture-server", backendType: protocol, allstarrUserId: user);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", AdminAuthSessionService.SessionCookieName + "=" + session.SessionId);
        return client;
    }

    private static async Task<JsonElement> ReadSuccess(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {body}");
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
    }
}
