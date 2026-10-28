using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task PublicationQueuesQualityCheckAndRetainsAlreadyMeasuredResult()
    {
        var check = new AudioQualityCheck(96000, 24, 20000, "upsampled", 0.8, 44100);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).QualityCheckJson = JsonSerializer.Serialize(check);
            await db.SaveChangesAsync();
        }
        var service = new RecordingKeptFiles(factory, artifacts,
            services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            placement, identities, new allstarr.Core.Operations.SystemPlatformClock(), jobs: queue, audioVerifier: FixtureAudioVerifier.Instance);
        var published = await service.PublishAsync(Request());
        await using var read = await factory.CreateDbContextAsync();
        var route = await read.Set<RecordingKeptRouteEntity>().SingleAsync();
        Assert.Equal(check, JsonSerializer.Deserialize<KeptRecordingMetadata>(route.DisplayMetadataJson!)!.QualityCheck);
        var job = await read.Jobs.SingleAsync(item => item.Type == AudioQualityCheckJobHandler.Type);
        Assert.Equal(artifact.Id, JsonSerializer.Deserialize<AudioQualityCheckPayload>(job.PayloadJson)!.ArtifactId);
        Assert.Equal(published.ManagedFileId, route.ManagedFileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundCheckPersistsWithDownloadAndKeptMetadataAndSurvivesReload(bool workspaceRemoved)
    {
        var service = new RecordingKeptFiles(factory, artifacts, services.GetRequiredService<IServiceScopeFactory>(),
            placement, identities, new allstarr.Core.Operations.SystemPlatformClock(), jobs: queue, audioVerifier: FixtureAudioVerifier.Instance);
        await service.PublishAsync(Request());
        if (workspaceRemoved) File.Delete(artifact.SourcePath);
        var claim = await queue.ClaimNextAsync("quality-fixture", [AudioQualityCheckJobHandler.Type]);
        Assert.NotNull(claim);
        using var cache = new FileMediaApplicationCache(new FileMediaCacheOptions(Path.Combine(root, "quality-cache")),
            new allstarr.Core.Operations.SystemPlatformClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileMediaApplicationCache>.Instance);
        var handler = new AudioQualityCheckJobHandler(factory, artifacts, new AudioQualityAnalyzer(cache), queue);
        var completion = await handler.ExecuteAsync(new(claim, services), CancellationToken.None);
        Assert.Equal(allstarr.Core.Jobs.DurableJobCompletionKind.Succeeded, completion.Kind);
        await using var read = await factory.CreateDbContextAsync();
        var download = await read.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleAsync();
        var route = await read.Set<RecordingKeptRouteEntity>().AsNoTracking().SingleAsync();
        var measured = JsonSerializer.Deserialize<AudioQualityCheck>(download.QualityCheckJson!);
        Assert.Equal("unknown", measured!.Verdict);
        Assert.Equal(measured, JsonSerializer.Deserialize<KeptRecordingMetadata>(route.DisplayMetadataJson!)!.QualityCheck);
        Assert.Equal("Fixture", JsonSerializer.Deserialize<KeptRecordingMetadata>(route.DisplayMetadataJson!)!.Title);
        var duplicate = await queue.EnqueueAsync(AudioQualityCheckJobHandler.Request(artifact.Id, owner));
        Assert.False(duplicate.Created);
    }


    [Fact]
    public async Task AdministratorRecheckQueuesFreshDurableWorkAndRejectsMissingFiles()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).IsAdmin = true;
            await db.SaveChangesAsync();
        }
        using var requestServices = new ServiceCollection().AddSingleton(queue).BuildServiceProvider();
        var controller = new allstarr.Controllers.DownloadsController(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Controllers.DownloadsController>.Instance,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), contextFactory: factory)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = requestServices } }
        };
        controller.HttpContext.Items[allstarr.Services.Admin.AdminAuthSessionService.HttpContextSessionItemKey] =
            new allstarr.Services.Admin.AdminAuthSession
            {
                SessionId = "fixture",
                UserId = owner.ToString("N"),
                UserName = "Listener",
                IsAdministrator = true,
                AllstarrUserId = owner,
                BackendType = "Jellyfin",
                BackendInstanceId = "fixture",
                JellyfinAccessToken = "",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
            };
        Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(await controller.RecheckQuality(artifact.Id));
        Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(await controller.RecheckQuality(artifact.Id));
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.RecheckQuality(Guid.NewGuid()));
        await using var read = await factory.CreateDbContextAsync();
        var checks = await read.Jobs.Where(item => item.Type == AudioQualityCheckJobHandler.Type).ToArrayAsync();
        Assert.Equal(2, checks.Length);
        Assert.All(checks, item => Assert.True(JsonSerializer.Deserialize<AudioQualityCheckPayload>(item.PayloadJson)!.Force));
    }


    [Fact]
    public async Task DownloadsReadIncludesStoredQualityWithoutStartingMoreWork()
    {
        var check = new AudioQualityCheck(96000, 24, 20000, "upsampled", 0.8, 44100);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).QualityCheckJson = JsonSerializer.Serialize(check);
            await db.SaveChangesAsync();
        }
        await Service().PublishAsync(Request());
        var controller = new allstarr.Controllers.DownloadsController(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Controllers.DownloadsController>.Instance,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), contextFactory: factory, placement: placement)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.GetDownloads());
        var file = Assert.Single(JsonSerializer.SerializeToElement(response.Value).GetProperty("files").EnumerateArray());
        Assert.Equal("upsampled", file.GetProperty("QualityCheck").GetProperty("verdict").GetString());
        Assert.Equal(artifact.Id, file.GetProperty("QualityCheckArtifactId").GetGuid());
        await using var read = await factory.CreateDbContextAsync();
        Assert.Empty(await read.Jobs.Where(item => item.Type == AudioQualityCheckJobHandler.Type).ToArrayAsync());
    }

}
