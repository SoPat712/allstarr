using allstarr.Services.Common;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed class OperationalNoiseRegressionTests
{
    [Fact]
    public void LegacyMissingTrackFileAuthority_IsRemoved()
    {
        var program = Read("allstarr/Program.cs");
        var controller = Read("allstarr/Controllers/SpotifyAdminController.cs");
        var extensionManager = Read("allstarr/Services/Common/ExtensionManager.cs");

        Assert.DoesNotContain("SpotifyMissingTracksFetcher", program, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify/sync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("IPlaylistMatchingCoordinator", controller, StringComparison.Ordinal);
        Assert.Contains("AddPlaylistOrchestration()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("BindingFlags.NonPublic", controller, StringComparison.Ordinal);
        Assert.Contains("\"extension.runtime.error\"", extensionManager, StringComparison.Ordinal);
        Assert.Contains("_extensionId", extensionManager, StringComparison.Ordinal);
        Assert.DoesNotContain("[JS EXT]", extensionManager, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderFallbackHelper_DoesNotWarnDuringConstruction()
    {
        var logger = new Mock<ILogger>();

        _ = new RoundRobinFallbackHelper([], logger.Object, "SquidWTF");

        VerifyNoLevel(logger, LogLevel.Warning);
        VerifyNoLevel(logger, LogLevel.Error);
        VerifyNoLevel(logger, LogLevel.Critical);
    }

    [Fact]
    public void RoutineOperationalEvents_AreNotWarningTemplates()
    {
        var dockerfile = Read("Dockerfile");
        var requestLogging = Read("allstarr/Middleware/RequestLoggingMiddleware.cs");
        var matching = Read("allstarr/Services/Spotify/PerProviderTrackMatcher.cs");
        var playlists = Read("allstarr/Controllers/PlaylistController.cs");

        Assert.Contains("ENV ASPNETCORE_HTTP_PORTS=", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LogWarning(\"Matching {Count} tracks",
            matching,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LogWarning(\"No missing tracks found",
            matching,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LogWarning(\"Playlist cache not available",
            playlists,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LogWarning(\n                \"🔍 Request logging ENABLED",
            requestLogging,
            StringComparison.Ordinal);
    }

    private static void VerifyNoLevel(Mock<ILogger> logger, LogLevel level)
    {
        logger.Verify(
            item => item.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((_, _) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
