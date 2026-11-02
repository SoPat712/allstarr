using allstarr.Core.Music;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class PersonalMixRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
    private static MixCandidate Candidate(int id, string tag = "rock", int plays = 1, int recent = 1,
        int age = 1, double score = 0, string? artist = null) => new(new Song
        { Id = id.ToString("D4"), Title = "Song " + id, Artist = artist ?? "Artist " + id },
        new Dictionary<string, double> { [tag] = 1 }, plays, recent, Now.AddDays(-age), false, "history", score);

    [Theory]
    [InlineData("discover-weekly", "Uncharted")]
    [InlineData("release-radar", "Just Dropped")]
    [InlineData("on-repeat", "Heavy Rotation")]
    [InlineData("rewind", "Long Lost")]
    public void GeneratedMixes_UseTheirOwnNames(string kind, string name) => Assert.Equal(name, PersonalMixRules.Name(kind));

    [Theory]
    [InlineData("daily-1", null, "Blend 1")]
    [InlineData("daily-2", "r&b", "R&B Blend")]
    [InlineData("daily-3", "hip-hop", "Hip-Hop Blend")]
    [InlineData("daily-4", "edm", "EDM Blend")]
    [InlineData("daily-1", "uk garage", "UK Garage Blend")]
    public void DailyNames_UseStyleOrStableSlot(string kind, string? style, string name) => Assert.Equal(name, PersonalMixRules.Name(kind, style));

    [Fact]
    public void Repeat_UsesRecentCountsAndCapsArtists()
    {
        var rows = new[] { Candidate(1, recent: 8, artist: "Same"), Candidate(2, recent: 7, artist: "Same"),
            Candidate(3, recent: 6, artist: "same"), Candidate(4, recent: 0), Candidate(5, recent: 4) };
        Assert.Equal(["0001", "0002", "0005"], PersonalMixRules.Select("on-repeat", rows, Now, []).Select(item => item.Song.Id));
    }

    [Fact]
    public void Rewind_RequiresOldLovedHistoryAndNoRecentPlay()
    {
        var rows = new[] { Candidate(1, plays: 5, age: 190), Candidate(2, plays: 2, age: 200),
            Candidate(3, plays: 10, age: 179), Candidate(4, plays: 10, age: 200) with { EarlySkipped = true },
            Candidate(5, plays: 1, age: 200) with { Loved = true } };
        Assert.Equal(["0001"], PersonalMixRules.Select("rewind", rows, Now, []).Select(item => item.Song.Id));
    }

    [Fact]
    public void HistoryOldFavourites_OrderByPlayCountBeforeTasteScore()
    {
        var rows = new[] { Candidate(1, plays: 5, age: 200, score: 10), Candidate(2, plays: 20, age: 200) };
        Assert.Equal(["0002", "0001"], PersonalMixRules.Select("rewind", rows, Now, []).Select(item => item.Song.Id));
    }

    [Fact]
    public void Discovery_ExcludesAnyPlayedSongPreviousWeekAndEarlySkips()
    {
        var rows = new[] { Candidate(1, plays: 0), Candidate(2), Candidate(3, plays: 0),
            Candidate(4, plays: 0) with { EarlySkipped = true }, Candidate(5, plays: 0) with { Heard = true } };
        Assert.Equal("0001", Assert.Single(PersonalMixRules.Select("discover-weekly", rows, Now, ["0003"])).Song.Id);
    }

    [Fact]
    public void Radar_UsesOnlyDatedRecentReleases()
    {
        var rows = new[] { Candidate(1) with { ReleasedAt = Now.AddDays(-13) },
            Candidate(2) with { ReleasedAt = Now.AddDays(-15) }, Candidate(3),
            Candidate(4) with { ReleasedAt = Now.AddDays(1) } };
        Assert.Equal("0001", Assert.Single(PersonalMixRules.Select("release-radar", rows, Now, [])).Song.Id);
    }

    [Fact]
    public void Daily_UsesEqualTiersWithoutDuplicatingSongsOrArtists()
    {
        var rows = Enumerable.Range(1, 100).Select(id => Candidate(id, plays: id <= 70 ? 1 : 0)).ToArray();
        var selected = PersonalMixRules.Select("daily-1", rows, Now, []);
        Assert.Equal(50, selected.Count);
        Assert.Equal(25, selected.Count(item => item.Plays > 0));
        Assert.Equal(25, selected.Count(item => item.Plays == 0));
        Assert.Equal(50, selected.Select(item => item.Song.Id).Distinct().Count());
        Assert.Equal(25, PersonalMixRules.Select("daily-1", rows.Where(item => item.Plays > 0).ToArray(), Now, []).Count);
    }

    [Fact]
    public void Hearts_CountAsFamiliarAndAreExcludedFromDiscovery()
    {
        var rows = Enumerable.Range(1, 100).Select(id => Candidate(id, plays: 0) with { Loved = id <= 70 }).ToArray();
        var daily = PersonalMixRules.Select("daily-1", rows, Now, []);
        Assert.Equal(50, daily.Count);
        Assert.Equal(25, daily.Count(item => item.Loved));
        Assert.DoesNotContain(PersonalMixRules.Select("discover-weekly", rows, Now, []), item => item.Loved);
    }

    [Theory]
    [InlineData(30, 25)]
    [InlineData(60, 50)]
    [InlineData(8, 25)]
    [InlineData(8, 50)]
    public void Custom_UsesSeedRelationshipsBeforeUnrelatedFavourites(int relatedCount, int length)
    {
        var related = Enumerable.Range(1, relatedCount).Select(id => Candidate(id, plays: 0, recent: 0,
            artist: id <= 2 ? "Artist A" : "Related artist " + id) with
        {
            Source = id <= 2 ? "library" : id % 2 == 0 ? "audiomuse" : "radio",
            RelatedTo = "Artist A",
            Less = id == 1
        }).ToArray();
        var unrelated = Enumerable.Range(100, 100).Select(id => Candidate(id, score: 100) with
        { Source = id % 2 == 0 ? "heart" : "library", Loved = true }).ToArray();

        var selected = PersonalMixRules.Select("custom", unrelated.Concat(related).ToArray(), Now, [], length);

        Assert.Equal(length, selected.Count);
        Assert.Equal(Math.Min(relatedCount, length), selected.Count(item => item.RelatedTo != null));
        Assert.All(selected.Take(Math.Min(relatedCount, length)), item => Assert.Equal("Artist A", item.RelatedTo));
        Assert.All(selected.GroupBy(item => item.Song.Artist), group => Assert.True(group.Count() <= 2));
        if (relatedCount < length) Assert.All(selected.Skip(relatedCount), item => Assert.Null(item.RelatedTo));
    }

    [Theory]
    [InlineData(30, 10, 25)]
    [InlineData(5, 20, 25)]
    [InlineData(60, 20, 50)]
    public void Custom_InterleavesRadioWithSoundAndUsesExtraSoundOnlyForShortfalls(int radioCount, int maximumSound, int length)
    {
        var radio = Enumerable.Range(1, radioCount).Select(id => Candidate(id) with { Source = "radio", RelatedTo = "Artist A" });
        var sound = Enumerable.Range(100, 60).Select(id => Candidate(id, score: 100) with { Source = "audiomuse" }).ToArray();
        sound[^1] = sound[^1] with { RelatedTo = "Artist A", Score = 0 };

        var selected = PersonalMixRules.Select("custom", sound.Concat(radio).ToArray(), Now, [], length);

        Assert.Equal(length, selected.Count);
        var soundCount = selected.Count(item => item.Source == "audiomuse");
        Assert.True(soundCount <= maximumSound);
        Assert.Equal(Math.Min(radioCount, length - soundCount), selected.Count(item => item.Source == "radio"));
        Assert.Contains(selected.Take(3), item => item.Source == "radio");
        Assert.Contains(selected.Take(3), item => item.Source == "audiomuse");
        Assert.Contains(selected, item => item.Song.Id == sound[^1].Song.Id);
    }

    [Fact]
    public void Custom_ExtraSoundFillsTheGapAfterTheArtistLimit()
    {
        var radio = Enumerable.Range(1, 20).Select(id => Candidate(id, artist: "Artist A") with { Source = "radio", RelatedTo = "Artist A" });
        var sound = Enumerable.Range(100, 50).Select(id => Candidate(id) with { Source = "audiomuse" });

        var selected = PersonalMixRules.Select("custom", radio.Concat(sound).ToArray(), Now, [], 25);

        Assert.Equal(25, selected.Count);
        Assert.Equal(2, selected.Count(item => item.Source == "radio"));
        Assert.Equal(23, selected.Count(item => item.Source == "audiomuse"));
    }

}
