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
        Assert.Equal(["0001", "0005"], PersonalMixRules.Select("rewind", rows, Now, []).Select(item => item.Song.Id));
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
    public void Clusters_AreDeterministicCoherentAndNamedFromTagsAndArtists()
    {
        var rows = Enumerable.Range(1, 60).Select(id => Candidate(id, id <= 30 ? "rock" : "soul")).ToArray();
        var first = PersonalMixRules.Cluster(rows);
        var second = PersonalMixRules.Cluster(rows.Reverse().ToArray());
        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(group => string.Join(',', group.Select(item => item.Song.Id))),
            second.Select(group => string.Join(',', group.Select(item => item.Song.Id))));
        Assert.All(first, group => Assert.Single(group.SelectMany(item => item.Tags.Keys).Distinct()));
        Assert.Equal("rock · Artist 1, Artist 10, Artist 11", PersonalMixRules.ClusterName(first[0]));
        Assert.Empty(PersonalMixRules.Cluster([Candidate(1) with { Tags = new Dictionary<string, double>() }]));
    }

    [Fact]
    public void Daily_UsesSeventyThirtyWithoutDuplicatingSongsOrArtists()
    {
        var rows = Enumerable.Range(1, 100).Select(id => Candidate(id, plays: id <= 70 ? 1 : 0)).ToArray();
        var selected = PersonalMixRules.Select("daily-1", rows, Now, []);
        Assert.Equal(50, selected.Count);
        Assert.Equal(35, selected.Count(item => item.Plays > 0));
        Assert.Equal(15, selected.Count(item => item.Plays == 0));
        Assert.Equal(50, selected.Select(item => item.Song.Id).Distinct().Count());
        Assert.Empty(PersonalMixRules.Select("daily-1", rows.Where(item => item.Plays > 0).ToArray(), Now, []));
    }

    [Fact]
    public void Hearts_CountAsFamiliarAndAreExcludedFromDiscovery()
    {
        var rows = Enumerable.Range(1, 100).Select(id => Candidate(id, plays: 0) with { Loved = id <= 70 }).ToArray();
        var daily = PersonalMixRules.Select("daily-1", rows, Now, []);
        Assert.Equal(50, daily.Count);
        Assert.Equal(35, daily.Count(item => item.Loved));
        Assert.DoesNotContain(PersonalMixRules.Select("discover-weekly", rows, Now, []), item => item.Loved);
        Assert.NotEmpty(PersonalMixRules.Cluster(rows));
    }

    [Theory]
    [InlineData("on-repeat", 20, 0, false, "10 more")]
    [InlineData("daily-1", 150, 0, true, "50 more")]
    [InlineData("discover-weekly", 200, 0, false, "recommendations")]
    [InlineData("release-radar", 0, 0, false, "Follow")]
    [InlineData("rewind", 300, 0, false, "six months")]
    public void Readiness_ExplainsMissingRequirements(string kind, int plays, int recent, bool source, string message)
    {
        var readiness = PersonalMixRules.Readiness(kind, plays, kind == "on-repeat" ? plays : recent, source, false, false);
        Assert.False(readiness.Ready);
        Assert.Contains(message, readiness.Message);
    }
}
