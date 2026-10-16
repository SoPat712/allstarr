using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Operations;

public sealed record BackgroundTaskSnapshot(string Id, string Name, double CadenceSeconds,
    string State, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt);

// Observes existing maintenance loops; their owners retain all scheduling and execution.
public sealed class BackgroundTaskActivity(IDbContextFactory<AllstarrDbContext> factory, TimeProvider time,
    ILogger<BackgroundTaskActivity> logger)
{
    private readonly ConcurrentDictionary<string, BackgroundTaskSnapshot> tasks = new(StringComparer.Ordinal);

    public void Register(string id, string name, TimeSpan interval, bool immediate = true, TimeSpan? initialDelay = null) =>
        tasks.TryAdd(id, new(id, name, interval.TotalSeconds, "Waiting", null, time.GetUtcNow() + (initialDelay ?? (immediate ? TimeSpan.Zero : interval))));

    public void Started(string id) => tasks.AddOrUpdate(id,
        _ => throw new InvalidOperationException("Register the background task first."),
        (_, task) => task with { State = "Running", NextRunAt = null });

    public async Task CompletedAsync(string id, bool success, CancellationToken token)
    {
        if (!tasks.TryGetValue(id, out var task)) return;
        var now = time.GetUtcNow();
        var next = task with { State = success ? "Scheduled" : "Retrying", LastRunAt = now, NextRunAt = now.AddSeconds(task.CadenceSeconds) };
        tasks[id] = next;
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                Category = "background-task",
                Action = "run-completed",
                SubjectType = "background-task",
                SubjectId = id,
                Outcome = success ? "success" : "failed",
                DetailsJson = JsonSerializer.Serialize(new { name = task.Name, nextRunAt = next.NextRunAt }),
                CreatedAt = now
            });
            await db.SaveChangesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { logger.LogDebug("Background task activity could not be saved"); }
    }

    public async Task<IReadOnlyList<BackgroundTaskSnapshot>> ReadAsync(CancellationToken token)
    {
        var current = tasks.Values.ToArray();
        var ids = current.Select(x => x.Id).ToArray();
        await using var db = await factory.CreateDbContextAsync(token);
        var previous = await db.AuditEvents.AsNoTracking().Where(x => x.Category == "background-task" &&
                x.SubjectType == "background-task" && ids.Contains(x.SubjectId))
            .GroupBy(x => x.SubjectId).Select(group => group.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First())
            .ToDictionaryAsync(x => x.SubjectId!, token);
        return current.Select(task => task.LastRunAt == null && previous.TryGetValue(task.Id, out var last)
                ? task with { LastRunAt = last.CreatedAt } : task).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
}
