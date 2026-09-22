using allstarr.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class ReleaseCompositionTests
{
    [Theory]
    [InlineData(null, "core", false)]
    [InlineData("core", "core", false)]
    [InlineData("development", "development", true)]
    public void Resolve_UsesExplicitClosedProfiles(
        string? configured,
        string expectedProfile,
        bool intelligenceEnabled)
    {
        var values = new Dictionary<string, string?> { ["Release:Profile"] = configured };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var composition = ReleaseComposition.Resolve(configuration);

        Assert.Equal(expectedProfile, composition.Profile);
        Assert.Equal(intelligenceEnabled, composition.IntelligenceEnabled);
        Assert.Equal(intelligenceEnabled,
            composition.Includes(ReleaseFeatureKind.Intelligence));
    }

    [Fact]
    public void Resolve_RejectsUnknownProfile()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Release:Profile"] = "preview"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ReleaseComposition.Resolve(configuration));
    }

    [Fact]
    public void CoreProfile_PausesRecommendationSchedulesWithoutDisablingCoreJobs()
    {
        Assert.False(ReleaseComposition.Core.IncludesScheduledJob("recommendation.generate"));
        Assert.True(ReleaseComposition.Core.IncludesScheduledJob("playlist.materialize"));
        Assert.True(ReleaseComposition.Development.IncludesScheduledJob("recommendation.generate"));
    }
}
