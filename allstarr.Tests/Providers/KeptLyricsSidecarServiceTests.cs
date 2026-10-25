using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Services.Lyrics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class KeptLyricsSidecarServiceTests
{
    [Fact]
    public async Task SpicySidecarPurposeAndWriterGuardPreventPermanentPublication()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-spicy-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "Fixture.mp3");
            await File.WriteAllTextAsync(audio, "owned audio");
            var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "listener",
                new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "listener", "Listener", false), "lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
            resolver.Setup(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(),
                    It.Is<ProtocolLyricsSources>(options => options.ForPermanentSidecar && options.HouseholdOnly)))
                .ReturnsAsync(new LyricsInfo { Source = "spicylyrics", PlainLyrics = "Do not keep this text" });
            var writer = new KeptLyricsSidecarService(resolver.Object, NullLogger<KeptLyricsSidecarService>.Instance);
            Assert.Null(await writer.EnsureSidecarAsync(audio, new Song { Title = "Fixture", Artist = "Artist" }, protocol: context));
            Assert.False(File.Exists(Path.ChangeExtension(audio, ".lrc")));
            Assert.Equal("owned audio", await File.ReadAllTextAsync(audio));
            resolver.VerifyAll();
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, null, true)]
    [InlineData(ProtocolKind.Jellyfin, true, true)]
    [InlineData(ProtocolKind.Jellyfin, false, false)]
    [InlineData(ProtocolKind.Subsonic, null, false)]
    [InlineData(ProtocolKind.Subsonic, true, false)]
    public async Task SidecarProtectsWordGapsOnlyForEnabledJellyfinFiles(ProtocolKind protocol, bool? enabled, bool converted)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Fixture.mp3");
            await File.WriteAllTextAsync(path, "synthetic audio");
            var backend = protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic";
            var context = new ProtocolExecutionContext(protocol, "backend", "principal",
                new AllstarrPrincipal(Guid.NewGuid(), backend, "backend", "principal", "Listener", false),
                "sidecar", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
            resolver.Setup(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
                    It.IsAny<string>(), It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly)))
                .ReturnsAsync(new LyricsInfo { SyncedLyrics = "[00:01.00]<00:01.00>first <00:02.00>last\n[00:03.00]line synced" });
            var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
            if (enabled.HasValue)
                settings.Setup(item => item.GetAsync("Lyrics:JellyfinWordGap", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new EffectiveRuntimeSetting("Lyrics:JellyfinWordGap", RuntimeSettingValueType.Boolean,
                        enabled.Value, enabled.Value ? "true" : "false", RuntimeSettingOrigin.Durable, 1, "fixture", null, null));
            var service = new KeptLyricsSidecarService(resolver.Object, NullLogger<KeptLyricsSidecarService>.Instance,
                enabled.HasValue ? settings.Object : null);
            var result = await service.EnsureSidecarAsync(path, new Song { Title = "Fixture", Artist = "Artist" }, protocol: context);
            Assert.NotNull(result);
            var text = await File.ReadAllTextAsync(result!);
            Assert.Contains(converted ? "first\u2800<00:02.00>last" : "first <00:02.00>last", text, StringComparison.Ordinal);
            Assert.Contains("[00:03.00]line synced", text, StringComparison.Ordinal);
            Assert.Equal("synthetic audio", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

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
