using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class FileMediaCacheSettingsTests
{
    [Fact]
    public void Defaults_AreConservativeAndRamIndependent()
    {
        var options = FileMediaCacheOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection().Build());

        Assert.Equal("/app/cache/media", options.RootPath);
        Assert.Equal(512L * 1024 * 1024, options.MaximumBytes);
        Assert.Equal(16 * 1024 * 1024, options.MaximumEntryBytes);
        Assert.Equal(10_000, options.MaximumCleanupFiles);
    }

    [Fact]
    public void OperatorSettings_AreConvertedAndBounded()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:MediaDirectory"] = "/cache/artwork",
                ["Cache:MediaMaximumMegabytes"] = "1024",
                ["Cache:MediaMaximumEntryMegabytes"] = "32",
                ["Cache:MediaCleanupFileLimit"] = "25000"
            })
            .Build();

        var options = FileMediaCacheOptions.FromConfiguration(configuration);

        Assert.Equal("/cache/artwork", options.RootPath);
        Assert.Equal(1024L * 1024 * 1024, options.MaximumBytes);
        Assert.Equal(32 * 1024 * 1024, options.MaximumEntryBytes);
        Assert.Equal(25_000, options.MaximumCleanupFiles);
    }

    [Fact]
    public void UnsafeExtremes_AreClamped()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:MediaMaximumMegabytes"] = "1",
                ["Cache:MediaMaximumEntryMegabytes"] = "512",
                ["Cache:MediaCleanupFileLimit"] = "1"
            })
            .Build();

        var options = FileMediaCacheOptions.FromConfiguration(configuration);

        Assert.Equal(16L * 1024 * 1024, options.MaximumBytes);
        Assert.Equal(16 * 1024 * 1024, options.MaximumEntryBytes);
        Assert.Equal(100, options.MaximumCleanupFiles);
    }
}
