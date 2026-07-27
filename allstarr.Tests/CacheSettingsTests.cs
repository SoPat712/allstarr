using allstarr.Models.Settings;

namespace allstarr.Tests;

public sealed class CacheSettingsTests
{
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
