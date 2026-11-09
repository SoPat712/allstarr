using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ListeningHistoryResolutionTests
{
    [Fact]
    public async Task PacedProviderBacklog_ResumesSameJobAndReportsCompletedAndRemainingSongs()
    {
        for (var song = 0; song < 3; song++) await Add("Missing " + song);
        var searched = 0;
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(() =>
            {
                if (searched++ == 0) time.Advance(TimeSpan.FromHours(2));
                return ProviderOutcome<IReadOnlyList<Song>>.Success([]);
            });
        var handler = Handler();
        await handler.RecoverAsync(default);
        var first = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-paced", [ListeningHistoryResolution.JobTypeName]));
        var checking = new List<string?>();
        var continued = await handler.ExecuteAsync(new(first, services)
        {
            ReportProgressAsync = (update, token) => { checking.Add(update.Track); return jobs.ReportProgressAsync(first, update, token); }
        }, default);
        Assert.Contains(checking, track => track?.StartsWith("Missing ", StringComparison.Ordinal) == true);
        Assert.Equal(DurableJobCompletionKind.Deferred, continued.Kind);
        Assert.True(continued.IsContinuation);
        await jobs.CompleteAsync(first, continued);
        await using (var db = factory.CreateDbContext())
        {
            var job = Assert.Single(await db.Jobs.ToArrayAsync());
            Assert.Equal(first.JobId, job.Id);
            Assert.Null(job.LastErrorCode);
            Assert.Equal(0, job.DeferralCount);
            using var payload = JsonDocument.Parse(job.PayloadJson);
            Assert.Equal(1, payload.RootElement.GetProperty("Cursor").GetInt32());
            Assert.Equal(3, payload.RootElement.GetProperty("Total").GetInt32());
            Assert.Equal(1d / TimeSpan.FromHours(2).TotalSeconds,
                payload.RootElement.GetProperty("ThroughputPerSecond").GetDouble(), 8);
            var progress = Assert.Single(await db.AuditEvents.Where(item => item.Category == "job-progress").ToArrayAsync());
            using var details = JsonDocument.Parse(progress.DetailsJson);
            Assert.Equal(1, details.RootElement.GetProperty("completed").GetInt32());
            Assert.Equal(3, details.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(JsonValueKind.Null, details.RootElement.GetProperty("track").ValueKind);
            using var outcomes = JsonDocument.Parse(details.RootElement.GetProperty("message").GetString()!);
            var latest = Assert.Single(outcomes.RootElement.GetProperty("recent").EnumerateArray());
            Assert.StartsWith("Missing ", latest.GetProperty("title").GetString());
            Assert.Equal("no-candidates", latest.GetProperty("reason").GetString());
            Assert.Equal(1, outcomes.RootElement.GetProperty("reasons").GetProperty("no-candidates").GetInt32());
            Assert.Empty(await db.AuditEvents.Where(item => item.Category == "job-attempt").ToArrayAsync());
        }

        clock.UtcNow += continued.RetryDelay!.Value;
        await handler.RecoverAsync(default);
        var second = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-paced", [ListeningHistoryResolution.JobTypeName]));
        Assert.Equal(first.JobId, second.JobId);
        var completed = await handler.ExecuteAsync(new(second, services)
        {
            ReportProgressAsync = (update, token) => jobs.ReportProgressAsync(second, update, token)
        }, default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completed.Kind);
        await jobs.CompleteAsync(second, completed);
        await using var finished = factory.CreateDbContext();
        var final = Assert.Single(await finished.Jobs.ToArrayAsync());
        Assert.Equal(3, JsonDocument.Parse(final.PayloadJson).RootElement.GetProperty("Cursor").GetInt32());
        Assert.Equal(3, await finished.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "history-resolution"));
        Assert.Single(await finished.AuditEvents.Where(item => item.Category == "job-progress").ToArrayAsync());
        Assert.Single(await finished.AuditEvents.Where(item => item.Category == "job-attempt").ToArrayAsync());
    }

    [Fact]
    public async Task ProviderOutageWithoutProgress_UsesGrowingBackoffAndKeepsSongsUnsettled()
    {
        await Add("Unavailable");
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Failure(new(ProviderErrorKind.TransientFailure)));
        await Handler().RecoverAsync(default);
        Guid? jobId = null;
        foreach (var seconds in new[] { 2, 4, 8 })
        {
            var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-blocked", [ListeningHistoryResolution.JobTypeName]));
            jobId ??= claim.JobId;
            Assert.Equal(jobId, claim.JobId);
            var result = await Handler().ExecuteAsync(new(claim, services)
            {
                ReportProgressAsync = (update, token) => jobs.ReportProgressAsync(claim, update, token)
            }, default);
            Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
            Assert.False(result.IsContinuation);
            await jobs.CompleteAsync(claim, result);
            await using var db = factory.CreateDbContext();
            var job = Assert.Single(await db.Jobs.ToArrayAsync());
            Assert.Equal(TimeSpan.FromSeconds(seconds), job.AvailableAt - clock.UtcNow);
            Assert.Equal("history_resolution_blocked", job.LastErrorCode);
            Assert.Equal(0, JsonDocument.Parse(job.PayloadJson).RootElement.GetProperty("Cursor").GetInt32());
            Assert.Null((await db.ListeningEvents.SingleAsync()).CanonicalRecordingId);
            Assert.Empty(await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "history-resolution").ToArrayAsync());
            Assert.Single(await db.AuditEvents.Where(item => item.Category == "job-progress").ToArrayAsync());
            clock.UtcNow += TimeSpan.FromSeconds(seconds);
        }
    }
}
