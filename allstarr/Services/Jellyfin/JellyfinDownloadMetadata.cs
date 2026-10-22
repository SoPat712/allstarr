using System.Security.Cryptography;
using System.Text;
using allstarr.Models.Domain;

namespace allstarr.Services.Jellyfin;

internal sealed record JellyfinDownloadMetadata(string Name, string? Container, string VirtualPath)
{
    public string Path => VirtualPath;

    public static JellyfinDownloadMetadata ForSong(Song song)
    {
        var title = FormattableString.Invariant($"{song.DiscNumber ?? 1:00}-{song.Track ?? 0:00} {song.Title}");
        var readable = new StringBuilder();
        var byteCount = 0;
        foreach (var rune in title.EnumerateRunes())
        {
            var safe = Rune.IsControl(rune) || "/\\?<>:*|.\"".Contains(rune.ToString(), StringComparison.Ordinal)
                ? new Rune('_') : rune;
            if (byteCount + safe.Utf8SequenceLength > 150) break;
            readable.Append(safe.ToString());
            byteCount += safe.Utf8SequenceLength;
        }
        // The complete identity keeps repeated titles and sanitized names distinct within an album.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(song.Id))).ToLowerInvariant();
        var extension = song.Container is { Length: > 0 } known && known != "unknown" ? known :
            System.IO.Path.GetExtension(song.LocalPath)?.TrimStart('.').ToLowerInvariant();
        var container = extension is "flac" or "mp3" or "m4a" or "mp4" or "aac" or "ogg" or "opus" or "wav" or "aif" or "aiff" or "wma"
            ? extension : null;
        return new($"{readable.ToString().Trim()} - {identity}", container,
            allstarr.Core.Protocols.ExternalTrackPresentation.VirtualPath(song, container));
    }
}
