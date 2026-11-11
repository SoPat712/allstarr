using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using allstarr.Controllers.ClientApi;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class RankedSearchSnapshotsTests
{
    [Fact]
    public async Task RankedMixedItemsMatchMergedRankingStayCappedAndKeepTheirIds()
    {
        const string query = "say now";
        var song = new Song { Id = "song", Title = "Brick By Brick", Artist = "Say Now", Duration = 200 };
        var album = new Album { Id = "album", Title = "Release", Artist = "Say Now" };
        var artist = new Artist { Id = "artist", Name = "Say Now" };
        var playlist = new ExternalPlaylist { Id = "playlist", Name = "Say Now", CuratorName = "Curator" };
        var rows = new[]
        {
            Row(song.Id, "songs", SearchRelevanceRanker.ScoreSong(query, song), song: song),
            Row(album.Id, "albums", SearchRelevanceRanker.ScoreAlbum(query, album), album: album),
            Row(artist.Id, "artists", SearchRelevanceRanker.Score(query, artist.Name), artist.Name),
            Row(playlist.Id, "playlists", SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName), playlist: playlist)
        };
        var merged = new MusicSearchResult<object>([new() { Song = song }],
            [new() { Album = album }, new() { Playlist = playlist }], [new() { Artist = artist }]);
        var expected = MusicSearchService.RankTogether(query, merged)
            .Select(entry => entry.Artist?.Id ?? entry.Album?.Id ?? entry.Playlist?.Id ?? entry.Song!.Id).ToArray();
        var clock = new ManualTimeProvider();
        var emitted = Channel.CreateUnbounded<JsonNode>();
        var snapshots = new RankedSearchSnapshots(query, 4, ["songs", "albums", "artists", "playlists"], "preferred", clock,
            (value, _) => { emitted.Writer.TryWrite(JsonSerializer.SerializeToNode(value)!); return Task.CompletedTask; });
        var running = snapshots.RunAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        await snapshots.AddAsync("first", rows, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        var first = await emitted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, first["items"]!.AsArray().Select(item => item!["Id"]!.GetValue<string>()));
        Assert.Contains(first["items"]!.AsArray(), item => item!["Id"]!.GetValue<string>() == first["top"]!["Id"]!.GetValue<string>());
        await snapshots.AddAsync("last", [Row("near-tie", "songs", 96, song:
            new Song { Id = "near-tie", Title = "Supermarket", Artist = "Say Now", Duration = 200, Isrc = "USAA12345678" })], CancellationToken.None);
        snapshots.Complete([]);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(250));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        var final = await emitted.Reader.ReadAsync();
        Assert.Equal(4, final["items"]!.AsArray().Count);
        Assert.Equal(expected, final["items"]!.AsArray().Select(item => item!["Id"]!.GetValue<string>()));
        Assert.All(final["items"]!.AsArray(), item => Assert.NotNull(item!["score"]));
    }

    [Fact]
    public async Task RankedSnapshotsHoldCoalesceRerankAndAlwaysFlushTheFinalState()
    {
        var clock = new ManualTimeProvider();
        var emitted = Channel.CreateUnbounded<(DateTimeOffset At, JsonNode Value)>();
        var snapshots = new RankedSearchSnapshots("say now", 2, ["songs", "artists"], "preferred", clock,
            (value, _) =>
            {
                emitted.Writer.TryWrite((clock.GetUtcNow(), JsonSerializer.SerializeToNode(value)!));
                return Task.CompletedTask;
            });
        var running = snapshots.RunAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        await snapshots.AddAsync("fast", [Row("loose", "songs", 60), Row("near", "artists", 95, "Say Now Club")], CancellationToken.None);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        clock.Advance(TimeSpan.FromMilliseconds(399));
        Assert.False(emitted.Reader.TryRead(out _));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var first = await emitted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.Value["revision"]!.GetValue<int>());
        Assert.Equal("near", first.Value["top"]!["Id"]!.GetValue<string>());
        Assert.Null(first.Value["late"]);
        await snapshots.AddAsync("preferred", [Row("brick", "songs", 95, song:
            new Song { Id = "brick", Title = "Brick By Brick", Artist = "Say Now", Duration = 200 }),
            Row("exact", "artists", 100, "Say Now")], CancellationToken.None);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(250));
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.False(emitted.Reader.TryRead(out _));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var second = await emitted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, second.Value["revision"]!.GetValue<int>());
        Assert.Equal("brick", second.Value["sections"]!["songs"]![0]!["Id"]!.GetValue<string>());
        Assert.Equal("exact", second.Value["top"]!["Id"]!.GetValue<string>());
        Assert.Equal(["fast", "preferred"], second.Value["providers"]!.AsArray().Select(value => value!.GetValue<string>()));
        await snapshots.AddAsync("third", [Row("near-tie", "songs", 96, song:
            new Song { Id = "near-tie", Title = "Supermarket", Artist = "Say Now", Duration = 200, Isrc = "USAA12345678" }),
            Row("another-exact", "artists", 100, "SAY NOW")], CancellationToken.None);
        await snapshots.AddAsync("fourth", [], CancellationToken.None);
        snapshots.Complete(["late-provider"]);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(250));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        var final = await emitted.Reader.ReadAsync();
        Assert.Equal(3, final.Value["revision"]!.GetValue<int>());
        Assert.True(final.Value["done"]!.GetValue<bool>());
        Assert.Equal("late-provider", final.Value["late"]![0]!.GetValue<string>());
        Assert.Equal("exact", final.Value["top"]!["Id"]!.GetValue<string>());
        Assert.Equal(["fast", "preferred", "third", "fourth"], final.Value["providers"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal(["brick", "near-tie"], final.Value["sections"]!["songs"]!.AsArray().Select(value => value!["Id"]!.GetValue<string>()));
        Assert.Equal(["songs", "artists"], final.Value["sections"]!.AsObject().Select(pair => pair.Key));
        Assert.All(final.Value["sections"]!.AsObject(), section => Assert.True(section.Value!.AsArray().Count <= 2));
        Assert.True(second.At - first.At >= TimeSpan.FromMilliseconds(250));
        Assert.True(final.At - second.At >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task RankedSnapshotsReleaseEarlyOnlyAfterThePreferredProviderAndAnotherAnswer()
    {
        var clock = new ManualTimeProvider();
        var emitted = Channel.CreateUnbounded<JsonNode>();
        var snapshots = new RankedSearchSnapshots("say now", 1, ["songs"], "preferred", clock,
            (value, _) => { emitted.Writer.TryWrite(JsonSerializer.SerializeToNode(value)!); return Task.CompletedTask; });
        var running = snapshots.RunAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        await snapshots.AddAsync("other", [Row("old", "songs", 60)], CancellationToken.None);
        await snapshots.AddAsync("third", [], CancellationToken.None);
        Assert.False(emitted.Reader.TryRead(out _));
        await snapshots.AddAsync("preferred", [Row("best", "songs", 95), Row("unrequested", "artists", 100, "Say Now")], CancellationToken.None);
        var first = await emitted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow());
        Assert.Equal("best", first["top"]!["Id"]!.GetValue<string>());
        Assert.Equal(["songs"], first["sections"]!.AsObject().Select(pair => pair.Key));
        Assert.Single(first["sections"]!["songs"]!.AsArray());
        snapshots.Complete([]);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(250));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await emitted.Reader.ReadAsync())["done"]!.GetValue<bool>());
    }

    private static RankedSearchRow Row(string id, string section, double score, string? artistName = null,
        Song? song = null, Album? album = null, ExternalPlaylist? playlist = null) => new(
        new JsonObject { ["Id"] = id, ["Name"] = artistName ?? id, ["score"] = score }, section,
        Song: song, Artist: artistName == null ? null : new Artist { Id = id, Name = artistName }, Album: album, Playlist: playlist);
}
