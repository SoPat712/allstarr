using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Services.Lyrics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class KeptLyricsSidecarServiceTests
{
    [Fact]
    public async Task PublishedSidecarUsesHouseholdLookupInsteadOfTheAdministratorsPersonalChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Fixture.mp3");
            await File.WriteAllTextAsync(path, "synthetic audio");
            var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal",
                new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "principal", "Administrator", true),
                "sidecar", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
            resolver.Setup(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(new LyricsInfo { PlainLyrics = "Personal words must stay personal" });
            resolver.Setup(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
                    It.IsAny<string>(), It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly)))
                .ReturnsAsync(new LyricsInfo { PlainLyrics = "Household words" });
            var service = new KeptLyricsSidecarService(resolver.Object, NullLogger<KeptLyricsSidecarService>.Instance);
            var result = await service.EnsureSidecarAsync(path, new Song { Title = "Fixture", Artist = "Artist" }, protocol: context);
            Assert.NotNull(result);
            var text = await File.ReadAllTextAsync(result!);
            Assert.Contains("Household words", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Personal words", text, StringComparison.Ordinal);
            Assert.Equal("synthetic audio", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SidecarCancellationUsesTheOriginalProtocolTokenAndLeavesNoFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Fixture.mp3");
            await File.WriteAllTextAsync(path, "synthetic audio");
            using var canceled = new CancellationTokenSource();
            var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal",
                new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "principal", "Listener", false),
                "sidecar", DateTimeOffset.UtcNow.AddMinutes(1), canceled.Token);
            var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
            resolver.Setup(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
                    It.IsAny<string>(), It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly)))
                .Callback(canceled.Cancel)
                .ReturnsAsync(new LyricsInfo { SyncedLyrics = "[00:01.234]Synthetic words" });
            var service = new KeptLyricsSidecarService(resolver.Object, NullLogger<KeptLyricsSidecarService>.Instance);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureSidecarAsync(path,
                new Song { Title = "Fixture", Artist = "Artist" }, protocol: context));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".lrc")));
            Assert.Equal("synthetic audio", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SidecarGenerationUsesSharedLyricsWithTheRequestingListener(bool linked)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Fixture [spotify-track].mp3");
            await File.WriteAllTextAsync(path, "synthetic audio fixture");
            var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal",
                new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "principal", "Listener", false),
                "sidecar", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
            resolver.Setup(item => item.FindAsync(context, It.Is<Song>(song => song.Title == "Fixture" && song.Artist == "Artist" &&
                    song.ExternalProvider == "spotify" && song.ExternalId == "track" && song.SpotifyId == "track"),
                    It.IsAny<string>(), It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly)))
                .ReturnsAsync(new LyricsInfo { SyncedLyrics = "[00:01.234]Synthetic words", Source = "fixture" });
            var service = new KeptLyricsSidecarService(resolver.Object, NullLogger<KeptLyricsSidecarService>.Instance);
            var result = await service.EnsureSidecarAsync(path, new Song
            { Title = "Fixture [A]", Artist = "Artist", SpotifyId = "track" }, protocol: linked ? context : null);
            if (linked)
            {
                Assert.Equal(Path.ChangeExtension(path, ".lrc"), result);
                Assert.Contains("[00:01.234]Synthetic words", await File.ReadAllTextAsync(result!), StringComparison.Ordinal);
                Assert.Equal("synthetic audio fixture", await File.ReadAllTextAsync(path));
                resolver.VerifyAll();
            }
            else
            {
                Assert.Null(result);
                Assert.False(File.Exists(Path.ChangeExtension(path, ".lrc")));
                resolver.VerifyNoOtherCalls();
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
