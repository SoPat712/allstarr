using System.IO.Compression;
using Moq;
using allstarr.Controllers;
using allstarr.Core.Downloads;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Lyrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public class DownloadsControllerLyricsArchiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpicyLyricsCannotEnterGeneratedSingleOrBulkExports(bool bulk)
    {
        var root = CreateTestRoot();
        var downloads = Path.Combine(root, "downloads");
        var folder = Path.Combine(downloads, "kept", "Artist");
        Directory.CreateDirectory(folder);
        var audio = Path.Combine(folder, "track.flac");
        using (var output = File.Create(audio))
        {
            output.Write(System.Text.Encoding.ASCII.GetBytes("fLaC"));
            output.Write(new byte[] { 0x80, 0, 0, 34 });
            var info = new byte[34];
            info[0] = 0x10; info[2] = 0x10;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10, 8),
                ((ulong)8_000 << 44) | ((ulong)15 << 36) | 8_000);
            output.Write(info);
        }
        try
        {
            using (var tag = TagLib.File.Create(audio)) { tag.Tag.Performers = ["Artist"]; tag.Tag.Title = "Fixture"; tag.Save(); }
            var resolver = new Moq.Mock<allstarr.Core.Protocols.IProtocolLyricsResolver>(Moq.MockBehavior.Strict);
            resolver.Setup(item => item.FindAsync(Moq.It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), Moq.It.IsAny<Song>(), Moq.It.IsAny<string>(),
                    Moq.It.Is<allstarr.Core.Protocols.ProtocolLyricsSources>(options => options.ForPermanentSidecar && options.HouseholdOnly)))
                .ReturnsAsync(new allstarr.Models.Lyrics.LyricsInfo { Source = "spicylyrics", PlainLyrics = "Must not enter an export" });
            var writer = new KeptLyricsSidecarService(resolver.Object, NullLogger<KeptLyricsSidecarService>.Instance);
            var controller = CreateController(downloads, writer);
            controller.HttpContext.Items[allstarr.Core.Protocols.ProtocolExecutionContextFactory.HttpContextItemKey] = new allstarr.Core.Protocols.ProtocolExecutionContext(
                allstarr.Core.Protocols.ProtocolKind.Jellyfin, "backend", "principal", new allstarr.Core.Identity.AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "principal", "Listener", false),
                "export", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var response = bulk ? await controller.DownloadAllFiles() : await controller.DownloadFile("Artist/track.flac");
            var file = Assert.IsType<FileStreamResult>(response);
            using var stream = file.FileStream;
            if (bulk)
            {
                var entries = ReadArchiveEntries(stream);
                Assert.Contains(entries, name => name.EndsWith("track.flac", StringComparison.Ordinal));
                Assert.DoesNotContain(entries, name => name.EndsWith(".lrc", StringComparison.Ordinal));
            }
            else Assert.NotEqual("application/zip", file.ContentType);
            Assert.False(File.Exists(Path.ChangeExtension(audio, ".lrc")));
            resolver.VerifyAll();
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task DownloadFile_WithLyricsSidecar_ReturnsZipContainingAudioAndLrc()
    {
        var testRoot = CreateTestRoot();
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var artistDir = Path.Combine(downloadsRoot, "kept", "Artist");
        var audioPath = Path.Combine(artistDir, "track.mp3");

        Directory.CreateDirectory(artistDir);
        await File.WriteAllTextAsync(audioPath, "audio-data");

        try
        {
            var controller = CreateController(downloadsRoot, new FakeKeptLyricsSidecarService(createSidecar: true));

            var result = await controller.DownloadFile("Artist/track.mp3");

            var fileResult = Assert.IsType<FileStreamResult>(result);
            Assert.Equal("application/zip", fileResult.ContentType);
            Assert.Equal("track.zip", fileResult.FileDownloadName);

            var entries = ReadArchiveEntries(fileResult.FileStream);
            Assert.Contains("track.mp3", entries);
            Assert.Contains("track.lrc", entries);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task DownloadAllFiles_BackfillsLyricsSidecarsIntoArchive()
    {
        var testRoot = CreateTestRoot();
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var artistDir = Path.Combine(downloadsRoot, "kept", "Artist", "Album");
        var audioPath = Path.Combine(artistDir, "01 - track.mp3");

        Directory.CreateDirectory(artistDir);
        await File.WriteAllTextAsync(audioPath, "audio-data");

        try
        {
            var controller = CreateController(downloadsRoot, new FakeKeptLyricsSidecarService(createSidecar: true));

            var result = await controller.DownloadAllFiles();

            var fileResult = Assert.IsType<FileStreamResult>(result);
            Assert.Equal("application/zip", fileResult.ContentType);

            var entries = ReadArchiveEntries(fileResult.FileStream);
            Assert.Contains(Path.Combine("Artist", "Album", "01 - track.mp3").Replace('\\', '/'), entries);
            Assert.Contains(Path.Combine("Artist", "Album", "01 - track.lrc").Replace('\\', '/'), entries);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task DeleteDownload_RemovesAdjacentLyricsSidecar()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var testRoot = CreateTestRoot();
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var artistDir = Path.Combine(downloadsRoot, "kept", "Artist");
        var audioPath = Path.Combine(artistDir, "track.mp3");
        var sidecarPath = Path.Combine(artistDir, "track.lrc");

        Directory.CreateDirectory(artistDir);
        File.WriteAllText(audioPath, "audio-data");
        File.WriteAllText(sidecarPath, "[00:00.00]lyrics");

        try
        {
            await using (var db = new AllstarrDbContext(database.Options))
            {
                db.DownloadedSongMappings.Add(new DownloadedSongMappingEntity
                {
                    Id = Guid.CreateVersion7(),
                    ProviderId = "fixture",
                    ExternalId = "track",
                    LocalPath = audioPath,
                    Title = "Track",
                    Artist = "Artist",
                    Album = "Album",
                    DownloadedAt = DateTimeOffset.UtcNow,
                    Revision = 1
                });
                await db.SaveChangesAsync();
            }
            var controller = CreateController(
                downloadsRoot,
                new FakeKeptLyricsSidecarService(createSidecar: false),
                new DbFactory(database.Options));

            var result = await controller.DeleteDownload("Artist/track.mp3");

            Assert.IsType<OkObjectResult>(result);
            Assert.False(File.Exists(audioPath));
            Assert.False(File.Exists(sidecarPath));
            await using var verification = new AllstarrDbContext(database.Options);
            Assert.Empty(await verification.DownloadedSongMappings.ToListAsync());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static DownloadsController CreateController(
        string downloadsRoot,
        IKeptLyricsSidecarService? keptLyricsSidecarService = null,
        IDbContextFactory<AllstarrDbContext>? contextFactory = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = downloadsRoot
            })
            .Build();

        return new DownloadsController(
            NullLogger<DownloadsController>.Instance,
            config,
            keptLyricsSidecarService,
            contextFactory: contextFactory)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static HashSet<string> ReadArchiveEntries(Stream archiveStream)
    {
        archiveStream.Position = 0;
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        return zip.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }

    private sealed class FakeKeptLyricsSidecarService : IKeptLyricsSidecarService
    {
        private readonly bool _createSidecar;

        public FakeKeptLyricsSidecarService(bool createSidecar)
        {
            _createSidecar = createSidecar;
        }

        public string GetSidecarPath(string audioFilePath)
        {
            return Path.ChangeExtension(audioFilePath, ".lrc");
        }

        public Task<string?> EnsureSidecarAsync(
            string audioFilePath,
            Song? song = null,
            string? externalProvider = null,
            string? externalId = null,
            CancellationToken cancellationToken = default,
            allstarr.Core.Protocols.ProtocolExecutionContext? protocol = null)
        {
            var sidecarPath = GetSidecarPath(audioFilePath);
            if (_createSidecar)
            {
                File.WriteAllText(sidecarPath, "[00:00.00]lyrics");
                return Task.FromResult<string?>(sidecarPath);
            }

            return Task.FromResult<string?>(null);
        }
    }
}
