using allstarr.Core.Storage;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.Matching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    public async Task<VerifiedProviderDownloadArtifact?> FindPromotionSourceAsync(ProviderActorContext actor,
        Guid recordingId, PromotionProfile profile, CancellationToken token)
    {
        var recording = await identities.ResolveRecordingAsync(actor, recordingId.ToString("D"), token);
        if (recording == null) return null;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        var sources = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                             join identity in db.CanonicalRecordings.AsNoTracking() on route.CanonicalRecordingId equals identity.Id
                             join artifact in db.Set<ProviderDownloadArtifactEntity>().AsNoTracking() on route.SourceArtifactId equals artifact.Id
                             where identity.Id == recording.Id || identity.MergedIntoId == recording.Id
                             select new { Route = route, Artifact = artifact }).ToArrayAsync(token);
        foreach (var source in sources.Where(item => PromotionQuality.Accepts(profile, item.Artifact.Codec, item.Artifact.BitDepth, item.Artifact.SampleRate) ||
                         string.IsNullOrWhiteSpace(item.Artifact.Codec) || item.Artifact.SampleRate == null ||
                         PromotionQuality.IsLossless(item.Artifact.Codec) && item.Artifact.BitDepth == null)
                     .OrderByDescending(item => PromotionQuality.IsLossless(item.Artifact.Codec)).ThenByDescending(item => item.Artifact.BitDepth)
                     .ThenByDescending(item => item.Artifact.SampleRate))
        {
            var candidate = await FindCandidateAsync(db, scope.ServiceProvider.GetRequiredService<FilePlacementService>(),
                scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>(), source.Route.CanonicalRecordingId, true, token);
            if (candidate?.File.Id != source.Route.ManagedFileId) continue;
            var file = candidate.File;
            var artifact = source.Artifact;
            ProviderMediaFormat? media = null;
            if (!PromotionQuality.Accepts(profile, artifact.Codec, artifact.BitDepth, artifact.SampleRate))
            {
                try
                {
                    media = (await (audioVerifier ?? new allstarr.Core.Music.ProviderAudioVerifier()).VerifyFileAsync(file.CanonicalPath,
                    new(LegacyMime(file.CanonicalPath), "unknown", "unknown"), null, token)).Media;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
                if (!PromotionQuality.Accepts(profile, media.Codec, media.BitDepth, media.SampleRate)) continue;
            }
            return new(artifact.Id, artifact.WorkspaceRecordId, file.CanonicalPath, file.ContentSha256, file.Length,
                artifact.OwnerUserId, artifact.DurableJobId, artifact.ProviderId, artifact.ProviderAccountId, artifact.State, file.Id)
            {
                Codec = media?.Codec ?? artifact.Codec,
                Container = media?.Container ?? artifact.Container,
                MimeType = media?.MimeType ?? artifact.MimeType,
                BitDepth = media?.BitDepth ?? artifact.BitDepth,
                SampleRate = media?.SampleRate ?? artifact.SampleRate,
                Bitrate = media?.Bitrate ?? artifact.Bitrate,
                Channels = media?.Channels ?? artifact.Channels
            };
        }
        var imported = await db.Set<LegacyMediaAdoptionRecord>().AsNoTracking().Where(item => item.Kept &&
            (item.CanonicalRecordingId == recording.Id || db.CanonicalRecordings.Any(identity =>
                identity.Id == item.CanonicalRecordingId && identity.MergedIntoId == recording.Id))).ToArrayAsync(token);
        foreach (var adoption in imported)
        {
            var file = await scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>().GetAsync(adoption.ManagedFileId, token);
            if (file == null) continue;
            try
            {
                ValidateHousehold(file);
                await scope.ServiceProvider.GetRequiredService<FilePlacementService>().VerifyOwnedFileAsync(file, token);
                var facts = await (audioVerifier ?? new allstarr.Core.Music.ProviderAudioVerifier()).VerifyFileAsync(file.CanonicalPath,
                    new(LegacyMime(file.CanonicalPath), "unknown", "unknown"), null, token);
                var media = facts.Media;
                if (!PromotionQuality.Accepts(profile, media.Codec, media.BitDepth, media.SampleRate)) continue;
                return new(file.Id, Guid.Empty, file.CanonicalPath, file.ContentSha256, file.Length, actor.EffectiveUserId,
                    file.SourceJobId ?? actor.DurableJobId ?? Guid.Empty, adoption.ProviderId ?? "kept", null, ProviderDownloadArtifactState.Verified, file.Id)
                {
                    Codec = media.Codec,
                    Container = media.Container,
                    MimeType = media.MimeType,
                    BitDepth = media.BitDepth,
                    SampleRate = media.SampleRate,
                    Bitrate = media.Bitrate,
                    Channels = media.Channels
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    public async Task ReleasePromotedCopyAsync(Guid userId, Guid recordingId, string libraryId, Guid operationId,
        string correlationId, CancellationToken token, bool releaseExplicitKeep = true)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == userId && item.Enabled, token);
        var tracks = libraryAccess == null ? db.LibraryTracks.Where(item => item.OwnerUserId == userId) :
            LibraryTrackAccess.Query(db, await libraryAccess.ResolveUserAsync(userId, token));
        if (!await tracks.AnyAsync(item => item.BackendInstanceId == user.BackendInstanceId &&
                item.BackendLibraryId == libraryId && item.CanonicalRecordingId == recordingId, token))
            throw new InvalidOperationException("The library link must be confirmed before releasing a kept copy.");
        var routes = await db.Set<RecordingKeptRouteEntity>().Where(item => item.CanonicalRecordingId == recordingId ||
            db.CanonicalRecordings.Any(recording => recording.Id == item.CanonicalRecordingId && recording.MergedIntoId == recordingId)).ToArrayAsync(token);
        var imported = await db.Set<LegacyMediaAdoptionRecord>().Where(item => item.CanonicalRecordingId == recordingId ||
            db.CanonicalRecordings.Any(recording => recording.Id == item.CanonicalRecordingId && recording.MergedIntoId == recordingId))
            .Select(item => item.ManagedFileId).ToArrayAsync(token);
        var fileIds = routes.Select(item => item.ManagedFileId).Concat(imported).Distinct().ToArray();
        if (fileIds.Length == 0) return;
        var now = clock.UtcNow;
        var keepPrefix = $"keep:user:{userId:N}:";
        var hearts = await db.Set<allstarr.Core.Favorites.FavoriteStateRecord>().AsNoTracking()
            .Where(item => item.OwnerUserId == userId).Select(item => item.Id).ToArrayAsync(token);
        var playlistIds = await db.Set<PlaylistLinkRecord>().AsNoTracking().Where(item => item.OwnerUserId == userId).Select(item => item.Id).ToArrayAsync(token);
        var heartKeys = hearts.Select(id => $"heart:{id:N}").ToHashSet(StringComparer.Ordinal);
        var playlistPrefixes = playlistIds.Select(id => $"playlist:{id:N}:").ToArray();
        foreach (var fileId in fileIds)
        {
            var references = await db.ManagedFileReferences.AsNoTracking().Where(item => item.ManagedFileId == fileId && item.ReleasedAt == null)
                .Select(item => item.ReferenceKey).ToArrayAsync(token);
            foreach (var key in references.Where(key => releaseExplicitKeep && key.StartsWith(keepPrefix, StringComparison.Ordinal) || heartKeys.Contains(key) ||
                         playlistPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))))
                await StageReleaseInterestAsync(db, key, userId, operationId, now, correlationId, token, fileId);
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
}
