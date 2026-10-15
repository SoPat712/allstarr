using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class DurableJobQueueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeptCleanupPolicyPreservesUserLineageAfterInitiatorDisable(bool disableBeforeEnqueue)
    {
        if (disableBeforeEnqueue) await DisableCleanupInitiatorAsync();
        var receipt = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<KeptRecordingCleanupPayload>(
            RecordingKeptFiles.KeptCleanupJobType, "cleanup-policy", new(Guid.NewGuid(), Guid.NewGuid()), _userId));
        var claim = (await _queue.ClaimNextAsync("cleanup-policy"))!;
        if (!disableBeforeEnqueue) await DisableCleanupInitiatorAsync();

        var authorization = await _queue.ReauthorizeAsync(claim);

        Assert.True(authorization.Authorized, authorization.SafeMessage);
        Assert.Equal(_userId, claim.OwnerUserId);
        Assert.Null(claim.ProviderAccountId);
        Assert.Null(claim.ProviderCapability);
        Assert.Equal("household_kept_cleanup", claim.PolicySnapshot.GetProperty("AuthorizationRule").GetString());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal($"user:{_userId:N}", (await db.Jobs.SingleAsync(item => item.Id == receipt.JobId)).ScopeKey);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _queue.EnqueueAsync(
            new DurableJobEnqueueRequest<object>("favorite.process", "disabled-user-work", new { }, _userId)));
    }

    [Fact]
    public async Task KeptCleanupPolicyCannotCarryProviderAuthority()
    {
        var account = await AddProviderAccount("deezer", _userId);

        await Assert.ThrowsAsync<ArgumentException>(() => _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            RecordingKeptFiles.KeptCleanupJobType, "cleanup-account", new { }, _userId,
            ProviderAccountId: account.Id, Capability: "download")));
    }

    [Theory]
    [InlineData("provider.download")]
    [InlineData("recording.keep")]
    [InlineData("other")]
    public async Task KeptCleanupPolicyCannotAuthorizeAnotherJobType(string otherType)
    {
        await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            RecordingKeptFiles.KeptCleanupJobType, "cleanup-type", new { }, _userId));
        var claim = (await _queue.ClaimNextAsync("cleanup-type"))!;

        Assert.False((await _queue.ReauthorizeAsync(claim with { Type = otherType })).Authorized);
    }

    [Fact]
    public async Task KeptCleanupPolicyCannotBeInferredFromAnOrdinaryUserSnapshot()
    {
        await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("favorite.process", "ordinary-policy", new { }, _userId));
        var claim = (await _queue.ClaimNextAsync("ordinary-policy"))!;

        Assert.False((await _queue.ReauthorizeAsync(claim with { Type = RecordingKeptFiles.KeptCleanupJobType })).Authorized);
    }

    [Fact]
    public async Task KeptCleanupPolicyStillRequiresAKnownInitiatingUser()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            RecordingKeptFiles.KeptCleanupJobType, "cleanup-no-user", new { })));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            RecordingKeptFiles.KeptCleanupJobType, "cleanup-unknown-user", new { }, Guid.NewGuid())));
    }

    private async Task DisableCleanupInitiatorAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        (await db.Users.SingleAsync(item => item.Id == _userId)).Enabled = false;
        await db.SaveChangesAsync();
    }
}
