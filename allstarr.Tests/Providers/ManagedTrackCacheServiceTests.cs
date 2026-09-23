using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class ManagedTrackCacheServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedFullRangeStream_IsPublishedAndRegistered(bool fallback)
    {
        var root = CreateRoot();
        try
        {
            string? registeredPath = null;
            var provider = fallback ? "qobuz" : "deezer";
            var externalId = fallback ? "other-track" : "track-1";
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(
                    It.IsAny<DownloadedSongMappingScope>(), provider, externalId))
                .ReturnsAsync((string?)null);
            local.Setup(item => item.RegisterDownloadedSongAsync(
                    It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((scope, song, path) =>
                {
                    Assert.Equal(ProviderAudioQuality.Lossless, scope.AudioQuality);
                    Assert.Equal(provider, song.ExternalProvider);
                    Assert.Equal(externalId, song.ExternalId);
                    registeredPath = path;
                })
                .Returns(Task.CompletedTask);
            var service = CreateService(root, "Cache", local.Object);
            using var response = Response(HttpStatusCode.PartialContent, [1, 2, 3, 4]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);

            await service.WrapAsync(
                ProviderStream(response) with
                {
                    ServingProviderId = provider,
                    ServingExternalId = externalId,
                    EffectiveQuality = ProviderAudioQuality.Lossless
                },
                Context(),
                "deezer",
                "track-1",
                headOnly: false,
                () => Task.FromResult<Song?>(new Song
                {
                    Title = "Track",
                    Artist = "Artist",
                    Album = "Album",
                    Track = 1
                }),
                CancellationToken.None);

            Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
            Assert.NotNull(registeredPath);
            Assert.True(File.Exists(registeredPath));
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(registeredPath));
            local.VerifyAll();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task CompletedFlacStream_PublishesSeekableFileWithoutGuidanceTag(int tagPayloadBytes)
    {
        var root = CreateRoot();
        try
        {
            string? registeredPath = null;
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(
                    It.IsAny<DownloadedSongMappingScope>(), "apple-download", "song-1"))
                .ReturnsAsync(() => registeredPath);
            local.Setup(item => item.RegisterDownloadedSongAsync(
                    It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((_, _, path) => registeredPath = path)
                .Returns(Task.CompletedTask);
            var tag = new byte[10 + tagPayloadBytes];
            "ID3"u8.CopyTo(tag);
            tag[3] = 4;
            tag[6] = (byte)((tagPayloadBytes >> 21) & 0x7f);
            tag[7] = (byte)((tagPayloadBytes >> 14) & 0x7f);
            tag[8] = (byte)((tagPayloadBytes >> 7) & 0x7f);
            tag[9] = (byte)(tagPayloadBytes & 0x7f);
            var flac = "fLaCtest-frames"u8.ToArray();
            var payload = tag.Concat(flac).ToArray();
            var service = CreateService(root, "Cache", local.Object);
            using var response = Response(HttpStatusCode.OK, payload);

            await service.WrapAsync(
                ProviderStream(response) with
                {
                    ServingProviderId = "apple-download",
                    ServingExternalId = "song-1"
                },
                Context(), "apple-download", "song-1", headOnly: false,
                () => Task.FromResult<Song?>(new Song
                {
                    Title = "Song",
                    Artist = "Artist",
                    Album = "Album"
                }),
                CancellationToken.None);

            Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
            Assert.NotNull(registeredPath);
            Assert.Equal(flac, await File.ReadAllBytesAsync(registeredPath));
            var cached = await service.TryOpenAsync(
                new ProviderExternalResourceId("apple-download", ProviderResourceKind.Track, "song-1"),
                new DownloadedSongMappingScope(
                    Context().Actor!.TenantId, null, "music", ProviderAudioQuality.Any),
                CancellationToken.None);
            Assert.NotNull(cached);
            Assert.True(cached.IsCached);
            Assert.True((await cached.Response.Content.ReadAsStreamAsync()).CanSeek);
            cached.Response.Dispose();
            local.VerifyAll();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyId3CacheIsRetiredWithoutTouchingUnownedFiles(bool ownedCache)
    {
        var root = CreateRoot();
        try
        {
            var directory = Path.Combine(root, ownedCache ? "cache" : "permanent");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "song.flac");
            await File.WriteAllBytesAsync(path, "ID3\x04\0\0\0\0\0\0fLaCold"u8.ToArray());
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(
                    It.IsAny<DownloadedSongMappingScope>(), "apple-download", "song-1"))
                .ReturnsAsync(path);
            var service = CreateService(root, "Cache", local.Object);
            var cached = await service.TryOpenAsync(
                new ProviderExternalResourceId("apple-download", ProviderResourceKind.Track, "song-1"),
                new DownloadedSongMappingScope(
                    Context().Actor!.TenantId, null, "music", ProviderAudioQuality.Any),
                CancellationToken.None);

            Assert.Equal(!ownedCache, cached != null);
            Assert.Equal(!ownedCache, File.Exists(path));
            if (ownedCache)
                Assert.Single(Directory.GetFiles(directory, "*.legacy-id3-*"));
            cached?.Response.Dispose();
            local.VerifyAll();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InterruptedStream_DeletesPartialFileAndDoesNotRegister()
    {
        var root = CreateRoot();
        try
        {
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            var service = CreateService(root, "Cache", local.Object);
            using var response = Response(HttpStatusCode.OK, [1, 2, 3, 4]);

            await service.WrapAsync(
                ProviderStream(response),
                Context(),
                "deezer",
                "track-2",
                headOnly: false,
                () => Task.FromResult<Song?>(null),
                CancellationToken.None);

            await using (var stream = await response.Content.ReadAsStreamAsync())
                Assert.Equal(1, stream.ReadByte());

            Assert.Empty(Directory.Exists(root)
                ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                : []);
            local.Verify(item => item.RegisterDownloadedSongAsync(
                It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PartialRangeOrPermanentMode_DoesNotCreateTrackCache()
    {
        foreach (var storageMode in new[] { "Cache", "Permanent" })
        {
            var root = CreateRoot();
            try
            {
                var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
                var service = CreateService(root, storageMode, local.Object);
                using var response = Response(
                    storageMode == "Cache" ? HttpStatusCode.PartialContent : HttpStatusCode.OK,
                    [2, 3, 4]);
                if (storageMode == "Cache")
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 3, 4);

                await service.WrapAsync(
                    ProviderStream(response),
                    Context(),
                    "deezer",
                    "track-3",
                    headOnly: false,
                    () => Task.FromResult<Song?>(null),
                    CancellationToken.None);

                Assert.Equal([2, 3, 4], await response.Content.ReadAsByteArrayAsync());
                Assert.False(Directory.Exists(Path.Combine(root, "cache")));
                local.VerifyNoOtherCalls();
            }
            finally
            {
                DeleteRoot(root);
            }
        }
    }

    private static ManagedTrackCacheService CreateService(
        string root,
        string storageMode,
        ILocalLibraryService local)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = root
            })
            .Build();
        return new ManagedTrackCacheService(
            configuration,
            Options.Create(new SubsonicSettings
            {
                StorageMode = Enum.Parse<StorageMode>(storageMode)
            }),
            local,
            NullLogger<ManagedTrackCacheService>.Instance);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        return response;
    }

    private static ProtocolProviderStream ProviderStream(HttpResponseMessage response) => new(
        response,
        new ProviderStreamLease(
            "lease",
            new Uri("https://media.example.test/track"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            supportsByteRanges: true,
            supportsSeeking: true,
            new ProviderMediaFormat("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry),
        "deezer");

    private static ProtocolExecutionContext Context() => new(
        ProtocolKind.Jellyfin,
        "backend",
        "principal",
        new AllstarrPrincipal(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            "jellyfin",
            "backend",
            "principal",
            "User",
            false),
        "cache-test",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None,
        new ProtocolClientDescriptor("client", "device"),
        libraryScopeId: "music");

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
