using allstarr.Services.Common;

namespace allstarr.Tests;

public class BotProbeDetectorTests
{
    [Theory]
    [InlineData("/.env")]
    [InlineData("/.git/config")]
    [InlineData("/wordpress")]
    [InlineData("/wp")]
    [InlineData("/wp-admin/install.php")]
    [InlineData("/vendor/phpunit/phpunit/src/Util/PHP/eval-stdin.php")]
    [InlineData("/public/vendor/laravel-filemanager/js/script.js")]
    [InlineData("/_ignition/execute-solution")]
    [InlineData("/debug/default/index")]
    [InlineData("https://jellyfin.joshpatra.me/.git/config")]
    public void IsHighConfidenceProbeUrl_ScannerPaths_ReturnsTrue(string path)
    {
        Assert.True(BotProbeDetector.IsHighConfidenceProbeUrl(path));
    }

    [Theory]
    [InlineData("/System/Info/Public")]
    [InlineData("/web/index.html")]
    [InlineData("/Items/123")]
    [InlineData("/Users/AuthenticateByName")]
    [InlineData("/new")]
    [InlineData("/blog")]
    public void IsHighConfidenceProbeUrl_NormalProxyPaths_ReturnsFalse(string path)
    {
        Assert.False(BotProbeDetector.IsHighConfidenceProbeUrl(path));
    }
}
