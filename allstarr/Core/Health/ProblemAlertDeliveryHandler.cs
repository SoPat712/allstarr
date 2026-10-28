using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Health;

public sealed class ProblemAlertDeliveryHandler(IDbContextFactory<AllstarrDbContext> factory,
    EncryptedSecretStore secrets, IHttpClientFactory clients, ProblemAlertService alerts, IPlatformClock clock) : IDurableJobHandler
{
    public string JobType => ProblemAlertService.DeliveryJobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken token)
    {
        using var configuration = await alerts.LockDeliveryAsync(token);
        var payload = context.Claim.Payload.Deserialize<ProblemAlertDeliveryPayload>();
        await using var db = await factory.CreateDbContextAsync(token);
        var row = payload == null ? null : await db.NotificationDeliveries.SingleOrDefaultAsync(item =>
            item.Id == payload.DeliveryId && item.JobId == context.Claim.JobId, token);
        if (row == null) return DurableJobCompletion.Failure("alert_unavailable", "The notification is unavailable.");
        if (row.Status is "delivered" or "cancelled") return DurableJobCompletion.Success();
        if (row.Status == "failed") return DurableJobCompletion.Failure("alert_delivery_failed", row.SafeError);
        var channel = await db.NotificationChannels.SingleOrDefaultAsync(item => item.Id == row.ChannelId &&
            item.OwnerUserId == context.Claim.OwnerUserId, token);
        var authorized = channel != null && channel.Revision == row.ChannelRevision && await alerts.ChannelAuthorizedAsync(db, channel, token);
        if (authorized && channel!.HealthScheduleId is { } scheduleId)
        {
            var schedule = await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.Id == scheduleId &&
                item.JobType == HealthSmokeService.JobType, token);
            var current = schedule == null ? null : HealthSmokeService.Parse<HealthSmokeSchedule>(schedule.PayloadTemplateJson);
            authorized = current?.WebhookReferenceId == channel.DestinationReferenceId;
        }
        if (authorized && row.IncidentId is { } incidentId)
        {
            var incident = await db.ProblemIncidents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == incidentId, token);
            authorized = incident != null && incident.Transition == row.Transition && (incident.OwnerUserId == null ? channel!.Household :
                !channel!.Household && incident.OwnerUserId == channel.OwnerUserId);
            if (authorized && incident!.Kind is "provider-login" or "source-failure" && Guid.TryParse(incident.Resource.Split('/')[0], out var accountId))
                authorized = await db.ProviderAccounts.AnyAsync(item => item.Id == accountId && item.Enabled &&
                    item.OwnerUserId == incident!.OwnerUserId, token);
        }
        if (authorized)
            authorized = await db.SecretReferences.AnyAsync(item => item.Id == channel!.DestinationReferenceId && item.RevokedAt == null &&
                (channel.HealthScheduleId == null ? item.UserId == channel.OwnerUserId && item.Purpose == ProblemAlertService.DestinationPurpose :
                    item.UserId == null && item.Purpose == HealthSmokeService.WebhookPurpose), token);
        if (!authorized && channel != null && channel.Enabled && channel.Revision == row.ChannelRevision &&
            !await db.SecretReferences.AnyAsync(item => item.Id == channel.DestinationReferenceId && item.RevokedAt == null, token))
        {
            row.Status = "failed";
            row.SafeError = "The notification destination is unavailable.";
            row.NextAttemptAt = null;
            await db.SaveChangesAsync(token);
            return DurableJobCompletion.Failure("alert_destination_unavailable", row.SafeError);
        }
        if (!authorized)
        {
            row.Status = "cancelled";
            row.NextAttemptAt = null;
            await db.SaveChangesAsync(token);
            return DurableJobCompletion.Success();
        }
        row.Attempt = context.Claim.AttemptNumber;
        row.Status = "sending";
        await db.SaveChangesAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var delay = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Clamp(row.Attempt - 1, 0, 7))));
        try
        {
            using var lease = await secrets.OpenAsync(channel!.DestinationReferenceId,
                channel.HealthScheduleId == null ? new(channel.OwnerUserId, ProblemAlertService.DestinationPurpose) :
                    new(null, HealthSmokeService.WebhookPurpose, AllowShared: true), deadline.Token);
            var uri = ProblemAlertService.ValidateDestination(lease.ReadUtf8());
            if (!await alerts.DestinationPermittedAsync(db, channel.OwnerUserId, uri, token))
            {
                row.Status = "cancelled";
                row.NextAttemptAt = null;
                await db.SaveChangesAsync(token);
                return DurableJobCompletion.Success();
            }
            using var client = clients.CreateClient("problem-alerts");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            var message = new ProblemAlertMessage(1, row.Id, row.IncidentId, row.Event, row.Kind, row.Severity,
                ProblemAlertService.Message(row.Kind, row.Event), ProblemAlertService.Action(row.Kind), row.CreatedAt, JsonSerializer.Deserialize<ProblemAlertDetail>(row.DetailJson, ProblemAlertService.Json));
            var details = message.Detail?.FreeBytes is { } free ? $" Free bytes: {free}. Roots: {string.Join(", ", message.Detail.Roots ?? [])}." :
                message.Detail?.HealthSteps is { Length: > 0 } steps ? $" Failed checks: {string.Join(", ", steps)}." : "";
            request.Content = channel.HealthScheduleId.HasValue ? JsonContent.Create(new
            {
                eventType = row.Event == "resolved" ? "allstarr.health.recovered" : "allstarr.health.failed",
                protocol = message.Detail?.Protocol,
                changes = (message.Detail?.HealthSteps ?? []).Select(step => new { step, state = row.Event == "resolved" ? "recovered" : "failed" }),
                occurredAt = row.CreatedAt,
                incidentId = row.IncidentId,
                deliveryId = row.Id
            }) : channel.Kind switch
            {
                "discord" => JsonContent.Create(new
                {
                    content = $"Allstarr: {message.Event} ({message.Severity}) — {message.Message}{details} {message.Action}",
                    allowed_mentions = new { parse = Array.Empty<string>() }
                }),
                "ntfy" => new StringContent($"Allstarr: {message.Event} ({message.Severity})\n{message.Message}{details}\n{message.Action}"),
                _ => JsonContent.Create(message, options: ProblemAlertService.Json)
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", row.Id.ToString("N"));
            if (channel.Kind == "ntfy") request.Headers.TryAddWithoutValidation("Title", "Allstarr problem alert");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.IsSuccessStatusCode)
            {
                row.Status = "delivered";
                row.SafeError = null;
                row.NextAttemptAt = null;
                if (row.HealthRunId is { } runId)
                    await db.HealthSmokeRuns.Where(item => item.Id == runId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.AlertStatus, "delivered"), token);
                await db.SaveChangesAsync(token);
                return DurableJobCompletion.Success();
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var after = response.Headers.RetryAfter;
                var requested = after?.Delta ?? (after?.Date - clock.UtcNow);
                if (requested is { } retry && retry > delay) delay = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 30, 3600));
            }
            if ((int)response.StatusCode < 500 && response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout))
            {
                row.Status = "failed";
                row.SafeError = "The destination rejected the notification.";
                row.NextAttemptAt = null;
                await db.SaveChangesAsync(token);
                return DurableJobCompletion.Failure("alert_rejected", row.SafeError);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or System.Security.Cryptography.CryptographicException)
        {
            row.Status = "failed";
            row.SafeError = "The notification destination is unavailable.";
            row.NextAttemptAt = null;
            await db.SaveChangesAsync(token);
            return DurableJobCompletion.Failure("alert_destination_unavailable", row.SafeError);
        }
        catch (Exception) { }
        row.Status = row.Attempt >= 5 ? "failed" : "retrying";
        row.SafeError = "The notification could not be delivered.";
        row.NextAttemptAt = row.Attempt >= 5 ? null : clock.UtcNow.Add(delay);
        if (row.HealthRunId is { } healthRunId)
            await db.HealthSmokeRuns.Where(item => item.Id == healthRunId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.AlertStatus, "failed"), token);
        await db.SaveChangesAsync(token);
        return DurableJobCompletion.Retry("alert_delivery_failed", row.SafeError, delay);
    }
}
