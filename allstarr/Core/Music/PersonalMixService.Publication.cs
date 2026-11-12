using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed record MixPublicationStatus(string Status, DateTimeOffset? LastPublishedAt, string? Reason);

public sealed partial class PersonalMixService
{
    public async Task<IReadOnlyDictionary<Guid, MixPublicationStatus>> PublicationStatusAsync(UserRecord user,
        IReadOnlyList<PersonalMix> cards, bool enabled, CancellationToken token)
    {
        var added = cards.Where(mix => mix.Added).ToArray();
        if (added.Length == 0) return new Dictionary<Guid, MixPublicationStatus>();
        var owner = user.Id;
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await RowAsync(db, user, token);
        var document = row == null ? new PersonalMixDocument() : JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
        var jobs = await db.Jobs.AsNoTracking().Where(job => job.OwnerUserId == owner && job.Type == JobType &&
            (job.State == DurableJobState.Pending || job.State == DurableJobState.Running ||
             job.State == DurableJobState.RetryScheduled || job.State == DurableJobState.Failed)).ToArrayAsync(token);
        var refreshes = jobs.Select(job => (Job: job, Request: PublicationRefresh(job.PayloadJson)))
            .Where(item => item.Request is { DryRun: false }).ToArray();
        var publishedCards = added.Where(mix => mix.Kind != "external").ToArray();
        var localIds = publishedCards.SelectMany(mix => mix.Songs)
            .Where(item => item.Song.IsLocal).Select(item => item.Song.Id).Distinct().ToArray();
        string[] allowed = [];
        if (localIds.Length > 0)
        {
            var context = await ContextAsync(user, token);
            allowed = await LibraryTrackAccess.Query(db, context, await libraryAccess.ResolveAsync(context, token))
                .Where(item => localIds.Contains(item.BackendItemId)).Select(item => item.BackendItemId).ToArrayAsync(token);
        }
        var result = publishedCards.ToDictionary(card => card.Id, card =>
        {
            var mix = document.Mixes.Values.SingleOrDefault(item => item.Id == card.Id);
            // Virtual playlists read the saved mix directly, so saving publishes its current songs atomically.
            var published = enabled && mix is { Added: true, Generation: > 0 };
            var publishedAt = published ? row?.RetrievedAt : null;
            var relevant = refreshes.Where(item => item.Request is { } request && (request.MixId == card.Id || card.Kind != "custom" &&
                (request.MixId == Guid.Empty || card.Kind.StartsWith("daily-", StringComparison.Ordinal) &&
                    document.Mixes.Values.Any(other => other.Id == request.MixId && other.Kind.StartsWith("daily-", StringComparison.Ordinal))))).ToArray();
            if (relevant.Any(item => item.Job.State is DurableJobState.Pending or DurableJobState.Running or DurableJobState.RetryScheduled))
                return new MixPublicationStatus("updating", publishedAt, null);
            if (relevant.Any(item => item.Job.State == DurableJobState.Failed &&
                (mix?.UpdatedAt is not { } refreshed || (item.Job.CompletedAt ?? item.Job.UpdatedAt) >= refreshed)))
                return new MixPublicationStatus("needsLook", publishedAt, "Served by Allstarr. This mix couldn't be refreshed. Try refreshing it again.");
            if (!published)
                return new MixPublicationStatus("needsLook", null, enabled
                    ? "Served by Allstarr. This mix isn't available in your apps yet. Try refreshing it."
                    : "Served by Allstarr. Your admin hasn't allowed publishing mixes to apps.");
            if (card.Songs.Any(item =>
            {
                if (item.Song.IsLocal) return !allowed.Contains(item.Song.Id);
                var parsed = LocalLibraryService.ParseExternalResource(item.Song.Id);
                return string.IsNullOrWhiteSpace(item.Song.ExternalProvider ?? parsed.provider) || string.IsNullOrWhiteSpace(item.Song.ExternalId ?? parsed.externalId);
            }))
                return new MixPublicationStatus("needsLook", publishedAt, "Served by Allstarr. Some songs are no longer available. Try refreshing this mix.");
            if (card.Kind != "custom" && mix!.UpdatedAt is { } updated &&
                DurableScheduleEngine.GetNextOccurrence(PersonalMixRules.Cron(card.Kind), document.TimeZoneId, updated) <= clock.UtcNow)
                return new MixPublicationStatus("needsLook", publishedAt, "Served by Allstarr. This mix is past its refresh time. Try refreshing it again.");
            return new MixPublicationStatus("healthy", publishedAt, null);
        });
        await ExternalPublicationStatusAsync(db, user, added.Where(card => card.Kind == "external").ToArray(), result, enabled, token);
        return result;
    }

