using allstarr.Core.Artwork;
using allstarr.Core.Playback;
using System.Buffers.Binary;
using allstarr.Controllers;
using allstarr.Core.Downloads;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class DownloadArtworkTests
{
    private const string Provider = "apple-musickit";
    private static readonly byte[] Cover = CreateCover();
    private readonly Mock<IProtocolProviderGateway> gateway = new();
    private readonly Mock<IMediaAssetResolver> media = new();
    private readonly List<string> requested = [];
    private readonly ProtocolExecutionContext protocol = new(ProtocolKind.Jellyfin, "home", "listener",
        null, "artwork", DateTimeOffset.UtcNow.AddMinutes(1), default);

    public DownloadArtworkTests()
    {
        media.Setup(item => item.ResolveAsync(It.IsAny<MediaAssetIdentity>(),
                It.IsAny<Func<CancellationToken, Task<MediaAssetSource?>>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (MediaAssetIdentity identity, Func<CancellationToken, Task<MediaAssetSource?>> fetch, int maximum, CancellationToken token) =>
            {
                Assert.Equal(8 * 1024 * 1024, maximum);
                var source = await fetch(token);
                return source == null ? null : new ResolvedMediaAsset(source.Bytes, source.ContentType, "fixture", null, null, false);
            });
    }

    [Theory]
    [InlineData("/api/admin/providers/apple-musickit/cover")]
    [InlineData("/images/providers/apple-musickit-cover.png")]
    public async Task Artwork_RefusesProviderLogos(string logo)
    {
        SongAlbum(logo);
        Assert.Null(await Read([]));
        Assert.Empty(requested);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task Artwork_EmbeddedCoverWinsWithoutCatalogOrCachedLogo(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"download-cover-{Guid.NewGuid():N}.{extension}");
        try
        {
            WriteAudio(path, extension);
            using (var file = TagLib.File.Create(path, TagLib.ReadStyle.None))
            {
                file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(Cover)) { MimeType = "image/png", Type = TagLib.PictureType.FrontCover }];
                file.Save();
            }
            var cover = Assert.IsType<MediaAssetSource>(await Read([path]));
            Assert.Equal(Cover, cover.Bytes);
            Assert.Equal("image/png", cover.ContentType);
            gateway.VerifyNoOtherCalls();
            media.VerifyNoOtherCalls();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("prima")]
    [InlineData("ext-apple-musickit-album-prima")]
    [InlineData("ext-apple-download-album-prima")]
    public async Task Artwork_UsesSongsAlbumWhenNoEmbeddedCoverExists(string albumId)
    {
        SongAlbum("https://covers.example.test/prima.png", albumId);
        var cover = Assert.IsType<MediaAssetSource>(await Read(["missing.flac"]));
        Assert.Equal(Cover, cover.Bytes);
        Assert.Equal(["https://covers.example.test/prima.png"], requested);
        gateway.Verify(item => item.GetAlbumAsync(protocol, Provider, "prima"), Times.Once);
    }

    [Fact]
    public async Task Artwork_TriesFreshAlbumAfterStoredAlbumLogo()
    {
        SongAlbum("https://covers.example.test/prima.png");
        gateway.Setup(item => item.GetStoredAlbumAsync(protocol, Provider, "prima"))
            .ReturnsAsync(new Album { CoverArtUrl = "/api/admin/providers/apple-musickit/cover" });
        Assert.NotNull(await Read([]));
        Assert.Single(requested);
    }

    [Fact]
    public async Task Artwork_ReturnsNothingWithoutAlbumAndIgnoresTrackLogo()
    {
        gateway.Setup(item => item.GetSongAsync(protocol, Provider, "song"))
            .ReturnsAsync(new Song { CoverArtUrl = "/api/admin/providers/apple-musickit/cover" });
        Assert.Null(await Read([]));
        Assert.Empty(requested);
    }

    [Fact]
    public async Task Artwork_OversizedEmbeddedCoverFallsBackToAlbum()
    {
        var path = Path.Combine(Path.GetTempPath(), $"download-cover-{Guid.NewGuid():N}.flac");
        try
        {
            WriteAudio(path, "flac");
            using (var file = TagLib.File.Create(path, TagLib.ReadStyle.None))
            {
                file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(new byte[8 * 1024 * 1024 + 1])) { MimeType = "image/png" }];
                file.Save();
            }
            SongAlbum("https://covers.example.test/prima.png");
            Assert.Equal(Cover, (await Read([path]))!.Bytes);
            Assert.Single(requested);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("kept")]
    [InlineData("cache")]
    public async Task Artwork_RouteReadsIndexedEmbeddedCoverAndUsesReleaseCachePolicy(string storage)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var path = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, $"{storage}.flac");
        WriteAudio(path, "flac");
        using (var file = TagLib.File.Create(path, TagLib.ReadStyle.None))
        {
            file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(Cover)) { MimeType = "image/png" }];
            file.Save();
        }
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.DownloadedSongMappings.Add(new()
            {
                Id = Guid.NewGuid(),
                ProviderId = Provider,
                ExternalId = "song",
                LocalPath = path,
                DownloadedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>();
        factory.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AllstarrDbContext(database.Options));
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(protocol, BackendLibraryAccess.Unavailable));
        var controller = new DownloadActivityController([], [], media.Object,
            NullLogger<DownloadActivityController>.Instance, access.Object, contextFactory: factory.Object,
            providerGateway: gateway.Object, httpClients: Mock.Of<IHttpClientFactory>())
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "listener",
            UserName = "Listener",
            AllstarrUserId = Guid.NewGuid(),
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        var result = Assert.IsType<FileContentResult>(await controller.GetPlaybackArtwork($"ext-{Provider}-song-song", default));
        Assert.Equal(Cover, result.FileContents);
        Assert.Equal("private, max-age=604800", controller.Response.Headers.CacheControl);
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Artwork_SongWithoutAlbumKeepsItsOwnCover()
    {
        gateway.Setup(item => item.GetSongAsync(protocol, Provider, "song"))
            .ReturnsAsync(new Song { CoverArtUrl = "https://covers.example.test/song.png" });
        Assert.Equal(Cover, (await Read([]))!.Bytes);
        Assert.Equal(["https://covers.example.test/song.png"], requested);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Artwork_UsesResolverSongCoverAndSkipsResolverLogo(bool logo)
    {
        if (logo) SongAlbum("https://covers.example.test/prima.png");
        var resolver = new Mock<IPlaybackMetadataResolver>();
        resolver.Setup(item => item.ResolveAsync($"ext-{Provider}-song-song", protocol, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaybackTrackMetadata("Song", "Artist", null,
                logo ? "/api/admin/providers/apple-musickit/cover" : "https://covers.example.test/song.png"));
        Assert.Equal(Cover, (await Read([], [resolver.Object]))!.Bytes);
        Assert.Equal([logo ? "https://covers.example.test/prima.png" : "https://covers.example.test/song.png"], requested);
        if (!logo) gateway.Verify(item => item.GetSongAsync(protocol, Provider, "song"), Times.Never);
    }

    [Fact]
    public async Task Artwork_SecondRequestUsesTrackCacheWithoutCatalogLookup()
    {
        gateway.Setup(item => item.GetSongAsync(protocol, Provider, "song"))
            .ReturnsAsync(new Song { CoverArtUrl = "https://covers.example.test/song.png" });
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new CoverHandler(requested)));
        var cachedMedia = new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance);
        var reader = new DownloadArtworkReader(gateway.Object, cachedMedia, clients.Object, NullLogger.Instance);
        var owner = Guid.NewGuid();
        await cachedMedia.ResolveAsync(new(owner, null, Provider, "track", $"ext-{Provider}-song-song", Width: 96),
            _ => Task.FromResult<MediaAssetSource?>(new(Cover, "image/png")), 8 * 1024 * 1024);
        Assert.NotNull(await reader.ReadAsync(owner, protocol, Provider, "song", [], default));
        gateway.Verify(item => item.GetSongAsync(protocol, Provider, "song"), Times.Once);
        gateway.Invocations.Clear();
        Assert.NotNull(await reader.ReadAsync(owner, protocol, Provider, "song", [], default));
        gateway.VerifyNoOtherCalls();
        Assert.Single(requested);
    }

    private void SongAlbum(string cover, string albumId = "prima")
    {
        gateway.Setup(item => item.GetSongAsync(protocol, Provider, "song"))
            .ReturnsAsync(new Song { AlbumId = albumId, CoverArtUrl = "/api/admin/providers/apple-musickit/cover" });
        gateway.Setup(item => item.GetAlbumAsync(protocol, Provider, "prima"))
            .ReturnsAsync(new Album { CoverArtUrl = cover });
    }

    private Task<MediaAssetSource?> Read(string[] paths, IPlaybackMetadataResolver[]? resolvers = null)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new CoverHandler(requested)));
        return new DownloadArtworkReader(gateway.Object, media.Object, clients.Object, NullLogger.Instance, resolvers)
            .ReadAsync(Guid.NewGuid(), protocol, Provider, "song", paths, default);
    }

    private sealed class CoverHandler(List<string> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            requested.Add(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Cover) };
            response.Content.Headers.ContentType = new("image/png");
            return Task.FromResult(response);
        }
    }

    private static byte[] CreateCover()
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(SKColors.DarkBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static void WriteAudio(string path, string extension)
    {
        using var output = File.Create(path);
        if (extension == "flac")
        {
            output.Write("fLaC"u8);
            output.Write(new byte[] { 0x80, 0, 0, 34 });
            var info = new byte[34];
            info[0] = 0x10; info[2] = 0x10;
            BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10, 8), ((ulong)8_000 << 44) | ((ulong)15 << 36) | 8_000);
            output.Write(info);
        }
        else if (extension == "mp3")
        {
            var frame = new byte[417];
            new byte[] { 0xff, 0xfb, 0x90, 0x00 }.CopyTo(frame, 0);
            for (var index = 0; index < 4; index++) output.Write(frame);
        }
        else
        {
            output.Write(new byte[] { 0, 0, 0, 20 }); output.Write("ftypM4A "u8);
            output.Write(new byte[] { 0, 0, 0, 0 }); output.Write("M4A "u8);
            output.Write(new byte[] { 0, 0, 0, 8 }); output.Write("moov"u8);
            output.Write(new byte[] { 0, 0, 0, 8 }); output.Write("mdat"u8);
        }
    }
}
