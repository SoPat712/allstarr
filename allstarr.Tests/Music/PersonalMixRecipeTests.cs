using allstarr.Core.Music;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class PersonalMixRecipeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
    private static MixCandidate Row(int id, int plays = 0, string? artist = null) => new(
        new Song { Id = id.ToString("D3"), Title = "Song " + id, Artist = artist ?? "Artist " + id },
        new Dictionary<string, double> { ["rock"] = 1 }, plays, plays, Now.AddMonths(-8), false, "library", 0);

    [Fact]
    public void LongLost_requires_five_plays_even_for_hearts_and_allows_three_per_artist()
    {
        var rows = new[] { Row(1, 20, "Same"), Row(2, 10, "Same"), Row(3, 5, "Same"), Row(4, 5, "Same"),
            Row(5, 1) with { Loved = true } };
        var selected = PersonalMixRules.Select("rewind", rows, Now, []);
        Assert.Equal(["001", "002", "003"], selected.Select(row => row.Song.Id));
        Assert.Contains("5", MixRecipes.For("rewind").Filters);
        Assert.Equal(3, MixRecipes.For("rewind").ArtistLimit);
    }

    [Fact]
    public void HeavyRotation_fills_from_ninety_days_and_ignores_taste_scores()
    {
        var rows = new[] { Row(1, 3) with { Score = -100 }, Row(2, 3) with { Score = 100 },
            Row(3) with { NinetyPlays = 9 }, Row(4) };
        Assert.Equal(["001", "002", "003"], PersonalMixRules.Select("on-repeat", rows, Now, []).Select(row => row.Song.Id));
        Assert.Equal(50, MixRecipes.For("on-repeat").Target);
    }

    [Fact]
    public void Blends_keep_half_from_each_tier_and_show_honest_partial_results()
    {
        var rows = Enumerable.Range(1, 60).Select(id => Row(id, id <= 40 ? 2 : 0)).ToArray();
        var selected = PersonalMixRules.Select("daily-1", rows, Now, []);
        Assert.Equal(45, selected.Count);
        Assert.Equal(25, selected.Count(row => row.Plays > 0));
        Assert.Equal(20, selected.Count(row => row.Plays == 0));
        Assert.Contains("25", MixRecipes.For("daily-1").Shortfall);
    }

    [Fact]
    public void Moods_order_by_plays_after_qualification_and_keep_partial_songs()
    {
        var rows = new[] { Row(1, 1) with { Tags = new Dictionary<string, double> { ["calm"] = 100 } },
            Row(2, 8) with { Tags = new Dictionary<string, double> { ["chill"] = 1 } }, Row(3, 99) };
        var pool = PersonalMixMoods.Qualify("chill", rows, new Dictionary<string, allstarr.Core.AudioMuse.AudioMuseScores>());
        Assert.Equal(["002", "001"], PersonalMixRules.Select("chill", pool, Now, []).Select(row => row.Song.Id));
    }

    [Theory]
    [InlineData("on-repeat")]
    [InlineData("rewind")]
    [InlineData("daily-1")]
    public void Familiar_service_songs_use_the_same_play_order_as_library_songs(string kind)
    {
        var rows = new[] { Row(1, 5) with { Song = new Song { Id = "library", Title = "Local", Artist = "Local", IsLocal = true } },
            Row(2, 20) with { ProviderFilled = true } };
        Assert.Equal("002", PersonalMixRules.Select(kind, rows, Now, [])[0].Song.Id);
    }

    [Fact]
    public void Blends_put_saved_familiar_songs_before_unplayed_songs()
    {
        var rows = new[] { Row(1), Row(2) with { Loved = true } };
        Assert.Equal("002", PersonalMixRules.Select("daily-1", rows, Now, [])[0].Song.Id);
    }

    [Fact]
    public void Rewind_uses_its_three_song_artist_limit_for_service_history()
    {
        var rows = Enumerable.Range(1, 4).Select(id => Row(id, 10, "Same") with { ProviderFilled = true }).ToArray();
        Assert.Equal(3, PersonalMixRules.Select("rewind", rows, Now, []).Count);
    }

    [Fact]
    public void Mood_and_discovery_library_order_uses_the_songs_actual_source()
    {
        var rows = new[] { Row(1, 20), Row(2, 1) with { Song = new Song { Id = "local", IsLocal = true } } };
        Assert.Equal("local", PersonalMixRules.Select("chill", rows, Now, [])[0].Song.Id);
        var fresh = rows.Select(row => row with { Plays = 0 }).ToArray();
        Assert.Equal("local", PersonalMixRules.Select("discover-weekly", fresh, Now, [])[0].Song.Id);
    }

    [Fact]
    public void Recording_buckets_preserve_identity_and_metadata_matches_at_library_scale()
    {
        var own = Enumerable.Range(0, 5000).Select(i => new Song
        {
            Id = "own" + i,
            Title = "Title " + i,
            Artist = "Artist " + i,
            Album = "Album",
            Duration = 180,
            CanonicalRecordingId = Guid.NewGuid()
        }).ToArray();
        var index = new PersonalMixService.RecordingIndex();
        foreach (var song in own) index.Add(song);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var read = 0; read < 240; read++)
            for (var result = 0; result < 100; result++)
            {
                var song = own[(read * 100 + result) % own.Length];
                Assert.Same(song, index.Find(new Song
                {
                    Id = "other",
                    Title = song.Title + " (Remastered)",
                    Artist = song.Artist,
                    Album = song.Album,
                    Duration = 180
                }));
            }
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"24,000 results against 5,000 songs took {timer.Elapsed}.");
        var sameId = new Song { Id = own[0].Id, Title = "Different", Artist = "Different" };
        var sameRecording = new Song { Id = "recording", CanonicalRecordingId = own[1].CanonicalRecordingId, Title = "Other" };
        var isrcSong = new Song { Id = "isrc", Title = "Other", Isrc = "US-AAA-12-34567" };
        own[2].Isrc = "USAAA1234567";
        index.Add(own[2]);
        foreach (var probe in new[] { sameId, sameRecording, isrcSong, new Song { Id = "absent", Title = "Absent" } })
            Assert.Same(own.FirstOrDefault(song => MusicMixService.SameRadioRecording(probe, song)), index.Find(probe));
    }
}
