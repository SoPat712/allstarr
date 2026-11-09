using System.Text.Json;
using allstarr.Core.Playlists;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task SyncRecordsWhoseThePlaylistIsAndKeepsItWhenALaterReadCannotTell()
    {
        _source.Snapshot = Snapshot("kind-one", Entry(0, "one", "source-1", "One")) with
        {
            SourceOwnerId = "spotify",
            SourceKind = PlaylistSourceKinds.Feed
        };
        await _service.RunAsync(Context(), new(_link, 9301));
        _source.Snapshot = Snapshot("kind-two", Entry(0, "one", "source-1", "One"), Entry(1, "two", "source-2", "Two"));
        await _service.RunAsync(Context(), new(_link, 9302));

        await using var db = await _factory.CreateDbContextAsync();
        var snapshots = await db.PlaylistSourceSnapshots.AsNoTracking().Where(item => item.PlaylistLinkId == _link)
            .OrderBy(item => item.SnapshotVersion).ToArrayAsync();
        Assert.Equal(2, snapshots.Length);
        Assert.All(snapshots, snapshot => Assert.Equal(PlaylistSourceKinds.Feed, snapshot.SourceKind));
        Assert.Equal("spotify", snapshots[0].SourceOwnerId);
        Assert.Equal(PlaylistSourceKinds.Feed, (await PlaylistSourceKinds.LatestAsync(db, [_link], default))[_link]);
    }

    [Fact]
    public async Task AnUnchangedPlaylistLearnsWhoseItIsWithoutANewSnapshot()
    {
        _source.Snapshot = Snapshot("kind-same", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9311));
        _source.Snapshot = Snapshot("kind-same", Entry(0, "one", "source-1", "One")) with
        {
            SourceOwnerId = "a-friend",
            SourceKind = PlaylistSourceKinds.Followed
        };
        await _service.RunAsync(Context(), new(_link, 9312));

        await using var db = await _factory.CreateDbContextAsync();
        var snapshot = Assert.Single(await db.PlaylistSourceSnapshots.AsNoTracking().Where(item => item.PlaylistLinkId == _link).ToArrayAsync());
        Assert.Equal(PlaylistSourceKinds.Followed, snapshot.SourceKind);
        Assert.Equal("a-friend", snapshot.SourceOwnerId);
    }

    [Fact]
    public async Task ListenerChoosesWhetherAFeedFillsTheirLibrary()
    {
        _source.Snapshot = Snapshot("kind-choice", Entry(0, "one", "source-1", "One")) with
        {
            SourceOwnerId = "spotify",
            SourceKind = PlaylistSourceKinds.Feed
        };
        await _service.RunAsync(Context(), new(_link, 9321));
        var controller = AppController(_service);
        long revision;
        await using (var db = await _factory.CreateDbContextAsync())
            revision = (await db.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == _link)).Revision;

        var saved = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await controller.SetLibrary(_link, new(revision, true), default)).Value);
        Assert.Equal("feed", saved.GetProperty("playlistKind").GetString());
        Assert.True(saved.GetProperty("countsTowardLibrary").GetBoolean());
        Assert.Equal(revision + 1, saved.GetProperty("revision").GetInt64());
        Assert.IsType<ConflictObjectResult>(await controller.SetLibrary(_link, new(revision, false), default));

        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "other-session",
            AllstarrUserId = Guid.CreateVersion7(),
            UserId = "other",
            UserName = "Other",
            BackendType = "Jellyfin",
            BackendInstanceId = "backend",
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = _now.AddHours(1).UtcDateTime
        };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.SetLibrary(_link, new(revision + 1, false), default)).StatusCode);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.True((await verify.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == _link)).CountsTowardLibrary);
    }

    [Fact]
    public async Task A_link_no_sync_has_classified_uses_what_its_source_says()
    {
        await SetSpotifyArtworkLinkAsync(false);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PlaylistLinks.SingleAsync(item => item.Id == _link)).SourcePlaylistId = "37i9dQZF1DWXRqgorJj26U";
            await db.SaveChangesAsync();
        }
        await using var read = await _factory.CreateDbContextAsync();
        Assert.Equal(PlaylistSourceKinds.Feed, (await PlaylistSourceKinds.LatestAsync(read, [_link], default))[_link]);
    }

    [Theory]
    [InlineData("spotify", "37i9dQZF1DXcBWIGoYBM5M", "spotify", "josh", PlaylistSourceKinds.Feed)]
    [InlineData("spotify", "37i9dQZF1DX0XUsuxWHRQd", "spotify:user:spotify", null, PlaylistSourceKinds.Feed)]
    [InlineData("spotify", "mine", "josh", "josh", PlaylistSourceKinds.Own)]
    [InlineData("spotify", "mine", "spotify:user:josh", "josh", PlaylistSourceKinds.Own)]
    [InlineData("spotify", "theirs", "a-friend", "josh", PlaylistSourceKinds.Followed)]
    [InlineData("spotify", "theirs", "a-friend", null, null)]
    [InlineData("spotify", "liked", "selected-account", null, PlaylistSourceKinds.Own)]
    [InlineData("spotify", "37i9dQZEVXcJZyENOWUFo7", null, null, PlaylistSourceKinds.Feed)]
    [InlineData("spotify", "4Dmine0wnPlaylist12345", null, null, null)]
    [InlineData("spotify", "37i9dQZF1E39vTG3GurFPW", null, null, PlaylistSourceKinds.Feed)]
    [InlineData("spotify", "unknown", "unknown-owner", "josh", null)]
    [InlineData("apple-musickit", "p.library", "selected-user", null, PlaylistSourceKinds.Own)]
    [InlineData("deezer", "editorial", "Deezer", null, PlaylistSourceKinds.Feed)]
    [InlineData("deezer", "public", "Someone", null, null)]
    [InlineData("listenbrainz", "createdfor:weekly-jams", null, null, PlaylistSourceKinds.Feed)]
    [InlineData("lastfm", "loved", null, null, PlaylistSourceKinds.Own)]
    [InlineData("lastfm", "top:7day", null, null, PlaylistSourceKinds.Feed)]
    public void PlaylistKindComesFromWhoOwnsIt(string provider, string playlist, string? owner, string? user, string? expected) =>
        Assert.Equal(expected, PlaylistSourceKinds.Classify(provider, playlist, owner, user));

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(PlaylistSourceKinds.Own, null, true)]
    [InlineData(PlaylistSourceKinds.Feed, null, false)]
    [InlineData(PlaylistSourceKinds.Followed, null, false)]
    [InlineData(PlaylistSourceKinds.Feed, true, true)]
    [InlineData(PlaylistSourceKinds.Own, false, false)]
    public void OnlyYourPlaylistsFillTheLibraryUnlessChosen(string? kind, bool? choice, bool expected) =>
        Assert.Equal(expected, PlaylistSourceKinds.CountsTowardLibrary(choice, kind));
}
