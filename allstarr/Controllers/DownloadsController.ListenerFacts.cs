using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Playback;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public partial class DownloadsController
{
    private async Task<ManagedDownloadFile[]> AddListenerFactsAsync(ManagedDownloadFile[] files, string storage, CancellationToken token)
    {
        if (_contextFactory == null || files.Length == 0 ||
            HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession
            { AllstarrUserId: { } viewerId } session || session.ExpiresAtUtc <= DateTime.UtcNow) return files;
        await using var db = await _contextFactory.CreateDbContextAsync(token);
        var backend = session.BackendType.Trim().ToLowerInvariant();
        var users = await db.Users.AsNoTracking().Where(item => item.Enabled && !item.IsSynthetic &&
            item.BackendType == backend && item.BackendInstanceId == session.BackendInstanceId).ToDictionaryAsync(item => item.Id, token);
        if (!users.TryGetValue(viewerId, out var viewer) || viewer.BackendPrincipalId != session.UserId ||
            viewer.IsAdmin != session.IsAdministrator) return files;

        var paths = new Dictionary<string, string>();
        foreach (var file in files)
            if (!file.Path.StartsWith("imported/", StringComparison.Ordinal) && TryResolveExistingFile(storage, file.Path, out _, out var path))
                paths[file.Path] = path;
        var physicalPaths = paths.Values.ToArray();
        var managed = await db.ManagedFiles.AsNoTracking().Where(item => item.RemovedAt == null && physicalPaths.Contains(item.CanonicalPath)).ToListAsync(token);
        var importedIds = files.Where(item => item.Path.StartsWith("imported/", StringComparison.Ordinal))
            .Select(item => Guid.TryParse(item.Path[9..], out var id) ? id : Guid.Empty).ToArray();
        var fileIds = managed.Select(item => item.Id).Concat(importedIds).ToArray();
        var references = await db.ManagedFileReferences.AsNoTracking().Where(item => fileIds.Contains(item.ManagedFileId) && item.ReleasedAt == null)
            .Select(item => new { item.ManagedFileId, item.ReferenceKey }).ToListAsync(token);
        var heartIds = references.Select(item => ReferenceId(item.ReferenceKey, "heart", 1)).OfType<Guid>().ToArray();
        var hearts = await db.Set<FavoriteStateRecord>().AsNoTracking().Where(item => heartIds.Contains(item.Id) && item.IsFavorite &&
            item.Protocol == backend && item.BackendInstanceId == session.BackendInstanceId).ToDictionaryAsync(item => item.Id, item => item.OwnerUserId, token);
        var playlistIds = references.Select(item => ReferenceId(item.ReferenceKey, "playlist", 1)).OfType<Guid>().ToArray();
        var playlists = await db.PlaylistLinks.AsNoTracking().Where(item => playlistIds.Contains(item.Id) &&
            item.TargetProtocol == backend && item.TargetBackendInstanceId == session.BackendInstanceId).ToDictionaryAsync(item => item.Id, item => item.OwnerUserId, token);
        var keepers = new Dictionary<Guid, Dictionary<Guid, HashSet<string>>>();
        foreach (var reference in references)
        {
            var owner = ReferenceId(reference.ReferenceKey, "keep:user", 2);
            var reason = "keep";
            if (owner == null && ReferenceId(reference.ReferenceKey, "heart", 1) is { } heart && hearts.TryGetValue(heart, out var heartOwner))
            { owner = heartOwner; reason = "favorite"; }
            if (owner == null && ReferenceId(reference.ReferenceKey, "playlist", 1) is { } playlist && playlists.TryGetValue(playlist, out var playlistOwner))
            { owner = playlistOwner; reason = "playlist"; }
            if (owner == null || !users.ContainsKey(owner.Value)) continue;
            if (!keepers.TryGetValue(reference.ManagedFileId, out var people)) keepers[reference.ManagedFileId] = people = [];
            if (!people.TryGetValue(owner.Value, out var reasons)) people[owner.Value] = reasons = [];
            reasons.Add(reason);
        }

        var mappings = await db.DownloadedSongMappings.AsNoTracking().Where(item => physicalPaths.Contains(item.LocalPath)).ToListAsync(token);
        var routes = await db.Set<RecordingKeptRouteEntity>().AsNoTracking().Where(item => fileIds.Contains(item.ManagedFileId))
            .Select(item => new { item.ManagedFileId, item.CanonicalRecordingId }).ToListAsync(token);
        var adoptions = await db.Set<LegacyMediaAdoptionRecord>().AsNoTracking().Where(item => fileIds.Contains(item.ManagedFileId) && item.CanonicalRecordingId != null)
            .Select(item => new { item.ManagedFileId, RecordingId = item.CanonicalRecordingId!.Value }).ToListAsync(token);
        var providerReferences = mappings.Select(item => $"{item.ProviderId}:{item.ExternalId}").Distinct().ToArray();
        var itemReferences = mappings.Select(item => $"ext-{item.ProviderId}-song-{item.ExternalId}").Distinct().ToArray();
        var identities = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
            item.ResourceKind == Core.Capabilities.ProviderResourceKind.Track && item.CatalogNamespace == "default" &&
            (item.Verification == Core.Storage.ProviderIdentityVerification.Verified || item.Verification == Core.Storage.ProviderIdentityVerification.Pinned) &&
            providerReferences.Contains(item.ProviderId + ":" + item.ExternalId))
            .Select(item => new { item.ProviderId, item.ExternalId, item.CanonicalRecordingId, item.Scope, item.ProviderAccountId }).ToListAsync(token);
        var identityRecordings = mappings.ToDictionary(mapping => mapping.Id, mapping =>
        {
            var matching = identities.Where(item => item.ProviderId == mapping.ProviderId && item.ExternalId == mapping.ExternalId).ToArray();
            var accountRecordings = matching.Where(item => item.Scope == Core.Storage.ProviderIdentityScope.Account &&
                mapping.ProviderAccountId != null && item.ProviderAccountId == mapping.ProviderAccountId)
                .Select(item => item.CanonicalRecordingId).ToArray();
            return accountRecordings.Length > 0 ? accountRecordings : matching.Where(item => item.Scope == Core.Storage.ProviderIdentityScope.Catalog)
                .Select(item => item.CanonicalRecordingId).ToArray();
        });
        var knownRecordingIds = routes.Select(item => item.CanonicalRecordingId).Concat(adoptions.Select(item => item.RecordingId))
            .Concat(identityRecordings.Values.SelectMany(item => item)).Distinct().ToArray();
        var roots = await db.CanonicalRecordings.AsNoTracking().Where(item => knownRecordingIds.Contains(item.Id))
            .Select(item => new { item.Id, RootId = item.MergedIntoId ?? item.Id }).ToArrayAsync(token);
        var rootIds = roots.Select(item => item.RootId).Distinct().ToArray();
        var aliases = await db.CanonicalRecordings.AsNoTracking().Where(item => item.MergedIntoId != null && rootIds.Contains(item.MergedIntoId.Value))
            .Select(item => new { item.Id, RootId = item.MergedIntoId!.Value }).ToArrayAsync(token);
        var rootById = roots.Concat(aliases).GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First().RootId);
        var equivalentRecordings = roots.Concat(aliases).GroupBy(item => item.RootId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Id).Append(group.Key).Distinct().ToArray());
        var recordingIds = equivalentRecordings.Values.SelectMany(item => item).Distinct().ToArray();
        var userIds = users.Keys.ToArray();
        var now = DateTimeOffset.UtcNow;
        var history = await db.ListeningEvents.AsNoTracking().Where(item => userIds.Contains(item.OwnerUserId) &&
            (viewer.IsAdmin || item.OwnerUserId == viewerId) && item.Protocol == backend && item.BackendInstanceId == session.BackendInstanceId &&
            (item.State == Core.Intelligence.ListeningEventState.Completed || item.State == Core.Intelligence.ListeningEventState.Playing || item.PositionTicks > 0) &&
            (item.ListenedAt ?? item.StartedAt) <= now &&
            (item.CanonicalRecordingId != null && recordingIds.Contains(item.CanonicalRecordingId.Value) ||
             item.ProviderTrackReference != null && providerReferences.Contains(item.ProviderTrackReference) || itemReferences.Contains(item.TrackReference)))
            .GroupBy(item => new { item.CanonicalRecordingId, item.ProviderAccountId, item.ProviderTrackReference, item.TrackReference })
            .Select(group => new DownloadPlay(group.Key.CanonicalRecordingId, group.Key.ProviderAccountId, group.Key.ProviderTrackReference,
                group.Key.TrackReference, group.Max(item => item.ListenedAt ?? item.StartedAt))).ToListAsync(token);
        var traceRows = await PlaybackStartTimingStore.RecentEvents(db, now).Where(item => item.ActorUserId != null &&
            userIds.Contains(item.ActorUserId.Value) && (viewer.IsAdmin || item.ActorUserId == viewerId) && item.CreatedAt <= now)
            .ToArrayAsync(token);
        history.AddRange(traceRows.Select(PlayTraceService.Read).OfType<PlayTraceDetail>()
            .Where(item => item.Timing.FirstByteMs != null && string.Equals(item.Request.Protocol, backend, StringComparison.OrdinalIgnoreCase))
            .Select(item => new DownloadPlay(item.Resolution.CanonicalRecordingId, null,
                item.Resolution.Provider != null && item.Resolution.ExternalId != null ? $"{item.Resolution.Provider}:{item.Resolution.ExternalId}" : null,
                item.Request.ItemId, item.StartedAt)));

        return files.Select(file =>
        {
            var path = paths.GetValueOrDefault(file.Path);
            var fileId = file.Path.StartsWith("imported/", StringComparison.Ordinal) && Guid.TryParse(file.Path[9..], out var imported)
                ? imported : managed.FirstOrDefault(item => PathComparer.Equals(item.CanonicalPath, path))?.Id;
            var mapping = mappings.FirstOrDefault(item => PathComparer.Equals(item.LocalPath, path));
            var providerReference = mapping == null ? null : $"{mapping.ProviderId}:{mapping.ExternalId}";
            var itemReference = mapping == null ? null : $"ext-{mapping.ProviderId}-song-{mapping.ExternalId}";
            var recordings = routes.Where(item => item.ManagedFileId == fileId).Select(item => item.CanonicalRecordingId)
                .Concat(adoptions.Where(item => item.ManagedFileId == fileId).Select(item => item.RecordingId))
                .Concat(mapping == null ? [] : identityRecordings[mapping.Id])
                .SelectMany(id => equivalentRecordings.GetValueOrDefault(rootById.GetValueOrDefault(id, id)) ?? [id]).ToHashSet();
            var by = fileId != null && keepers.TryGetValue(fileId.Value, out var people)
                ? people.OrderByDescending(item => item.Key == viewerId).ThenBy(item => users[item.Key].DisplayName, StringComparer.Ordinal)
                    .Select(item => new DownloadKeeper(item.Key == viewerId ? "You" : viewer.IsAdmin && !string.IsNullOrWhiteSpace(users[item.Key].DisplayName)
                        ? users[item.Key].DisplayName : "Someone else", item.Key == viewerId, item.Value.Order(StringComparer.Ordinal).ToArray())).ToArray()
                : [];
            return file with
            {
                KeptBy = by,
                LastPlayedAt = history.Where(item => item.CanonicalRecordingId is { } recording && recordings.Contains(recording) ||
                    (item.CanonicalRecordingId == null || recordings.Count == 0) && mapping != null && item.ProviderAccountId == mapping.ProviderAccountId &&
                    (providerReference != null && item.ProviderTrackReference == providerReference ||
                     itemReference != null && item.TrackReference == itemReference)).Select(item => item.At).Max()
            };
        }).ToArray();
    }

    private sealed record DownloadPlay(Guid? CanonicalRecordingId, Guid? ProviderAccountId, string? ProviderTrackReference, string? TrackReference, DateTimeOffset? At);

    private static Guid? ReferenceId(string key, string prefix, int index)
    {
        if (!key.StartsWith(prefix + ":", StringComparison.Ordinal)) return null;
        var parts = key.Split(':');
        return parts.Length > index && Guid.TryParseExact(parts[index], "N", out var id) ? id : null;
    }
}
