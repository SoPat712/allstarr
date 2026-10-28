using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageListingUsesExactKeptMetadataAndDownloadedSourceWithoutCacheMappings(bool conflictingMapping)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Protocols", "audio-delivery.json")));
        var bytes = Convert.FromBase64String(fixture.RootElement.GetProperty("flac").GetProperty("bytes").GetString()!);
        var workspace = await artifacts.CreateWorkspaceAsync(new(owner, jobId, "apple-musickit", null, "storage-metadata"));
        await using var input = new MemoryStream(bytes);
        var written = await artifacts.WriteAsync(new(workspace.Reference, jobId, "apple-musickit", "source.flac", input, bytes.Length));
        var source = await artifacts.ResolveAsync(workspace.Reference,
            new(written.ArtifactId, written.Sha256, written.SizeBytes, new("audio/flac", "flac", "flac"), true),
            sourceTrack: new("apple-musickit", allstarr.Core.Capabilities.ProviderResourceKind.Track, "1780787517"));
        ManagedFileRecord file;
        await using (var scope = services.CreateAsyncScope())
            file = (await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
                new(placement.RootId, placement.RootPath, null), source.SourcePath, "{artist}/{album}/{title}",
                new("Unknown - Check", "FLO", "Access All Areas"), null,
                ManagedFileScopeKey.Create(null, placement.RootId), true, ExpectedContentSha256: source.ContentSha256,
                ExpectedLength: source.Length)
            { ReferenceKey = "heart:storage-fixture", DestinationIsImmutable = true })).File;
        using (var tags = TagLib.File.Create(file.CanonicalPath))
        {
            Assert.True(string.IsNullOrEmpty(tags.Tag.Title));
            Assert.Empty(tags.Tag.Performers);
        }
        await using (var db = factory.CreateDbContext())
        {
            db.Add(new RecordingKeptRouteEntity
            {
                CanonicalRecordingId = recordingId,
                ManagedFileId = file.Id,
                PublishedByUserId = owner,
                PublicationJobId = jobId,
                SourceArtifactId = source.Id,
                DisplayMetadataJson = "{\"Title\":\"Check\",\"Artists\":[\"FLO\"],\"Album\":\"Access All Areas\",\"DurationMilliseconds\":165106}"
            });
            if (conflictingMapping) db.DownloadedSongMappings.Add(new()
            {
                Id = Guid.NewGuid(),
                LocalPath = file.CanonicalPath,
                Title = "Wrong mapping title",
                Artist = "Wrong artist",
                Album = "Wrong album",
                ProviderId = "deezer",
                ExternalId = "matched-alternative"
            });
            await db.SaveChangesAsync();
            Assert.Null((await db.Set<ProviderDownloadArtifactEntity>().SingleAsync(item => item.Id == source.Id)).ManagedFileId);
        }
        await DisableSourceAccountAsync();

        var row = Assert.Single((await StorageMetadataRowsAsync()).EnumerateArray());

        Assert.Equal("Check", row.GetProperty("title").GetString());
        Assert.Equal("FLO", row.GetProperty("artist").GetString());
        Assert.Equal("Access All Areas", row.GetProperty("album").GetString());
        Assert.Equal(165106, row.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("apple-musickit", row.GetProperty("provider").GetString());
        Assert.Equal("1780787517", row.GetProperty("externalId").GetString());
        Assert.Equal("/api/admin/downloads/artwork/ext-apple-musickit-song-1780787517", row.GetProperty("artworkUrl").GetString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file.CanonicalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageListingUsesOnlyExactPathMappingsBeforeFilenameFallback(bool exactMapping)
    {
        var folder = Path.Combine(placement.RootPath, "Path artist", "Path album");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "04 - Path title [deezer-path-id].flac");
        await File.WriteAllBytesAsync(path, audio);
        await using (var db = factory.CreateDbContext())
        {
            db.DownloadedSongMappings.Add(new()
            {
                Id = Guid.NewGuid(),
                LocalPath = exactMapping ? path : path + ".other",
                Title = "Mapped title",
                Artist = "Mapped artist",
                Album = "Mapped album",
                ProviderId = "apple-musickit",
                ExternalId = "mapped-id"
            });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single((await StorageMetadataRowsAsync()).EnumerateArray());

        Assert.Equal(exactMapping ? "Mapped title" : "Path title", row.GetProperty("title").GetString());
        Assert.Equal(exactMapping ? "Mapped artist" : "Path artist", row.GetProperty("artist").GetString());
        Assert.Equal(exactMapping ? "Mapped album" : "Path album", row.GetProperty("album").GetString());
        Assert.Equal(exactMapping ? "apple-musickit" : "deezer", row.GetProperty("provider").GetString());
        Assert.Equal(exactMapping ? "mapped-id" : "path-id", row.GetProperty("externalId").GetString());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"Title\":\" \",\"Artists\":[],\"Album\":null}")]
    public async Task StorageListingFallsBackForMissingDisplayFieldsWithoutChangingDownloadedProvenance(string snapshot)
    {
        var published = await Service().PublishAsync(Request());
        await using (var db = factory.CreateDbContext())
        {
            (await db.Set<RecordingKeptRouteEntity>().SingleAsync()).DisplayMetadataJson = snapshot;
            db.DownloadedSongMappings.Add(new()
            {
                Id = Guid.NewGuid(),
                LocalPath = published.Path,
                Title = "Mapped title",
                Artist = "Mapped artist",
                Album = "Mapped album",
                ProviderId = "apple-musickit",
                ExternalId = "different-source"
            });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single((await StorageMetadataRowsAsync()).EnumerateArray());

        Assert.Equal("Mapped title", row.GetProperty("title").GetString());
        Assert.Equal("Mapped artist", row.GetProperty("artist").GetString());
        Assert.Equal("Mapped album", row.GetProperty("album").GetString());
        Assert.Equal("deezer", row.GetProperty("provider").GetString());
        Assert.Equal("source-track", row.GetProperty("externalId").GetString());
    }

    [Theory]
    [InlineData(null, "Check.flac")]
    [InlineData(4, "04 - Check.flac")]
    public async Task PublicationRetainsCatalogFieldsAndOmitsMissingTrackPrefix(int? track, string filename)
    {
        placement.PathTemplate = "{albumArtist}/{album}/{track:00} - {title}{extension}";
        var published = await Service().PublishAsync(Request() with
        {
            Track = new("Check", "FLO", "Access All Areas"),
            Metadata = new("Check", ["FLO"], "Access All Areas", Track: track, AlbumArtist: "FLO and friends")
        });

        Assert.Equal(Path.Combine(placement.RootPath, "FLO and friends", "Access All Areas", filename), published.Path);
        await using var db = factory.CreateDbContext();
        var metadata = JsonSerializer.Deserialize<KeptRecordingMetadata>(
            (await db.Set<RecordingKeptRouteEntity>().SingleAsync()).DisplayMetadataJson!)!;
        Assert.Equal(track, metadata.Track);
        Assert.Equal("FLO and friends", metadata.AlbumArtist);
        Assert.Equal("deezer", metadata.Artwork?.ProviderId);
        Assert.Equal("source-track", metadata.Artwork?.ExternalId);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
        var legacy = JsonSerializer.Deserialize<KeptRecordingMetadata>("{\"Title\":\"Old title\",\"Artists\":[\"Artist\"]}")!;
        Assert.Null(legacy.Track);
        Assert.Null(legacy.AlbumArtist);
        Assert.Null(legacy.Artwork);
    }

    private async Task<JsonElement> StorageMetadataRowsAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = root }).Build();
        var controller = new DownloadsController(NullLogger<DownloadsController>.Instance, configuration,
            contextFactory: factory, placement: placement)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var response = Assert.IsType<OkObjectResult>(await controller.GetDownloads());
        return JsonSerializer.SerializeToElement(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("files");
    }
}
