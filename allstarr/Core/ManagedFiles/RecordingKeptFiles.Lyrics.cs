using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Services.Lyrics;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    private async Task EnsureLyricsAsync(KeptRecordingFile file, Guid listenerId, string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(Path.ChangeExtension(file.Path, ".lrc"))) return;
        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetService<IKeptLyricsSidecarService>();
        if (writer == null) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.Set<LegacyMediaAdoptionRecord>().AsNoTracking().AnyAsync(item => item.ManagedFileId == file.ManagedFileId, cancellationToken)) return;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == listenerId && item.Enabled,
            cancellationToken);
        if (user == null || user.BackendType is not ("jellyfin" or "subsonic")) return;
        var actor = new ProviderActorContext(ProviderActorKind.User, user.Id,
            new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
        var current = await FindAsync(actor, file.RecordingId, cancellationToken);
        if (current == null || current.ManagedFileId != file.ManagedFileId || current.Path != file.Path ||
            current.ContentSha256 != file.ContentSha256 || current.Length != file.Length) return;
        var song = await FindSongAsync(actor, file.RecordingId.ToString("D"), cancellationToken);
        if (song == null) return;
        var source = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                            join recording in db.CanonicalRecordings on route.CanonicalRecordingId equals recording.Id
                            join artifact in db.ProviderDownloadArtifacts on route.SourceArtifactId equals artifact.Id
                            where route.ManagedFileId == file.ManagedFileId &&
                                  (recording.Id == file.RecordingId || recording.MergedIntoId == file.RecordingId)
                            orderby route.CanonicalRecordingId
                            select new { artifact.ProviderId, artifact.SourceExternalTrackId })
            .FirstOrDefaultAsync(cancellationToken);
        var protocol = new ProtocolExecutionContext(user.BackendType == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
            user.BackendInstanceId, user.BackendPrincipalId,
            new(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, false),
            correlationId, clock.UtcNow.AddMinutes(1), cancellationToken);
        await writer.EnsureSidecarAsync(file.Path, song, source?.ProviderId, source?.SourceExternalTrackId,
            cancellationToken, protocol);
    }
}
