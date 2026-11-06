using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed class PlaylistSourceEmptyException(string message) : InvalidOperationException(message)
{
    public const string Code = "playlist_source_empty_kept";
}

public sealed partial class PlaylistOrchestrationService
{
    private sealed record EmptySourceRead(Guid SnapshotId, string Scope, DateTimeOffset FirstReadAt, DateTimeOffset ConfirmAfter);

    private async Task GuardEmptySourceAsync(ProtocolExecutionContext execution, PlaylistLinkRecord link,
        int entryCount, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var last = await LastEmptySourceReadAsync(db, link, cancellationToken);
        if (entryCount > 0)
        {
            await ResetEmptySourceReadAsync(db, execution, link, last, cancellationToken);
            return;
        }
        var current = await db.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.OwnerUserId == link.OwnerUserId && item.PlaylistLinkId == link.Id && item.PublishedAt.HasValue)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);
        if (current == null || !await db.PlaylistSourceEntries.AnyAsync(item => item.PlaylistSourceSnapshotId == current.Id, cancellationToken))
            return;
        var now = _clock.UtcNow;
        var scope = Hash($"{link.SourceProviderId}:{link.ProviderAccountId}:{link.SourcePlaylistIdHash}");
        EmptySourceRead? pending = null;
        if (last?.Action == "source-empty-kept")
        {
            using var details = JsonDocument.Parse(last.DetailsJson);
            pending = details.RootElement.GetProperty("emptyRead").Deserialize<EmptySourceRead>();
        }
        if (pending?.SnapshotId != current.Id || pending.Scope != scope)
        {
            var interval = TimeSpan.FromDays(1);
            var schedule = link.ScheduleId.HasValue
                ? await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.Id == link.ScheduleId && item.OwnerUserId == link.OwnerUserId, cancellationToken)
                : null;
            if (schedule != null)
            {
                var next = DurableScheduleEngine.GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, now);
                var following = next.HasValue
                    ? DurableScheduleEngine.GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, next.Value)
                    : null;
                if (next.HasValue && following.HasValue) interval = following.Value - next.Value;
            }
            pending = new(current.Id, scope, now, now + interval);
        }
        // Confirm emptiness only after consecutive successful empty reads span a full scheduled interval.
        // Manual retries cannot shorten that interval; failed or nonempty reads restart confirmation.
        // Links without a schedule use one day. The durable read record survives process restarts.
        else if (now >= pending.ConfirmAfter)
            return;
        var sourceName = link.SourceProviderId == "lastfm" ? "Last.fm" : "The playlist source";
        var message = $"{sourceName} returned an empty list, so the previous songs were kept. The next scheduled refresh will check again.";
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = link.OwnerUserId,
            Category = "playlist",
            Action = "source-empty-kept",
            Outcome = "conflicted",
            CorrelationId = execution.CorrelationId,
            SubjectType = "playlist-link",
            SubjectId = link.Id.ToString("N"),
            CreatedAt = now,
            DetailsJson = JsonSerializer.Serialize(new
            {
                message,
                providerId = link.SourceProviderId,
                playlistLinkId = link.Id.ToString("N"),
                playlistName = current.Name,
                emptyRead = pending
            })
        });
        await db.SaveChangesAsync(cancellationToken);
        throw new PlaylistSourceEmptyException(message);
    }

    private static Task<AuditEventRecord?> LastEmptySourceReadAsync(AllstarrDbContext db,
        PlaylistLinkRecord link, CancellationToken cancellationToken) =>
        db.AuditEvents.AsNoTracking().Where(item => item.ActorUserId == link.OwnerUserId &&
                item.Category == "playlist" && item.SubjectType == "playlist-link" && item.SubjectId == link.Id.ToString("N") &&
                (item.Action == "source-empty-kept" || item.Action == "source-empty-reset"))
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken);

    private async Task ResetEmptySourceReadAsync(ProtocolExecutionContext execution, PlaylistLinkRecord link,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await ResetEmptySourceReadAsync(db, execution, link,
            await LastEmptySourceReadAsync(db, link, cancellationToken), cancellationToken);
    }

    private async Task ResetEmptySourceReadAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        PlaylistLinkRecord link, AuditEventRecord? last, CancellationToken cancellationToken)
    {
        if (last?.Action != "source-empty-kept") return;
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = link.OwnerUserId,
            Category = "playlist",
            Action = "source-empty-reset",
            Outcome = "success",
            CorrelationId = execution.CorrelationId,
            SubjectType = "playlist-link",
            SubjectId = link.Id.ToString("N"),
            CreatedAt = _clock.UtcNow,
            DetailsJson = JsonSerializer.Serialize(new
            {
                message = "Empty playlist confirmation was reset by a nonempty or unsuccessful source read.",
                providerId = link.SourceProviderId,
                playlistLinkId = link.Id.ToString("N")
            })
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
