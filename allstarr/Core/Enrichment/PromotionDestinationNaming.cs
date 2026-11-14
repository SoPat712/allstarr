using System.Globalization;
using System.Text.RegularExpressions;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromotionDestinationYearChoice(int Year, string RelativeFolder);
public sealed record PromotionDestinationNamingResult(int? Year, bool RequiresChoice,
    PromotionDestinationYearChoice[] Choices);

public static class PromotionDestinationNaming
{
    private const int MaxIndexedRows = 2000;
    private const int MaxCandidateYears = 64;
    private static readonly Regex FourDigitYear = new(@"(?<!\d)\d{4}(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<PromotionDestinationNamingResult> ResolveAsync(
        AllstarrDbContext db,
        ProtocolExecutionContext context,
        BackendLibraryAccess access,
        PromotionRoot root,
        PromotionProfile profile,
        Song song,
        int? namingYear,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(song);

        if (!access.Succeeded) throw new BackendLibraryAccessException(access);
        if (root.BackendLibraryId is not { } libraryId || !access.Allows(libraryId))
            throw new PromotionException(PromotionFailure.LibraryFolderMismatch);

        var backendRoot = NormalizePath(root.BackendPath ?? root.Path);
        if (backendRoot == null) throw new PromotionException(PromotionFailure.LibraryFolderUnavailable);
        var wantedAlbum = song.Album;
        var rows = await LibraryTrackAccess.Query(db, context, access)
            .Where(item => item.BackendLibraryId == libraryId && EF.Functions.Collate(item.Album!, "NOCASE") == wantedAlbum)
            .OrderBy(item => item.FilePath).ThenBy(item => item.Id)
            .Select(item => new IndexedPath(item.Id, item.FilePath, item.Album, item.AlbumArtist, item.Artist))
            .Take(MaxIndexedRows + 1)
            .ToArrayAsync(token);

        if (rows.Length > MaxIndexedRows)
            throw new PromotionException(PromotionFailure.ExistingAlbumLayoutMismatch);

        var matching = rows.Where(item => MatchesAlbumArtist(item, song)).ToArray();
        var existingFolders = new Dictionary<string, string>(FolderComparer);
        foreach (var row in matching)
        {
            var relative = RelativePath(backendRoot, row.FilePath);
            if (relative == null) continue;
            foreach (var folder in ParentFolders(relative))
                existingFolders.TryAdd(folder, folder);
        }

        var sourceFields = PromotionSongFields.FromSong(song);
        var sourceYear = ParseYear(sourceFields.GetValueOrDefault("year"));
        var sourceFolder = RenderFolder(profile.NamingScript, sourceFields);
        if (matching.Length == 0 || existingFolders.Count == 0 || sourceFolder is null || ContainsFolder(existingFolders, sourceFolder) &&
            (namingYear == null || namingYear == sourceYear))
            return NoChoice(sourceYear, namingYear);

        var candidateYears = existingFolders.Keys
            .SelectMany(folder => FourDigitYear.Matches(folder).Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture)))
            .Where(year => year > 0)
            .Distinct()
            .OrderBy(year => year)
            .Take(MaxCandidateYears)
            .ToArray();
        var choices = new List<PromotionDestinationYearChoice>();
        foreach (var year in candidateYears)
        {
            var candidateFolder = RenderFolder(profile.NamingScript, OverrideYear(sourceFields, year));
            if (candidateFolder is null || !TryFindFolder(existingFolders, candidateFolder, out var existing)) continue;
            choices.Add(new(year, existing));
        }

        if (choices.Count == 0)
            throw new PromotionException(PromotionFailure.ExistingAlbumLayoutMismatch);

        if (sourceYear is { } original && choices.All(choice => choice.Year != original))
            choices.Add(new(original, sourceFolder!));
        choices = choices.OrderBy(choice => choice.Year).ThenBy(choice => choice.RelativeFolder, StringComparer.Ordinal).ToList();
        if (namingYear == null) return new(null, true, choices.ToArray());
        if (!choices.Any(choice => choice.Year == namingYear.Value))
            throw new PromotionException(PromotionFailure.NamingMetadataMissing);
        return new(namingYear, false, choices.ToArray());
    }

    private static PromotionDestinationNamingResult NoChoice(int? sourceYear, int? namingYear)
    {
        if (namingYear is { } requested && requested != sourceYear)
            throw new PromotionException(PromotionFailure.NamingMetadataMissing);
        return new(namingYear ?? sourceYear, false, []);
    }

    private static bool MatchesAlbumArtist(IndexedPath row, Song song)
    {
        var wanted = new[] { song.AlbumArtist, song.Artist }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var actual = new[] { row.AlbumArtist, row.Artist }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return wanted.Length > 0 && actual.Any(candidate => wanted.Any(value =>
            string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<string, string?> OverrideYear(IReadOnlyDictionary<string, string?> fields, int year)
    {
        var copy = fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var value = year.ToString(CultureInfo.InvariantCulture);
        copy["year"] = value;
        return copy;
    }

    private static string? RenderFolder(string script, IReadOnlyDictionary<string, string?> fields)
    {
        var rendered = PromotionNaming.Render(script, fields).RelativePath;
        if (string.IsNullOrWhiteSpace(rendered))
            throw new PromotionException(PromotionFailure.NamingMetadataMissing);
        rendered = NormalizeRelative(rendered);
        var separator = rendered.LastIndexOf('/');
        return separator < 0 ? null : rendered[..separator];
    }

    private static int? ParseYear(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year > 0 ? year : null;

    private static IEnumerable<string> ParentFolders(string relative)
    {
        var parts = relative.Split('/');
        for (var count = 1; count < parts.Length; count++)
            yield return string.Join('/', parts[..count]);
    }

    private static string? RelativePath(string root, string path)
    {
        var normalizedRoot = root.TrimEnd('/');
        var normalized = NormalizePath(path);
        if (normalized == null || !normalized.StartsWith(normalizedRoot + "/", PathComparison)) return null;
        var relative = normalized[(normalizedRoot.Length + 1)..];
        var parts = relative.Split('/');
        return relative.Length == 0 || relative.Contains(':') || relative.Any(char.IsControl) ||
            parts.Any(part => part.Length == 0 || part is "." or "..") ? null : relative;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    private static bool ContainsFolder(IReadOnlyDictionary<string, string> folders, string folder) =>
        TryFindFolder(folders, folder, out _);

    private static bool TryFindFolder(IReadOnlyDictionary<string, string> folders, string folder, out string existing)
    {
        if (folders.TryGetValue(folder, out existing!)) return true;
        existing = string.Empty;
        return false;
    }

    private static StringComparer FolderComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record IndexedPath(Guid Id, string FilePath, string? Album, string? AlbumArtist, string? Artist);
}
