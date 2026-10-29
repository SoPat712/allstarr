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
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task UnheartAndDueCleanupPreservePromotedLibraryCopy()
    {
        var original = await PublishInitialFavoriteAsync();
        await using (var db = factory.CreateDbContext())
        {
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).QualityCheckJson =
                JsonSerializer.Serialize(new AudioQualityCheck(44100, 16, 21000, "standard", .9));
            await db.SaveChangesAsync();
        }
        var library = Path.Combine(root, "library", "music");
        Directory.CreateDirectory(library);
        var libraryRoot = new PromotionRoot(Guid.NewGuid(), "Music", library);
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
