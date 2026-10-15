using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

internal static class PlaylistRetentionTargets
{
    internal static async Task<IReadOnlyDictionary<int, Guid?>> ResolveAsync(AllstarrDbContext db, ITrackIdentityService identities,
        ProviderActorContext actor, IReadOnlyList<DurablePlaylistEntryProjection> entries, CancellationToken cancellationToken)
    {
        static string PublicId(DurablePlaylistEntryProjection entry) => $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}";
        var publicIds = entries.Select(PublicId).Concat(entries.Where(item =>
                item.ProviderRoutes.FirstOrDefault()?.IsManual != true && item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value.ToString("D"))).Distinct(StringComparer.Ordinal).ToArray();
        var known = await identities.ResolveRecordingsAsync(actor, publicIds, cancellationToken);
        var manual = entries.Where(item => item.ProviderRoutes.FirstOrDefault()?.IsManual == true &&
                known.GetValueOrDefault(PublicId(item)) == null)
            .Select(item => allstarr.Services.Local.LocalLibraryService.ParseExternalResource(PublicId(item)))
            .Where(item => item.isExternal && item.type == "song").ToArray();
        var providers = manual.Select(item => item.provider).Distinct().ToArray();
        var externalIds = manual.Select(item => item.externalId).Distinct().ToArray();
        var published = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                               join artifact in db.Set<ProviderDownloadArtifactEntity>().AsNoTracking() on route.SourceArtifactId equals artifact.Id
                               join recording in db.CanonicalRecordings.AsNoTracking() on route.CanonicalRecordingId equals recording.Id
                               where EF.Parameter(providers).Contains(artifact.ProviderId) && EF.Parameter(externalIds).Contains(artifact.SourceExternalTrackId)
                               select new { artifact.ProviderId, artifact.SourceExternalTrackId, RecordingId = recording.MergedIntoId ?? recording.Id })
            .Distinct().ToArrayAsync(cancellationToken);
        var historical = published.GroupBy(item => (item.ProviderId, item.SourceExternalTrackId))
            .ToDictionary(group => group.Key, group => group.Select(item => item.RecordingId).Distinct().ToArray());
        var result = new Dictionary<int, Guid?>();
        foreach (var entry in entries)
        {
            Guid? recordingId = known.GetValueOrDefault(PublicId(entry))?.Id;
            if (recordingId == null && entry.ProviderRoutes.FirstOrDefault()?.IsManual == true)
            {
                var parsed = allstarr.Services.Local.LocalLibraryService.ParseExternalResource(PublicId(entry));
                if (parsed.provider != null && parsed.externalId != null &&
                    historical.TryGetValue((parsed.provider, parsed.externalId), out var matches) && matches.Length == 1)
                    recordingId = matches[0];
            }
            else if (recordingId == null && entry.CanonicalRecordingId is { } canonical)
                recordingId = known.GetValueOrDefault(canonical.ToString("D"))?.Id;
            result[entry.Position] = recordingId;
        }
        return result;
    }

}
