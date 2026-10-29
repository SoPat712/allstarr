using System.Text.Json;
using allstarr.Core.AudioMuse;
using allstarr.Core.Music;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class PersonalMixMoodTests
{
    [Fact]
    public void Analysis_payload_without_optional_measurements_uses_feature_pairs()
    {
        using var payload = JsonDocument.Parse("""
            {"item_id":"sample","mood_vector":"pop:0.588,dance:0.566,female vocalists:0.553,electronic:0.545,rnb:0.528",
             "other_features":"danceable:0.70,aggressive:0.64,happy:0.64,party:0.69,relaxed:0.62,sad:0.57"}
            """);
        var scores = AudioMuseClient.ParseScores(payload.RootElement);
        Assert.Null(scores.Energy);
        Assert.Null(scores.Tempo);
        Assert.Equal(.545, scores.Tags!["electronic"]);
        Assert.NotNull(PersonalMixMoods.Score("chill", scores));
        Assert.NotNull(PersonalMixMoods.Score("party", scores));
        Assert.NotNull(PersonalMixMoods.Score("workout", scores));
        Assert.Null(PersonalMixMoods.Score("focus", scores));
        Assert.Null(PersonalMixMoods.Score("workout", scores with { Tempo = 180 }));
        Assert.Null(PersonalMixMoods.Score("sleep", scores with { Energy = .8 }));
    }

    [Theory]
    [InlineData("chill", "chill", .7, .4, .7, .7, .7, .6, .5, 100)]
    [InlineData("focus", "ambient", .8, .2, .4, .2, .3, .5, .3, 90)]
    [InlineData("workout", "energetic", .3, .8, .8, .6, .7, .2, .8, 140)]
    [InlineData("party", "dance", .6, .6, .8, .8, .8, .4, .8, 125)]
    [InlineData("sleep", "calm", .8, .2, .2, .1, .2, .8, .2, 65)]
    public void Recipes_accept_measured_features_and_canonical_tags(string kind, string tag, double relaxed,
        double aggressive, double danceable, double party, double happy, double sad, double energy, double tempo)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            item_id = "sample",
            energy,
            tempo,
            mood_vector = "ambient:0.6,electronic:0.5",
            other_features = $"danceable:{danceable.ToString(System.Globalization.CultureInfo.InvariantCulture)},aggressive:{aggressive.ToString(System.Globalization.CultureInfo.InvariantCulture)},happy:{happy.ToString(System.Globalization.CultureInfo.InvariantCulture)},party:{party.ToString(System.Globalization.CultureInfo.InvariantCulture)},relaxed:{relaxed.ToString(System.Globalization.CultureInfo.InvariantCulture)},sad:{sad.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        }));
        var row = new MixCandidate(new Song { Id = "sample", Title = "Song", Artist = "Artist", IsLocal = true },
            new Dictionary<string, double> { [tag] = 1 }, 0, 0, null, false, "library", 0);
        var measured = PersonalMixMoods.Qualify(kind, [row], new Dictionary<string, AudioMuseScores>
        { ["sample"] = AudioMuseClient.ParseScores(payload.RootElement) });
        Assert.Equal(kind switch { "workout" => "Energetic sound", "party" => "Party sound", _ => "Calm sound" }, PersonalMixMoods.Reason(kind, Assert.Single(measured)));
        var tagged = PersonalMixMoods.Qualify(kind, [row], new Dictionary<string, AudioMuseScores>());
        Assert.Equal("Tagged " + kind, PersonalMixMoods.Reason(kind, Assert.Single(tagged)));
        Assert.Equal("0 4 * * 1", PersonalMixRules.Cron(kind));
    }

    [Fact]
    public void Measured_mismatch_does_not_fall_back_to_tags_and_streaming_requires_low_coverage()
    {
        var row = new MixCandidate(new Song { Id = "one", IsLocal = true }, new Dictionary<string, double> { ["chill"] = 1 },
            0, 0, null, false, "library", 0);
        var scores = new Dictionary<string, AudioMuseScores> { ["one"] = new(.9, .1, .8, .8, .9, .1, .1) };
        Assert.Empty(PersonalMixMoods.Qualify("chill", [row], scores));
        Assert.DoesNotContain(PersonalMixMoods.Qualify("chill", [row, row with { Song = new Song { Id = "stream" } }], scores),
            item => item.Song.Id == "stream");
        Assert.Single(PersonalMixMoods.Qualify("chill", [row with { Song = new Song { Id = "stream" } }], new Dictionary<string, AudioMuseScores>()));
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(25, true)]
    [InlineData(40, true)]
    public void Readiness_requires_twenty_five_selected_songs(int count, bool ready) =>
        Assert.Equal(ready, PersonalMixMoods.Readiness(count).Ready);
}
