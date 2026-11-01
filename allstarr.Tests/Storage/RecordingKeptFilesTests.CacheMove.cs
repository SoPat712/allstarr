using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task CachedKeepMovesBytesAndLyricsWithoutDownloadingAndPreservesOpenReads()
    {
        var source = await SeedKeepCacheAsync();
        Assert.True(new PhysicalManagedFileOperations().TryGetFileIdentity(source, out var before));
        await using var opened = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var first = opened.ReadByte();
        var claim = await AcceptKeepAsync();
        var kept = Assert.IsType<KeptRecordingFile>(await ExplicitKeepService().RetainRequestedKeepAsync(claim));
        Assert.False(File.Exists(source));
        Assert.True(new PhysicalManagedFileOperations().TryGetFileIdentity(kept.Path, out var after));
        Assert.Equal(before.DeviceId, after.DeviceId);
        Assert.Equal(before.FileId.Split(':')[0], after.FileId.Split(':')[0]);
        var metadata = await Service().FindSongAsync(Actor(other), recordingId.ToString("D"));
        Assert.Equal("Cached title", metadata!.Title);
        Assert.Equal("Cached artist", metadata.Artist);
        Assert.Equal("Cached album", metadata.Album);
        Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
        Assert.Equal("[00:01.000]Cached words", await File.ReadAllTextAsync(Path.ChangeExtension(kept.Path, ".lrc")));
        using var remaining = new MemoryStream();
        remaining.WriteByte((byte)first);
        await opened.CopyToAsync(remaining);
        Assert.Equal(audio, remaining.ToArray());
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Single(await db.ManagedFiles.ToArrayAsync());
        Assert.Equal(1, (await db.ManagedFiles.SingleAsync()).ReferenceCount);
        Assert.Equal(kept.Path, (await db.DownloadedSongMappings.SingleAsync()).LocalPath);
        Assert.Equal(kept.ManagedFileId, (await Service().FindAsync(Actor(other), recordingId))!.ManagedFileId);
        Assert.Equal(kept.ManagedFileId, (await ExplicitKeepService().RetainRequestedKeepAsync(claim))!.ManagedFileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedKeepMissingOrInvalidBytesUseTheExistingArtifactPath(bool invalid)
    {
        var source = await SeedKeepCacheAsync();
        var claim = await AcceptKeepAsync();
        if (invalid) await File.WriteAllBytesAsync(source, "invalid audio"u8.ToArray());
        else File.Delete(source);
        Assert.Null(await ExplicitKeepService(invalid ? new allstarr.Core.Music.ProviderAudioVerifier() : null).RetainRequestedKeepAsync(claim));
        var kept = await ExplicitKeepService().PublishRequestedKeepAsync(claim, await CreateKeepArtifactAsync(claim));
        Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
    }

    [Fact]
    public async Task CachedKeepDoublePressSharesOneJobAndReportsMoving()
    {
        await SeedKeepCacheAsync();
        var first = Assert.IsType<AcceptedResult>(await KeepController(true).PromoteCachedDownload("cached.flac"));
        var second = Assert.IsType<AcceptedResult>(await KeepController(true).PromoteCachedDownload("cached.flac"));
        var a = JsonSerializer.SerializeToElement(first.Value);
        var b = JsonSerializer.SerializeToElement(second.Value);
        Assert.Equal(a.GetProperty("jobId").GetGuid(), b.GetProperty("jobId").GetGuid());
        Assert.Equal("Moving to Kept…", a.GetProperty("message").GetString());
        Assert.True(b.GetProperty("queued").GetBoolean());
    }

    [Fact]
    public async Task CachedKeepMovesEveryMappingOfTheSameFile()
    {
        var source = await SeedKeepCacheAsync();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.DownloadedSongMappings.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ProviderId = "deezer",
                ExternalId = "shared-track",
                LocalPath = source,
                Title = "Shared title",
                Artist = "Artist",
                DownloadedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var kept = Assert.IsType<KeptRecordingFile>(await ExplicitKeepService().RetainRequestedKeepAsync(await AcceptKeepAsync()));
        var mapping = await new EfDownloadedSongMappingStore(factory).FindAsync("deezer", "shared-track");
        Assert.Equal(kept.Path, mapping!.LocalPath);
        Assert.Equal(audio, await File.ReadAllBytesAsync(mapping.LocalPath));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedKeepRecoversAfterRenameOrPublicationBeforeCleanup(bool committed)
    {
        var source = await SeedKeepCacheAsync();
        var claim = await AcceptKeepAsync();
        placementBoundary = committed
            ? () => placementBoundary = () => throw new IOException("Interrupted cleanup")
            : () => throw new IOException("Interrupted publication");
        await Assert.ThrowsAsync<IOException>(() => ExplicitKeepService().RetainRequestedKeepAsync(claim));
        await using (var db = new AllstarrDbContext(database.Options))
        {
            Assert.Single(await db.ManagedFiles.ToArrayAsync());
            Assert.Equal(committed ? 1 : 0, await db.Set<RecordingKeptRouteEntity>().CountAsync());
            var row = await db.DownloadedSongMappings.SingleAsync();
            Assert.Equal(audio, await File.ReadAllBytesAsync(row.LocalPath));
            Assert.Equal(1, (await db.ManagedFiles.SingleAsync()).ReferenceCount);
        }
        await using (var finish = new AllstarrDbContext(database.Options))
            await finish.Jobs.Where(item => item.Id == jobId).ExecuteUpdateAsync(set => set.SetProperty(item => item.State, DurableJobState.Succeeded));
        await queue.RecoverInterruptedAsync();
        var retry = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("resume-move", [RecordingKeptFiles.ExplicitKeepJobType]));
        var kept = Assert.IsType<KeptRecordingFile>(await ExplicitKeepService().RetainRequestedKeepAsync(retry));
        Assert.False(File.Exists(source));
        Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
        Assert.NotNull(await Service().FindAsync(Actor(other), recordingId));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Single(await verify.ManagedFiles.ToArrayAsync());
        Assert.Equal(1, (await verify.ManagedFiles.SingleAsync()).ReferenceCount);
    }

    [Fact]
    public async Task CachedKeepCompletedRequestReportsMovedAndStorageTotalsFollowTheFile()
    {
        await SeedKeepCacheAsync();
        var claim = await AcceptKeepAsync();
        await ExplicitKeepService().RetainRequestedKeepAsync(claim);
        var done = Assert.IsType<OkObjectResult>(await KeepController(true).PromoteCachedDownload("cached.flac"));
        var body = JsonSerializer.SerializeToElement(done.Value);
        Assert.False(body.GetProperty("queued").GetBoolean());
        Assert.Equal("Moved to Kept.", body.GetProperty("message").GetString());
        var cached = Assert.IsType<OkObjectResult>(await KeepController(true).GetDownloads("cache"));
        var kept = Assert.IsType<OkObjectResult>(await KeepController(true).GetDownloads("kept"));
        Assert.Equal(0, JsonSerializer.SerializeToElement(cached.Value).GetProperty("totalSize").GetInt64());
        Assert.Equal(audio.Length, JsonSerializer.SerializeToElement(kept.Value).GetProperty("totalSize").GetInt64());
        Assert.Empty(JsonSerializer.SerializeToElement(cached.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("files").EnumerateArray());
        Assert.Single(JsonSerializer.SerializeToElement(kept.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task CachedKeepOriginalMappingStillReplaysWithinItsAccountScope()
    {
        var source = await SeedKeepCacheAsync();
        Assert.True(new PhysicalManagedFileOperations().TryGetFileIdentity(source, out var identity));
        var scope = new DownloadedSongMappingScope(account, allstarr.Core.Capabilities.ProviderAudioQuality.Any,
            DownloadedSongRepresentation.Original, 7);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var row = await db.DownloadedSongMappings.SingleAsync();
            row.ScopeKey = scope.Key;
            row.CacheIdentityJson = JsonSerializer.Serialize(new
            {
                Representation = "Original",
                SourceAccountRevision = 7,
                Device = identity.DeviceId,
                File = identity.FileId,
                Length = audio.Length,
                Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(audio))
            });
            await db.SaveChangesAsync();
        }
        var kept = Assert.IsType<KeptRecordingFile>(await ExplicitKeepService().RetainRequestedKeepAsync(await AcceptKeepAsync()));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build();
        await using var cache = new allstarr.Services.Common.ManagedTrackCacheService(configuration,
            Microsoft.Extensions.Options.Options.Create(new allstarr.Models.Settings.SubsonicSettings
            { StorageMode = allstarr.Models.Settings.StorageMode.Cache }),
            new Moq.Mock<allstarr.Services.Local.ILocalLibraryService>(Moq.MockBehavior.Strict).Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Services.Common.ManagedTrackCacheService>.Instance,
            mappings: new EfDownloadedSongMappingStore(factory));
        var replay = await cache.TryOpenAsync(new("deezer", allstarr.Core.Capabilities.ProviderResourceKind.Track, "source-track"),
            scope, default, 7, originalDownload: true, retainOriginalMedia: true);
        Assert.NotNull(replay);
        using var response = replay.Response;
        Assert.Equal(audio, await response.Content.ReadAsByteArrayAsync());
        Assert.Null(await cache.TryOpenAsync(new("deezer", allstarr.Core.Capabilities.ProviderResourceKind.Track, "source-track"),
            scope with { ProviderAccountId = Guid.CreateVersion7() }, default, 7, originalDownload: true, retainOriginalMedia: true));
        Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
    }

    [Fact]
    public async Task CachedKeepSurvivesCacheClearingAndCanBeDeletedFromKept()
    {
        await SeedKeepCacheAsync();
        var service = ExplicitKeepService();
        var claim = await AcceptKeepAsync();
        var kept = Assert.IsType<KeptRecordingFile>(await service.RetainRequestedKeepAsync(claim));
        await queue.CompleteAsync(claim, DurableJobCompletion.Success());
        Assert.IsType<OkObjectResult>(await KeepController(true).DeleteAllDownloads("cache"));
        Assert.True(File.Exists(kept.Path));
        var context = await DeletionAdministratorAsync();
        var preview = await service.PreviewDeletionAsync(context, kept.ManagedFileId);
        await service.RequestDeletionAsync(context, DeleteConfirmation(preview));
        var deletion = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("delete-moved", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await service.CleanupAsync(deletion)).Kind);
        Assert.False(File.Exists(kept.Path));
        Assert.False(File.Exists(Path.ChangeExtension(kept.Path, ".lrc")));
        Assert.Null(await Service().FindAsync(Actor(other), recordingId));
    }

    [MediaToolsFact]
    public async Task CachedKeepVerifiesAndMovesOriginalAudioWithoutAssumingItsCodecFromTheExtension()
    {
        var original = await SeedKeepCacheAsync();
        var wave = Path.Combine(root, "input.wav");
        var source = Path.ChangeExtension(original, ".m4a");
        await File.WriteAllBytesAsync(wave, PlaybackAudioFixtures.Wave());
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var value in new[] { "-v", "error", "-i", wave, "-c:a", "alac", "-threads", "1", source })
            start.ArgumentList.Add(value);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }
        File.Delete(original);
        var bytes = await File.ReadAllBytesAsync(source);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.DownloadedSongMappings.SingleAsync()).LocalPath = source;
            await db.SaveChangesAsync();
        }
        Assert.IsType<AcceptedResult>(await KeepController(true).PromoteCachedDownload("cached.m4a"));
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("original-keep", [RecordingKeptFiles.ExplicitKeepJobType]));
        var kept = Assert.IsType<KeptRecordingFile>(await ExplicitKeepService(new allstarr.Core.Music.ProviderAudioVerifier())
            .RetainRequestedKeepAsync(claim));
        Assert.False(File.Exists(source));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(kept.Path));
        var metadata = await Service().FindSongAsync(Actor(other), recordingId.ToString("D"));
        Assert.Equal("alac", metadata!.Codec);
        Assert.Equal(TimeSpan.FromSeconds(8), metadata.PreciseDuration);
    }

    [Fact]
    public async Task CachedKeepMissingFileAtAcceptanceStillQueuesTheScopedDownload()
    {
        var source = await SeedKeepCacheAsync();
        File.Delete(source);
        var claim = await AcceptKeepAsync();
        Assert.Null(await ExplicitKeepService().RetainRequestedKeepAsync(claim));
        var kept = await ExplicitKeepService().PublishRequestedKeepAsync(claim, await CreateKeepArtifactAsync(claim));
        Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
    }

}
