using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    internal static bool IsExternalLink(PlaylistLinkRecord link) => link.ImportMode == PlaylistImportMode.Linked &&
        (link.SourceProviderId == "listenbrainz" && link.SourcePlaylistId.StartsWith("createdfor:", StringComparison.Ordinal) ||
         link.SourceProviderId == "lastfm" && (link.SourcePlaylistId is "loved" or "similar" ||
             link.SourcePlaylistId.StartsWith("top:", StringComparison.Ordinal)));

    private static IQueryable<PlaylistLinkRecord> ExternalLinks(AllstarrDbContext db, UserRecord user) =>
        db.PlaylistLinks.Where(link => link.OwnerUserId == user.Id && link.TargetProtocol == user.BackendType &&
            link.TargetBackendInstanceId == user.BackendInstanceId && db.ProviderAccounts.Any(account =>
                account.Id == link.ProviderAccountId && account.OwnerUserId == user.Id && account.Enabled &&
                account.ProviderId == link.SourceProviderId));

    private async Task<IReadOnlyList<PersonalMix>> ExternalMixesAsync(UserRecord user, CancellationToken token, Guid? id = null)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var links = (await ExternalLinks(db, user).Where(link => id == null || link.Id == id).AsNoTracking().OrderBy(link => link.CreatedAt).ToArrayAsync(token))
            .Where(IsExternalLink).ToArray();
        var reader = new DurablePlaylistProjectionReader(factory, libraryAccess, providers, policies);
        var policy = await policies.ResolveForUserAsync(user.Id, token);
        var result = new List<PersonalMix>(links.Length);
        foreach (var link in links)
        {
            var projection = await reader.ReadByLinkIdAsync(user.Id, link.Id, token, user.Id);
            var songs = projection?.Entries.Where(entry => entry.RouteKind is "local" or "external").Select(entry =>
            {
                var metadata = projection.SourceEntries.FirstOrDefault(item => item.Position == entry.Position)?.Metadata;
                return new Song
                {
                    Id = entry.BackendItemId ?? $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}",
                    Title = entry.Title,
                    Artist = entry.Artists.FirstOrDefault() ?? "",
                    Artists = entry.Artists.ToList(),
                    Album = entry.Album ?? "",
                    Duration = (int)((entry.DurationMilliseconds ?? 0) / 1000),
                    IsLocal = entry.RouteKind == "local",
                    ExternalProvider = entry.RouteProviderId,
                    ExternalId = entry.ExternalId,
                    CanonicalRecordingId = entry.CanonicalRecordingId,
                    Isrc = entry.Isrc,
                    ExplicitContentLyrics = metadata?.IsExplicit is { } explicitContent ? explicitContent ? 1 : 0 : null
                };
            }).ToArray() ?? [];
            var scores = await taste.ScoreManyAsync(user.Id, songs, token);
            var items = songs.Where(song => !scores[song].Blocked && policy.Includes(song))
                .Select(song => new PersonalMixItem(link.Id, projection!.SnapshotVersion, SafeSong(song),
                    link.SourceProviderId, "From your linked playlist", 0)).ToArray();
            result.Add(new(link.Id, "external", projection?.Name ?? ExternalName(link),
                "Kept in sync · " + ExternalName(link), projection?.SnapshotVersion ?? 0,
                projection?.RetrievedAt, new(items.Length > 0, items.Length > 0 ? "Your linked playlist is ready." : "Waiting for playable songs from your linked playlist."),
                link.Enabled, items)
            { SourceLabel = link.SourceProviderId == "lastfm" ? "Last.fm" : "ListenBrainz" });
        }
        return result;
    }

    internal static PersonalMix PendingExternalMix(PlaylistLinkRecord link) => new(link.Id, "external", ExternalName(link),
        "Kept in sync · " + ExternalName(link), 0, null, new(false, "Matching songs…"), true, [])
    { SourceLabel = link.SourceProviderId == "lastfm" ? "Last.fm" : "ListenBrainz" };

    private static string ExternalName(PlaylistLinkRecord link) => link.SourceProviderId == "listenbrainz"
        ? "Created for you · " + link.SourcePlaylistId[11..].Replace('-', ' ')
        : link.SourcePlaylistId switch
        {
            "loved" => "Loved on Last.fm",
            "similar" => "Similar to your top tracks",
            _ => "Your top tracks · " + link.SourcePlaylistId.Split(':')[1]
        };

    private async Task<bool> SetExternalInAppsAsync(UserRecord user, Guid id, bool enabled, CancellationToken token, string timeZoneId)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var link = await ExternalLinks(db, user).SingleOrDefaultAsync(item => item.Id == id, token);
        if (link == null || !IsExternalLink(link)) return false;
        if (link.Enabled == enabled && (!enabled || link.ScheduleId != null)) return true;
        JobScheduleRecord? schedule = null;
        if (link.ScheduleId is { } scheduleId)
        {
            schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == scheduleId &&
                item.OwnerUserId == user.Id && item.JobType == DurableScheduleEngine.PlaylistSyncJobType, token);
            if (schedule == null) throw new UnauthorizedAccessException();
        }
        if (schedule == null && enabled)
        {
            schedule = new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user.Id,
                JobType = DurableScheduleEngine.PlaylistSyncJobType,
                CronExpression = "0 4 * * *",
                TimeZoneId = timeZoneId,
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                CreatedAt = clock.UtcNow
            };
            db.JobSchedules.Add(schedule);
            link.ScheduleId = schedule.Id;
        }
        if (schedule != null)
        {
            schedule.Enabled = enabled;
            schedule.NextRunAt = enabled ? DurableScheduleEngine.GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, clock.UtcNow) : null;
            schedule.UpdatedAt = clock.UtcNow;
            schedule.Revision++;
        }
        link.Enabled = enabled;
        link.UpdatedAt = clock.UtcNow;
        link.Revision++;
        await db.SaveChangesAsync(token);
        return true;
    }

    internal async Task<bool> IsExternalVisibleAsync(ProtocolExecutionContext context, PlaylistLinkRecord link, CancellationToken token)
    {
        if (!IsExternalLink(link)) return true;
        if (context.Actor?.EffectiveUserId is not { } owner || link.OwnerUserId != owner ||
            !(bool)(await settings.GetAsync(PublishKey, token)).Value) return false;
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            await LoadAsync(db, user, token);
            var enabled = await db.PlaylistLinks.AsNoTracking().AnyAsync(item => item.Id == link.Id && item.Enabled, token);
            return enabled &&
                await db.ProviderAccounts.AsNoTracking().AnyAsync(account => account.Id == link.ProviderAccountId &&
                    account.ProviderId == link.SourceProviderId && account.OwnerUserId == owner && account.Enabled, token);
        }
        finally { writes.Release(); }
    }

    internal async Task<VirtualPlaylistReadModel> FilterExternalTracksAsync(ProtocolExecutionContext context,
        VirtualPlaylistReadModel playlist, CancellationToken token)
    {
        var owner = context.Actor!.EffectiveUserId!.Value;
        var songs = playlist.Tracks.Select(track => new Song
        {
            Id = track.BackendItemId,
            Title = track.Title,
            Artist = track.Artist,
            Artists = [track.Artist],
            Album = track.Album ?? "",
            IsLocal = track.RouteKind == allstarr.Core.Matching.TrackRouteKind.Local,
            ExternalProvider = track.RouteProviderId,
            ExternalId = track.RouteExternalId,
            CanonicalRecordingId = track.SourceMetadata?.CanonicalRecordingId,
            ExplicitContentLyrics = track.SourceMetadata?.IsExplicit is { } explicitContent
                ? explicitContent ? 1 : 0 : track.PlaybackExplicitContentLyrics
        }).ToArray();
        var scores = await taste.ScoreManyAsync(owner, songs, token);
        var policy = await policies.ResolveForUserAsync(owner, token);
        return playlist with
        {
            Tracks = playlist.Tracks.Where((_, index) =>
                !scores[songs[index]].Blocked && policy.Includes(songs[index])).ToArray()
        };
    }
}
