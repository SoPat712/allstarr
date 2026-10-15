using allstarr.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed class ContainerStorageLayoutTests
{
    [Theory]
    [InlineData(null, "/app/cache/allstarr-v3")]
    [InlineData("/app/data", "/app/cache/allstarr-v3")]
    [InlineData("/data/music", "/data/music")]
    public void LegacyLayout_UsesOnlyPersistentStorage(string? configured, string expected)
    {
        var settings = new ConfigurationManager();
        settings["Storage:DataDirectory"] = configured;
        ContainerStorageLayout.Configure(settings, true, ["/app/cache", "/data"]);
        new ServiceCollection().AddDurableStorage(settings, new Mock<IHostEnvironment>().Object);
        Assert.Equal(expected, settings["Storage:DataDirectory"]);
        Assert.Equal(expected + "/kept", settings["Library:KeptPath"]);
    }

    [Fact]
    public void ExplicitDataMount_WinsOverLegacyCache()
    {
        var settings = new ConfigurationManager();
        settings["Storage:DataDirectory"] = "/app/data";
        ContainerStorageLayout.Configure(settings, true, ["/app/data", "/app/cache"]);
        Assert.Equal("/app/data", settings["Storage:DataDirectory"]);
    }

    [Theory]
    [InlineData("/app/cache-other")]
    [InlineData("/")]
    public void EphemeralContainerData_IsRejectedBeforeCreatingAnything(string mount)
    {
        var settings = new ConfigurationManager();
        var error = Assert.Throws<InvalidOperationException>(() => ContainerStorageLayout.Configure(settings, true, [mount]));
        Assert.Contains("persistent", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyRoots_AreDiscoveryInputsRatherThanNewWriteTargets()
    {
        var settings = new ConfigurationManager();
        settings["Library:DownloadPath"] = "/app/downloads/cache";
        settings["Library:KeptPath"] = "/app/downloads/kept";
        ContainerStorageLayout.Configure(settings, true, ["/app/cache", "/app/downloads"]);
        Assert.Equal("/app/downloads/cache", settings["Legacy:DownloadPath"]);
        Assert.Equal("/app/downloads/kept", settings["Legacy:KeptPath"]);
        Assert.Equal("/app/cache/allstarr-v3/downloads", settings["Library:DownloadPath"]);
        Assert.Equal("/app/cache/allstarr-v3/kept", settings["Library:KeptPath"]);
    }

    [Fact]
    public void MountInventory_RejectsRootOverlayAndTemporaryFilesystems()
    {
        var mounts = ContainerStorageLayout.PersistentMounts("""
            31 20 0:30 / / rw - overlay overlay rw
            32 31 8:1 /cache /app/cache rw - ext4 /dev/sda1 rw
            33 31 0:31 / /app/data rw - tmpfs tmpfs rw
            34 31 8:1 /a /some\040folder rw - ext4 /dev/sda1 rw
            35 31 8:1 /a /read-only ro - ext4 /dev/sda1 ro
            """);
        Assert.Equal(["/app/cache", "/some folder"], mounts);
    }
}
