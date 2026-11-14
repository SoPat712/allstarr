using allstarr.Core.Caching;
using allstarr.Core.Playback;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionAcquisition_MeasuresAStoredKeptCopyWithoutDownloading(bool imported)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AppleProgressive", "alac-16-44100.flac"));
        await File.WriteAllBytesAsync(artifact.SourcePath, bytes);
        await using (var db = factory.CreateDbContext())
        {
            var source = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
            source.ContentSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            source.Length = bytes.Length;
            await db.SaveChangesAsync();
        }
        var kept = Service(new allstarr.Core.Music.ProviderAudioVerifier());
        KeptRecordingFile copy;
        if (imported)
        {
            var legacyRoot = Path.Combine(root, "legacy");
            Directory.CreateDirectory(legacyRoot);
            var path = Path.Combine(legacyRoot, "song.flac");
            await File.WriteAllBytesAsync(path, bytes);
            await using var scope = services.CreateAsyncScope();
            var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
            var identity = files.InspectLegacyFile(legacyRoot, path);
            var snapshot = new LegacyMediaFileSnapshot(Guid.NewGuid(), legacyRoot, path, bytes.Length, identity.DeviceId,
                identity.FileId, File.GetLastWriteTimeUtc(path).Ticks, true, "deezer", "source-track", new("Song", ["Artist"]));
            var prepared = await files.PrepareLegacyAdoptionAsync(snapshot, DateTimeOffset.UtcNow, default);
            var file = await files.AdoptLegacyFileAsync(prepared, snapshot, $"legacy-original:{prepared.Id:N}", default);
            copy = new(recordingId, file.Id, file.Length, file.ContentSha256, "audio/flac", file.CanonicalPath);
            await using var db = factory.CreateDbContext();
            (await db.Users.SingleAsync(item => item.Id == owner)).IsAdmin = true;
            await db.SaveChangesAsync();
            var audit = new AuditEventRecord { Id = Guid.NewGuid(), ActorUserId = owner };
            var import = new LegacyEnvImportRecord { Id = Guid.NewGuid(), ActorUserId = owner, AuditEventId = audit.Id, SourceSha256 = new string('a', 64) };
            db.AddRange(audit, import, new LegacyMediaAdoptionRecord
            {
                Id = Guid.NewGuid(),
                ImportId = import.Id,
                ManagedFileId = copy.ManagedFileId,
                CanonicalRecordingId = recordingId,
                Kept = true,
                ProviderId = "deezer",
                ExternalId = "source-track"
            });
            await db.SaveChangesAsync();
        }
        else copy = await kept.PublishAsync(Request());

        var result = await kept.FindPromotionSourceAsync(Actor(owner), recordingId, new(), default);

        Assert.NotNull(result);
        Assert.Equal(copy.Path, result.SourcePath);
        Assert.Equal("flac", result.Codec);
        Assert.Equal(16, result.BitDepth);
        Assert.Equal(44100, result.SampleRate);
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.Jobs.Where(item => item.Type == PromotionAcquisition.JobType).ToArrayAsync());
    }

    [Fact]
    public async Task PromotionConfirmation_ReleasesOnlyTheListenersKeptInterestsAndRetainsTheGracePeriod()
    {
        var copy = await PublishInitialFavoriteAsync();
        var (clock, jobs, _, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(other, CanonicalItemId(), FavoriteOperation.Favorite, "other-kept"));
        var access = new TestBackendLibraryAccess(factory, "music");
        var kept = new RecordingKeptFiles(factory, artifacts, services.GetRequiredService<IServiceScopeFactory>(), placement,
            identities, clock, jobs: jobs, libraryAccess: access);
        var operation = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => kept.ReleasePromotedCopyAsync(owner, recordingId, "music", operation, "promotion", default));
        await using (var db = factory.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = other,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendLibraryId = "music",
                BackendItemId = "native",
                FilePath = "/music/song.flac",
                Title = "Song",
                Artist = "Artist",
                CanonicalRecordingId = recordingId
            });
            await new EfManagedFileOwnershipStore(db).StageReferenceAsync(copy.ManagedFileId,
                $"keep:user:{other:N}:{recordingId:N}", clock.UtcNow, default);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await kept.ReleasePromotedCopyAsync(owner, recordingId, "music", operation, "promotion", default);
        await using (var db = factory.CreateDbContext())
        {
            var active = await db.ManagedFileReferences.Where(item => item.ManagedFileId == copy.ManagedFileId && item.ReleasedAt == null).ToArrayAsync();
            Assert.Equal(2, active.Length);
            var otherHeart = await db.FavoriteStates.SingleAsync(item => item.OwnerUserId == other);
            Assert.Contains(active, item => item.ReferenceKey == $"heart:{otherHeart.Id:N}");
            Assert.Contains(active, item => item.ReferenceKey == $"keep:user:{other:N}:{recordingId:N}");
            Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        }
        await kept.ReleasePromotedCopyAsync(other, recordingId, "music", operation, "promotion", default, releaseExplicitKeep: false);
        await using (var db = factory.CreateDbContext())
        {
            var active = Assert.Single(await db.ManagedFileReferences.Where(item => item.ManagedFileId == copy.ManagedFileId && item.ReleasedAt == null).ToArrayAsync());
            Assert.StartsWith("keep:user:", active.ReferenceKey);
            Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        }
        await kept.ReleasePromotedCopyAsync(other, recordingId, "music", operation, "promotion", default);
        await kept.ReleasePromotedCopyAsync(other, recordingId, "music", operation, "promotion", default);

        await using var verify = factory.CreateDbContext();
        var cleanup = Assert.Single(await verify.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(clock.UtcNow.AddHours(24), cleanup.AvailableAt);
        Assert.True(File.Exists(copy.Path));
    }

    [Theory]
    [InlineData("flac", 24, false, true)]
    [InlineData("aac", null, false, false)]
    [InlineData("flac", 16, true, false)]
    public async Task PromotionAcquisition_ReusesOnlyAnIntactKeptCopyMeetingTheProfile(string codec, int? bits, bool changed, bool reused)
    {
        await using (var db = factory.CreateDbContext())
        {
            var source = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
            source.Codec = codec;
            source.BitDepth = bits;
            source.SampleRate = 96000;
            await db.SaveChangesAsync();
        }
        var kept = Service();
        var copy = await kept.PublishAsync(Request());
        if (changed) await File.AppendAllTextAsync(copy.Path, "changed");
        var runId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.Add(new PromotionRunRecord { Id = runId, OwnerUserId = owner, JobId = jobId, RequestKey = "kept-source" });
            await db.SaveChangesAsync();
        }
        var acquisition = new PromotionAcquisition(factory, queue, identities, artifacts, kept);
        var result = await acquisition.AcquireAsync(owner, runId, new()
        {
            RecordingId = recordingId,
            Song = new() { ExternalProvider = "deezer", ExternalId = "source-track", Codec = "flac", BitDepth = 24, SampleRate = 96000 }
        }, new(), default);

        Assert.Equal(!reused, result.Pending);
        if (reused)
        {
            Assert.Equal(copy.Path, result.Artifact!.SourcePath);
            Assert.Equal(copy.ContentSha256, result.Artifact.ContentSha256);
            Assert.Equal(24, result.Artifact.BitDepth);
        }
        else Assert.Null(result.Artifact);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(reused ? 0 : 1, await verify.Jobs.CountAsync(item => item.Type == PromotionAcquisition.JobType));
        Assert.True(File.Exists(copy.Path));
    }

    [Fact]
    public async Task PromotionNamingPreview_UsesKeptMetadataOnlyForThePublisher()
    {
        await Service().PublishAsync(Request());
        var preview = new PromotionNamingPreviewService(factory, new(factory), new TestBackendLibraryAccess(factory, "music"));
        ProtocolExecutionContext Context(Guid user) => new(ProtocolKind.Jellyfin, "fixture", user.ToString("N"),
            new allstarr.Core.Identity.AllstarrPrincipal(user, "jellyfin", "fixture", user.ToString("N"), "Listener", false),
            "promotion-preview", DateTimeOffset.UtcNow.AddMinutes(5), default);
        var result = await preview.PreviewAsync(Context(owner), new("%title%.%_extension%"));
        var example = Assert.Single(result.Examples);
        Assert.Equal(recordingId, example.RecordingId);
        Assert.Null(example.LibraryTrackId);
        Assert.EndsWith(".flac", example.RelativePath);
        Assert.Empty(example.Errors);
        Assert.Empty((await preview.PreviewAsync(Context(other), new())).Examples);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preview.PreviewAsync(Context(other), new(RecordingIds: [recordingId])));
    }

    [Fact]
    public async Task UnheartAndDueCleanupPreservePromotedLibraryCopy()
    {
        var original = await PublishInitialFavoriteAsync();
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).IsTrustedListener = true;
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).QualityCheckJson =
                JsonSerializer.Serialize(new AudioQualityCheck(44100, 16, 21000, "standard", .9));
            await db.SaveChangesAsync();
        }
        var library = Path.Combine(root, "library", "music");
        Directory.CreateDirectory(library);
        var libraryRoot = new PromotionRoot(Guid.NewGuid(), "Music", library, BackendLibraryId: "music");
        var settings = new Mock<IDurableRuntimeSettings>();
        var json = JsonSerializer.Serialize(new[] { libraryRoot });
        settings.Setup(item => item.GetAsync(PromotionRoots.Key, It.IsAny<CancellationToken>())).ReturnsAsync(
            new EffectiveRuntimeSetting(PromotionRoots.Key, RuntimeSettingValueType.String, json, json,
                RuntimeSettingOrigin.Durable, 1, "test", null, null));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Promotion:AllowedBase"] = Path.Combine(root, "library") }).Build();
        using var cache = new FileMediaApplicationCache(new FileMediaCacheOptions(Path.Combine(root, "cache")),
            new SystemPlatformClock(), NullLogger<FileMediaApplicationCache>.Instance);
        var promotion = new LibraryPromotionService(factory, queue, new(settings.Object, factory, configuration), artifacts,
            new() { RootPath = Path.Combine(root, "workspaces") }, services.GetRequiredService<IServiceScopeFactory>(),
            new UnchangedPromotionWriter(), new AudioQualityAnalyzer(cache), new TestBackendLibraryAccess(factory, "music"));
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", owner.ToString("N"),
            new allstarr.Core.Identity.AllstarrPrincipal(owner, "jellyfin", "fixture", owner.ToString("N"), "Listener", false),
            "promotion", DateTimeOffset.UtcNow.AddMinutes(5), default);
        await new PromotionProfiles(factory).SaveMineAsync(context, new()
        { NamingConfirmed = true, NamingScript = "%title%.%_extension%" });
        var run = await promotion.StartAsync(context, new(recordingId, artifact.Id, libraryRoot.Id, "music", "promote"));
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("promotion", [LibraryPromotionService.JobType]));
        await queue.CompleteAsync(claim, await promotion.ExecuteAsync(new(claim, services), default));
        var result = await promotion.GetAsync(context, run.Id);
        Assert.Equal("Placed", result.Outcome);

        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "promoted-unheart"));
        clock.UtcNow = clock.UtcNow.AddHours(25);
        var cleanup = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("cleanup", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(cleanup)).Kind);

        await using var verify = factory.CreateDbContext();
        var promoted = await verify.ManagedFiles.SingleAsync(item => item.Id == result.ManagedFileId);
        Assert.False(File.Exists(original.Path));
        Assert.True(File.Exists(promoted.CanonicalPath));
        Assert.Equal(audio, await File.ReadAllBytesAsync(promoted.CanonicalPath));
        Assert.Null((await verify.ManagedFileReferences.SingleAsync(item => item.ManagedFileId == promoted.Id)).ReleasedAt);
    }

    private sealed class UnchangedPromotionWriter : IManagedMetadataWriter
    {
        public Task<ManagedMetadataWriteResult> WriteAsync(ManagedMetadataArtifact artifact,
            IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken) =>
            Task.FromResult(new ManagedMetadataWriteResult(artifact.ContentSha256, new FileInfo(artifact.Path).Length, false));
    }
}
