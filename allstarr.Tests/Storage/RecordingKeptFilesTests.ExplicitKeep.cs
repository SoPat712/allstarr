using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData("disabled", 401)]
    [InlineData("listener", 401)]
    [InlineData("foreign-account", 403)]
    [InlineData("request-id", 400)]
    public async Task ExplicitKeepRejectsChangedAuthorityWithoutMovingBytes(string change, int status)
    {
        var path = await SeedKeepCacheAsync();
        await using (var update = new AllstarrDbContext(database.Options))
        {
            if (change == "disabled") (await update.Users.SingleAsync(item => item.Id == owner)).Enabled = false;
            else if (change == "listener") (await update.Users.SingleAsync(item => item.Id == owner)).IsAdmin = false;
            else if (change == "foreign-account")
            {
                var foreign = Guid.CreateVersion7();
                update.ProviderAccounts.Add(new()
                {
                    Id = foreign,
                    OwnerUserId = other,
                    ProviderId = "deezer",
                    DisplayName = "Other account",
                    Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                var mapping = await update.DownloadedSongMappings.SingleAsync();
                mapping.ProviderAccountId = foreign;
                mapping.ScopeKey = new DownloadedSongMappingScope(foreign, mapping.AudioQuality).Key;
            }
            await update.SaveChangesAsync();
        }
        var controller = KeepController(linked: true);
        if (change == "request-id") controller.Request.Headers["Idempotency-Key"] = "invalid-request";
        var response = Assert.IsAssignableFrom<IStatusCodeActionResult>(await controller.PromoteCachedDownload("cached.flac"));
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(audio, await File.ReadAllBytesAsync(path));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.Jobs.Where(item => item.State == DurableJobState.Pending).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitKeepReferenceRequiresATypedCurrentKeepRequest(bool otherJobType)
    {
        if (otherJobType)
        {
            await using var db = new AllstarrDbContext(database.Options);
            (await db.Jobs.SingleAsync(item => item.Id == jobId)).Type = "playback.signal";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(Request() with
        { ReferenceKey = $"keep:user:{owner:N}:{recordingId:N}" }));
    }

    [Fact]
    public async Task ExplicitKeepQueuesDurableWorkWithoutMovingOrTrustingLegacyCacheBytes()
    {
        var path = await SeedKeepCacheAsync();
        var controller = KeepController(linked: true);
        controller.Request.Headers["Idempotency-Key"] = Guid.CreateVersion7().ToString("D");
        var result = Assert.IsType<AcceptedResult>(await controller.PromoteCachedDownload("cached.flac"));
        Assert.Equal(202, result.StatusCode);
        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.True(body.GetProperty("queued").GetBoolean());
        Assert.Equal(audio, await File.ReadAllBytesAsync(path));
        Assert.Equal("[00:01.000]Cached words", await File.ReadAllTextAsync(Path.ChangeExtension(path, ".lrc")));
        await using var db = new AllstarrDbContext(database.Options);
        var pending = Assert.Single(await db.Jobs.Where(item => item.Type == "recording.keep" && item.State == DurableJobState.Pending)
            .ToArrayAsync());
        Assert.Equal(owner, pending.OwnerUserId);
        Assert.Equal(path, (await db.DownloadedSongMappings.SingleAsync()).LocalPath);
        Assert.Empty(await db.Set<allstarr.Core.ManagedFiles.RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.Empty(await db.ManagedFiles.ToArrayAsync());
        Assert.False(Directory.Exists(Path.Combine(root, "permanent")));
    }

    [Fact]
    public async Task ExplicitKeepRequiresALinkedCurrentAdministratorBeforeChangingStorage()
    {
        var path = await SeedKeepCacheAsync();
        var result = await KeepController(linked: false).PromoteCachedDownload("cached.flac");
        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(audio, await File.ReadAllBytesAsync(path));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.Jobs.Where(item => item.State == DurableJobState.Pending).ToArrayAsync());
        Assert.Equal(path, (await db.DownloadedSongMappings.SingleAsync()).LocalPath);
    }

    private async Task<string> SeedKeepCacheAsync()
    {
        var path = Path.Combine(root, "cache", "cached.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, audio);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), "[00:01.000]Cached words");
        await using var db = new AllstarrDbContext(database.Options);
        (await db.Users.SingleAsync(item => item.Id == owner)).IsAdmin = true;
        db.DownloadedSongMappings.Add(new()
        {
            Id = Guid.CreateVersion7(),
            ProviderAccountId = account,
            ScopeKey = new DownloadedSongMappingScope(account,
                allstarr.Core.Capabilities.ProviderAudioQuality.Any).Key,
            ProviderId = "deezer",
            ExternalId = "source-track",
            LocalPath = path,
            Title = "Cached title",
            Artist = "Cached artist",
            Album = "Cached album",
            DownloadedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return path;
    }

    private DownloadsController KeepController(bool linked, string backendType = "jellyfin")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = root }).Build();
        var http = new DefaultHttpContext();
        if (linked) http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture-session",
            UserId = owner.ToString("N"),
            UserName = "Keeper",
            IsAdministrator = true,
            BackendType = backendType,
            BackendInstanceId = "fixture",
            AllstarrUserId = owner,
            JellyfinAccessToken = string.Empty,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new(NullLogger<DownloadsController>.Instance, configuration, contextFactory: factory,
            protocolContexts: new AdminProtocolExecutionContextFactory(factory, new SystemPlatformClock()),
            kept: new(factory, artifacts, services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                placement, identities, new SystemPlatformClock(), jobs: queue), placement: placement)
        { ControllerContext = new() { HttpContext = http } };
    }
}