    private async Task ExternalPublicationStatusAsync(AllstarrDbContext db, UserRecord user, PersonalMix[] cards,
        Dictionary<Guid, MixPublicationStatus> result, bool enabled, CancellationToken token)
    {
        if (cards.Length == 0) return;
        var ids = cards.Select(card => card.Id).ToArray();
        var links = await ExternalLinks(db, user).AsNoTracking().Where(link => ids.Contains(link.Id)).ToArrayAsync(token);
        var runs = await db.PlaylistSyncRuns.AsNoTracking().Where(run => run.OwnerUserId == user.Id && ids.Contains(run.PlaylistLinkId))
            .GroupBy(run => run.PlaylistLinkId).Select(group => group.OrderByDescending(run => run.Generation).First()).ToArrayAsync(token);
        var runIds = runs.Select(run => run.Id).ToArray();
        var unavailable = await db.PlaylistSyncEntryResults.AsNoTracking().Where(entry => runIds.Contains(entry.PlaylistSyncRunId) &&
            (entry.Outcome == PlaylistEntryOutcome.Failed || entry.Outcome == PlaylistEntryOutcome.Unsupported))
            .Select(entry => entry.PlaylistSyncRunId).Distinct().ToArrayAsync(token);
        var schedules = await db.JobSchedules.AsNoTracking().Where(schedule => schedule.OwnerUserId == user.Id &&
            schedule.JobType == DurableScheduleEngine.PlaylistSyncJobType).ToArrayAsync(token);
        var jobs = await db.Jobs.AsNoTracking().Where(job => job.OwnerUserId == user.Id && job.Type == DurableScheduleEngine.PlaylistSyncJobType &&
            (job.State == DurableJobState.Pending || job.State == DurableJobState.Running || job.State == DurableJobState.RetryScheduled ||
             job.State == DurableJobState.Failed)).ToArrayAsync(token);
        var snapshots = await db.PlaylistSourceSnapshots.AsNoTracking().Where(snapshot => snapshot.OwnerUserId == user.Id &&
            ids.Contains(snapshot.PlaylistLinkId)).ToArrayAsync(token);
        foreach (var card in cards)
        {
            var link = links.SingleOrDefault(item => item.Id == card.Id);
            var run = runs.SingleOrDefault(item => item.PlaylistLinkId == card.Id);
            var snapshot = snapshots.Where(item => item.PlaylistLinkId == card.Id && item.PublishedAt.HasValue)
                .OrderByDescending(item => item.SnapshotVersion).FirstOrDefault();
            var publishedAt = snapshot?.PublishedAt;
            if (card.AppsUnavailableReason != null)
            {
                result[card.Id] = new("needsLook", publishedAt, card.AppsUnavailableReason);
                continue;
            }
            var served = link?.Mode == PlaylistLinkMode.Virtual;
            var delivery = served ? "Served by Allstarr." : link?.TargetProtocol == "jellyfin" ? "Saved in Jellyfin." : "Saved in your music server.";
            string? reason = null;
            var relevant = jobs.Where(job => PlaylistRefreshLink(job.PayloadJson) == card.Id).ToArray();
            var updating = relevant.Any(job => job.State is DurableJobState.Pending or DurableJobState.Running or DurableJobState.RetryScheduled) ||
                run?.State is PlaylistSyncState.Pending or PlaylistSyncState.Running;
            if (relevant.Any(job => job.State == DurableJobState.Failed && (publishedAt == null || (job.CompletedAt ?? job.UpdatedAt) >= publishedAt)) ||
                run?.State is PlaylistSyncState.Failed or PlaylistSyncState.Conflicted or PlaylistSyncState.PartiallySucceeded)
                reason = "This mix couldn't be updated. Try refreshing it again.";
            else if (link != null && !enabled && IsExternalLink(link))
                reason = "Your admin hasn't allowed publishing mixes to apps.";
            else if (link == null || snapshot == null || publishedAt == null || !served && string.IsNullOrWhiteSpace(link.TargetPlaylistId))
                reason = "This mix isn't available in your apps yet. Try refreshing it.";
            else if (!served && (run == null || run.VerificationCode != "verified" || run.PlaylistSourceSnapshotId != snapshot.Id ||
                snapshots.Any(item => item.PlaylistLinkId == card.Id && item.SnapshotVersion > snapshot.SnapshotVersion)))
                reason = "The saved playlist hasn't been verified with the current songs. Try refreshing it.";
            else if (run != null && unavailable.Contains(run.Id))
                reason = "Some songs are no longer available. Try refreshing this mix.";
            else if (link.ScheduleId is { } scheduleId && schedules.SingleOrDefault(item => item.Id == scheduleId) is { Enabled: true } schedule &&
                DurableScheduleEngine.GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, publishedAt.Value) <= clock.UtcNow)
                reason = "This mix is past its refresh time. Try refreshing it again.";
            result[card.Id] = new(updating ? "updating" : reason == null ? "healthy" : "needsLook", publishedAt,
                updating || reason == null ? null : $"{delivery} {reason}");
        }
    }

    private static Guid? PlaylistRefreshLink(string json)
    {
        try
        {
            using var payload = JsonDocument.Parse(json);
            return payload.RootElement.TryGetProperty("PlaylistLinkId", out var id) && id.TryGetGuid(out var link) ? link : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static MixRefreshRequest? PublicationRefresh(string json)
    {
        try
        {
            using var payload = JsonDocument.Parse(json);
            return payload.RootElement.ValueKind == JsonValueKind.Object
                ? payload.RootElement.Deserialize<MixRefreshRequest>()
                : new MixRefreshRequest(payload.RootElement.GetGuid());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}
