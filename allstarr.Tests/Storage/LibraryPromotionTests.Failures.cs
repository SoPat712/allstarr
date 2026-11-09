using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Theory]
    [InlineData(PromotionFailure.LibraryFolderNotSet, 503, "library-folder-not-set")]
    [InlineData(PromotionFailure.LibraryFolderUnavailable, 503, "library-folder-unavailable")]
    [InlineData(PromotionFailure.LibraryFolderMismatch, 400, "library-folder-mismatch")]
    [InlineData(PromotionFailure.SourceChanged, 409, "source-changed")]
    [InlineData(PromotionFailure.DestinationExists, 409, "destination-exists")]
    [InlineData(PromotionFailure.PromotionChanged, 409, "promotion-changed")]
    [InlineData(PromotionFailure.NotAuthorized, 403, "not-authorized")]
    [InlineData(PromotionFailure.InvalidRequest, 400, "invalid-request")]
    public async Task TypedFailuresKeepTheirHttpStatusAndReason(PromotionFailure failure, int status, string reason)
    {
        settings.Setup(item => item.GetAsync(PromotionRoots.Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PromotionException(failure));
        var result = Assert.IsType<ObjectResult>(await FailureController().Roots(default));
        Assert.Equal(status, result.StatusCode);
        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task MissingFolderPreviewExplainsSetupInsteadOfAConflict()
    {
        settings.Setup(item => item.GetAsync(PromotionRoots.Key, It.IsAny<CancellationToken>())).ReturnsAsync(
            new EffectiveRuntimeSetting(PromotionRoots.Key, RuntimeSettingValueType.String, "[]", "[]",
                RuntimeSettingOrigin.Durable, 1, "test", null, null));
        var failure = await Assert.ThrowsAsync<PromotionException>(() => service.PreviewAsync(Context, Request()));
        Assert.Equal(PromotionFailure.LibraryFolderNotSet, failure.Failure);
        var result = Assert.IsType<ObjectResult>(await FailureController().Preview(Request(), default));
        Assert.Equal(503, result.StatusCode);
        Assert.DoesNotContain("changed", JsonSerializer.Serialize(result.Value));
        Assert.Equal("library-folder-not-set", (await roots.SetupAsync()).Reason);
    }

    [Fact]
    public async Task OccupiedDestinationReturnsItsOwnConflictWithoutOverwriting()
    {
        var path = (await service.PreviewAsync(Context, Request())).DestinationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "existing library file");
        var failure = await Assert.ThrowsAsync<PromotionException>(() => service.PreviewAsync(Context, Request()));
        Assert.Equal(PromotionFailure.DestinationExists, failure.Failure);
        Assert.Equal(PromotionFailure.DestinationExists,
            (await Assert.ThrowsAsync<PromotionException>(() => service.StartAsync(Context, Request()))).Failure);
        var result = Assert.IsType<ObjectResult>(await FailureController().Preview(Request(), default));
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("destination-exists", JsonSerializer.Serialize(result.Value));
        Assert.Equal("existing library file", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MissingSourceReturnsSourceChangedInsteadOfAnUnexpectedServerError()
    {
        File.Delete(Path.Combine(directory, "workspaces", "source", "song.flac"));
        var failure = await Assert.ThrowsAsync<PromotionException>(() => service.PreviewAsync(Context, Request()));
        Assert.Equal(PromotionFailure.SourceChanged, failure.Failure);
        var result = Assert.IsType<ObjectResult>(await FailureController().Preview(Request(), default));
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("source-changed", JsonSerializer.Serialize(result.Value));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("length")]
    [InlineData("content")]
    public async Task ChangedKeptSourceReturnsItsOwnConflict(string change)
    {
        var path = Path.Combine(directory, "workspaces", "source", "song.flac");
        await using (var db = factory.CreateDbContext())
        {
            var artifact = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
            var fileId = Guid.NewGuid();
            db.ManagedFiles.Add(new()
            {
                Id = fileId,
                RootId = rootId,
                TargetRootPath = Path.GetDirectoryName(path)!,
                CanonicalPath = path,
                IsManaged = true,
                ScopeKey = ManagedFileScopeKey.Create(null, rootId),
                ContentSha256 = artifact.ContentSha256,
                Length = artifact.Length
            });
            db.Add(new RecordingKeptRouteEntity
            {
                CanonicalRecordingId = recordingId,
                ManagedFileId = fileId,
                PublishedByUserId = owner,
                PublicationJobId = artifact.DurableJobId,
                SourceArtifactId = artifactId
            });
            await db.SaveChangesAsync();
        }
        if (change == "missing") File.Delete(path);
        else await File.WriteAllTextAsync(path, change == "length" ? "short" : "modified original");

        var failure = await Assert.ThrowsAsync<PromotionException>(() => service.PreviewAsync(Context, Request()));
        Assert.Equal(PromotionFailure.SourceChanged, failure.Failure);
        var result = Assert.IsType<ObjectResult>(await FailureController().Preview(Request(), default));
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("source-changed", JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task ApprovedFolderRemainsAvailableWithoutOptionalBaseAndMissingMountHasItsOwnReason()
    {
        var unconfigured = new PromotionRoots(settings.Object, factory, new ConfigurationBuilder().Build());
        Assert.True((await unconfigured.SetupAsync()).Ready);
        Assert.Equal(rootId, (await unconfigured.RequireAsync(rootId, default)).Id);
        Directory.Delete(Path.Combine(directory, "library", "music"));
        Assert.Equal("library-folder-unavailable", (await roots.SetupAsync()).Reason);
        Assert.Equal(PromotionFailure.LibraryFolderUnavailable,
            (await Assert.ThrowsAsync<PromotionException>(() => roots.RequireAsync(rootId, default))).Failure);
    }

    [Fact]
    public async Task AlmostFullLibraryDriveStopsBeforeAnythingIsAdded()
    {
        var music = Path.Combine(directory, "library", "music");
        var full = new PromotionRoots(settings.Object, factory, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Promotion:MinimumFreeBytes"] = long.MaxValue.ToString() }).Build());
        var failure = await Assert.ThrowsAsync<PromotionException>(() => full.RequireAsync(rootId, default));
        Assert.Equal(PromotionFailure.LibraryFolderFull, failure.Failure);
        Assert.Equal(503, failure.Status);
        Assert.Equal("library-folder-full", failure.Reason);
        var roomy = new PromotionRoots(settings.Object, factory, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Promotion:MinimumFreeBytes"] = "0" }).Build());
        roomy.RequireFreeSpace(music, 1);
        Assert.Equal(PromotionFailure.LibraryFolderFull,
            Assert.Throws<PromotionException>(() => roomy.RequireFreeSpace(music, long.MaxValue)).Failure);
        Assert.NotNull(roomy.FreeBytes(music));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedFailuresAreLoggedWithRedactedMessagesAndReturnServerError(bool io)
    {
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<LibraryPromotionsController>>();
        settings.Setup(item => item.GetAsync(PromotionRoots.Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(io ? new IOException("Failed /private/token-secret/song.flac '/private/secret folder/file name.flac' token=hidden Authorization: Bearer hidden-bearer")
                : new InvalidOperationException("Failed /private/token-secret/song.flac '/private/secret folder/file name.flac' token=hidden Authorization: Bearer hidden-bearer"));
        var result = Assert.IsType<ObjectResult>(await FailureController(logger.Object).Roots(default));
        Assert.Equal(500, result.StatusCode);
        var log = Assert.Single(logger.Invocations).Arguments[2].ToString()!;
        Assert.Contains(io ? "IOException" : "InvalidOperationException", log);
        Assert.Contains("Failed", log);
        Assert.DoesNotContain("token-secret", log);
        Assert.DoesNotContain("hidden", log);
        Assert.DoesNotContain("name.flac", log);
        Assert.DoesNotContain("changed", JsonSerializer.Serialize(result.Value));
    }

    private LibraryPromotionsController FailureController(Microsoft.Extensions.Logging.ILogger<LibraryPromotionsController>? logger = null)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            AllstarrUserId = owner,
            UserId = "principal",
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            SessionId = "failure-session",
            UserName = "Owner",
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new(service, roots, new(factory, new SystemPlatformClock()), logger)
        { ControllerContext = new() { HttpContext = http } };
    }
}
