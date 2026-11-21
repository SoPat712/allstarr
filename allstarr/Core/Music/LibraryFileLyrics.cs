using System.Collections.Concurrent;
using allstarr.Core.Enrichment;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

/// <summary>
/// The word-by-word lyrics Allstarr puts in an added song's own file, read back from that file. A media server only
/// passes on the line-by-line ones in the tag it reads, so these are kept in a tag of their own, and they are read
/// here for the songs whose folder Allstarr can reach.
/// </summary>
public sealed class LibraryFileLyrics(PromotionRoots roots)
{
    private const int MaximumRemembered = 1024;
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, string? Lyrics)> remembered = new(StringComparer.Ordinal);

    /// <summary>
    /// The media server's lyrics for a library song with the word times its file holds for the same lines. The media
    /// server's own are kept when the file cannot be reached, holds none, or holds other lines (its lyrics were
    /// changed since).
    /// </summary>
    public async Task<LyricsInfo?> WithWordTimesAsync(LyricsInfo? fromServer, string? backendPath, CancellationToken token)
    {
        if (fromServer == null || string.IsNullOrWhiteSpace(fromServer.SyncedLyrics) || fromServer.TimedLines is { Count: > 0 }) return fromServer;
        var words = await ReadAsync(backendPath, token);
        if (words == null || !SameLines(fromServer.SyncedLyrics, words)) return fromServer;
        fromServer.SyncedLyrics = words;
        return fromServer;
    }

    internal static bool SameLines(string lines, string words)
    {
        static string[] Texts(string lyrics) => MusicLyrics.ParseSyncedLines(lyrics)
            .Select(line => string.Join(' ', line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(text => text.Length > 0).ToArray();
        var timed = MusicLyrics.ParseSyncedLines(words);
        return timed.Any(line => line.Cues is { Count: > 0 }) && Texts(lines).SequenceEqual(Texts(words), StringComparer.Ordinal);
    }

    /// <summary>Where Allstarr itself finds a library song's file, or null when no music folder it knows holds it.</summary>
    public async Task<string?> LocalPathAsync(string? backendPath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(backendPath)) return null;
        foreach (var root in await roots.ReadAsync(token))
        {
            // The media server may see the music folder under another path than Allstarr does.
            var theirs = PromotionLibraryFolders.WithoutTrailingSeparator(root.BackendPath ?? root.Path);
            if (backendPath.Length <= theirs.Length + 1 || !backendPath.StartsWith(theirs, StringComparison.Ordinal) ||
                backendPath[theirs.Length] is not ('/' or '\\')) continue;
            try
            {
                var local = PromotionNaming.ContainedPath(root.Path, backendPath[(theirs.Length + 1)..].Replace('\\', '/'));
                PromotionRoots.RejectLinks(local);
                return local;
            }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    private async Task<string?> ReadAsync(string? backendPath, CancellationToken token)
    {
        var local = await LocalPathAsync(backendPath, token);
        if (local == null) return null;
        return await Task.Run(() =>
        {
            try
            {
                var file = new FileInfo(local);
                if (!file.Exists) return null;
                if (remembered.TryGetValue(local, out var known) && known.Length == file.Length && known.Modified == file.LastWriteTimeUtc)
                    return known.Lyrics;
                var lyrics = TagLibManagedTagFileMutator.ReadWordLyrics(local);
                if (remembered.Count >= MaximumRemembered) remembered.Clear();
                remembered[local] = (file.Length, file.LastWriteTimeUtc, lyrics);
                return lyrics;
            }
            // A file that cannot be read as audio has no word times to give; the media server's lyrics stand.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                return null;
            }
        }, token);
    }
}
