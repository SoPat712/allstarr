using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Health;

public sealed class HealthSmokeAlertJobHandler(IDbContextFactory<AllstarrDbContext> factory,
    EncryptedSecretStore secrets, IHttpClientFactory clients, HealthSmokeService health) : IDurableJobHandler
{
    public string JobType => HealthSmokeService.AlertJobType;
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken token)
    {
        var payload = context.Claim.Payload.Deserialize<HealthSmokeAlertPayload>();
        if (payload?.Changes is not { Length: > 0 } changes || changes.Length > HealthSmokeProtocolProbe.StepNames.Length ||
            changes.Any(change => !HealthSmokeProtocolProbe.StepNames.Contains(change.Step) || change.State is not ("failed" or "recovered")) ||
            changes.Select(change => change.Step).Distinct(StringComparer.Ordinal).Count() != changes.Length)
            return DurableJobCompletion.Failure("health_alert_invalid", "The health alert is unavailable.");
        using var configuration = await health.LockConfigurationAsync(token);
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.HealthSmokeRuns.SingleOrDefaultAsync(item => item.Id == payload.RunId && item.OwnerUserId == context.Claim.OwnerUserId, token);
        var binding = row == null ? null : HealthSmokeService.Parse<HealthSmokeRunBinding>(row.BindingJson);
        if (row == null || binding?.Configuration.WebhookReferenceId is not { } reference)
            return DurableJobCompletion.Failure("health_alert_unavailable", "The health alert has no configured destination.");
        var schedule = await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.Id == binding.ScheduleId &&
            item.JobType == HealthSmokeService.JobType && item.OwnerUserId == row.OwnerUserId, token);
        var current = schedule == null ? null : HealthSmokeService.Parse<HealthSmokeSchedule>(schedule.PayloadTemplateJson);
        if (current?.WebhookReferenceId != reference)
        {
            row.AlertStatus = "none";
            await db.SaveChangesAsync(token);
            return DurableJobCompletion.Success();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var lease = await secrets.OpenAsync(reference, new(null, HealthSmokeService.WebhookPurpose, AllowShared: true), deadline.Token);
            using var client = clients.CreateClient("health-webhook");
            using var request = new HttpRequestMessage(HttpMethod.Post, lease.ReadUtf8())
            {
                Content = JsonContent.Create(new
                {
                    eventType = changes.Any(change => change.State == "failed") ? "allstarr.health.failed" : "allstarr.health.recovered",
                    protocol = binding.Protocol,
                    changes = changes.Select(change => new { step = change.Step, state = change.State }),
                    occurredAt = row.CompletedAt
                })
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", context.Claim.JobId.ToString("N"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            row.AlertStatus = "delivered";
            await db.SaveChangesAsync(token);
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            row.AlertStatus = "failed";
            await db.SaveChangesAsync(token);
            return DurableJobCompletion.Retry("health_alert_delivery_failed", "The health webhook could not be reached.", TimeSpan.FromMinutes(1));
        }
    }
}
