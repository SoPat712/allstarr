using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class DurableJobQueueTests
{
    [Fact]
    public async Task StorageVerificationHasOnlyItsExactSystemPolicyAndNeverProviderAuthority()
    {
        var queued = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<ManagedStorageRecoveryPayload>(
            ManagedStorageRecovery.JobType, "copy-policy", new(Guid.CreateVersion7())));
        var claim = (await _queue.ClaimNextAsync("copy-policy"))!;
        Assert.True((await _queue.ReauthorizeAsync(claim)).Authorized);
        Assert.Null(claim.OwnerUserId);
        Assert.Null(claim.ProviderAccountId);
        Assert.Null(claim.ProviderCapability);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("system:managed-storage", (await db.Jobs.SingleAsync(item => item.Id == queued.JobId)).ScopeKey);
        Assert.False((await _queue.ReauthorizeAsync(claim with { Type = "recording.keep" })).Authorized);
        Assert.False((await _queue.ReauthorizeAsync(claim with { OwnerUserId = _userId })).Authorized);
        Assert.False((await _queue.ReauthorizeAsync(claim with { ProviderAccountId = Guid.CreateVersion7(), ProviderCapability = "download" })).Authorized);
        await Assert.ThrowsAsync<ArgumentException>(() => _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            ManagedStorageRecovery.JobType, "copy-user", new { }, _userId)));
        await Assert.ThrowsAsync<ArgumentException>(() => _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            ManagedStorageRecovery.JobType, "copy-provider", new { }, ProviderAccountId: Guid.CreateVersion7(), Capability: "download")));
        await Assert.ThrowsAsync<ArgumentException>(() => _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("recording.keep", "no-user-copy", new { })));
    }

    [Fact]
    public async Task OrdinaryUserPolicyCannotBeRecastAsStorageVerification()
    {
        await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("favorite.process", "copy-forged", new { }, _userId));
        var claim = (await _queue.ClaimNextAsync("copy-forged"))!;
        Assert.False((await _queue.ReauthorizeAsync(claim with { Type = ManagedStorageRecovery.JobType, OwnerUserId = null })).Authorized);
    }
}
