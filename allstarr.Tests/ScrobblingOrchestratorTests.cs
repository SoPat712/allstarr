using allstarr.Models.Scrobbling;
using allstarr.Models.Settings;
using allstarr.Services.Scrobbling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public class ScrobblingOrchestratorTests
{
    [Fact]
    public async Task OnPlaybackStartAsync_DuplicateStartForSameTrack_SendsNowPlayingOnce()
    {
        var service = new Mock<IScrobblingService>();
        service.SetupGet(s => s.IsEnabled).Returns(true);
        service.SetupGet(s => s.ServiceName).Returns("MockService");
        service.Setup(s => s.UpdateNowPlayingAsync(It.IsAny<ScrobbleTrack>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScrobbleResult.CreateSuccess());

        var orchestrator = CreateOrchestrator(service.Object);
        var track = CreateTrack();

        await orchestrator.OnPlaybackStartAsync("device-1", track);
        await orchestrator.OnPlaybackStartAsync("device-1", track);

        service.Verify(
            s => s.UpdateNowPlayingAsync(It.IsAny<ScrobbleTrack>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static ScrobblingOrchestrator CreateOrchestrator(IScrobblingService service)
    {
        var settings = Options.Create(new ScrobblingSettings
        {
            Enabled = true
        });

        var logger = Mock.Of<ILogger<ScrobblingOrchestrator>>();
        return new ScrobblingOrchestrator(new[] { service }, settings, logger);
    }

    private static ScrobbleTrack CreateTrack()
    {
        return new ScrobbleTrack
        {
            Title = "Sad Girl Summer",
            Artist = "Maisie Peters",
            DurationSeconds = 180,
            IsExternal = true,
            StartPositionSeconds = 0
        };
    }
}
