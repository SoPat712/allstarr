using allstarr.Services.Common;

namespace allstarr.Tests;

public class VersionUpgradePolicyTests
{
    [Fact]
    public void ShouldTriggerRebuild_ReturnsTrue_ForMinorUpgrade()
    {
        var shouldRebuild = VersionUpgradePolicy.ShouldTriggerRebuild("1.1.0", "1.2.0", out var reason);

        Assert.True(shouldRebuild);
        Assert.Equal("minor version upgrade", reason);
    }

    [Fact]
    public void ShouldTriggerRebuild_ReturnsTrue_ForMajorUpgrade()
    {
        var shouldRebuild = VersionUpgradePolicy.ShouldTriggerRebuild("1.9.3", "2.0.0", out var reason);

        Assert.True(shouldRebuild);
        Assert.Equal("major version upgrade", reason);
    }

    [Fact]
    public void ShouldTriggerRebuild_ReturnsFalse_ForPatchUpgrade()
    {
        var shouldRebuild = VersionUpgradePolicy.ShouldTriggerRebuild("1.2.0", "1.2.1", out var reason);

        Assert.False(shouldRebuild);
        Assert.Equal("patch-only upgrade", reason);
    }

    [Fact]
    public void ShouldTriggerRebuild_ReturnsFalse_ForDowngrade()
    {
        var shouldRebuild = VersionUpgradePolicy.ShouldTriggerRebuild("2.0.0", "1.9.9", out var reason);

        Assert.False(shouldRebuild);
        Assert.Equal("version is not an upgrade", reason);
    }
}
