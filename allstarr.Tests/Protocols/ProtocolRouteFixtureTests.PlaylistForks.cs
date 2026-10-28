using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("add", false)]
    [InlineData("remove", false)]
    [InlineData("move", false)]
    [InlineData("add", true)]
    [InlineData("remove", true)]
    [InlineData("move", true)]
    public async Task JellyfinPlaylistFork_FirstMembershipEditPreservesOriginalAndLaterEditsUseCopy(string edit, bool nativeId)
    {
        using var fixture = new ForkFixture();
        await fixture.SeedAsync();
        var id = nativeId ? ForkFixture.NativeId : fixture.Source.ProtocolId;
        var before = await fixture.ReadAsync(id);
        var editable = await fixture.GetAsync($"/Playlists/{id}/Users/user-1?api_key=fixture-key");
        Assert.True(editable["CanEdit"]!.GetValue<bool>());
        var entries = before.Select(item => item["PlaylistItemId"]!.GetValue<string>()).ToArray();
        Assert.Equal(3, entries.Distinct().Count());
        using var changed = edit switch
        {
            "remove" => await fixture.Client.DeleteAsync($"/Playlists/{id}/Items?api_key=fixture-key&EntryIds={entries[1]}"),
            "move" => await fixture.Client.PostAsync($"/Playlists/{id}/Items/{entries[2]}/Move/0?api_key=fixture-key", null),
            _ => await fixture.Client.PostAsync($"/Playlists/{id}/Items?api_key=fixture-key&Ids=ext-deezer-song-B", null)
        };
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        string[] expected = edit switch
        {
            "remove" => ["native-song", "ext-deezer-song-A"],
            "move" => ["ext-deezer-song-A", "native-song", "native-song"],
            _ => ["native-song", "native-song", "ext-deezer-song-A", "ext-deezer-song-B"]
        };
        Assert.Equal(expected, ForkFixture.Ids(await fixture.ReadAsync(id)));
        Assert.Equal(expected, ForkFixture.Ids(await fixture.ReadAsync(fixture.Source.ProtocolId)));
        Assert.Equal(expected, ForkFixture.Ids(await fixture.ReadAsync(ForkFixture.NativeId)));
        Assert.Equal(expected, ForkFixture.Ids(await fixture.ReadAsync(Guid.Parse(ForkFixture.NativeId).ToString("D").ToUpperInvariant())));
        Assert.Equal(expected, ForkFixture.Ids(await fixture.ReadAsync("ext-spotify-playlist-weekly")));
        Assert.Equal(["native-song", "native-song", "ext-deezer-song-A"], ForkFixture.Ids(await fixture.ReadAsync(id, other: true)));

        using var added = await fixture.Client.PostAsync($"/Playlists/{id}/Items?api_key=fixture-key&Ids=ext-deezer-song-B", null);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        expected = [.. expected, "ext-deezer-song-B"];
        fixture.Source = fixture.Source with { Name = "Source renamed", SourceRevision = "next", Tracks = [] };
        Assert.Equal(expected, ForkFixture.Ids(await fixture.ReadAsync(id)));
        Assert.Empty(await fixture.ReadAsync(id, other: true));
        var detail = await fixture.GetAsync($"/Items/{id}?api_key=fixture-key");
        Assert.Equal("Weekly", detail["Name"]!.GetValue<string>());
        Assert.Equal(expected.Length, detail["ChildCount"]!.GetValue<int>());
        var batch = await fixture.GetAsync($"/Items?Ids={id}&api_key=fixture-key");
        Assert.Equal("Weekly", Assert.Single(batch["Items"]!.AsArray())!["Name"]!.GetValue<string>());
        var children = await fixture.GetAsync($"/Users/user-1/Items?ParentId={id}&IncludeItemTypes=Audio&api_key=fixture-key");
        Assert.Equal(expected, ForkFixture.Ids(children["Items"]!.AsArray().Select(item => item!.AsObject()).ToArray()));
        var definition = await fixture.GetAsync($"/Playlists/{id}?api_key=fixture-key");
        Assert.Equal(expected, definition["ItemIds"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.False(definition["OpenAccess"]!.GetValue<bool>());
        var permission = await fixture.GetAsync($"/Playlists/{id}/Users/user-1?api_key=fixture-key");
        Assert.True(permission["CanEdit"]!.GetValue<bool>());
        var browse = await fixture.GetAsync("/Users/user-1/Items?IncludeItemTypes=Playlist&Fields=ChildCount&api_key=fixture-key");
        var summary = Assert.Single(browse["Items"]!.AsArray())!;
        Assert.Equal("Weekly", summary["Name"]!.GetValue<string>());
        Assert.Equal(expected.Length, summary["ChildCount"]!.GetValue<int>());
        Assert.Single(await fixture.ForkRowsAsync());
        Assert.Equal(0, fixture.UpstreamWrites);
    }

    [Fact]
    public async Task JellyfinPlaylistFork_ConcurrentFirstEditsCreateOneDurableCopyAndKeepBothEdits()
    {
        using var fixture = new ForkFixture();
        await fixture.SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "B"))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return new allstarr.Models.Domain.Song { Id = "ext-deezer-song-B", Title = "B", ExternalProvider = "deezer", ExternalId = "B" };
            });
        var first = fixture.Client.PostAsync($"/Playlists/{fixture.Source.ProtocolId}/Items?api_key=fixture-key&Ids=ext-deezer-song-B", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = fixture.Client.PostAsync($"/Playlists/{ForkFixture.NativeId}/Items?api_key=fixture-key&Ids=ext-deezer-song-B", null);
        release.SetResult();
        var responses = await Task.WhenAll(first, second);
        foreach (var response in responses)
        {
            using (response) Assert.True(response.StatusCode == HttpStatusCode.NoContent,
                $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        var rows = await fixture.ForkRowsAsync();
        Assert.Single(rows);
        await using (var db = await fixture.Factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var revisions = await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "playlist-fork")
                .Select(item => item.PayloadJson).ToArrayAsync();
            Assert.Equal(2, revisions.Length);
            Assert.Single(revisions.Select(payload => JsonNode.Parse(payload)!["Playlist"]!["ProtocolId"]!.GetValue<string>()).Distinct());
        }
        var copy = JsonNode.Parse(rows[0].PayloadJson)!["Playlist"]!["ProtocolId"]!.GetValue<string>();
        Assert.NotEqual(fixture.Source.ProtocolId, copy);
        var members = await fixture.ReadAsync(copy);
        Assert.Equal(5, members.Length);
        Assert.Equal(5, members.Select(item => item["PlaylistItemId"]!.GetValue<string>()).Distinct().Count());
        Assert.Equal(2, ForkFixture.Ids(members).Count(id => id == "ext-deezer-song-B"));
        Assert.Equal(ForkFixture.Ids(members), ForkFixture.Ids(await fixture.ReadAsync(fixture.Source.ProtocolId)));
        var storage = new WritablePlaylistMembership(fixture.Factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(),
            Mock.Of<IProtocolProviderGateway>(), Mock.Of<ILibraryIndexService>());
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "user-1",
            new AllstarrPrincipal(ForkFixture.Owner, "jellyfin", "primary", "user-1", "Owner", false),
            "reload-copy", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var reloaded = await storage.ReadForkAsync(context, fixture.Source.ProtocolId, default);
        Assert.NotNull(reloaded);
        Assert.Equal(copy, reloaded.Playlist.ProtocolId);
        Assert.Equal(5, reloaded.Playlist.Tracks.Count);
        using var denied = await fixture.Other.GetAsync($"/Items/{copy}?api_key=other-key");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var editDenied = await fixture.Other.PostAsync($"/Playlists/{copy}/Items?api_key=other-key&Ids=ext-deezer-song-B", null);
        Assert.Equal(HttpStatusCode.NotFound, editDenied.StatusCode);
        Assert.Equal(0, fixture.UpstreamWrites);
    }

    [Fact]
    public async Task JellyfinPlaylistFork_InvalidEditsAndRenameDoNotCreateCopy_EmptyImportCanFork()
    {
        using var fixture = new ForkFixture();
        await fixture.SeedAsync();
        var id = fixture.Source.ProtocolId;
        using var rename = await fixture.Client.PostAsync($"/Playlists/{id}?api_key=fixture-key",
            new StringContent("""{"Name":"Changed"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        using var nativeRename = await fixture.Client.PostAsync($"/Playlists/{ForkFixture.NativeId}?api_key=fixture-key",
            new StringContent("""{"Name":"Changed"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, nativeRename.StatusCode);
        using var unchangedMembership = await fixture.Client.PostAsync($"/Playlists/{id}?api_key=fixture-key",
            new StringContent("""{"Name":"Changed","Ids":["native-song","native-song","ext-deezer-song-A"]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, unchangedMembership.StatusCode);
        using var remove = await fixture.Client.DeleteAsync($"/Playlists/{id}/Items?api_key=fixture-key&EntryIds=missing");
        Assert.Equal(HttpStatusCode.BadRequest, remove.StatusCode);
        using var move = await fixture.Client.PostAsync($"/Playlists/{id}/Items/source-entry/Move/9?api_key=fixture-key", null);
        Assert.Equal(HttpStatusCode.BadRequest, move.StatusCode);
        using var unavailable = await fixture.Client.PostAsync($"/Playlists/{id}/Items?api_key=fixture-key&Ids=missing", null);
        Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
        using var unlinkedRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/Playlists/{ForkFixture.NativeId}/Items?api_key=fixture-key&Ids=ext-deezer-song-B");
        unlinkedRequest.Headers.Add("X-Fork-Listener", "unlinked");
        using var unlinked = await fixture.Client.SendAsync(unlinkedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, unlinked.StatusCode);
        Assert.Empty(await fixture.ForkRowsAsync());
        fixture.Source = fixture.Source with { Tracks = [] };
        using var added = await fixture.Client.PostAsync($"/Playlists/{id}/Items?api_key=fixture-key&Ids=ext-deezer-song-B", null);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        Assert.Equal(["ext-deezer-song-B"], ForkFixture.Ids(await fixture.ReadAsync(id)));
        Assert.Single(await fixture.ForkRowsAsync());
        Assert.Equal(0, fixture.UpstreamWrites);
    }

    [Fact]
    public async Task JellyfinPlaylistFork_AdminListingIncludesOriginalAndEnforcesListenerScope()
    {
        using var fixture = new ForkFixture();
        await fixture.SeedAsync();
        using var edit = await fixture.Client.PostAsync($"/Playlists/{fixture.Source.ProtocolId}/Items?api_key=fixture-key&Ids=ext-deezer-song-B", null);
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        var controller = new PlaylistLinksController(fixture.Factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(),
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = fixture.Factory.Services } } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.ListPlaylistForks(ForkFixture.Owner, default));
        var session = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "user-1",
            UserName = "Owner",
            IsAdministrator = false,
            AllstarrUserId = ForkFixture.Owner,
            JellyfinAccessToken = "",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        var result = Assert.IsType<OkObjectResult>(await controller.ListPlaylistForks(ForkFixture.Owner, default));
        var row = Assert.Single(JsonSerializer.SerializeToElement(result.Value).EnumerateArray());
        Assert.Equal(fixture.Source.ProtocolId, row.GetProperty("originalPlaylistId").GetString());
        Assert.Equal(ForkFixture.NativeId, row.GetProperty("originalTargetPlaylistId").GetString());
        Assert.Equal("Weekly", row.GetProperty("name").GetString());
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.ListPlaylistForks(ForkFixture.OtherOwner, default)).StatusCode);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "admin-fixture",
            UserId = "user-1",
            UserName = "Owner",
            IsAdministrator = true,
            AllstarrUserId = ForkFixture.Owner,
            JellyfinAccessToken = "",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var other = Assert.IsType<OkObjectResult>(await controller.ListPlaylistForks(ForkFixture.OtherOwner, default));
        Assert.Empty(JsonSerializer.SerializeToElement(other.Value).EnumerateArray());
    }

    [Fact]
    public async Task JellyfinPlaylistFork_VirtualImportRejectsStaleOccurrencesAndCopiesCurrentMembership()
    {
        using var fixture = new ForkFixture(virtualOnly: true);
        await fixture.SeedAsync();
        var id = fixture.Source.ProtocolId;
        var entries = await fixture.ReadAsync(id);
        var editable = await fixture.GetAsync($"/Playlists/{id}/Users/user-1?api_key=fixture-key");
        Assert.True(editable["CanEdit"]!.GetValue<bool>());
        fixture.Source = fixture.Source with { Tracks = fixture.Source.Tracks.Skip(1).ToArray(), SourceRevision = "next" };
        var staleEntry = entries[0]["PlaylistItemId"]!.GetValue<string>();
        using var stale = await fixture.Client.DeleteAsync($"/Playlists/{id}/Items?api_key=fixture-key&EntryIds={staleEntry}");
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Empty(await fixture.ForkRowsAsync());
        using var added = await fixture.Client.PostAsync($"/Playlists/{id}/Items?api_key=fixture-key&Ids=ext-deezer-song-B", null);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        Assert.Equal(["native-song", "ext-deezer-song-A", "ext-deezer-song-B"], ForkFixture.Ids(await fixture.ReadAsync(id)));
        Assert.Single(await fixture.ForkRowsAsync());
        Assert.Equal(0, fixture.UpstreamWrites);
    }

    [Fact]
    public async Task JellyfinPlaylistFork_MembershipReplacementPreservesSurvivingOccurrencesAndIgnoresMetadata()
    {
        using var fixture = new ForkFixture();
        await fixture.SeedAsync();
        var id = fixture.Source.ProtocolId;
        var before = await fixture.ReadAsync(id);
        using var replaced = await fixture.Client.PostAsync($"/Playlists/{id}?api_key=fixture-key", new StringContent(
            """{"Name":"Changed","IsPublic":true,"Ids":["ext-deezer-song-A","native-song","native-song"]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        var after = await fixture.ReadAsync(id);
        Assert.Equal([before[2]["PlaylistItemId"]!.GetValue<string>(), before[0]["PlaylistItemId"]!.GetValue<string>(), before[1]["PlaylistItemId"]!.GetValue<string>()],
            after.Select(item => item["PlaylistItemId"]!.GetValue<string>()));
        var detail = await fixture.GetAsync($"/Items/{id}?api_key=fixture-key");
        Assert.Equal("Weekly", detail["Name"]!.GetValue<string>());
        using var emptied = await fixture.Client.PostAsync($"/Playlists/{id}?api_key=fixture-key", new StringContent(
            """{"Ids":[]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, emptied.StatusCode);
        Assert.Empty(await fixture.ReadAsync(id));
        Assert.Single(await fixture.ForkRowsAsync());
        Assert.Equal(0, fixture.UpstreamWrites);
    }

    [Fact]
    public async Task JellyfinPlaylistFork_SourceRefreshCanChangeNativeTargetWithoutChangingPersonalCopy()
    {
        using var fixture = new ForkFixture();
        await fixture.SeedAsync();
        using var added = await fixture.Client.PostAsync($"/Playlists/{ForkFixture.NativeId}/Items?api_key=fixture-key&Ids=ext-deezer-song-B", null);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        var target = Guid.CreateVersion7().ToString("N");
        await using (var db = await fixture.Factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            (await db.PlaylistLinks.SingleAsync()).TargetPlaylistId = target;
            await db.SaveChangesAsync();
        }
        fixture.Source = fixture.Source with { TargetPlaylistId = target, SourceRevision = "next", Tracks = [] };
        Assert.Equal(["native-song", "native-song", "ext-deezer-song-A", "ext-deezer-song-B"], ForkFixture.Ids(await fixture.ReadAsync(target)));
        Assert.Empty(await fixture.ReadAsync(target, other: true));
        Assert.Single(await fixture.ForkRowsAsync());
        Assert.Equal(0, fixture.UpstreamWrites);
    }

    private sealed class ForkFixture : IDisposable
    {
        public const string NativeId = "4383a46d8bcac3be2ef9385053ea18df";
        public static readonly Guid Owner = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd");
        public static readonly Guid OtherOwner = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60ce");
        public ProtocolFactory Factory { get; }
        public HttpClient Client { get; }
        public HttpClient Other { get; }
        public Mock<IProtocolProviderGateway> Gateway { get; } = MixedPlaylistGateway();
        public int UpstreamWrites;
        public VirtualPlaylistReadModel Source { get; set; }

        public ForkFixture(bool virtualOnly = false)
        {
            var link = Guid.CreateVersion7();
            Source = new(PlaylistVirtualizationService.CreateProtocolId(link), link, Guid.CreateVersion7(),
                "Weekly", null, null, "spotify", "weekly", "revision", PlaylistLinkMode.Materialized,
                [
                    new(0, "native-song", "Native", "Artist", "Album", null, 1000, null, TrackMatchState.Accepted, NativePlaylistEntryId: "native-first"),
                    new(1, "native-song", "Native", "Artist", "Album", null, 1000, null, TrackMatchState.Accepted, NativePlaylistEntryId: "native-second"),
                    new(2, "ext-deezer-song-A", "A", "Artist", "Album", null, 1000, null, TrackMatchState.Accepted,
                        RouteKind: TrackRouteKind.External, RouteProviderId: "deezer", RouteExternalId: "A", SourceEntryId: Guid.CreateVersion7())
                ], TargetPlaylistId: NativeId);
            if (virtualOnly) Source = Source with { Mode = PlaylistLinkMode.Virtual, TargetPlaylistId = null };
            var virtualization = new Mock<IPlaylistVirtualizationService>();
            virtualization.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProtocolExecutionContext _, string id, CancellationToken _) =>
                    id == Source.ProtocolId || id == Source.TargetPlaylistId ? Source : null);
            virtualization.Setup(service => service.ListAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => [Source]);
            Factory = new("Jellyfin", request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
                {
                    Interlocked.Increment(ref UpstreamWrites);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (path == "/Users/Me") return Json(200,
                    QueryHelpers.ParseQuery(request.RequestUri.Query).GetValueOrDefault("ApiKey") == "other-key"
                        ? """{"Id":"user-2","Name":"Other"}""" : """{"Id":"user-1","Name":"Owner"}""");
                if (path == "/Items")
                {
                    var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                    if (query.ContainsKey("UserId") && query.GetValueOrDefault("ApiKey") != "fixture-key" && query.GetValueOrDefault("ApiKey") != "other-key")
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    var ids = query.GetValueOrDefault("Ids").ToString().Split(',');
                    return Json(200, JsonSerializer.Serialize(new { Items = ids.Where(id => id != "missing").Select(id => new { Id = id, Name = "Native", Type = "Audio" }), TotalRecordCount = ids.Length }));
                }
                if (path.StartsWith("/Users/", StringComparison.Ordinal) && path.EndsWith("/Items", StringComparison.Ordinal))
                    return Json(200, $$"""{"Items":[{"Id":"{{NativeId}}","Name":"Weekly","Type":"Playlist","ChildCount":3}],"TotalRecordCount":1}""");
                return Json(200, """{"Items":[],"TotalRecordCount":0}""");
            }, services =>
            {
                ConfigureMixedPlaylists(services, Gateway);
                services.RemoveAll<IPlaylistVirtualizationService>();
                services.AddSingleton(virtualization.Object);
                services.RemoveAll<allstarr.Core.Protocols.Jellyfin.IJellyfinPlaylistMutationResolver>();
                services.AddSingleton<allstarr.Core.Protocols.Jellyfin.IJellyfinPlaylistMutationResolver, allstarr.Core.Protocols.Jellyfin.JellyfinPlaylistMutationResolver>();
                services.AddSingleton<IStartupFilter, ForkPrincipalStartupFilter>();
            });
            Client = Factory.CreateClient();
            Other = Factory.CreateClient();
            Other.DefaultRequestHeaders.Add("X-Fork-Listener", "other");
        }

        public async Task SeedAsync()
        {
            await SeedMixedPlaylistUserAsync(Factory, "jellyfin", "user-1");
            await using var db = await Factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
            db.Users.Add(new() { Id = OtherOwner, BackendType = "jellyfin", BackendInstanceId = "primary", BackendPrincipalId = "user-2", DisplayName = "Other", Enabled = true });
            db.PlaylistLinks.Add(new()
            {
                Id = Source.LinkId,
                OwnerUserId = Owner,
                SourceProviderId = "spotify",
                SourcePlaylistId = "weekly",
                SourcePlaylistIdHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("weekly"))),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "primary",
                TargetPlaylistId = Source.TargetPlaylistId,
                Mode = Source.Mode
            });
            await db.SaveChangesAsync();
        }

        public async Task<JsonObject[]> ReadAsync(string id, bool other = false)
        {
            using var response = await (other ? Other : Client).GetAsync($"/Playlists/{id}/Items?api_key={(other ? "other-key" : "fixture-key")}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["Items"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        }

        public async Task<JsonObject> GetAsync(string path)
        {
            using var response = await Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        public async Task<ExternalMetadataSnapshotRecord[]> ForkRowsAsync()
        {
            await using var db = await Factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
            var rows = await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "playlist-fork").ToArrayAsync();
            return rows.GroupBy(item => item.ExternalIdHash).Select(group => group.MaxBy(item => item.SnapshotVersion)!).ToArray();
        }

        public static string[] Ids(JsonObject[] rows) => rows.Select(row => row["Id"]!.GetValue<string>()).ToArray();
        public void Dispose() { Other.Dispose(); Client.Dispose(); Factory.Dispose(); }
    }

    private sealed class ForkPrincipalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Headers["X-Fork-Listener"] == "unlinked")
                    context.Items.Remove(BackendIdentityResolver.HttpContextPrincipalItemKey);
                else if (context.Request.Headers["X-Fork-Listener"] == "other")
                    context.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = new AllstarrPrincipal(
                        ForkFixture.OtherOwner, "jellyfin", "primary", "user-2", "Other", false);
                await continuation();
            });
            next(app);
        };
    }
}
