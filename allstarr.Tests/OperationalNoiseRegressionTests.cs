using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Scrobbling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class OperationalNoiseRegressionTests
{
    [Fact]
    public void ScrobblingServices_DoNotLogConfigurationDuringConstruction()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(item => item.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient());
        var settings = Options.Create(new ScrobblingSettings
        {
            Enabled = true,
            LastFm = new LastFmSettings { Enabled = true },
            ListenBrainz = new ListenBrainzSettings { Enabled = true }
        });
        var lastFmLogger = new Mock<ILogger<LastFmScrobblingService>>();
        var listenBrainzLogger = new Mock<ILogger<ListenBrainzScrobblingService>>();

        _ = new LastFmScrobblingService(settings, factory.Object, lastFmLogger.Object);
        _ = new ListenBrainzScrobblingService(settings, factory.Object, listenBrainzLogger.Object);

        VerifyNoLogs(lastFmLogger);
        VerifyNoLogs(listenBrainzLogger);
    }

    [Fact]
    public void LegacySpotifyAdminOperations_UseTypedBoundedServicePaths()
    {
        var program = Read("allstarr/Program.cs");
        var controller = Read("allstarr/Controllers/SpotifyAdminController.cs");
        var fetcher = Read("allstarr/Services/Spotify/SpotifyMissingTracksFetcher.cs");
        var extensionManager = Read("allstarr/Services/Common/ExtensionManager.cs");

        Assert.Contains("AddSingleton<allstarr.Services.Spotify.SpotifyMissingTracksFetcher>()", program, StringComparison.Ordinal);
        Assert.Contains("fetcherService.TriggerFetchAsync(HttpContext.RequestAborted)", controller, StringComparison.Ordinal);
        Assert.Contains("matchingService.TriggerMatchingAsync(HttpContext.RequestAborted)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("BindingFlags.NonPublic", controller, StringComparison.Ordinal);
        Assert.Contains("MaximumCandidateProbes = 256", fetcher, StringComparison.Ordinal);
        Assert.Contains("waitForActiveRun", fetcher, StringComparison.Ordinal);
        Assert.Contains("MissingTrackExportRetryPolicy", fetcher, StringComparison.Ordinal);
        Assert.Contains("MissingTrackFileProbeStatus.Unavailable", fetcher, StringComparison.Ordinal);
        Assert.Contains("LogDebug(", fetcher, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "No missing-track export is available for {Playlist} in the bounded schedule window\");",
            fetcher,
            StringComparison.Ordinal);
        Assert.DoesNotContain("totalMinutesToSearch = 72 * 60", fetcher, StringComparison.Ordinal);
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

    private static void VerifyNoLogs<T>(Mock<ILogger<T>> logger)
    {
        logger.Verify(
            item => item.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((_, _) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
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
