using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;

namespace allstarr.Core.Jobs;

public sealed record DurableJobAttemptHistory(
    int AttemptNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Outcome,
    string? ErrorCode,
    string? ErrorMessage);

internal static class DurableJobHistory
{
    public static void Append(AllstarrDbContext context, DurableJobRecord job, DateTimeOffset now)
    {
        context.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = job.OwnerUserId,
            Category = "job-attempt",
            Action = "attempt-completed",
            Outcome = job.LastAttemptOutcome ?? "interrupted",
            CorrelationId = job.CorrelationId,
            SubjectType = "job",
            SubjectId = job.Id.ToString("D"),
            CreatedAt = now,
            DetailsJson = JsonSerializer.Serialize(new
            {
                jobId = job.Id,
                jobType = job.Type,
                attemptNumber = job.AttemptCount,
                startedAtTicks = (job.LastAttemptStartedAt ?? job.StartedAt ?? job.CreatedAt).UtcTicks,
                completedAtTicks = job.LastAttemptCompletedAt?.UtcTicks,
                outcome = job.LastAttemptOutcome,
                errorCode = job.LastErrorCode,
                errorMessage = job.LastErrorMessage
            })
        });
    }

    public static DurableJobAttemptHistory? Read(AuditEventRecord item, Guid jobId)
    {
        try
        {
            using var document = JsonDocument.Parse(item.DetailsJson);
            var value = document.RootElement;
            if (!value.TryGetProperty("jobId", out var id) || !id.TryGetGuid(out var savedId) || savedId != jobId ||
                !value.TryGetProperty("attemptNumber", out var number) || !number.TryGetInt32(out var attempt) || attempt <= 0 ||
                !value.TryGetProperty("startedAtTicks", out var start) || !start.TryGetInt64(out var ticks))
                return null;
            var started = new DateTimeOffset(ticks, TimeSpan.Zero);
            DateTimeOffset? completed = value.TryGetProperty("completedAtTicks", out var end) && end.TryGetInt64(out var endTicks)
                ? new DateTimeOffset(endTicks, TimeSpan.Zero) : null;
            return new(attempt, started, completed, SafeOperationalText.Sanitize(item.Outcome, 50),
                Text(value, "errorCode", 100), Text(value, "errorMessage", 1000));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement value, string name, int maximum) =>
        value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String
            ? SafeOperationalText.Sanitize(text.GetString(), maximum) : null;
}
