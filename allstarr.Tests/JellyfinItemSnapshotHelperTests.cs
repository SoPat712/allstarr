using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Tests;

public class JellyfinItemSnapshotHelperTests
{
    [Fact]
    public void TryGetClonedRawItemSnapshot_RoundTripsThroughJsonSerialization()
    {
        var song = new Song { Id = "song-1", IsLocal = true };
        using var doc = JsonDocument.Parse("""
            {
              "Id": "song-1",
              "ServerId": "c17d351d3af24c678a6d8049c212d522",
              "RunTimeTicks": 2234068710,
              "MediaSources": [
                {
                  "Id": "song-1",
                  "RunTimeTicks": 2234068710
                }
              ]
            }
            """);

        JellyfinItemSnapshotHelper.StoreRawItemSnapshot(song, doc.RootElement);

        var roundTripped = JsonSerializer.Deserialize<Song>(JsonSerializer.Serialize(song));

        Assert.NotNull(roundTripped);
        Assert.True(JellyfinItemSnapshotHelper.TryGetClonedRawItemSnapshot(roundTripped, out var rawItem));

        Assert.Equal("song-1", ((JsonElement)rawItem["Id"]!).GetString());
        Assert.Equal("c17d351d3af24c678a6d8049c212d522", ((JsonElement)rawItem["ServerId"]!).GetString());
        Assert.Equal(2234068710L, ((JsonElement)rawItem["RunTimeTicks"]!).GetInt64());

        var mediaSources = (JsonElement)rawItem["MediaSources"]!;
        Assert.Equal(JsonValueKind.Array, mediaSources.ValueKind);
        Assert.Equal(2234068710L, mediaSources[0].GetProperty("RunTimeTicks").GetInt64());
    }

    [Fact]
    public void HasRawItemSnapshot_ReturnsFalse_WhenSnapshotMissing()
    {
        var song = new Song { Id = "song-1", IsLocal = true };

        Assert.False(JellyfinItemSnapshotHelper.HasRawItemSnapshot(song));
        Assert.False(JellyfinItemSnapshotHelper.TryGetClonedRawItemSnapshot(song, out _));
    }
}
