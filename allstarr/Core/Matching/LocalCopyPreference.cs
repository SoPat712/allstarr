using System.Text.Json;
using allstarr.Core.Storage;

namespace allstarr.Core.Matching;

internal static class LocalCopyPreference
{
    internal static int FormatRank(string? format) => format?.ToLowerInvariant() switch
    {
        ".flac" or ".wav" or ".wave" or ".aiff" or ".aif" or ".ape" or ".wv" => 2,
        ".mp3" or ".aac" or ".ogg" or ".opus" or ".wma" => 0,
        _ => 1
    };

    internal static LocalTrackMatchCandidate ToCandidate(LibraryTrackRecord track)
    {
        IReadOnlyDictionary<string, string>? providers = null;
        try { providers = JsonSerializer.Deserialize<Dictionary<string, string>>(track.ProviderIdsJson); }
        catch (JsonException) { }
        return new(track.Id, track.OwnerUserId, track.BackendInstanceId, track.BackendLibraryId,
            track.BackendItemId, track.CanonicalRecordingId, track.Title, track.Artist, track.Album,
            track.AlbumArtist, track.DurationMilliseconds is > 0 ? track.DurationMilliseconds : null,
            track.Isrc, track.MusicBrainzRecordingId, null, providers)
        {
            FileFormat = Path.GetExtension(track.FilePath) is { Length: > 0 } extension ? extension.ToLowerInvariant() : null,
            SourceModifiedAt = track.SourceModifiedAt
        };
    }
}
