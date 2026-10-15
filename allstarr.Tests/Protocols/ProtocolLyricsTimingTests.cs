using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolLyricsTimingTests
{
    [Fact]
    public async Task SharedSelectionRetainsTimingWhileProtocolsKeepLineResponses()
    {
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener",
            new AllstarrPrincipal(Guid.CreateVersion7(), "jellyfin", "fixture", "listener", "Listener", false),
            "timing", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var lines = new ProviderLyricsTimedLine[]
        {
            new("Synthetic words", TimeSpan.FromMilliseconds(1234), TimeSpan.FromSeconds(2),
                [new("Synthetic ", TimeSpan.FromTicks(12_345_678), null, true, false),
                 new("words", TimeSpan.FromMilliseconds(1600), TimeSpan.FromMilliseconds(400))])
        };
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["fixture"]);
        providers.Setup(item => item.GetLyricsAsync(context, "fixture", "track", ProviderLyricsFormat.LineTimed,
                "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "fixture",
                ProviderLyricsFormat.LineTimed, "[00:01.234]Synthetic words", "revision", lines));
        var song = new Song
        {
            Id = "ext-fixture-song-track",
            ExternalProvider = "fixture",
            ExternalId = "track",
            Title = "Fixture",
            Artist = "Artist",
            Album = "Album",
            Duration = 120
        };
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAsync(context, song, "track", new ProtocolLyricsSources());
        Assert.NotNull(result);
        var retained = Assert.Single(result.TimedLines!);
        Assert.Equal(TimeSpan.FromTicks(12_345_678), retained.Segments![0].Start);
        Assert.True(retained.Segments[0].IsBackground);
        var jellyfin = new JellyfinLyricsProtocolAdapter().Shape(result);
        using var document = JsonDocument.Parse(jellyfin.Body);
        var cue = Assert.Single(document.RootElement.GetProperty("Lyrics").EnumerateArray());
        Assert.Equal("Synthetic words", cue.GetProperty("Text").GetString());
        Assert.Equal(12_340_000, cue.GetProperty("Start").GetInt64());
        Assert.DoesNotContain("TimedLines", jellyfin.Body);
        Assert.DoesNotContain("Segments", jellyfin.Body);
        var subsonic = SubsonicStructuredLyricsMapper.Map(song, result);
        Assert.NotNull(subsonic);
        var line = Assert.Single(subsonic.Lines);
        Assert.Equal((1234L, "Synthetic words"), (line.StartMilliseconds, line.Text));
        Assert.Equal(TimeSpan.FromTicks(12_345_678), result.TimedLines![0].Segments![0].Start);
    }
}
