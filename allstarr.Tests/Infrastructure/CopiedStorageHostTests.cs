using System.Net;
using System.Security.Cryptography;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

namespace allstarr.Tests;

public sealed class CopiedStorageHostTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RestartedHostServesHealthWhileCopiedFilesWaitForVerificationThenRestoresIdentityOnlyReads()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var root = Path.Combine(database.StorageOptions.DataDirectory, "kept");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fixture.flac");
        var bytes = "fLaCcopied host fixture"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var physical = new PhysicalManagedFileOperations();
        Assert.True(physical.TryGetFileIdentity(path, out var identity));
        var rootId = Guid.CreateVersion7();
        var record = new ManagedFileRecord(Guid.CreateVersion7(), rootId, path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length,
            ManagedFilePlacementMethod.Copy, null, null, ManagedFileScopeKey.Create(null, rootId), 1, true, DateTimeOffset.UtcNow)
        { TargetRootPath = root, FileSystemDeviceId = identity.DeviceId, FileSystemFileId = identity.FileId, FileSystemLinkCount = identity.LinkCount };
        await using (var db = new AllstarrDbContext(database.Options))
            await new EfManagedFileOwnershipStore(db).AddAsync(record,
                new(Guid.CreateVersion7(), record.Id, null, record.ScopeKey, "copied-host-fixture", record.CreatedAt), default);

        using (var first = new CopyHost(database.StorageOptions.DataDirectory))
        {
            Assert.Equal(HttpStatusCode.OK, (await first.CreateClient().GetAsync("/health")).StatusCode);
            await WaitForVerificationAsync(database);
        }
        database.ClearHostPool();
        var backup = CopiedDataFolderFixture.ReplaceWithCopy(database.StorageOptions.DataDirectory);
        var pause = new PausingManagedFileOperations();
        pause.Arm(path);
        try
        {
            using var second = new CopyHost(database.StorageOptions.DataDirectory, pause);
            using var client = second.CreateClient();
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(DurableStorageReadiness.Ready, second.Services.GetRequiredService<DurableStorageState>().GetSnapshot().Readiness);
            await using (var scope = second.Services.CreateAsyncScope())
            {
                var placement = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
                Assert.Throws<IOException>(() => placement.OpenOwnedPlaybackRead(record, default));
            }
            pause.Resume.TrySetResult();
            await WaitForVerificationAsync(database);
            await using var finalScope = second.Services.CreateAsyncScope();
            var db = finalScope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            var rebound = EfManagedFileOwnershipStore.Map(await db.ManagedFiles.AsNoTracking().SingleAsync())!;
            Assert.NotEqual(identity.FileId, rebound.FileSystemFileId);
            using var playback = finalScope.ServiceProvider.GetRequiredService<FilePlacementService>().OpenOwnedPlaybackRead(rebound, default);
            Assert.Equal(bytes.Length, playback.Length);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            pause.Resume.TrySetResult();
            database.ClearHostPool();
            Directory.Delete(backup, true);
        }
    }

    private static async Task WaitForVerificationAsync(SqliteTestDatabase database)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var db = new AllstarrDbContext(database.Options);
            var identity = await db.Set<ManagedStorageIdentityRecord>().AsNoTracking().SingleAsync(timeout.Token);
            var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == identity.VerificationJobId, timeout.Token);
            if (job.State == DurableJobState.Succeeded) return;
            Assert.DoesNotContain(job.State, new[] { DurableJobState.Failed, DurableJobState.Cancelled });
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class CopyHost(string data, IManagedFileOperations? files = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Storage:DataDirectory", data);
            builder.UseSetting("Release:Profile", "core");
            builder.UseSetting("Jobs:PollIntervalMilliseconds", "50");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, TestRateLimiterCleanup>();
                services.RemoveAll<IHostedService>();
                services.AddHostedService<ManagedStorageRecoveryStartup>();
                services.AddHostedService<DurableJobWorker>();
                if (files != null) services.AddSingleton(files);
                services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(
                    http => http.PrimaryHandler = new NoNetwork()));
            });
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The copied-data host fixture cannot contact external services.");
    }
}
