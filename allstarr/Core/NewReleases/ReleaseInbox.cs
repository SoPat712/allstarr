using System.Globalization;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Providers.AppleMusicKit;

namespace allstarr.Core.Releases;

public sealed record FollowedReleaseArtist(string ProviderId, string ArtistId, string DisplayName,
    string? MusicBrainzArtistId = null);
public sealed record ReleaseSource(string ProviderId, string AlbumId, string Title, string Artist,
    string? ReleaseDate, string DatePrecision, string Type, bool? IsExplicit, string Edition,
    string? Upc, string[] Isrcs, string[] Aliases, string? ExclusionReason)
{
    public string Key => ProviderId + ":" + AlbumId;
}
public sealed record ReleaseInboxEntry(string Id, string Title, string Artist, string? ReleaseDate,
    string DatePrecision, string Type, bool? IsExplicit, string Edition, string? ExclusionReason,
    IReadOnlyList<ReleaseSource> Sources);
public sealed record ReleaseCoverage(FollowedReleaseArtist Artist, string Status, DateTimeOffset? CheckedAt);
public sealed record ReleaseInbox(IReadOnlyList<ReleaseInboxEntry> Entries,
    IReadOnlyList<ReleaseInboxEntry> Excluded, IReadOnlyList<ReleaseCoverage> Coverage);
internal sealed record ReleaseCatalog(string Status, DateTimeOffset CheckedAt, DateTimeOffset NextCheckAt,
    ReleaseSource[] Releases, string? Cursor = null, string[]? SeenCursors = null);
internal sealed record ReleaseRate(DateTimeOffset NextRequestAt);

internal static class ReleaseInboxProjection
{
    internal static ReleaseSource From(ProviderAlbumMetadata album, FollowedReleaseArtist artist)
    {
        var type = album.ReleaseType?.Trim().ToLowerInvariant() ?? "unknown";
        var edition = album.Edition ?? string.Join(" ", Regex.Matches(album.Title,
            @"[\(\[]([^\)\]]*(?:deluxe|expanded|anniversary|remaster|reissue)[^\)\]]*)[\)\]]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Select(match => match.Groups[1].Value));
        if (string.IsNullOrWhiteSpace(edition))
            edition = Regex.Match(album.Title, @"\b(deluxe|expanded|anniversary)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Value;
        var exclusion = album.ExclusionReason;
        if (type is "compilation" or "compile") exclusion = "compilation";
        if (type is "appearance" or "feature") exclusion = "appearance";
        if (type is "reissue" or "remaster" || Regex.IsMatch(album.Title + " " + edition,
                @"\b(remaster(?:ed)?|reissue(?:d)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) exclusion = "reissue";
        if (album.Artists[0].ArtistId is { } primary && primary.Value != artist.ArtistId) exclusion = "appearance";
        var complete = album.TrackCount is > 0 && album.Tracks.Count == album.TrackCount &&
            album.Tracks.All(item => !string.IsNullOrWhiteSpace(item.Isrc));
        var aliases = album.ProviderIds.Where(item => item.Key.ToLowerInvariant() is "deezer" or "qobuz" or "apple" or
                "apple-download" or "apple-musickit" or "spotify" or "tidal" or "musicbrainzalbum")
            .Select(item => Alias(item.Key, item.Value)).Append(Alias(album.Id.ProviderId, album.Id.Value)).ToArray();
        Dates(album.ReleaseDate, out _, out _, out var precision);
        return new(album.Id.ProviderId, album.Id.Value, album.Title, album.Artists[0].Name, album.ReleaseDate,
            precision, type, album.IsExplicit, edition, MusicAlbumReleases.NormalizeUpc(album.Upc),
            complete ? album.Tracks.Select(item => item.Isrc!.Trim().ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray() : [],
            aliases, exclusion);
    }

    internal static IReadOnlyList<ReleaseInboxEntry> Group(IEnumerable<ReleaseSource> sources, DateTimeOffset now)
    {
        var groups = new List<List<ReleaseSource>>();
        foreach (var source in sources.OrderBy(item => item.ExclusionReason == "appearance").DistinctBy(item => item.Key)
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var dated = Dates(source.ReleaseDate, out var start, out var end, out _);
            if (dated && (end < DateOnly.FromDateTime(now.UtcDateTime).AddDays(-60) || start > DateOnly.FromDateTime(now.UtcDateTime))) continue;
            var item = !dated && source.ExclusionReason == null ? source with { ExclusionReason = "date-unknown" } : source;
            var matches = groups.Where(group => group.All(other => Compatible(item, other)) && group.Any(other => Proven(item, other))).ToArray();
            if (matches.Length == 0) groups.Add([item]);
            else
            {
                matches[0].Add(item);
                foreach (var match in matches.Skip(1).Where(match => match.All(other => matches[0].All(member => Compatible(other, member)))))
                { matches[0].AddRange(match); groups.Remove(match); }
            }
        }
        return groups.Select(group =>
        {
            var first = group.OrderBy(item => item.Type == "unknown").ThenByDescending(item => item.ReleaseDate, StringComparer.Ordinal).First();
            return new ReleaseInboxEntry(NewReleasesService.Hash(string.Join("|", group.Select(item => item.Key).Order(StringComparer.Ordinal))),
                first.Title, first.Artist, first.ReleaseDate, first.DatePrecision, first.Type, first.IsExplicit,
                first.Edition, first.ExclusionReason, group);
        }).OrderByDescending(item => item.ReleaseDate, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static string Alias(string provider, string id)
    {
        if (id.StartsWith(provider + ":", StringComparison.OrdinalIgnoreCase)) id = id[(provider.Length + 1)..];
        provider = provider.Equals("apple", StringComparison.OrdinalIgnoreCase) ? AppleMusicClient.ProviderId :
            AppleMusicClient.NormalizeProviderId(provider);
        return provider + ":" + id;
    }

    private static bool Compatible(ReleaseSource first, ReleaseSource second) => first.IsExplicit == second.IsExplicit &&
        (first.Type == second.Type || first.Type == "unknown" || second.Type == "unknown") &&
        string.Equals(first.Edition.Trim(), second.Edition.Trim(), StringComparison.OrdinalIgnoreCase) &&
        first.ExclusionReason == second.ExclusionReason;
    internal static bool Dismissed(ReleaseSource source, ReleaseSource dismissed) => source.Key == dismissed.Key ||
        Compatible(source, dismissed) && Proven(source, dismissed);
    private static bool Proven(ReleaseSource first, ReleaseSource second) =>
        first.Aliases.Intersect(second.Aliases, StringComparer.Ordinal).Any() || first.Upc != null && first.Upc == second.Upc ||
        first.Isrcs.Length > 0 && first.Isrcs.SequenceEqual(second.Isrcs);

    private static bool Dates(string? value, out DateOnly start, out DateOnly end, out string precision)
    {
        start = end = default;
        precision = "unknown";
        if (value == null) return false;
        var format = value.Length switch { 4 => "yyyy", 7 => "yyyy-MM", _ => "yyyy-MM-dd" };
        if (!DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) return false;
        end = value.Length switch { 4 => new(start.Year, 12, 31), 7 => new(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month)), _ => start };
        precision = value.Length switch { 4 => "year", 7 => "month", _ => "day" };
        return true;
    }
}
