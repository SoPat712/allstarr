using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.ManagedFiles;
using TagLib;

namespace allstarr.Core.Enrichment;

public interface IManagedTagFileMutator
{
    void Apply(string path, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken);
    bool Matches(string path, IReadOnlyDictionary<string, string> tags);
}

public sealed class TagLibManagedTagFileMutator : IManagedTagFileMutator
{
    public void Apply(string path, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var file = TagLib.File.Create(path);
        ApplyTags(file.Tag, tags);
        cancellationToken.ThrowIfCancellationRequested();
        file.Save();
    }

    public bool Matches(string path, IReadOnlyDictionary<string, string> tags)
    {
        using var file = TagLib.File.Create(path);
        return tags.All(pair => TagMatches(file.Tag, pair.Key, pair.Value));
    }

    public static void ApplyTags(Tag tag, IReadOnlyDictionary<string, string> tags)
    {
        if (tags.TryGetValue("title", out var title)) tag.Title = title;
        if (tags.TryGetValue("artist", out var artist)) tag.Performers = [artist];
        if (tags.TryGetValue("album", out var album)) tag.Album = album;
        if (tags.TryGetValue("albumArtist", out var albumArtist)) tag.AlbumArtists = [albumArtist];
        if (tags.TryGetValue("genre", out var genre)) tag.Genres = genre.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tags.TryGetValue("year", out var year) && uint.TryParse(year, out var parsedYear)) tag.Year = parsedYear;
        if (tags.TryGetValue("track", out var track) && uint.TryParse(track, out var parsedTrack)) tag.Track = parsedTrack;
        if (tags.TryGetValue("totaltracks", out var tracks) && uint.TryParse(tracks, out var parsedTracks)) tag.TrackCount = parsedTracks;
        if (tags.TryGetValue("disc", out var disc) && uint.TryParse(disc, out var parsedDisc)) tag.Disc = parsedDisc;
        if (tags.TryGetValue("totaldiscs", out var discs) && uint.TryParse(discs, out var parsedDiscs)) tag.DiscCount = parsedDiscs;
        if (tags.TryGetValue("comment", out var comment)) tag.Comment = comment;
        if (tags.TryGetValue("isrc", out var isrc)) tag.ISRC = isrc;
        if (tags.TryGetValue("date", out var date)) WriteDate(tag, date, original: false);
        if (tags.TryGetValue("originaldate", out var originalDate)) WriteDate(tag, originalDate, original: true);
        if (tags.TryGetValue("lyrics", out var lyrics)) WriteLyrics(tag, lyrics);
        ApplyReplayGain(tag, tags, "REPLAYGAIN_TRACK_GAIN", value => tag.ReplayGainTrackGain = value);
        ApplyReplayGain(tag, tags, "REPLAYGAIN_TRACK_PEAK", value => tag.ReplayGainTrackPeak = value);
        ApplyReplayGain(tag, tags, "REPLAYGAIN_ALBUM_GAIN", value => tag.ReplayGainAlbumGain = value);
        ApplyReplayGain(tag, tags, "REPLAYGAIN_ALBUM_PEAK", value => tag.ReplayGainAlbumPeak = value);
        if (tags.TryGetValue("musicbrainz_recordingid", out var recordingId)) tag.MusicBrainzTrackId = recordingId;
        if (tags.TryGetValue("musicbrainz_releaseid", out var releaseId)) tag.MusicBrainzReleaseId = releaseId;
        if (tags.TryGetValue("musicbrainz_releasegroupid", out var releaseGroupId)) tag.MusicBrainzReleaseGroupId = releaseGroupId;
        if (tags.TryGetValue("musicbrainz_artistid", out var artistId)) tag.MusicBrainzArtistId = artistId;
        if (tags.TryGetValue("cover", out var cover))
            tag.Pictures = cover.Length == 0 ? [] : [new Picture(new ByteVector(Convert.FromBase64String(cover))) { Type = PictureType.FrontCover }];
    }

    private static string ReadTag(Tag tag, string key) => key switch
    {
        "title" => tag.Title ?? string.Empty,
        "artist" => tag.Performers.FirstOrDefault() ?? string.Empty,
        "album" => tag.Album ?? string.Empty,
        "albumArtist" => tag.AlbumArtists.FirstOrDefault() ?? string.Empty,
        "genre" => string.Join("; ", tag.Genres),
        "year" => tag.Year == 0 ? string.Empty : tag.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "track" => tag.Track == 0 ? string.Empty : tag.Track.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "totaltracks" => tag.TrackCount == 0 ? string.Empty : tag.TrackCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "disc" => tag.Disc == 0 ? string.Empty : tag.Disc.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "totaldiscs" => tag.DiscCount == 0 ? string.Empty : tag.DiscCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "comment" => tag.Comment ?? string.Empty,
        "isrc" => tag.ISRC ?? string.Empty,
        "date" => ReadDate(tag, original: false),
        "originaldate" => ReadDate(tag, original: true),
        "lyrics" => tag.Lyrics ?? string.Empty,
        "REPLAYGAIN_TRACK_GAIN" => PromotionReplayGain.FormatGain(tag.ReplayGainTrackGain),
        "REPLAYGAIN_TRACK_PEAK" => PromotionReplayGain.FormatPeak(tag.ReplayGainTrackPeak),
        "REPLAYGAIN_ALBUM_GAIN" => PromotionReplayGain.FormatGain(tag.ReplayGainAlbumGain),
        "REPLAYGAIN_ALBUM_PEAK" => PromotionReplayGain.FormatPeak(tag.ReplayGainAlbumPeak),
        "musicbrainz_recordingid" => tag.MusicBrainzTrackId ?? string.Empty,
        "musicbrainz_releaseid" => tag.MusicBrainzReleaseId ?? string.Empty,
        "musicbrainz_releasegroupid" => tag.MusicBrainzReleaseGroupId ?? string.Empty,
        "musicbrainz_artistid" => tag.MusicBrainzArtistId ?? string.Empty,
        "cover" => tag.Pictures.FirstOrDefault() is { } picture ? Convert.ToBase64String(picture.Data.Data) : string.Empty,
        _ => string.Empty
    };

    private static bool TagMatches(Tag tag, string key, string expected)
    {
        var actual = ReadTag(tag, key);
        if (!key.StartsWith("REPLAYGAIN_", StringComparison.Ordinal))
            return actual.Equals(expected, StringComparison.Ordinal);
        return TryReplayGain(actual, key.EndsWith("_GAIN", StringComparison.Ordinal), out var actualNumber) &&
            TryReplayGain(expected, key.EndsWith("_GAIN", StringComparison.Ordinal), out var expectedNumber) &&
            Math.Abs(actualNumber - expectedNumber) <= (key.EndsWith("_GAIN", StringComparison.Ordinal) ? 0.005 : 0.00000001);
    }

    private static void ApplyReplayGain(Tag tag, IReadOnlyDictionary<string, string> tags, string key, Action<double> apply)
    {
        if (!tags.TryGetValue(key, out var text)) return;
        var gain = key.EndsWith("_GAIN", StringComparison.Ordinal);
        if (!TryReplayGain(text, gain, out var value)) throw new InvalidDataException("The ReplayGain tag is invalid.");
        apply(value);
        // Preserve full peak precision where the format offers text fields.
        foreach (var component in Components(tag))
            WriteText(component, key, gain ? PromotionReplayGain.FormatGain(value) : PromotionReplayGain.FormatPeak(value));
    }

    private static bool TryReplayGain(string text, bool gain, out double value)
    {
        text = text.Trim();
        if (gain && text.EndsWith("dB", StringComparison.OrdinalIgnoreCase)) text = text[..^2].TrimEnd();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value) && (gain || value >= 0);
    }

    private static IEnumerable<Tag> Components(Tag tag)
    {
        if (tag is CombinedTag combined)
        {
            foreach (var component in combined.Tags)
                foreach (var nested in Components(component)) yield return nested;
        }
        else if (tag is TagLib.Ogg.GroupedComment grouped)
        {
            foreach (var component in grouped.Comments) yield return component;
        }
        else yield return tag;
    }

    private static void WriteText(Tag tag, string key, string value)
    {
        switch (tag)
        {
            case TagLib.Ogg.XiphComment xiph:
                xiph.SetField(key, value);
                break;
            case TagLib.Id3v2.Tag id3:
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, true).Text = [value];
                break;
            case TagLib.Mpeg4.AppleTag apple:
                apple.SetDashBox("com.apple.iTunes", key, value);
                break;
        }
    }

    private static readonly ByteVector Mp4Date = new(new byte[] { 0xa9, 0x64, 0x61, 0x79 });

    private static void WriteDate(Tag tag, string value, bool original)
    {
        foreach (var component in Components(tag))
        {
            switch (component)
            {
                case TagLib.Ogg.XiphComment xiph:
                    xiph.SetField(original ? "ORIGINALDATE" : "DATE", value);
                    break;
                case TagLib.Id3v2.Tag id3:
                    TagLib.Id3v2.TextInformationFrame.Get(id3, original ? "TDOR" : "TDRC", true).Text = [value];
                    WriteText(id3, original ? "ORIGINALDATE" : "DATE", value);
                    break;
                case TagLib.Mpeg4.AppleTag apple:
                    if (original) apple.SetDashBox("com.apple.iTunes", "ORIGINALDATE", value);
                    else apple.SetText(Mp4Date, value);
                    break;
            }
        }
    }

    private static string ReadDate(Tag tag, bool original)
    {
        foreach (var component in Components(tag))
        {
            var value = component switch
            {
                TagLib.Ogg.XiphComment xiph => xiph.GetField(original ? "ORIGINALDATE" : "DATE").FirstOrDefault(),
                TagLib.Id3v2.Tag id3 => TagLib.Id3v2.UserTextInformationFrame.Get(id3, original ? "ORIGINALDATE" : "DATE", false)?.Text.FirstOrDefault()
                    ?? TagLib.Id3v2.TextInformationFrame.Get(id3, original ? "TDOR" : "TDRC", false)?.Text.FirstOrDefault(),
                TagLib.Mpeg4.AppleTag apple when original => apple.GetDashBox("com.apple.iTunes", "ORIGINALDATE"),
                TagLib.Mpeg4.AppleTag apple => apple.GetText(Mp4Date).FirstOrDefault(),
                _ => null
            };
            if (value is not null) return value;
        }
        return string.Empty;
    }

    private static void WriteLyrics(Tag tag, string lyrics)
    {
        if (lyrics.Length > 1024 * 1024) throw new InvalidDataException("Embedded lyrics exceed their size limit.");
        tag.Lyrics = lyrics;
        foreach (var component in Components(tag).OfType<TagLib.Id3v2.Tag>())
        {
            var text = ParseSynchronizedLyrics(lyrics);
            var frame = TagLib.Id3v2.SynchronisedLyricsFrame.Get(component, "", "eng", TagLib.Id3v2.SynchedTextType.Lyrics, text.Length > 0);
            if (frame is null) continue;
            if (text.Length == 0) component.RemoveFrame(frame);
            else
            {
                frame.Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds;
                frame.Text = text;
            }
        }
    }

    internal static TagLib.Id3v2.SynchedText[] ParseSynchronizedLyrics(string lyrics)
    {
        if (lyrics.Length > 1024 * 1024) throw new InvalidDataException("Embedded lyrics exceed their size limit.");
        var timestamps = new System.Text.RegularExpressions.Regex(@"\[(?<minutes>\d{1,4}):(?<seconds>[0-5]\d)(?:[.:](?<fraction>\d{1,3}))?\]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var offsetMatch = System.Text.RegularExpressions.Regex.Match(lyrics, @"\[offset:(?<offset>[+-]?\d{1,8})\]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        var offset = offsetMatch.Success ? long.Parse(offsetMatch.Groups["offset"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var text = new List<TagLib.Id3v2.SynchedText>();
        foreach (var line in lyrics.Split('\n'))
        {
            var matches = timestamps.Matches(line);
            if (matches.Count == 0 || matches[0].Index != 0) continue;
            var content = line[(matches[^1].Index + matches[^1].Length)..].TrimEnd('\r');
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var minutes = long.Parse(match.Groups["minutes"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var seconds = long.Parse(match.Groups["seconds"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var fraction = match.Groups["fraction"].Value;
                var millis = fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(3, '0'), System.Globalization.CultureInfo.InvariantCulture);
                text.Add(new(Math.Max(0, minutes * 60000 + seconds * 1000 + millis - offset), content));
            }
        }
        return text.OrderBy(item => item.Time).ToArray();
    }
}

/// <summary>Stages tag changes beside the managed artifact, then atomically replaces it.</summary>
public sealed class TagLibManagedMetadataWriter(
    IManagedTagFileMutator mutator,
    IManagedFileOperations files) : IManagedMetadataWriter
{
    public async Task<ManagedMetadataWriteResult> WriteAsync(
        ManagedMetadataArtifact artifact,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(artifact.Path);
        var root = ValidateManagedPath(artifact.TargetRootPath, path);
        var file = new FileInfo(path);
        var lockPath = Path.Combine(file.DirectoryName!, $".{file.Name}.allstarr-tags.lock");
        var journalPath = Path.Combine(file.DirectoryName!, $".{file.Name}.allstarr-tags.swap.json");
        var lease = await SwapLease.AcquireAsync(lockPath, journalPath, cancellationToken);
        var transferredLease = false;
        try
        {
            root = ValidateManagedPath(root, path);
            file.Refresh();
            if (!file.Exists || file.LinkTarget != null)
                throw new IOException("The managed metadata artifact is missing or is a symbolic link.");

            var current = await FingerprintAsync(path, cancellationToken);
            if (!current.Equals(artifact.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                var journal = await ReadJournalAsync(journalPath, cancellationToken);
                if (journal is null ||
                    !journal.InputSha256.Equals(artifact.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
                    !journal.OutputSha256.Equals(current, StringComparison.OrdinalIgnoreCase) ||
                    !journal.OperationFingerprint.Equals(artifact.OperationFingerprint, StringComparison.OrdinalIgnoreCase) ||
                    !mutator.Matches(path, tags))
                    throw new IOException("The managed metadata artifact changed outside this enrichment operation.");
                var recoveredIdentity = ReadIdentity(path);
                transferredLease = true;
                return Result(current, file.Length, reused: true, recoveredIdentity, lease);
            }

            ValidateExpectedIdentity(artifact, path);
            if (System.IO.File.Exists(journalPath)) System.IO.File.Delete(journalPath);
            var extension = Path.GetExtension(path);
            var staging = Path.Combine(file.DirectoryName!,
                $".{Path.GetFileNameWithoutExtension(path)}.allstarr-tags-{Guid.NewGuid():N}{extension}");
            var unixMode = ReadUnixMode(path);
            try
            {
                await CopyAsync(path, staging, cancellationToken);
                mutator.Apply(staging, tags, cancellationToken);
                if (unixMode.HasValue) SetUnixMode(staging, unixMode.Value);
                var output = await FingerprintAsync(staging, cancellationToken);
                ValidateManagedPath(root, path);
                ValidateExpectedIdentity(artifact, path);
                var unchanged = await FingerprintAsync(path, cancellationToken);
                if (!unchanged.Equals(current, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The managed metadata artifact changed while enrichment was staged.");
                await WriteJournalAsync(journalPath,
                    new(current, output, artifact.OperationFingerprint.ToLowerInvariant()), cancellationToken);
                ValidateManagedPath(root, path);
                ValidateExpectedIdentity(artifact, path);
                System.IO.File.Move(staging, path, overwrite: true);
                ValidateManagedPath(root, path);
                var identity = ReadIdentity(path);
                transferredLease = true;
                return Result(output, new FileInfo(path).Length, reused: false, identity, lease);
            }
            finally
            {
                if (System.IO.File.Exists(staging)) System.IO.File.Delete(staging);
            }
        }
        finally
        {
            if (!transferredLease) await lease.DisposeAsync();
        }
    }

    private ManagedFileSystemIdentity? ReadIdentity(string path) =>
        files.TryGetFileIdentity(path, out var identity) ? identity : null;

    private void ValidateExpectedIdentity(ManagedMetadataArtifact artifact, string path)
    {
        if (string.IsNullOrWhiteSpace(artifact.FileSystemDeviceId) ||
            string.IsNullOrWhiteSpace(artifact.FileSystemFileId)) return;
        if (!files.TryGetFileIdentity(path, out var identity) ||
            !StringComparer.Ordinal.Equals(artifact.FileSystemDeviceId, identity.DeviceId) ||
            !StringComparer.Ordinal.Equals(artifact.FileSystemFileId, identity.FileId))
            throw new IOException("The managed metadata artifact identity changed before enrichment.");
    }

    private static ManagedMetadataWriteResult Result(
        string sha256,
        long length,
        bool reused,
        ManagedFileSystemIdentity? identity,
        IManagedMetadataWriteLease lease) => new(sha256, length, reused)
        {
            FileSystemDeviceId = identity?.DeviceId,
            FileSystemFileId = identity?.FileId,
            FileSystemLinkCount = identity?.LinkCount,
            Lease = lease
        };

    private static string ValidateManagedPath(string configuredRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathRooted(configuredRoot))
            throw new IOException("The managed metadata artifact has no valid target root.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new IOException("The managed metadata artifact escaped its target root.");
        RejectSymlink(root);
        var current = root;
        foreach (var part in Path.GetRelativePath(root, full)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (System.IO.File.Exists(current) || Directory.Exists(current)) RejectSymlink(current);
        }
        return root;
    }

    private static void RejectSymlink(string path)
    {
        if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Managed metadata paths may not traverse symbolic links.");
    }

    private static async Task WriteJournalAsync(string path, SwapJournal journal, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            System.IO.File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        }
    }

    private static async Task<SwapJournal?> ReadJournalAsync(string path, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<SwapJournal>(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new IOException("The managed metadata recovery journal is invalid.", exception);
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static UnixFileMode? ReadUnixMode(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        try { return System.IO.File.GetUnixFileMode(path); }
        catch (PlatformNotSupportedException) { return null; }
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        System.IO.File.SetUnixFileMode(path, mode);
    }

    private sealed record SwapJournal(string InputSha256, string OutputSha256, string OperationFingerprint);

    private sealed class SwapLease : IManagedMetadataWriteLease
    {
        private readonly FileStream stream;
        private readonly string journalPath;
        private bool disposed;

        private SwapLease(FileStream stream, string journalPath)
        {
            this.stream = stream;
            this.journalPath = journalPath;
        }

        public static async Task<SwapLease> AcquireAsync(
            string lockPath,
            string journalPath,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    return new(stream, journalPath);
                }
                catch (IOException) when (attempt < 200)
                {
                    // CreateNew is the cross-platform lock. If a process crashed,
                    // its handle is gone and an exclusive open lets us reap the orphan.
                    try
                    {
                        await using var orphan = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        orphan.Close();
                        System.IO.File.Delete(lockPath);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    await Task.Delay(25, cancellationToken);
                }
            }
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (System.IO.File.Exists(journalPath)) System.IO.File.Delete(journalPath);
            return DisposeAsync().AsTask();
        }

        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            stream.Dispose();
            try { if (System.IO.File.Exists(stream.Name)) System.IO.File.Delete(stream.Name); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }
    }
}
