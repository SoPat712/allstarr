using System.Net;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class NewReleasesTests
{
    [Fact]
    public void LibraryArtist_admin_json_contract_uses_requested_field_names()
    {
        var row = new ArtistReviewRow("artist", "Artist", 1, null, "needs-look",
            [new("deezer", "Deezer", "service", "Artist", "candidate", null, 0)]);
        var json = JsonSerializer.SerializeToElement(new ArtistReviewPage(new(1, 0, 0, 0, 1), [row], null));
        Assert.Equal(["counts", "artists", "nextCursor"], json.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["needsLook", "linked", "decided", "apart", "all"], json.GetProperty("counts").EnumerateObject().Select(property => property.Name));
        var artist = Assert.Single(json.GetProperty("artists").EnumerateArray());
        Assert.Equal(["id", "name", "songCount", "imageUrl", "status", "services"], artist.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["providerId", "providerName", "artistId", "name", "state", "scope", "sharedSongs"],
            Assert.Single(artist.GetProperty("services").EnumerateArray()).EnumerateObject().Select(property => property.Name));
        var request = JsonSerializer.SerializeToElement(new ArtistLinksController.FixRequest { Same = true, Scope = "you" });
        Assert.Equal(["same", "scope"], request.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task LibraryArtist_review_filters_counts_search_and_cursor_round_trip()
    {
        var (review, index, _) = await ArtistReviewSetupAsync();
        await ArtistSongAsync("Shared song", "song-artist", "Songs");
        await index.ReadAsync(_first, "Songs");
        await index.RecordCandidatesAsync(_first, ["Artist"], [new() { Name = "Artist", ExternalProvider = "deezer", ExternalId = "candidate" }]);
        await index.SetFixAsync(_first, "Decided", "deezer", "same", true);
        await index.SetFixAsync(_first, "Apart", "deezer", "apart", false);
        var context = ArtistReviewContext(_first);
        foreach (var (filter, name) in new[] { ("needs-look", "Artist"), ("linked", "Songs"), ("decided", "Decided"), ("apart", "Apart") })
        {
            var page = await review.ListAsync(context, filter, null, null, 50, default);
            Assert.Equal(new ArtistReviewCounts(1, 1, 1, 1, 5), page.Counts);
            Assert.Equal(name, Assert.Single(page.Artists).Name);
        }
        var first = await review.ListAsync(context, "all", null, null, 2, default);
        var second = await review.ListAsync(context, "all", null, first.NextCursor, 2, default);
        var third = await review.ListAsync(context, "all", null, second.NextCursor, 2, default);
        Assert.Equal(["Artist", "Songs", "Decided", "Apart", "Library"], first.Artists.Concat(second.Artists).Concat(third.Artists).Select(row => row.Name));
        Assert.Null(third.NextCursor);
        Assert.Equal("library-only", Assert.Single(third.Artists).Status);
        var searched = await review.ListAsync(context, "all", "songs", null, 50, default);
        Assert.Equal(new ArtistReviewCounts(0, 1, 0, 0, 1), searched.Counts);
        var songService = Assert.Single(Assert.Single(searched.Artists).Services);
        Assert.Equal("song-artist", songService.ArtistId);
        Assert.Null(songService.SharedSongs);
        var detail = await review.DetailAsync(context, "songs", default);
        Assert.Equal(["Shared song"], Assert.Single(detail!.Services).SharedSongTitles);
        await Assert.ThrowsAsync<ArgumentException>(() => review.ListAsync(context, "all", "changed", first.NextCursor, 2, default));
        await Assert.ThrowsAsync<ArgumentException>(() => review.ListAsync(context, "all", null, "invalid", 2, default));
        await Assert.ThrowsAsync<ArgumentException>(() => review.ListAsync(context, "invalid", null, null, 2, default));
        await Assert.ThrowsAsync<ArgumentException>(() => review.ListAsync(context, "all", null, null, 101, default));
    }

    [Fact]
    public async Task LibraryArtist_admin_put_and_undo_keep_you_separate_from_household()
    {
        var (review, index, registry) = await ArtistReviewSetupAsync();
        await index.RecordCandidatesAsync(_first, ["Artist"], [new() { Name = "Artist", ExternalProvider = "deezer", ExternalId = "candidate" }]);
        var controller = ArtistReviewController(review, index, registry);
        var put = Assert.IsType<OkObjectResult>(await controller.Put("artist", "deezer", "candidate", new() { Same = true, Scope = "you" }, default));
        var row = Assert.IsType<ArtistReviewRow>(put.Value);
        Assert.Equal("decided", row.Status);
        Assert.Equal("you", Assert.Single(row.Services).Scope);
        Assert.Equal("needs-look", (await review.DetailAsync(ArtistReviewContext(_second), "artist", default))!.Status);
        Assert.IsType<OkObjectResult>(await controller.Delete("artist", "deezer", "candidate", "you", default));
        Assert.Equal("needs-look", (await review.DetailAsync(ArtistReviewContext(_first), "artist", default))!.Status);
        Assert.IsType<OkObjectResult>(await controller.Put("artist", "deezer", "candidate", new() { Same = false, Scope = "household" }, default));
        Assert.Equal("apart", (await review.DetailAsync(ArtistReviewContext(_second), "artist", default))!.Status);
        Assert.IsType<OkObjectResult>(await controller.Delete("artist", "deezer", "candidate", "household", default));
        Assert.Equal("needs-look", (await review.DetailAsync(ArtistReviewContext(_second), "artist", default))!.Status);
        Assert.IsType<NotFoundResult>(await controller.Detail("missing", default));
        Assert.IsType<NotFoundResult>(await controller.Put("missing", "deezer", "candidate", new() { Same = true, Scope = "you" }, default));
        Assert.IsType<BadRequestResult>(await controller.Put("artist", "deezer", "candidate", new() { Same = true, Scope = "invalid" }, default));
        Assert.IsType<BadRequestResult>(await controller.Put("artist", "!invalid", "candidate", new() { Same = true, Scope = "you" }, default));
    }

    [Fact]
    public async Task LibraryArtist_review_counts_refresh_when_songs_start_linking_a_previously_checked_artist()
    {
        var (review, index, _) = await ArtistReviewSetupAsync();
        var song = await ArtistSongAsync("New shared song", null, "Songs");
        Assert.Empty(await index.ReadAsync(_first, "Songs"));
        await ArtistIdentityAsync(song, "linked");
        Assert.Equal(0, (await review.ListAsync(ArtistReviewContext(_first), "linked", null, null, 50, default)).Counts.Linked);
        await index.ReadPartsAsync(_first, "Songs");
        var page = await review.ListAsync(ArtistReviewContext(_first), "linked", null, null, 50, default);
        Assert.Equal(1, page.Counts.Linked);
        Assert.Equal("Songs", Assert.Single(page.Artists).Name);
    }

    [Fact]
    public async Task LibraryArtist_admin_api_denies_listeners_and_the_public_port()
    {
        var controller = ArtistReviewController(null!, null!, null!, administrator: false);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Get()).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Detail("artist", default)).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Put("artist", "deezer", "candidate", new() { Same = true, Scope = "you" }, default)).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Delete("artist", "deezer", "candidate", "you", default)).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Review(default)).StatusCode);
        var http = new DefaultHttpContext();
        http.Connection.LocalPort = 5274;
        var action = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [], new Dictionary<string, object?>(), controller);
        new AdminPortFilter(NullLogger<AdminPortFilter>.Instance).OnActionExecuting(action);
        Assert.IsType<NotFoundResult>(action.Result);
        Assert.Contains(typeof(ArtistLinksController).GetCustomAttributes(typeof(ServiceFilterAttribute), false),
            attribute => ((ServiceFilterAttribute)attribute).ServiceType == typeof(AdminPortFilter));
    }

    [Fact]
    public async Task LibraryArtist_media_server_paging_uses_only_permitted_libraries_and_is_cached()
    {
        var handler = new ArtistReviewHttp(201);
        var (review, _, _) = await ArtistReviewSetupAsync(handler);
        var first = await review.ReadLibraryAsync(ArtistReviewContext(_first), default);
        var again = await review.ReadLibraryAsync(ArtistReviewContext(_first), default);
        Assert.Equal(201, first.Count);
        Assert.Equal(first, again);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Parameters, value => Assert.Contains("musicFolderId=music", value));
        Assert.Contains("artistOffset=200", handler.Parameters[1]);
    }

    [Fact]
    public async Task LibraryArtist_song_counts_use_the_permitted_index_when_the_backend_omits_them()
    {
        await ArtistSongAsync("Shared", null);
        var (review, _, _) = await ArtistReviewSetupAsync(new ArtistReviewHttp(noCounts: true));
        var artists = await review.ReadLibraryAsync(ArtistReviewContext(_first), default);
        Assert.Equal(1, artists.Single(artist => artist.Name == "Artist").SongCount);
        Assert.Equal(0, artists.Single(artist => artist.Name == "Library").SongCount);
    }

    [Theory]
    [InlineData(new[] { "candidate", "same", "not-same", "songs" }, "needs-look")]
    [InlineData(new[] { "same", "not-same", "songs" }, "decided")]
    [InlineData(new[] { "not-same", "songs" }, "apart")]
    [InlineData(new[] { "songs" }, "linked")]
    [InlineData(new string[] { }, "library-only")]
    public void LibraryArtist_review_status_precedence(string[] states, string expected) => Assert.Equal(expected, ArtistReviewRow.StatusFor(states));

    private async Task<(LibraryArtistReview Review, LibraryArtistIndex Index, IProviderRegistry Registry)> ArtistReviewSetupAsync(ArtistReviewHttp? handler = null, Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor? interceptor = null)
    {
        await using var db = _factory.CreateDbContext();
        (await db.Users.SingleAsync(user => user.Id == _first)).IsAdmin = true;
        await db.SaveChangesAsync();
        var descriptor = new ProviderDescriptor("deezer", "Deezer", "Music", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack", "searchArtists"])], new());
        _metadata.SetupGet(value => value.ProviderId).Returns("deezer");
        _metadata.SetupGet(value => value.Capability).Returns(ProviderCapabilityKind.Metadata);
        var registry = new ProviderRegistry([new(descriptor, [_metadata.Object])]);
        var permissions = new Mock<IBackendLibraryPermissionSource>();
        permissions.Setup(value => value.CreateRequestAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpRequestMessage(HttpMethod.Post, "https://library.example.test/rest/getMusicFolders.view")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" }) });
        var index = ArtistIndex(administrator: _first, factory: interceptor == null ? null : new Factory(
            new DbContextOptionsBuilder<allstarr.Core.Storage.AllstarrDbContext>(_database.Options).AddInterceptors(interceptor).Options));
        return (new(index, registry, new TestBackendLibraryAccess(_factory, "music"), permissions.Object,
            new CoverClients(handler ?? new ArtistReviewHttp()), new TestMemoryApplicationCache()), index, registry);
    }

    private ProtocolExecutionContext ArtistReviewContext(Guid owner) => new(ProtocolKind.Subsonic, "main", owner.ToString(),
        new AllstarrPrincipal(owner, "subsonic", "main", owner.ToString(), "Listener", owner == _first), "artist-review", _clock.UtcNow.AddMinutes(5), default);

    private ArtistLinksController ArtistReviewController(LibraryArtistReview review, LibraryArtistIndex index, IProviderRegistry registry, bool administrator = true)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "review",
            UserId = _first.ToString(),
            UserName = "Listener",
            IsAdministrator = administrator,
            AllstarrUserId = _first,
            BackendType = "subsonic",
            BackendInstanceId = "main",
            JellyfinAccessToken = "",
            ExpiresAtUtc = _clock.UtcNow.UtcDateTime.AddHours(1)
        };
        return new(review, index, null!, new(_factory, _clock), registry) { ControllerContext = new() { HttpContext = http } };
    }

    private sealed class ArtistReviewHttp(int? total = null, bool noCounts = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Parameters { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var parameters = await request.Content!.ReadAsStringAsync(token);
            Parameters.Add(parameters);
            var offset = int.Parse(parameters.Split("artistOffset=")[1].Split('&')[0]);
            var names = total.HasValue ? Enumerable.Range(0, total.Value).Select(number => "Artist " + number).ToArray() :
                new[] { "Artist", "Songs", "Decided", "Apart", "Library" };
            var artists = names.Skip(offset).Take(200).Select((name, number) => new { id = name.ToLowerInvariant(), name, songCount = noCounts ? (int?)null : names.Length - offset - number });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                { ["subsonic-response"] = new { status = "ok", searchResult3 = new { artist = artists } } }))
            };
        }
    }
}
