using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
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
    [Theory]
    [InlineData(".lrc")]
    [InlineData(".ttml")]
    public async Task StoredTimedTextSidecarsReturnCleanWordTimedLines(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "Fixture.mp3");
            await File.WriteAllTextAsync(audio, "synthetic audio");
            await File.WriteAllTextAsync(Path.ChangeExtension(audio, extension),
                """<tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="1s" end="3s"><span begin="1s" end="2s">café </span><span begin="2s" end="3s">世界</span></p></div></body></tt>""");
            var saved = await KeptLyricsReader.ReadStoredSidecarAsync(new Song { Title = "Fixture" }, audio, default);
            Assert.Equal("word", RichLyricsMapper.Map(saved)!.Format);
            using var response = JsonDocument.Parse(new JellyfinLyricsProtocolAdapter().Shape(saved!).Body);
            var line = Assert.Single(response.RootElement.GetProperty("Lyrics").EnumerateArray());
            Assert.Equal("café 世界", line.GetProperty("Text").GetString());
            Assert.Equal(2, line.GetProperty("Cues").GetArrayLength());
            var subsonic = SubsonicStructuredLyricsMapper.Map(new Song { Title = "Fixture" }, saved);
            Assert.Equal((1000L, "café 世界"), (Assert.Single(subsonic!.Lines).StartMilliseconds, subsonic.Lines[0].Text));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StructuredWordsSurviveSavingAndReadingTheKeptSidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "Fixture.mp3");
            await File.WriteAllTextAsync(audio, "synthetic audio");
            var context = new ProtocolExecutionContext(ProtocolKind.Subsonic, "backend", "listener",
                new AllstarrPrincipal(Guid.NewGuid(), "subsonic", "backend", "listener", "Listener", false),
                "sidecar", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var song = new Song { Title = "Fixture", Artist = "Artist", Duration = 5 };
            var lyrics = new LyricsInfo
            {
                Source = "lrcred",
                SyncedLyrics = "[00:01.000]café 世界",
                Duration = 5,
                TimedLines = [new("café echo 世界", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3),
                    [new("café ", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500)),
                     new("echo ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), true),
                     new("世界", TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500))])]
            };
            var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
            resolver.Setup(item => item.FindAsync(context, It.IsAny<Song>(), It.IsAny<string>(),
                    It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly && sources.ForPermanentSidecar)))
                .ReturnsAsync(lyrics);
            var writer = new KeptLyricsSidecarService(resolver.Object, NullLogger<KeptLyricsSidecarService>.Instance);
            var path = await writer.EnsureSidecarAsync(audio, song, protocol: context);
            Assert.NotNull(path);
            var content = await File.ReadAllTextAsync(path!);
            Assert.Contains("<00:01.000>café <00:01.500>", content);
            Assert.Contains("<00:02.000>世界<00:02.500>", content);
            Assert.EndsWith("\n\n[00:01.000]<00:01.000>café <00:01.500><00:02.000>世界<00:02.500>\n[00:01.000]<00:01.000>echo<00:02.000>", content);
            var saved = await KeptLyricsReader.ReadStoredSidecarAsync(song, audio, default);
            Assert.Equal("word", RichLyricsMapper.Map(saved)!.Format);
            using var response = JsonDocument.Parse(new JellyfinLyricsProtocolAdapter().Shape(saved!).Body);
            var lines = response.RootElement.GetProperty("Lyrics");
            Assert.Equal(2, lines.GetArrayLength());
            var line = lines[0];
            Assert.Equal("café 世界", line.GetProperty("Text").GetString());
            Assert.Equal(15_000_000, line.GetProperty("Cues")[0].GetProperty("End").GetInt64());
            Assert.Equal(5, line.GetProperty("Cues")[1].GetProperty("Position").GetInt32());
            Assert.Equal(25_000_000, line.GetProperty("Cues")[1].GetProperty("End").GetInt64());
            Assert.Equal("echo", lines[1].GetProperty("Text").GetString());
            Assert.Equal(10_000_000, lines[1].GetProperty("Start").GetInt64());
            var backgroundCue = Assert.Single(lines[1].GetProperty("Cues").EnumerateArray());
            Assert.Equal(0, backgroundCue.GetProperty("Position").GetInt32());
            Assert.Equal(4, backgroundCue.GetProperty("EndPosition").GetInt32());
            Assert.Equal(10_000_000, backgroundCue.GetProperty("Start").GetInt64());
            Assert.Equal(20_000_000, backgroundCue.GetProperty("End").GetInt64());
            await File.WriteAllTextAsync(path!, "[00:01.000]Existing line only");
            Assert.Equal(path, await writer.EnsureSidecarAsync(audio, song, protocol: context));
            Assert.Equal("[00:01.000]Existing line only", await File.ReadAllTextAsync(path!));
            resolver.Verify(item => item.FindAsync(context, It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()), Times.Once);
        }
        finally { Directory.Delete(root, true); }
    }

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
