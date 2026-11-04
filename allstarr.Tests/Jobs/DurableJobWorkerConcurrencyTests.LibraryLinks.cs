using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class DurableJobWorkerConcurrencyTests
{
    [Fact]
    public async Task LibraryLinkJobsForDistinctListenersRunOneAtATimeAlongsideOtherWork()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:MaximumConcurrentJobs"] = "4",
            ["Jobs:PollIntervalMilliseconds"] = "50",
            [$"Jobs:TypeConcurrencyLimits:{LibraryRecordingLinkQueue.JobType}"] = "4"
        }).Build();
        using var services = new ServiceCollection().AddDurableJobs(configuration).BuildServiceProvider();
        _options = services.GetRequiredService<DurableJobOptions>();
        _queue = new DurableJobQueue(_factory, _options, new JobPayloadPolicy(_options), _clock);

        var secondListenerId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = secondListenerId,
                BackendType = "jellyfin",
                BackendInstanceId = "worker-fixture",
                BackendPrincipalId = "second-listener",
                DisplayName = "Second listener",
                Enabled = true,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                LastSeenAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var first = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<LibraryRecordingLinkPayload>(
            LibraryRecordingLinkQueue.JobType, "existing-library", new(_userId), _userId, Priority: 2));
        var second = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<LibraryRecordingLinkPayload>(
            LibraryRecordingLinkQueue.JobType, "existing-library", new(secondListenerId), secondListenerId, Priority: 1));
        var other = await EnqueueAsync("independent", "during-library-link");
        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.NotEqual(first.JobId, second.JobId);
        var linking = new GateHandler(LibraryRecordingLinkQueue.JobType, expectedInvocations: 2);
        var independent = new GateHandler("independent", expectedInvocations: 1);
        _cleanupActions.Add(linking.Release);
        _cleanupActions.Add(independent.Release);
        var worker = CreateWorker([linking, independent], services);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAll(
            linking.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            independent.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, linking.InvocationCount);
        Assert.False(linking.SecondStarted.Task.IsCompleted);
        Assert.Equal(DurableJobState.Running, await ReadStateAsync(first.JobId));
        Assert.Equal(DurableJobState.Pending, await ReadStateAsync(second.JobId));
        Assert.Equal(DurableJobState.Running, await ReadStateAsync(other.JobId));

        linking.Release();
        await linking.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        independent.Release();
        await WaitForStateAsync(first.JobId, DurableJobState.Succeeded);
        await WaitForStateAsync(second.JobId, DurableJobState.Succeeded);
        await WaitForStateAsync(other.JobId, DurableJobState.Succeeded);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(2, linking.InvocationCount);
    }
}

public sealed class DurableJobRegistrationLibraryLinkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("4")]
    [InlineData("32")]
    public void RegistrationAlwaysLimitsLibraryLinkJobsToOne(string? configuredLimit)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jobs:MaximumConcurrentJobs"] = "4",
            ["Jobs:TypeConcurrencyLimits:independent"] = "2"
        };
        if (configuredLimit != null)
            values[$"Jobs:TypeConcurrencyLimits:{LibraryRecordingLinkQueue.JobType}"] = configuredLimit;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var services = new ServiceCollection().AddDurableJobs(configuration).BuildServiceProvider();

        var options = services.GetRequiredService<DurableJobOptions>();

        Assert.Equal(1, options.TypeConcurrencyLimits[LibraryRecordingLinkQueue.JobType]);
        Assert.Equal(2, options.TypeConcurrencyLimits["independent"]);
        Assert.Equal(4, options.MaximumConcurrentJobs);
    }
}
