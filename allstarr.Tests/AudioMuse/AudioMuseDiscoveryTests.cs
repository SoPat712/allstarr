using System.Net;
using System.Text.Json;
using allstarr.Core.AudioMuse;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;
using Moq;
using static allstarr.Tests.AudioMuseTests;

namespace allstarr.Tests;

public sealed class AudioMuseDiscoveryTests
{
    private const string Servers = "{\"servers\":[{\"server_id\":\"home\",\"server_type\":\"jellyfin\",\"creds\":{\"url\":\"http://backend.example:8096\"}}]}";
    private const string Models = "{\"server_id\":\"home\",\"models\":{\"musicnn\":{\"enabled\":true,\"local\":{\"count\":20}}}}";

    [Fact]
    public async Task Search_and_neighbors_drop_foreign_ids_and_use_library_metadata()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed"), Track("1"), Track("2"));
        var hidden = Track("hidden");
        hidden.BackendLibraryId = "restricted";
        var foreign = Track("foreign");
        foreign.BackendInstanceId = "other";
        await AddAsync(f, hidden, foreign);
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/similar_tracks" when r.RequestUri.Query.Contains("eliminate_duplicates=true", StringComparison.Ordinal) => Fixture.Json("[{\"item_id\":\"hidden\"}]"),
            "/api/search_tracks" or "/api/similar_tracks" => Fixture.Json("[{\"item_id\":\"hidden\",\"title\":\"restricted title\"},{\"item_id\":\"foreign\"},{\"item_id\":\"fp_internal\"},{\"item_id\":\"seed\"},{\"item_id\":\"2\"},{\"item_id\":\"1\"}]"),
            "/api/track" => Fixture.Json("{\"item_id\":\"seed\",\"title\":\"upstream title\"}"),
            _ => original(r)
        };
        var search = await f.Discovery.SearchAsync(f.Context, "song", default);
        Assert.Equal(new[] { "seed", "2", "1" }, search.Tracks.Select(t => t.Id));
        Assert.DoesNotContain("restricted title", JsonSerializer.Serialize(search), StringComparison.Ordinal);
        Assert.All(search.Tracks, t => Assert.StartsWith("Library ", t.Title));
        var preview = await f.Discovery.SimilarAsync(f.Context, "seed", 25, default);
        Assert.Null(preview.Reason);
        Assert.Equal(new[] { "2", "1" }, preview.Tracks.Select(t => t.Id));
        Assert.All(f.Requests.Where(p => p.StartsWith("/api/models") || p.StartsWith("/api/search_tracks") ||
            p.StartsWith("/api/track?") || p.StartsWith("/api/similar_tracks")), p => Assert.Contains("server=home", p));
    }

    [Theory]
    [InlineData("unreachable", "not_reachable")]
    [InlineData("auth", "not_set_up")]
    [InlineData("binding", "not_set_up")]
    [InlineData("missing_api", "not_set_up")]
    [InlineData("malformed", "not_set_up")]
    [InlineData("ambiguous", "not_set_up")]
    [InlineData("models", "not_set_up")]
    [InlineData("empty", "too_little_analyzed_music")]
    public async Task Status_explains_unavailability(string failure, string reason)
    {
        await using var f = await ReadyAsync();
        var original = f.Respond;
        f.Respond = r => (failure, r.RequestUri!.AbsolutePath) switch
        {
            ("unreachable", _) => throw new HttpRequestException("private-fixture-key"),
            ("missing_api", "/api/version") => Fixture.Json("{}", HttpStatusCode.NotFound),
            ("malformed", "/api/version") => Fixture.Json("not json"),
            ("auth", "/api/version") => Fixture.Json("private-fixture-key", HttpStatusCode.Unauthorized),
            ("binding", "/api/servers") => Fixture.Json(Servers.Replace("backend.example", "other.example", StringComparison.Ordinal)),
            ("ambiguous", "/api/servers") => Fixture.Json(Servers.Replace("}]}", "},{\"server_id\":\"second\",\"server_type\":\"jellyfin\",\"creds\":{\"url\":\"http://backend.example:8096\"}}]}")),
            ("models", "/api/models") => Fixture.Json("{\"models\":[]}"),
            ("empty", "/api/models") => Fixture.Json(Models.Replace("20", "0", StringComparison.Ordinal)),
            _ => original(r)
        };
        var result = await f.Discovery.StatusAsync(f.Context, default);
        Assert.Equal(reason, result.Reason);
        Assert.DoesNotContain(Fixture.Key, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_seed_and_empty_permitted_pool_have_distinct_reasons()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed"));
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath == "/api/track" ? Fixture.Json("{}", HttpStatusCode.NotFound) : original(r);
        Assert.Equal("song_not_analyzed", (await f.Discovery.SimilarAsync(f.Context, "seed", 25, default)).Reason);
        f.Respond = r => r.RequestUri!.AbsolutePath == "/api/track" ? Fixture.Json("{\"item_id\":\"seed\"}") : original(r);
        Assert.Equal("too_little_analyzed_music", (await f.Discovery.SimilarAsync(f.Context, "seed", 25, default)).Reason);
    }

    [Fact]
    public async Task Save_revalidates_permissions_and_creates_an_ordinary_private_playlist_after_disable()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed"), Track("1"), Track("2"));
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/track" => Fixture.Json("{\"item_id\":\"seed\"}"),
            "/api/similar_tracks" => Fixture.Json("[{\"item_id\":\"1\"},{\"item_id\":\"2\"}]"),
            _ => original(r)
        };
        var preview = await f.Discovery.SimilarAsync(f.Context, "seed", 25, default);
        var connection = await f.Settings.GetAsync(default);
        await f.Settings.ApplyAsync(f.Actor, "", null, connection.Revision, default);
        await using (var db = f.Factory.CreateDbContext())
        {
            (await db.LibraryTracks.SingleAsync(t => t.BackendItemId == "2")).BackendLibraryId = "revoked";
            await db.SaveChangesAsync();
        }
        f.Target.Setup(t => t.WriteAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<BackendPlaylistWriteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>(BackendPlaylistTargetStatus.Success,
                new(new("ordinary", "Saved", [], "revision"), true, [])));
        var result = await f.Discovery.SaveAsync(f.Context, preview.PreviewId!.Value, "Saved", default);
        Assert.Equal("ordinary", result.PlaylistId);
        f.Target.Verify(t => t.WriteAsync(It.Is<BackendPlaylistTargetContext>(c => c.VerifiedPrincipalId == "listener"),
            It.Is<BackendPlaylistWriteRequest>(r => r.Metadata.Private && r.OrderedBackendItemIds.SequenceEqual(new[] { "1" })),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("ordinary", (await f.Discovery.SaveAsync(f.Context, preview.PreviewId.Value, "Saved", default)).PlaylistId);
        f.Target.Verify(t => t.WriteAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<BackendPlaylistWriteRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        await using var saved = f.Factory.CreateDbContext();
        var versions = await saved.ExternalMetadataSnapshots.OrderBy(r => r.SnapshotVersion).ToArrayAsync();
        Assert.Equal(new[] { 1, 2 }, versions.Select(r => r.SnapshotVersion));
        Assert.DoesNotContain("ordinary", versions[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("ordinary", versions[1].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_empty_is_too_little_music_and_missing_neighbor_results_stay_empty()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed"));
        var original = f.Respond;
        var emptyIndex = true;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/track" => Fixture.Json("{\"item_id\":\"seed\"}"),
            "/api/similar_tracks" => emptyIndex ? Fixture.Json("{\"error_code\":3002}", HttpStatusCode.ServiceUnavailable)
                : Fixture.Json("{\"error_code\":1004}", HttpStatusCode.NotFound),
            _ => original(r)
        };
        Assert.Equal("too_little_analyzed_music", (await f.Discovery.SimilarAsync(f.Context, "seed", 25, default)).Reason);
        emptyIndex = false;
        Assert.Equal("too_little_analyzed_music", (await f.Discovery.SimilarAsync(f.Context, "seed", 25, default)).Reason);
    }

    [Fact]
    public async Task Another_listener_cannot_save_a_preview()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed"), Track("1"));
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/track" => Fixture.Json("{\"item_id\":\"seed\"}"),
            "/api/similar_tracks" => Fixture.Json("[{\"item_id\":\"1\"}]"),
            _ => original(r)
        };
        var preview = await f.Discovery.SimilarAsync(f.Context, "seed", 25, default);
        var other = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "other-listener",
            new allstarr.Core.Identity.AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "primary", "other-listener", "Other", false),
            "fixture", DateTimeOffset.UtcNow.AddMinutes(5), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Discovery.SaveAsync(other, preview.PreviewId!.Value, "Saved", default));
        var wrongBackend = new ProtocolExecutionContext(ProtocolKind.Subsonic, "primary", f.Actor.UserId,
            new allstarr.Core.Identity.AllstarrPrincipal(f.Actor.AllstarrUserId!.Value, "subsonic", "primary", f.Actor.UserId, "Listener", false),
            "fixture", DateTimeOffset.UtcNow.AddMinutes(5), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Discovery.SaveAsync(wrongBackend, preview.PreviewId!.Value, "Saved", default));
        f.Target.Verify(t => t.WriteAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<BackendPlaylistWriteRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Mood_recipe_is_repeatable_and_reports_eligible_pool_before_artist_cap()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("1"), Track("2"), Track("3"), Track("4"), Track("missing", "other"));
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/mood_centroids" => Fixture.Json("{\"relaxed\":[{\"index\":0}],\"danceable\":[{\"index\":0}]}"),
            "/api/similar_tracks" when r.RequestUri.Query.Contains("eliminate_duplicates=true", StringComparison.Ordinal) => Fixture.Json("[{\"item_id\":\"4\"},{\"item_id\":\"missing\"}]"),
            "/api/similar_tracks" => Fixture.Json("[{\"item_id\":\"4\"},{\"item_id\":\"3\"},{\"item_id\":\"2\"},{\"item_id\":\"1\"},{\"item_id\":\"missing\"}]"),
            "/external/get_score" when r.RequestUri.Query.Contains("missing", StringComparison.Ordinal) => Fixture.Json("{\"energy\":0.2}"),
            "/external/get_score" => Fixture.Json(JsonSerializer.Serialize(new
            {
                item_id = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(r.RequestUri.Query)["id"].ToString(),
                energy = .2,
                mood_vector = "ambient:0.6",
                other_features = "relaxed:0.7,danceable:0.5,party:0.4"
            })),
            _ => original(r)
        };
        var first = await f.Discovery.MoodAsync(f.Context, "calm", 25, default);
        var second = await f.Discovery.MoodAsync(f.Context, "calm", 25, default);
        Assert.Equal("calm-v1", first.RecipeVersion);
        Assert.Equal(4, first.EligibleCount);
        Assert.Equal(new[] { "1", "2", "3" }, first.Tracks.Select(t => t.Id));
        Assert.Equal(first.Tracks, second.Tracks);
        var workout = await f.Discovery.MoodAsync(f.Context, "workout", 50, default);
        Assert.Equal("workout-v1", workout.RecipeVersion);
        Assert.Equal(4, workout.EligibleCount);
        Assert.Contains(f.Requests, p => p.Contains("mood=danceable&centroid_index=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clean_only_drops_explicit_and_unknown_on_preview_and_rechecks_on_save()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed"), Track("clean"), Track("explicit"), Track("unknown"));
        await f.SetCleanOnlyAsync();
        var original = f.Respond;
        var clean = true;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/Items" => Fixture.Json(clean
                ? "{\"Items\":[{\"Id\":\"seed\",\"Type\":\"Audio\",\"OfficialRating\":\"Clean\"},{\"Id\":\"clean\",\"Type\":\"Audio\",\"OfficialRating\":\"Edited\"},{\"Id\":\"explicit\",\"Type\":\"Audio\",\"OfficialRating\":\"Explicit\"},{\"Id\":\"unknown\",\"Type\":\"Audio\"}]}"
                : "{\"Items\":[]}"),
            "/api/track" => Fixture.Json("{\"item_id\":\"seed\"}"),
            "/api/similar_tracks" => Fixture.Json("[{\"item_id\":\"clean\"},{\"item_id\":\"explicit\"},{\"item_id\":\"unknown\"}]"),
            _ => original(r)
        };
        var preview = await f.Discovery.SimilarAsync(f.Context, "seed", 25, default);
        Assert.Equal("clean", Assert.Single(preview.Tracks).Id);
        clean = false;
        Assert.Equal("too_little_analyzed_music", (await f.Discovery.SaveAsync(f.Context, preview.PreviewId!.Value, "Saved", default)).Reason);
        f.Target.Verify(t => t.WriteAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<BackendPlaylistWriteRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Status_counts_only_accessible_analyzed_songs()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("indexed"), Track("metadata"));
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath == "/external/get_embedding" && r.RequestUri.Query.Contains("metadata", StringComparison.Ordinal)
            ? Fixture.Json("{\"item_id\":\"metadata\",\"embedding\":null}") : original(r);
        var status = await f.Discovery.StatusAsync(f.Context, default);
        Assert.Null(status.Reason);
        Assert.Equal(2, status.AccessibleLibrarySongs);
        Assert.Equal(1, status.AnalyzedSongs);
        Assert.True(status.CoverageComplete);
        f.Respond = r => r.RequestUri!.AbsolutePath == "/external/get_embedding" ? Fixture.Json("{}", HttpStatusCode.NotFound) : original(r);
        Assert.Equal("too_little_analyzed_music", (await f.Discovery.StatusAsync(f.Context, default)).Reason);
    }

    [Fact]
    public async Task Seed_search_omits_metadata_only_rows_and_missing_vectors()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("indexed"), Track("metadata"));
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/search_tracks" => Fixture.Json("[{\"item_id\":\"indexed\"},{\"item_id\":\"metadata\"}]"),
            "/external/get_embedding" when r.RequestUri.Query.Contains("metadata", StringComparison.Ordinal) => Fixture.Json("{\"item_id\":\"metadata\",\"embedding\":null}"),
            _ => original(r)
        };
        Assert.Equal("indexed", Assert.Single((await f.Discovery.SearchAsync(f.Context, "song", default)).Tracks).Id);
    }

    [Fact]
    public async Task Metadata_only_and_placeholder_scores_do_not_qualify_a_recipe()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("1"));
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/mood_centroids" => Fixture.Json("{\"relaxed\":[{\"index\":0}]}"),
            "/api/similar_tracks" => Fixture.Json("[{\"item_id\":\"1\"}]"),
            "/external/get_score" => Fixture.Json("{\"item_id\":\"1\",\"energy\":0.2,\"other_features\":\"danceable:0.00,aggressive:0.00,happy:0.00,party:0.00,relaxed:0.00,sad:0.00\"}"),
            _ => original(r)
        };
        Assert.Equal("not_set_up", (await f.Discovery.MoodAsync(f.Context, "calm", 25, default)).Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task Preview_length_is_bounded(int n)
    {
        await using var f = await ReadyAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Discovery.SimilarAsync(f.Context, "seed", n, default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Discovery.MoodAsync(f.Context, "calm", n, default));
        Assert.Empty(f.Requests);
    }

    internal static async Task<Fixture> ReadyAsync()
    {
        var f = await Fixture.CreateAsync();
        await f.ConfigureAsync();
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/health" => Fixture.Json("{\"status\":\"ok\"}"),
            "/api/version" => Fixture.Json("{\"app_version\":\"v3.6.4\"}"),
            "/api/servers" => Fixture.Json(Servers),
            "/api/models" => Fixture.Json(Models),
            "/external/get_embedding" => Fixture.Json(JsonSerializer.Serialize(new
            {
                item_id = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(r.RequestUri.Query)["id"].ToString(),
                embedding = Enumerable.Repeat(.1, 200).ToArray()
            })),
            _ => Fixture.Json("[]")
        };
        return f;
    }

    internal static async Task AddAsync(Fixture f, params allstarr.Core.Storage.LibraryTrackRecord[] tracks)
    {
        await using var db = f.Factory.CreateDbContext();
        foreach (var track in tracks) track.OwnerUserId = f.Actor.AllstarrUserId!.Value;
        db.LibraryTracks.AddRange(tracks);
        await db.SaveChangesAsync();
    }
}
