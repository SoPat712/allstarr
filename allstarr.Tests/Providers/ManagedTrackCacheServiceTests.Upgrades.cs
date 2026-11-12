using allstarr.Core.Playback;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    internal static byte[] ConstantBitrateMp3()
    {
        var bytes = new byte[417 * 100];
        for (var offset = 0; offset < bytes.Length; offset += 417)
        {
            bytes[offset] = 0xff;
            bytes[offset + 1] = 0xfb;
            bytes[offset + 2] = 0x90;
        }
        return bytes;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CacheUpgradeKeepsTheOldFileUntilTheBetterSourceCompletes(bool fail, bool downgrade)
    {
        var root = CreateRoot();
        try
        {
            var old = Path.Combine(root, "cache", "old.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            await File.WriteAllBytesAsync(old, ConstantBitrateMp3());
            string? published = null;
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "track-1"))
                .ReturnsAsync(() => published ?? old);
            local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((_, _, path) => published = path).Returns(Task.CompletedTask);
            var verifier = new Mock<IProviderAudioVerifier>(MockBehavior.Strict);
            verifier.Setup(item => item.VerifyFileAsync(It.IsAny<string>(), It.IsAny<ProviderMediaFormat>(),
                    It.IsAny<double?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .ReturnsAsync(new TrackMediaFacts(new("audio/mpeg", "mp3", "mp3", 128000, 44100)));
            await using var service = CreateService(root, "Cache", local.Object, audioVerifier: downgrade ? verifier.Object : null);
            var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            async Task<ProtocolProviderStream?> Open(CancellationToken token)
            {
                Interlocked.Increment(ref calls);
                opening.TrySetResult();
                await finish.Task.WaitAsync(token);
                if (fail) throw new IOException("Incomplete source");
                return ProviderStream(Response(HttpStatusCode.OK, "fLaCbetter audio"u8.ToArray())) with { ServingExternalId = "track-1" };
            }
            var upgrade = service.ScheduleUpgrade(Context(), new("deezer", ProviderResourceKind.Track, "track-1"),
                new(null, ProviderAudioQuality.Any), null, Open, (_, _) => Task.FromResult<Song?>(new Song { Title = "Track", Artist = "Artist" }));
            await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.ScheduleUpgrade(Context(), new("deezer", ProviderResourceKind.Track, "track-1"),
                new(null, ProviderAudioQuality.Any), null, Open, (_, _) => Task.FromResult<Song?>(null));
            Assert.True(File.Exists(old));
            Assert.Null(published);
            Assert.Equal(1, calls);
            finish.TrySetResult();
            await upgrade.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(fail || downgrade, File.Exists(old));
            if (fail || downgrade) Assert.Null(published);
            else
            {
                Assert.True(File.Exists(published));
                using var replay = (await service.TryOpenAsync(new("deezer", ProviderResourceKind.Track, "track-1"),
                    new(null, ProviderAudioQuality.Any), default))!.Response;
                Assert.Equal("fLaCbetter audio"u8.ToArray(), await replay.Content.ReadAsByteArrayAsync());
            }
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CacheUpgradeDoesNotOpenASourceWhenCapacityIsFull()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "cache", "cached.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [0xff, 0xfb, 0x90, 0x00]);
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "track-1"))
                .ReturnsAsync(path);
            await using var service = new ManagedTrackCacheService(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build(),
                Options.Create(new SubsonicSettings()), local.Object, NullLogger<ManagedTrackCacheService>.Instance,
                audioMaximumBytes: () => 4, audioVerifier: FixtureAudioVerifier.Instance);
            var opens = 0;
            await service.ScheduleUpgrade(Context(), new("deezer", ProviderResourceKind.Track, "track-1"),
                new(null, ProviderAudioQuality.Any), null,
                _ => { opens++; return Task.FromResult<ProtocolProviderStream?>(null); },
                (_, _) => throw new InvalidOperationException("No replacement metadata is needed."));
            Assert.Equal(0, opens);
            Assert.True(File.Exists(path));
            Assert.Equal(4, new FileInfo(path).Length);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CachedMp3ReportsItsFileQualityAfterRestart()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "cached.mp3");
            await File.WriteAllBytesAsync(path, ConstantBitrateMp3());
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "track-1"))
                .ReturnsAsync(path);
            await using var service = CreateService(root, "Cache", local.Object);
            var media = await service.ReadCachedMediaAsync(new("deezer", ProviderResourceKind.Track, "track-1"),
                new(null, ProviderAudioQuality.Any), default, null);
            Assert.Equal("mp3", media!.Codec);
            Assert.Equal(128000, media.Bitrate);
            Assert.Equal(44100, media.SampleRate);
            Assert.Null(media.BitDepth);
        }
        finally { DeleteRoot(root); }
    }
}
