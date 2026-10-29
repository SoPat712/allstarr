using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Enrichment;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using static allstarr.Tests.AudioMuseTests;

namespace allstarr.Tests;

public sealed class AudioMuseCaptureTests
{
    private const string ServerId = "ffc0b4a2c079e850cf329cdbaa2d465b";

    [Fact]
    public async Task Servers_and_models_preserve_default_binding_and_local_musicnn_coverage()
    {
        await using var f = await ReadyAsync();
        var endpoint = (await f.Settings.EndpointAsync(default))!;
        Assert.True(await f.Client.HealthAsync(endpoint, default));
        Assert.Equal("v3.6.4", await f.Client.VersionAsync(endpoint, default));
        var server = Assert.Single(await f.Client.ServersAsync(endpoint, default));
        Assert.Equal(ServerId, server.Id);
        Assert.Equal("jellyfin", server.Type);
        Assert.Equal("Jellyfin", server.Name);
        Assert.True(server.IsDefault);
        var coverage = await f.Client.CoverageAsync(endpoint, server.Id, default);
        Assert.Equal(ServerId, coverage.ServerId);
        Assert.True(coverage.Enabled);
        Assert.Equal(1466, coverage.Analyzed);
        Assert.Equal(1466, coverage.Total);
        Assert.True(coverage.Complete);
        Assert.All(f.Requests.Where(r => r.StartsWith("/api/models", StringComparison.Ordinal)),
            r => Assert.Contains("server=" + ServerId, r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coverage_uses_local_count_and_total_instead_of_global_or_percentage()
    {
        await using var f = await ReadyAsync();
        await AudioMuseDiscoveryTests.AddAsync(f, Track("one"));
        var models = JsonNode.Parse(Capture("models"))!;
        models["models"]!["musicnn"]!["local"]!["count"] = 1000;
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath == "/api/models" ? Fixture.Json(models.ToJsonString()) : original(r);
        var status = await f.Discovery.StatusAsync(f.Context, default);
        Assert.Null(status.Reason);
        Assert.Equal(1000, status.AnalyzedSongs);
        Assert.False(status.CoverageComplete);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("enabled")]
    [InlineData("total")]
    public async Task Status_rejects_unbound_disabled_or_incomplete_model_metadata(string field)
    {
        await using var f = await ReadyAsync();
        var models = JsonNode.Parse(Capture("models"))!;
        if (field == "server") models["server_id"] = "another";
        if (field == "enabled") models["models"]!["musicnn"]!["enabled"] = false;
        if (field == "total") models["models"]!["musicnn"]!["local"]!.AsObject().Remove("total");
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath == "/api/models" ? Fixture.Json(models.ToJsonString()) : original(r);
        Assert.Equal("not_set_up", (await f.Discovery.StatusAsync(f.Context, default)).Reason);
    }

    [Fact]
    public async Task Multiple_jellyfin_servers_require_a_unique_backend_url_match()
    {
        await using var f = await ReadyAsync();
        var servers = JsonNode.Parse(Capture("servers"))!;
        var other = servers["servers"]![0]!.DeepClone();
        other["server_id"] = "another";
        servers["servers"]!.AsArray().Add(other);
        servers["servers"]![0]!["creds"]!["url"] = f.Configuration["Jellyfin:Url"];
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath == "/api/servers" ? Fixture.Json(servers.ToJsonString()) : original(r);
        Assert.Null((await f.Discovery.StatusAsync(f.Context, default)).Reason);
        servers["servers"]![0]!.AsObject().Remove("creds");
        Assert.Equal("not_set_up", (await f.Discovery.StatusAsync(f.Context, default)).Reason);
    }

    [Fact]
    public async Task Search_and_similar_parse_native_ids_and_track_metadata_from_plain_arrays()
    {
        await using var f = await ReadyAsync();
        var endpoint = (await f.Settings.EndpointAsync(default))!;
        var search = await f.Client.SearchAsync(endpoint, ServerId, "love & life", default);
        Assert.Equal(5, search.Length);
        Assert.Equal("c5afa1db55c9205bd14571423faa7303", search[0].Id);
        Assert.Equal("How Deep Is Your Love", search[0].Title);
        Assert.Equal("Calvin Harris", search[0].Author);
        Assert.Equal("How Deep Is Your Love", search[0].Album);
        Assert.Equal("Calvin Harris", search[0].AlbumArtist);
        var similar = await f.Client.SimilarAsync(endpoint, ServerId, search[0].Id, null, default);
        Assert.Equal(5, similar.Length);
        var track = similar[0];
        Assert.Equal("c1ec864f31cf5cd2b3e0506f20044d1b", track.Id);
        Assert.Equal("Mr Right", track.Title);
        Assert.Equal("Mae Stephens", track.Author);
        Assert.Equal("Mr Right", track.Album);
        Assert.Equal("Mae Stephens", track.AlbumArtist);
        Assert.Equal(0.053148627281188965, track.Distance);
        Assert.Equal("pop:0.588,dance:0.566,female vocalists:0.553,electronic:0.545,rnb:0.528", track.MoodVector);
        Assert.Equal("danceable:0.70,aggressive:0.64,happy:0.64,party:0.69,relaxed:0.62,sad:0.57", track.OtherFeatures);
        Assert.Equal("pop", track.TopGenre);
        Assert.Equal("danceable", track.TopMood);
        Assert.Contains(f.Requests, r => r.Contains("search_query=love%20%26%20life", StringComparison.Ordinal));
        Assert.Contains(f.Requests, r => r.Contains("item_id=" + search[0].Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mood_centroids_parse_real_keys_and_cluster_fields()
    {
        await using var f = await ReadyAsync();
        var endpoint = (await f.Settings.EndpointAsync(default))!;
        var moods = await f.Client.CentroidsAsync(endpoint, default);
        Assert.Equal(new[] { "aggressive", "danceable", "happy", "party", "relaxed", "sad" }, moods.Keys.Order());
        Assert.Equal(42, moods["relaxed"].Length);
        var centroid = moods["relaxed"][0];
        Assert.Equal(0, centroid.ClusterId);
        Assert.Equal(0, centroid.Index);
        Assert.Equal(0.9998500338347522, centroid.MoodScore);
        Assert.Equal(62, centroid.SongCount);
        Assert.Equal(new[] { "female vocalists", "indie", "electronic", "pop", "ambient" }, centroid.TopTags);
        Assert.True(await f.Client.HasAnchorAsync(endpoint, "relaxed", default));
        Assert.True(await f.Client.HasAnchorAsync(endpoint, "danceable", default));
        Assert.False(await f.Client.HasAnchorAsync(endpoint, "calm", default));
        Assert.False(await f.Client.HasAnchorAsync(endpoint, "workout", default));
    }

    [Theory]
    [InlineData("calm", "relaxed")]
    [InlineData("workout", "danceable")]
    public async Task Recipes_select_existing_centroids_and_preserve_library_permissions(string recipe, string anchor)
    {
        await using var f = await ReadyAsync();
        using var similar = JsonDocument.Parse(Capture("similar"));
        var rows = similar.RootElement.EnumerateArray().Select(r => Track(r.GetProperty("item_id").GetString()!)).ToArray();
        rows[1].BackendLibraryId = "restricted";
        rows[2].BackendInstanceId = "other";
        await AudioMuseDiscoveryTests.AddAsync(f, rows);
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath == "/external/get_score" ? Fixture.Json(JsonSerializer.Serialize(new
        {
            item_id = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(r.RequestUri.Query)["id"].ToString(),
            energy = .2,
            other_features = similar.RootElement[0].GetProperty("other_features").GetString()
        })) : original(r);
        var preview = await f.Discovery.MoodAsync(f.Context, recipe, 25, default);
        Assert.Null(preview.Reason);
        Assert.Equal(3, preview.EligibleCount);
        Assert.Equal(3, preview.Tracks.Length);
        Assert.DoesNotContain(preview.Tracks, r => r.Id == rows[1].BackendItemId || r.Id == rows[2].BackendItemId);
        Assert.All(preview.Tracks, r => Assert.StartsWith("Library ", r.Title));
        Assert.Contains(f.Requests, r => r.Contains("mood=" + anchor + "&centroid_index=0", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Requests, r => r.StartsWith("/external/get_score", StringComparison.Ordinal) &&
            (r.Contains(rows[1].BackendItemId, StringComparison.Ordinal) || r.Contains(rows[2].BackendItemId, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Search_and_neighbors_use_real_ids_without_exposing_restricted_rows()
    {
        await using var f = await ReadyAsync();
        var seed = Track("c5afa1db55c9205bd14571423faa7303");
        var allowed = Track("c1ec864f31cf5cd2b3e0506f20044d1b");
        var hidden = Track("edcd63c3fef241cb102a0b6258d35456");
        hidden.BackendLibraryId = "restricted";
        await AudioMuseDiscoveryTests.AddAsync(f, seed, allowed, hidden);
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath == "/api/track"
            ? Fixture.Json(JsonSerializer.Serialize(new { item_id = seed.BackendItemId })) : original(r);
        var search = await f.Discovery.SearchAsync(f.Context, "love", default);
        Assert.Equal(seed.BackendItemId, Assert.Single(search.Tracks).Id);
        var preview = await f.Discovery.SimilarAsync(f.Context, seed.BackendItemId, 25, default);
        Assert.Equal(allowed.BackendItemId, Assert.Single(preview.Tracks).Id);
        Assert.Equal(allowed.Title, preview.Tracks[0].Title);
    }

    [Fact]
    public async Task Similar_analysis_reaches_shared_tags_only_for_permitted_canonical_recordings()
    {
        await using var f = await ReadyAsync();
        var allowed = Track("c1ec864f31cf5cd2b3e0506f20044d1b");
        var hidden = Track("edcd63c3fef241cb102a0b6258d35456");
        allowed.CanonicalRecordingId = Guid.NewGuid();
        hidden.CanonicalRecordingId = Guid.NewGuid();
        hidden.BackendLibraryId = "restricted";
        await using (var setup = f.Factory.CreateDbContext())
        {
            foreach (var row in new[] { allowed, hidden })
                setup.CanonicalRecordings.Add(new()
                {
                    Id = row.CanonicalRecordingId!.Value,
                    CreatedByUserId = f.Actor.AllstarrUserId!.Value,
                    PublicId = CanonicalRecordingRecord.DefaultPublicId(row.CanonicalRecordingId.Value),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            await setup.SaveChangesAsync();
        }
        var seed = Track("c5afa1db55c9205bd14571423faa7303");
        await AudioMuseDiscoveryTests.AddAsync(f, seed, allowed, hidden);
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath == "/api/track"
            ? Fixture.Json(JsonSerializer.Serialize(new { item_id = seed.BackendItemId })) : original(r);
        var preview = await f.Discovery.SimilarAsync(f.Context, seed.BackendItemId, 25, default);
        Assert.Null(preview.Reason);
        await using var db = f.Factory.CreateDbContext();
        var profile = Assert.Single(await db.SharedTagProfiles.ToArrayAsync());
        Assert.Equal(SharedTagStore.RecordingKey(allowed.CanonicalRecordingId.Value), profile.Key);
        var evidence = SharedTagStore.Read(profile).Evidence;
        Assert.All(evidence, r => Assert.Equal("audiomuse", r.Source));
        Assert.Contains(evidence, r => r.RawLabel == "pop" && r.Weight == .588);
        Assert.Contains(evidence, r => r.RawLabel == "relaxed" && r.Weight == .62);
        Assert.Contains(evidence, r => r.RawLabel == "danceable" && r.Weight == .70);
        Assert.Contains(evidence, r => r.RawLabel == "pop" && r.IsGenre);
        Assert.DoesNotContain(evidence, r => r.RawLabel is "energy" or "tempo");
    }

    [Fact]
    public async Task Shared_tags_preserve_top_labels_and_ignore_invalid_numeric_features()
    {
        await using var f = await ReadyAsync();
        var store = new SharedTagStore(f.Factory, new allstarr.Core.Operations.SystemPlatformClock());
        await store.CaptureAudioMuseAsync(Guid.NewGuid(), "pop:0.5,broken,rock:NaN", "relaxed:0.6,sad:Infinity", "jazz", "chill", default);
        await using var db = f.Factory.CreateDbContext();
        var evidence = SharedTagStore.Read(await db.SharedTagProfiles.SingleAsync()).Evidence;
        Assert.Equal(4, evidence.Count);
        Assert.Contains(evidence, r => r.RawLabel == "pop" && r.Weight == .5);
        Assert.Contains(evidence, r => r.RawLabel == "relaxed" && r.Weight == .6);
        Assert.Contains(evidence, r => r.RawLabel == "jazz" && r.IsGenre);
        Assert.Contains(evidence, r => r.RawLabel == "chill");
    }

    private static async Task<Fixture> ReadyAsync()
    {
        var f = await AudioMuseDiscoveryTests.ReadyAsync();
        var original = f.Respond;
        f.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/servers" => Fixture.Json(Capture("servers")),
            "/api/models" => Fixture.Json(Capture("models")),
            "/api/search_tracks" => Fixture.Json(Capture("search")),
            "/api/similar_tracks" => Fixture.Json(Capture("similar")),
            "/api/mood_centroids" => Fixture.Json(Capture("moods")),
            _ => original(r)
        };
        return f;
    }
}
