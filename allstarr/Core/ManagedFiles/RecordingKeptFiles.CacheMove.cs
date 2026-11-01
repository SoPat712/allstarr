using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    private async Task<KeptRecordingFile?> MoveRequestedCacheAsync(DurableJobClaim claim, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
        var proof = await FindKeepAsync(db, claim.JobId, claim.OwnerUserId, claim.AttemptNumber, token) ?? throw Denied();
        if (proof.Payload.CachedSource is not { } cached) return null;
        var mapping = await db.DownloadedSongMappings.SingleOrDefaultAsync(item => item.Id == cached.MappingId, token);
        if (mapping == null) return null;
        var prepared = await db.ProviderDownloadArtifacts.SingleOrDefaultAsync(item => item.DurableJobId == claim.JobId &&
            item.ProviderArtifactId.StartsWith("cached-keep:") && item.SupersededAt == null, token);
        if (prepared == null && CachedMoveSource(mapping) != null) return null;
        await ValidateCachedKeepSourceAsync(db, proof.OwnerUserId,
            new(proof.Payload.ProviderId, ProviderResourceKind.Track, proof.Payload.ExternalId),
            prepared == null ? cached : new(mapping.Id, mapping.Revision), token);
        var source = CachedMoveSource(mapping) ?? mapping.LocalPath;
        ManagedFileOwnershipEntity owned;
        if (prepared == null)
        {
            if (!File.Exists(source)) return null;
            ManagedFileRecord snapshot;
            ProviderMediaFormat media;
            try
            {
                var root = Path.GetDirectoryName(source)!;
                var identity = files.InspectLegacyFile(root, source);
                var info = new FileInfo(source);
                snapshot = await files.PrepareLegacyAdoptionAsync(new(placement.RootId, root, source,
                    info.Length, identity.DeviceId, identity.FileId, info.LastWriteTimeUtc.Ticks, false,
                    proof.Payload.ProviderId, proof.Payload.ExternalId, proof.Payload.Metadata), clock.UtcNow, token);
                media = new(LegacyMime(source), "unknown", "unknown");
                var facts = await (audioVerifier ?? new allstarr.Core.Music.ProviderAudioVerifier()).VerifyFileAsync(source,
                    media, proof.Payload.Metadata.DurationMilliseconds / 1000d, token);
                media = facts.Media;
                if (mapping.CacheIdentityJson != null && JsonNode.Parse(mapping.CacheIdentityJson)?["Sha256"]?.GetValue<string>() is { } expected &&
                    expected != snapshot.ContentSha256) return null;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or JsonException) { return null; }
            var candidate = await FindCandidateAsync(db, files,
                scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>(), proof.RecordingId, true, token);
            if (candidate is { Route: null }) candidate = null;
            var workspace = await artifacts.CreateWorkspaceAsync(new(proof.OwnerUserId, claim.JobId,
                proof.Payload.ProviderId, mapping.ProviderAccountId, "cached-keep"), token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (await FindKeepAsync(db, claim.JobId, claim.OwnerUserId, claim.AttemptNumber, token) == null) throw Denied();
            await ValidateCachedKeepSourceAsync(db, proof.OwnerUserId,
                new(proof.Payload.ProviderId, ProviderResourceKind.Track, proof.Payload.ExternalId), cached, token);
            var destination = candidate?.File.CanonicalPath ?? Path.Combine(Path.GetFullPath(placement.RootPath),
                ManagedPathTemplate.Render(KeptPathTemplate(placement.PathTemplate, proof.Payload.Metadata.Track),
                    new(proof.Payload.Metadata.Title, proof.Payload.Metadata.Artists[0], proof.Payload.Metadata.Album,
                        proof.Payload.Metadata.AlbumArtist, Track: proof.Payload.Metadata.Track,
                        Extension: Path.GetExtension(source))));
            if (candidate == null && (File.Exists(destination) || await db.ManagedFiles.AnyAsync(item => item.CanonicalPath == destination, token)))
                destination = Path.Combine(Path.GetDirectoryName(destination)!, $"{Path.GetFileNameWithoutExtension(destination)}-{claim.JobId:N}{Path.GetExtension(destination)}");
            owned = candidate == null ? new()
            {
                Id = snapshot.Id,
                RootId = placement.RootId,
                TargetRootPath = Path.GetFullPath(placement.RootPath),
                CanonicalPath = destination,
                ContentSha256 = snapshot.ContentSha256,
                Length = snapshot.Length,
                FileSystemDeviceId = snapshot.FileSystemDeviceId,
                FileSystemFileId = snapshot.FileSystemFileId,
                FileSystemLinkCount = snapshot.FileSystemLinkCount,
                PlacementMethod = ManagedFilePlacementMethod.Adopted,
                ScopeKey = ManagedFileScopeKey.Create(null, placement.RootId),
                IsManaged = true,
                CreatedAt = clock.UtcNow
            } : await db.ManagedFiles.SingleAsync(item => item.Id == candidate.File.Id, token);
            if (candidate == null) db.ManagedFiles.Add(owned);
            await db.SaveChangesAsync(token);
            await new EfManagedFileOwnershipStore(db).StageReferenceAsync(owned.Id,
                ExplicitKeepKey(proof.OwnerUserId, proof.Payload.RecordingId), clock.UtcNow, token);
            prepared = new()
            {
                Id = Guid.CreateVersion7(),
                WorkspaceRecordId = workspace.RecordId,
                WorkspaceId = workspace.Reference.WorkspaceId,
                OwnerUserId = proof.OwnerUserId,
                DurableJobId = claim.JobId,
                ProviderId = proof.Payload.ProviderId,
                ProviderAccountId = mapping.ProviderAccountId,
                SourceExternalTrackId = proof.Payload.ExternalId,
                SourceAccountRevision = mapping.ProviderAccountId == null ? null : await db.ProviderAccounts
                    .Where(item => item.Id == mapping.ProviderAccountId).Select(item => item.Revision).SingleAsync(token),
                ProviderArtifactId = $"cached-keep:{owned.Id:N}",
                RelativePath = Path.GetFileName(source),
                ContentSha256 = owned.ContentSha256,
                Length = owned.Length,
                MimeType = candidate?.MimeType ?? media.MimeType,
                Container = media.Container,
                Codec = media.Codec,
                Bitrate = media.Bitrate,
                SampleRate = media.SampleRate,
                BitDepth = media.BitDepth,
                Channels = media.Channels,
                State = ProviderDownloadArtifactState.Verified,
                CreatedAt = clock.UtcNow,
                VerifiedAt = clock.UtcNow
            };
            db.ProviderDownloadArtifacts.Add(prepared);
            var shared = await db.DownloadedSongMappings.Where(item => item.LocalPath == source).ToArrayAsync(token);
            foreach (var row in shared)
            {
                var json = row.CacheIdentityJson == null ? new JsonObject() : JsonNode.Parse(row.CacheIdentityJson)!.AsObject();
                json["KeptMoveSource"] = source;
                json["KeptMoveHash"] = snapshot.ContentSha256;
                row.CacheIdentityJson = json.ToJsonString();
                row.LocalPath = destination;
                row.Revision++;
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            await db.Entry(owned).ReloadAsync(token);
        }
        else
        {
            var fileId = Guid.ParseExact(prepared.ProviderArtifactId["cached-keep:".Length..], "N");
            owned = await db.ManagedFiles.SingleAsync(item => item.Id == fileId, token);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(owned.CanonicalPath)!);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(owned.CanonicalPath)!); parent != null; parent = parent.Parent)
            if (parent.LinkTarget != null) throw Denied();
        var lyrics = Path.ChangeExtension(source, ".lrc");
        var keptLyrics = Path.ChangeExtension(owned.CanonicalPath, ".lrc");
        if (File.Exists(lyrics) && !File.Exists(keptLyrics))
        {
            files.InspectLegacyFile(Path.GetDirectoryName(source)!, lyrics);
            var partial = keptLyrics + ".partial";
            if (File.Exists(partial))
            {
                files.InspectLegacyFile(owned.TargetRootPath, partial);
                File.Delete(partial);
            }
            File.Copy(lyrics, partial, overwrite: false);
            File.Move(partial, keptLyrics, overwrite: false);
        }
        if (!File.Exists(owned.CanonicalPath))
        {
            files.InspectLegacyFile(Path.GetDirectoryName(source)!, source, owned.Length);
            await VerifyMoveBytesAsync(source, owned.ContentSha256, token);
            var physical = new PhysicalManagedFileOperations();
            if (!physical.TryOpenDirectory(Path.GetDirectoryName(owned.CanonicalPath)!, out var directory, out var targetIdentity))
                throw new IOException("The kept directory could not be verified.");
            using (directory)
            {
                if (targetIdentity.DeviceId == owned.FileSystemDeviceId)
                    physical.MoveNoReplace(source, owned.CanonicalPath);
                else
                {
                    var partial = owned.CanonicalPath + ".partial";
                    if (File.Exists(partial)) File.Delete(partial);
                    await physical.CopyAsync(source, partial, token);
                    await VerifyMoveBytesAsync(partial, owned.ContentSha256, token);
                    physical.MoveNoReplace(partial, owned.CanonicalPath);
                }
            }
        }
        var finalIdentity = files.InspectLegacyFile(owned.TargetRootPath, owned.CanonicalPath, owned.Length);
        await VerifyMoveBytesAsync(owned.CanonicalPath, owned.ContentSha256, token);
        var display = proof.Payload.Metadata;
        if (display.DurationMilliseconds == null &&
            (await allstarr.Services.Common.AudioMediaFacts.ReadFileAsync(owned.CanonicalPath, token))?.DurationSeconds is { } seconds && seconds > 0)
            display = display with { DurationMilliseconds = (long)Math.Round(seconds * 1000) };
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            if (await FindKeepAsync(db, claim.JobId, claim.OwnerUserId, claim.AttemptNumber, token) == null) throw Denied();
            if (!await db.Set<RecordingKeptRouteEntity>().AnyAsync(item => item.CanonicalRecordingId == proof.RecordingId, token))
            {
                owned.FileSystemDeviceId = finalIdentity.DeviceId;
                owned.FileSystemFileId = finalIdentity.FileId;
                owned.FileSystemLinkCount = finalIdentity.LinkCount;
                db.Add(new RecordingKeptRouteEntity
                {
                    CanonicalRecordingId = proof.RecordingId,
                    ManagedFileId = owned.Id,
                    PublishedByUserId = proof.OwnerUserId,
                    PublicationJobId = claim.JobId,
                    SourceArtifactId = prepared.Id,
                    DisplayMetadataJson = SerializeMetadata(display with
                    { Artwork = new(proof.Payload.ProviderId, proof.Payload.ExternalId) }),
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow
                });
            }
            await CancelCleanupAsync(db, owned.Id, clock.UtcNow, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        await files.VerifyOwnedFileAsync(EfManagedFileOwnershipStore.Map(owned)!, token);
        if (File.Exists(source))
        {
            files.InspectLegacyFile(Path.GetDirectoryName(source)!, source);
            // Only remove the original bytes accepted by this request.
            var originalHash = JsonNode.Parse(mapping.CacheIdentityJson!)?["KeptMoveHash"]?.GetValue<string>();
            await VerifyMoveBytesAsync(source, originalHash ?? owned.ContentSha256, token);
            File.Delete(source);
        }
        if (File.Exists(lyrics) && File.Exists(keptLyrics))
        {
            var before = await File.ReadAllBytesAsync(lyrics, token);
            var after = await File.ReadAllBytesAsync(keptLyrics, token);
            if (before.AsSpan().SequenceEqual(after)) File.Delete(lyrics);
        }
        var result = ToResult(proof.RecordingId, EfManagedFileOwnershipStore.Map(owned)!, prepared.MimeType);
        await EnsureLyricsAsync(result, proof.OwnerUserId, claim.CorrelationId, token);
        return result;
    }

    internal static string? CachedMoveSource(DownloadedSongMappingEntity mapping)
    {
        try { return mapping.CacheIdentityJson == null ? null : JsonNode.Parse(mapping.CacheIdentityJson)?["KeptMoveSource"]?.GetValue<string>(); }
        catch (JsonException) { return null; }
    }

    private static async Task VerifyMoveBytesAsync(string path, string hash, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != hash)
            throw new IOException("The cached file failed verification.");
    }
}
