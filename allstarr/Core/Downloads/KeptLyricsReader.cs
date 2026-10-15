using System.Text;
using allstarr.Core.Protocols;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace allstarr.Core.Downloads;

public sealed record KeptLyricsIdentity(string ProviderId, string ExternalId);

public interface IKeptLyricsReader
{
    Task<LyricsInfo?> ReadHouseholdAsync(ProtocolExecutionContext context, Song song,
        IReadOnlyList<KeptLyricsIdentity> identities);

    Task<LyricsInfo?> ReadAsync(
        ProtocolExecutionContext context,
        Song song,
        IReadOnlyList<KeptLyricsIdentity> identities);
}

public sealed class KeptLyricsReader(
    IDbContextFactory<AllstarrDbContext> factory,
    IConfiguration configuration) : IKeptLyricsReader
{
    private const int MaximumLyricsBytes = 2 * 1024 * 1024;

    public Task<LyricsInfo?> ReadHouseholdAsync(ProtocolExecutionContext context, Song song,
        IReadOnlyList<KeptLyricsIdentity> identities) => ReadCoreAsync(context, song, identities, householdOnly: true);

    public Task<LyricsInfo?> ReadAsync(
        ProtocolExecutionContext context,
        Song song,
        IReadOnlyList<KeptLyricsIdentity> identities) => ReadCoreAsync(context, song, identities, householdOnly: false);

    private async Task<LyricsInfo?> ReadCoreAsync(ProtocolExecutionContext context, Song song,
        IReadOnlyList<KeptLyricsIdentity> identities, bool householdOnly)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(identities);

        var cancellationToken = context.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        var principal = context.Principal;
        var actor = context.Actor;
        if (principal == null || actor?.UserId is not { } listenerId || listenerId == Guid.Empty)
            return null;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == listenerId &&
            item.Enabled &&
            item.BackendType == principal.BackendType &&
            item.BackendInstanceId == principal.BackendInstanceId &&
            item.BackendPrincipalId == principal.BackendPrincipalId,
            cancellationToken);
        if (user == null)
            return null;

        var roots = GetAllowedRoots(configuration);
        LyricsInfo? plainFallback = null;
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity == null ||
                string.IsNullOrWhiteSpace(identity.ProviderId) ||
                string.IsNullOrWhiteSpace(identity.ExternalId))
                continue;

            var mappings = await db.DownloadedSongMappings
                .AsNoTracking()
                .Where(item => item.ProviderId == identity.ProviderId && item.ExternalId == identity.ExternalId)
                .OrderBy(item => item.AudioQuality)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);

            foreach (var mapping in mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await IsMappingAllowedAsync(db, mapping, identity.ProviderId, listenerId, householdOnly, cancellationToken) ||
                    !TryGetSafePath(mapping.LocalPath, roots, out var audioPath))
                    continue;

                var sidecarPath = Path.ChangeExtension(audioPath, ".lrc");
                if (!TryGetSafePath(sidecarPath, roots, out _) ||
                    !File.Exists(audioPath) ||
                    !File.Exists(sidecarPath))
                    continue;

                var managedFiles = await db.ManagedFiles
                    .AsNoTracking()
                    .Where(item => item.CanonicalPath == audioPath)
                    .ToListAsync(cancellationToken);
                if (managedFiles.Any(item =>
                        item.RemovedAt != null ||
                        !item.IsManaged ||
                        item.OwnerUserId != null && (householdOnly || item.OwnerUserId != listenerId)))
                    continue;

                var text = await ReadSidecarAsync(sidecarPath, cancellationToken);
                if (text == null)
                    continue;

                var parsed = MusicLyrics.ParseSyncedLines(text);
                cancellationToken.ThrowIfCancellationRequested();
                if (parsed.Any(item => !string.IsNullOrWhiteSpace(item.Text)))
                    return CreateLyrics(song, text, synced: true);
                var plain = PlainText(text);
                if (parsed.Count != 0 || string.IsNullOrWhiteSpace(plain))
                    continue;
                plainFallback ??= CreateLyrics(song, plain, synced: false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return plainFallback;
    }

    private static IReadOnlyList<string> GetAllowedRoots(IConfiguration configuration)
    {
        var downloadRoot = Path.GetFullPath(configuration["Library:DownloadPath"] ?? "./downloads");
        return
        [
            Path.GetFullPath(Path.Combine(downloadRoot, "permanent")),
            Path.GetFullPath(Path.Combine(downloadRoot, "kept"))
        ];
    }

    private static async Task<bool> IsMappingAllowedAsync(
        AllstarrDbContext db,
        DownloadedSongMappingEntity mapping,
        string providerId,
        Guid listenerId,
        bool householdOnly,
        CancellationToken cancellationToken)
    {
        if (mapping.ProviderAccountId is not { } accountId)
            return mapping.ScopeKey == DownloadedSongMappingScope.LegacyKey;
        if (accountId == Guid.Empty ||
            mapping.ScopeKey != new DownloadedSongMappingScope(accountId, mapping.AudioQuality).Key)
            return false;

        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == accountId && item.ProviderId == providerId,
            cancellationToken);
        return account is { Enabled: true } &&
               (account.OwnerUserId == null || !householdOnly && account.OwnerUserId == listenerId);
    }

    private static bool TryGetSafePath(
        string? path,
        IReadOnlyList<string> roots,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || ContainsTraversal(path))
            return false;

        try
        {
            fullPath = Path.GetFullPath(path);
            var candidate = fullPath;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!roots.Any(root => IsPathUnderRoot(candidate, root, comparison)) ||
                HasLinkInPath(fullPath))
            {
                fullPath = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static bool ContainsTraversal(string path) =>
        path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");

    private static bool IsPathUnderRoot(
        string candidate,
        string root,
        StringComparison comparison)
    {
        var normalizedRoot = Path.GetFullPath(root);
        if (string.Equals(candidate, normalizedRoot, comparison))
            return false;
        var separator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(separator, comparison);
    }

    private static bool HasLinkInPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return true;

            var current = root;
            var remainder = fullPath[root.Length..];
            foreach (var segment in remainder.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var info = Directory.Exists(current)
                    ? (FileSystemInfo)new DirectoryInfo(current)
                    : new FileInfo(current);
                if (info.LinkTarget != null ||
                    info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return true;
            }

            return false;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return true;
        }
    }

    private static async Task<string?> ReadSidecarAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumLyricsBytes)
                return null;

            var bytes = new byte[MaximumLyricsBytes + 1];
            var length = 0;
            await using (var stream = new FileStream(
                             path,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.Open,
                                 Access = FileAccess.Read,
                                 Share = FileShare.Read,
                                 BufferSize = 81920,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                             }))
            {
                while (length < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken);
                    if (read == 0)
                        break;
                    length += read;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (length > MaximumLyricsBytes)
                return null;

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, 0, length);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string PlainText(string text) => string.Join('\n', text
        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
        .Where(line => !IsMetadataTag(line.Trim()))).Trim();

    private static bool IsMetadataTag(string value)
    {
        if (!value.StartsWith("[", StringComparison.Ordinal) ||
            !value.EndsWith("]", StringComparison.Ordinal))
            return false;
        var separator = value.IndexOf(':');
        if (separator <= 1)
            return false;
        var key = value[1..separator].Trim().ToLowerInvariant();
        return key is "ar" or "al" or "ti" or "by" or "offset" or "re" or "ve" or
            "length" or "id" or "la" or "au" or "tool";
    }

    private static LyricsInfo CreateLyrics(Song song, string text, bool synced)
    {
        var duration = song.Duration.GetValueOrDefault();
        if (!song.Duration.HasValue && song.PreciseDuration is { } precise && precise > TimeSpan.Zero)
            duration = (int)Math.Min(int.MaxValue, Math.Round(precise.TotalSeconds, MidpointRounding.AwayFromZero));

        var result = new LyricsInfo
        {
            TrackName = song.Title,
            ArtistName = song.Artist,
            AlbumName = song.Album,
            Duration = Math.Max(0, duration),
            Source = "kept"
        };
        if (synced)
            result.SyncedLyrics = text;
        else
            result.PlainLyrics = text;
        return result;
    }
}
