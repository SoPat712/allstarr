using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    private async Task<Candidate?> FindLegacyCandidateAsync(AllstarrDbContext db, FilePlacementService files,
        IManagedFileRemovalStore records, Guid recordingId, bool skipUnavailable, bool verifyContent, CancellationToken token)
    {
        var candidates = await (from adoption in db.Set<LegacyMediaAdoptionRecord>().AsNoTracking()
                                join recording in db.CanonicalRecordings.AsNoTracking() on adoption.CanonicalRecordingId equals (Guid?)recording.Id
                                where recording.Id == recordingId || recording.MergedIntoId == recordingId
                                orderby adoption.Kept descending, adoption.CreatedAt, adoption.ManagedFileId
                                select adoption.ManagedFileId).ToArrayAsync(token);
        foreach (var id in candidates)
        {
            var file = await records.GetAsync(id, token);
            if (file == null) continue;
            try
            {
                ValidateHousehold(file);
                if (verifyContent) await files.VerifyOwnedFileAsync(file, token);
                else files.VerifyOwnedFileIdentity(file, token);
                return new(file, LegacyMime(file.CanonicalPath), null);
            }
            catch (Exception error) when (skipUnavailable && error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    private static string LegacyMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".flac" => "audio/flac",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".mp4" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        ".aiff" => "audio/aiff",
        _ => "application/octet-stream"
    };
}
