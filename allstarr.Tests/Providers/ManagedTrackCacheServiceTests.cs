using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
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
    [Fact]
    public async Task CompletedFullRangeStream_IsPublishedAndRegistered()
    {
        var root = CreateRoot();
        try
        {
            string? registeredPath = null;
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync("deezer", "track-1"))
                .ReturnsAsync((string?)null);
            local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<Song, string>((song, path) =>
                {
                    Assert.Equal("deezer", song.ExternalProvider);
                    Assert.Equal("track-1", song.ExternalId);
                    registeredPath = path;
                })
                .Returns(Task.CompletedTask);
            var service = CreateService(root, "Cache", local.Object);
            using var response = Response(HttpStatusCode.PartialContent, [1, 2, 3, 4]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);

            await service.WrapAsync(
                ProviderStream(response),
                "deezer",
                "track-1",
                ProviderAudioQuality.Any,
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
                "deezer",
                "track-2",
                ProviderAudioQuality.Any,
                headOnly: false,
                () => Task.FromResult<Song?>(null),
                CancellationToken.None);

            await using (var stream = await response.Content.ReadAsStreamAsync())
                Assert.Equal(1, stream.ReadByte());

            Assert.Empty(Directory.Exists(root)
                ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                : []);
            local.Verify(item => item.RegisterDownloadedSongAsync(
                It.IsAny<Song>(), It.IsAny<string>()), Times.Never);
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
                    "deezer",
                    "track-3",
                    ProviderAudioQuality.Any,
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
