using allstarr.Models.Settings;

namespace allstarr.Tests;

public sealed class CacheSettingsTests
{
    [Fact]
    public void SearchWindows_HaveBoundedDefaultsAndClampBootstrapValues()
    {
        var defaults = new CacheSettings();
        Assert.Equal(TimeSpan.FromMilliseconds(3500), defaults.SearchBudget);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.SearchBackgroundWindow);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.SearchFailureCooldown);

        var settings = new CacheSettings
        {
            SearchBudgetMilliseconds = -1,
            SearchBackgroundSeconds = int.MaxValue,
            SearchFailureCooldownSeconds = 0
        };
        Assert.Equal(TimeSpan.FromMilliseconds(100), settings.SearchBudget);
        Assert.Equal(TimeSpan.FromSeconds(120), settings.SearchBackgroundWindow);
        Assert.Equal(TimeSpan.FromSeconds(1), settings.SearchFailureCooldown);
    }

    [Fact]
    public void UnsafeBootstrapTtls_AreClampedToRuntimeLimits()
    {
        var settings = new CacheSettings
        {
            SearchResultsMinutes = -1,
            MetadataDays = int.MaxValue,
            TranscodeCacheMinutes = 0
        };

        Assert.Equal(TimeSpan.FromMinutes(1), settings.SearchResultsTTL);
        Assert.Equal(TimeSpan.FromDays(3650), settings.MetadataTTL);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.TranscodeCacheTTL);
    }
}
