using System.Text;
using allstarr.Core.Configuration;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class DurableBackupServiceTests
{
    [Fact]
    public async Task ScheduledQueueCreatesOneVerifiedArchiveThenRestoresAndDecryptsTheCredential()
    {
        var (service, queue, clock) = ScheduledService();
        await service.ReconcileScheduleAsync();
        clock.UtcNow = new(2026, 12, 31, 3, 0, 0, TimeSpan.Zero);
        var engine = new DurableScheduleEngine(_factory, queue, clock, ReleaseComposition.Development, service);
        Assert.Equal(1, (await engine.TickAsync()).Enqueued);
        Assert.Equal(0, (await engine.TickAsync()).Enqueued);
        var claim = (await queue.ClaimNextAsync("backup-fixture", [DurableBackupService.JobType]))!;
        Assert.Null(claim.OwnerUserId);
        Assert.True((await queue.ReauthorizeAsync(claim)).Authorized);
        using var services = new ServiceCollection().BuildServiceProvider();
        var execution = new DurableJobExecutionContext(claim, services);
        var handler = new DurableBackupJobHandler(service);
        var completed = await handler.ExecuteAsync(execution, default);
        Assert.True(completed.Kind == DurableJobCompletionKind.Succeeded, completed.ErrorCode + ": " + completed.SafeMessage);
        var repeated = await handler.ExecuteAsync(execution, default);
        Assert.True(repeated.Kind == DurableJobCompletionKind.Succeeded, repeated.ErrorCode + ": " + repeated.SafeMessage);
        await queue.CompleteAsync(claim, DurableJobCompletion.Success());
        var artifact = Assert.Single(await service.ListAsync());
        Assert.Equal("scheduled", artifact.Origin);
        Assert.Equal("verified", artifact.Verification);
        Assert.Equal(["daily", "weekly"], artifact.RetentionBuckets);
        Assert.Single(Directory.GetFiles(_options.BackupDirectory, "*.zip"));
        var status = await service.GetStatusAsync();
        Assert.Equal(clock.UtcNow, status.LastSuccessAt);
        Assert.Equal(artifact.Bytes, status.LastBytes);
        Assert.Null(status.LastFailure);
        await AddNewKeyAndSecretAsync();
        await StageAsync(service, artifact);
        await RestartAsync();
        var store = new EncryptedSecretStore(_factory, new FileSecretKeyRingProvider(_secrets), _secrets, new SystemPlatformClock());
        using var restored = await store.OpenAsync(_secretId, new SecretAccessContext(_userId, "backup.fixture"));
        Assert.Equal("backup-fixture-secret", restored.ReadUtf8());
    }

    [Fact]
    public async Task ConfigurationIsAtomicHotAndCompareAndSwapAndPendingRestoreBlocksMutation()
    {
        var (service, _, _) = ScheduledService();
        await EnableAdministratorAsync();
        var actor = BackupActor();
        var initial = await service.GetPolicyAsync();
        var directory = Path.Combine(_options.DataDirectory, "mounted");
        Directory.CreateDirectory(directory);
        var request = new BackupConfiguration(true, "04:25", directory, 7, 3, 2, initial.Revision);
        var changed = await service.ConfigureAsync(request, actor);
        Assert.Equal("04:25", changed.BackupPolicy.TimeUtc);
        Assert.Equal(directory, changed.BackupPolicy.Directory);
        Assert.Equal(2, changed.BackupPolicy.ManualRetentionCount);
        await Assert.ThrowsAsync<BackupConfigurationConflictException>(() => service.ConfigureAsync(request, actor));
        var invalid = request with { TimeUtc = "25:00", DailyRetentionCount = 0, ExpectedRevision = changed.BackupPolicy.Revision };
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfigureAsync(invalid, actor));
        Assert.Equal(changed.BackupPolicy, await service.GetPolicyAsync());
        var archive = await service.CreateAsync();
        await using (var uploaded = File.OpenRead(Path.Combine(directory, archive.FileName)))
            await service.StageRestoreAsync(uploaded);
        await Assert.ThrowsAsync<RestorePendingException>(() => service.ConfigureAsync(request with { ExpectedRevision = changed.BackupPolicy.Revision }, actor));
        await Assert.ThrowsAsync<RestorePendingException>(() => service.EnqueueAsync(actor));
    }

    [Fact]
    public async Task ManualAndScheduleAdmissionShareOneAtomicHouseholdFence()
    {
        var (service, queue, clock) = ScheduledService();
        await EnableAdministratorAsync();
        await service.ReconcileScheduleAsync();
        clock.UtcNow = new(2026, 12, 31, 3, 0, 0, TimeSpan.Zero);
        var engine = new DurableScheduleEngine(_factory, queue, clock, ReleaseComposition.Development, service);
        var manual = service.EnqueueAsync(BackupActor());
        var scheduled = engine.TickAsync();
        try { await manual; } catch (BackupRunningException) { }
        await scheduled;
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.Where(item => item.Type == DurableBackupService.JobType).ToArrayAsync());
        Assert.Single(await db.BackupArtifacts.ToArrayAsync());
    }

    [Fact]
    public async Task MissingOptionalDirectoryDoesNotCreateFallbackAndCanRecoverForTheSameJob()
    {
        var (service, queue, _) = ScheduledService();
        await EnableAdministratorAsync();
        var policy = await service.GetPolicyAsync();
        var unavailable = Path.Combine(_options.DataDirectory, "not-mounted");
        Directory.CreateDirectory(unavailable);
        await service.ConfigureAsync(new(true, "03:00", unavailable, 14, 8, 10, policy.Revision), BackupActor());
        Directory.Move(unavailable, unavailable + ".detached");
        var queued = await service.EnqueueAsync(BackupActor());
        var claim = (await queue.ClaimNextAsync("backup-fixture", [DurableBackupService.JobType]))!;
        Assert.Equal(queued.JobId, claim.JobId);
        using var services = new ServiceCollection().BuildServiceProvider();
        var execution = new DurableJobExecutionContext(claim, services);
        var handler = new DurableBackupJobHandler(service);
        var failure = await handler.ExecuteAsync(execution, default);
        Assert.Equal(DurableJobCompletionKind.Retry, failure.Kind);
        Assert.Equal("backup_directory_unavailable", failure.ErrorCode);
        Assert.DoesNotContain(unavailable, failure.SafeMessage);
        Assert.False(Directory.Exists(unavailable));
        Assert.False(Directory.Exists(_options.BackupDirectory));
        Directory.Move(unavailable + ".detached", unavailable);
        var recovered = await handler.ExecuteAsync(execution, default);
        Assert.True(recovered.Kind == DurableJobCompletionKind.Succeeded, recovered.ErrorCode + ": " + recovered.SafeMessage);
        Assert.Single(await service.ListAsync());
    }

    [Fact]
    public void DailyAndIsoWeeklyRetentionUseOneUnionAcrossTheYearBoundary()
    {
        var rows = Enumerable.Range(0, 24).Select(index => new BackupArtifactRecord
        {
            Id = Guid.NewGuid(),
            Origin = "scheduled",
            CreatedAt = new DateTimeOffset(2026, 12, 20, 3, 0, 0, TimeSpan.Zero).AddDays(index),
            ScheduledFor = new DateTimeOffset(2026, 12, 20, 3, 0, 0, TimeSpan.Zero).AddDays(index),
            VerifiedAt = new DateTimeOffset(2026, 12, 20, 3, 0, 0, TimeSpan.Zero).AddDays(index)
        }).ToArray();
        var selected = DurableBackupService.SelectRetention(rows, new(true, "03:00", "", 2, 3, 10, "fixture"));
        Assert.Equal(4, selected.Count);
        Assert.Equal(["daily", "weekly"], selected[rows[^1].Id]);
        Assert.Equal(["daily"], selected[rows[^2].Id]);
        Assert.All(selected.Values, buckets => Assert.NotEmpty(buckets));
        Assert.Equal(3, selected.Count(pair => pair.Value.Contains("weekly")));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("role")]
    [InlineData("instance")]
    public async Task BackupMutationRequiresTheCurrentExactAdministrator(string revoked)
    {
        var (service, _, _) = ScheduledService();
        await EnableAdministratorAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            if (revoked == "disabled") await db.Users.Where(item => item.Id == _userId).ExecuteUpdateAsync(update => update.SetProperty(item => item.Enabled, false));
            if (revoked == "role") await db.Users.Where(item => item.Id == _userId).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsAdmin, false));
        }
        var actor = BackupActor(revoked == "instance" ? "other-instance" : "backup-fixture");
        var before = await service.GetPolicyAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EnqueueAsync(actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfigureAsync(new(true, "04:00", "", 7, 2, 3, before.Revision), actor));
        Assert.Equal(before, await service.GetPolicyAsync());
        await using var read = await _factory.CreateDbContextAsync();
        Assert.Empty(await read.BackupArtifacts.ToArrayAsync());
        Assert.False(Directory.Exists(_options.BackupDirectory));
    }

    [Fact]
    public async Task SavingAMissingCustomDirectoryIsAtomicAndDoesNotCreateItOrFallback()
    {
        var (service, _, _) = ScheduledService();
        await EnableAdministratorAsync();
        var initial = await service.GetPolicyAsync();
        var missing = Path.Combine(_options.DataDirectory, "not-mounted");
        await Assert.ThrowsAsync<BackupDirectoryException>(() => service.ConfigureAsync(new(true, "04:00", missing, 7, 2, 3, initial.Revision), BackupActor()));
        Assert.Equal(initial, await service.GetPolicyAsync());
        Assert.False(Directory.Exists(missing));
        Assert.False(Directory.Exists(_options.BackupDirectory));
    }

    [Fact]
    public async Task ARepeatedOccurrenceDoesNotAllocateAnotherArtifactAndLateRecoveryUsesItsVerificationDay()
    {
        var (service, queue, clock) = ScheduledService();
        await service.ReconcileScheduleAsync();
        var due = new DateTimeOffset(2026, 12, 31, 3, 0, 0, TimeSpan.Zero);
        clock.UtcNow = due;
        var engine = new DurableScheduleEngine(_factory, queue, clock, ReleaseComposition.Development, service);
        Assert.Equal(1, (await engine.TickAsync()).Enqueued);
        var claim = (await queue.ClaimNextAsync("backup-fixture", [DurableBackupService.JobType]))!;
        clock.UtcNow = new(2027, 1, 10, 3, 0, 0, TimeSpan.Zero);
        await queue.RecoverInterruptedAsync();
        claim = (await queue.ClaimNextAsync("backup-restarted", [DurableBackupService.JobType]))!;
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await new DurableBackupJobHandler(service).ExecuteAsync(new(claim, services), default)).Kind);
        await queue.CompleteAsync(claim, DurableJobCompletion.Success());
        await using (var db = await _factory.CreateDbContextAsync())
            await db.JobSchedules.Where(item => item.Id == DurableBackupService.ScheduleId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.NextRunAt, due));
        Assert.Equal(0, (await engine.TickAsync()).Enqueued);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var archive = Assert.Single(await db.BackupArtifacts.ToArrayAsync());
            Assert.Equal(due, archive.CreatedAt);
            Assert.Equal(due, archive.ScheduledFor);
            Assert.Equal(clock.UtcNow, archive.VerifiedAt);
            Assert.Single(await db.Jobs.Where(item => item.Type == DurableBackupService.JobType).ToArrayAsync());
            var earlierToday = new BackupArtifactRecord
            {
                Id = Guid.NewGuid(),
                Origin = "scheduled",
                CreatedAt = clock.UtcNow.AddMinutes(-1),
                ScheduledFor = clock.UtcNow.AddMinutes(-1),
                VerifiedAt = clock.UtcNow.AddMinutes(-1)
            };
            var retained = DurableBackupService.SelectRetention([archive, earlierToday], new(true, "03:00", "", 1, 2, 10, "fixture"));
            Assert.Single(retained);
            Assert.Equal(["daily", "weekly"], retained[archive.Id]);
        }
        Assert.Single(Directory.GetFiles(_options.BackupDirectory, "*.zip"));
    }

    private (DurableBackupService Service, DurableJobQueue Queue, BackupClock Clock) ScheduledService(allstarr.Core.ManagedFiles.IManagedFileOperations? files = null)
    {
        var clock = new BackupClock { UtcNow = new(2026, 12, 31, 2, 59, 0, TimeSpan.Zero) };
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), clock);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Storage:DataDirectory"] = _options.DataDirectory }).Build();
        var runtime = new allstarr.Core.Settings.DurableRuntimeSettingsService(_factory, config, clock,
            new allstarr.Core.Settings.RuntimeSettingsChangeSignal());
        return (new(_factory, _options, _state, _secrets, runtime, queue, clock, files, config), queue, clock);
    }
    private async Task EnableAdministratorAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Users.Where(item => item.Id == _userId).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsAdmin, true));
    }
    private AdminAuthSession BackupActor(string instance = "backup-fixture") => new()
    {
        SessionId = "backup-fixture",
        UserId = "listener",
        UserName = "Administrator",
        BackendType = "Jellyfin",
        BackendInstanceId = instance,
        JellyfinAccessToken = "fixture",
        AllstarrUserId = _userId,
        IsAdministrator = true,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };
    private sealed class BackupClock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } }
}
