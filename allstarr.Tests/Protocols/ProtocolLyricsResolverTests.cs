using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolLyricsResolverTests
{
    [Fact]
    public async Task Resolver_UsesOneConfiguredOrderForMetadataSourcesAndPreservesFacts()
    {
        var protocol = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "backend",
            "api-key",
            null,
            "lyrics-test",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics))
            .Returns(["spotify", "lrclib"]);
        providers.Setup(item => item.GetLyricsAsync(
                protocol,
                "lrclib",
                It.Is<string>(value => value.Length == 64),
                ProviderLyricsFormat.LineTimed,
                "Fixture song",
                It.Is<IReadOnlyList<string>>(artists => artists.SequenceEqual(
                    new[] { "First artist", "Second artist" })),
                "Fixture album",
                180))
            .ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available,
                "lrclib",
                ProviderLyricsFormat.LineTimed,
                "[00:01.00]Fixture line\n",
                "sha256:fixture"));
        var resolver = new ProtocolLyricsResolver(
            providers.Object,
            NullLogger<ProtocolLyricsResolver>.Instance);

        var result = await resolver.FindAsync(protocol, new Song
        {
            Title = "Fixture song",
            Artist = "First artist",
            Artists = ["First artist", "Second artist"],
            Album = "Fixture album",
            Duration = 180
        }, "library-song-id");

        Assert.NotNull(result);
        Assert.Equal("[00:01.00]Fixture line\n", result.SyncedLyrics);
        Assert.Null(result.PlainLyrics);
        Assert.Equal("lrclib", result.Source);
        Assert.Equal("sha256:fixture", result.Revision);
        providers.Verify(item => item.GetLyricsAsync(
            It.IsAny<ProtocolExecutionContext>(), "spotify", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task Resolver_TriesExactSourceProviderBeforeConfiguredFallbacks()
    {
        var protocol = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin, "backend", "api-key", null, "lyrics-source-first",
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics))
            .Returns(["lrclib", "spotiflac-apple-music"]);
        providers.Setup(item => item.GetLyricsAsync(
                protocol, "spotiflac-apple-music", "apple-track", ProviderLyricsFormat.LineTimed,
                "Fixture song", It.IsAny<IReadOnlyList<string>>(), "Fixture album", 240))
            .ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "apple-music",
                ProviderLyricsFormat.LineTimed, "[00:01.00]Apple line\n"));
        var resolver = new ProtocolLyricsResolver(
            providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);

        var result = await resolver.FindAsync(
            protocol,
            new Song { Title = "Fixture song", Artist = "Artist", Album = "Fixture album", Duration = 240 },
            "library-song-id",
            "spotiflac-apple-music",
            "apple-track");

        Assert.Equal("apple-music", result?.Source);
        providers.Verify(item => item.GetLyricsAsync(
            It.IsAny<ProtocolExecutionContext>(), "lrclib", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }
}
