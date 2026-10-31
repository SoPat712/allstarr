using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.Playback;

public sealed record ListeningStatsPeriod(DateTimeOffset? From, DateTimeOffset To, string TimeZoneId);
public sealed record ListeningStatsCoverage(long UnknownSongIdentityCount, long UnknownDurationCount);
public sealed record ListeningStatsSong(string Title, string Artist, string? Album, long PlayCount);
public sealed record ListeningStatsArtist(string Artist, long PlayCount);
public sealed record ListeningStatsAlbum(string Album, string Artist, long PlayCount);
public sealed record ListeningStats(ListeningStatsPeriod Period, long PlayCount, int ActiveDays, long ImportedCount,
    DateTimeOffset? ObservedSince, long EstimatedTimeFromSongLengthsMilliseconds, ListeningStatsCoverage Coverage,
    IReadOnlyList<ListeningStatsSong> TopSongs, IReadOnlyList<ListeningStatsArtist> TopArtists,
    IReadOnlyList<ListeningStatsAlbum> TopAlbums);

public sealed class ListeningStatsService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 256 });

    public async Task<ListeningStats> GetAsync(AdminAuthSession session, string? period, int? year, CancellationToken token)
    {
        if (session.AllstarrUserId is not { } owner || session.ExpiresAtUtc <= clock.UtcNow.UtcDateTime)
            throw new UnauthorizedAccessException();
        var protocol = session.BackendType.ToLowerInvariant();
        if (protocol is not ("jellyfin" or "subsonic")) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateDbContextAsync(token);
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner && user.Enabled &&
            user.BackendType == protocol && user.BackendInstanceId == session.BackendInstanceId &&
            user.BackendPrincipalId == session.UserId && user.IsAdmin == session.IsAdministrator, token)
            ?? throw new UnauthorizedAccessException();
        var scope = new IntelligenceScope(owner, protocol, session.BackendInstanceId);
        var zone = TimeZoneInfo.Local;
        var interval = CreatePeriod(period, year, clock.UtcNow, zone);
        var key = (scope, interval, actor.IsSynthetic);
        if (cache.TryGetValue(key, out (ListeningStats Stats, DateTimeOffset ExpiresAt) cached) && cached.ExpiresAt > clock.UtcNow)
            return cached.Stats;
        var result = await ReadAsync(db, scope, interval, zone, token);
        cache.Set(key, (result, clock.UtcNow.AddSeconds(30)), new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });
        return result;
    }

    internal static ListeningStatsPeriod CreatePeriod(string? period, int? year, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (year.HasValue && (period != null || year is < 1 or > 9998))
            throw new ArgumentException("The stats period is invalid.");
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        DateTime? from;
        var to = today.AddDays(1);
        if (year.HasValue)
        {
            from = new DateTime(year.Value, 1, 1);
            to = from.Value.AddYears(1);
        }
        else
        {
            from = (period ?? "30d") switch
            {
                "7d" => today.AddDays(-6),
                "30d" => today.AddDays(-29),
                "year" => new DateTime(today.Year, 1, 1),
                "all" => null,
                _ => throw new ArgumentException("The stats period is invalid.")
            };
            if (period == "year") to = from!.Value.AddYears(1);
        }
        return new(from.HasValue ? Boundary(from.Value, zone) : null, Boundary(to, zone), zone.Id);
    }

    private static DateTimeOffset Boundary(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Some zones skip or repeat midnight; use the first instant of the local date.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return zone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    internal static async Task<ListeningStats> ReadAsync(AllstarrDbContext db, IntelligenceScope scope,
        ListeningStatsPeriod period, TimeZoneInfo zone, CancellationToken token)
    {
        var qualified = db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
            item.State == ListeningEventState.Completed && item.ListenedAt != null &&
            db.Users.Any(user => user.Id == scope.OwnerUserId && !user.IsSynthetic));
        var observed = qualified.Where(item => item.ImportProvenance == null &&
            (item.SourceKind == "protocol" || item.SourceKind == "listenbrainz-api"));
        var observedSince = await observed.OrderBy(item => item.ListenedAt).Select(item => item.ListenedAt).FirstOrDefaultAsync(token);
        var selected = qualified.Where(item => item.ListenedAt < period.To);
        if (period.From is { } from) selected = selected.Where(item => item.ListenedAt >= from);
        var imported = await selected.LongCountAsync(item => item.SourceKind == "import" || item.ImportProvenance != null, token);
        var plays = await selected.Where(item => item.SourceKind == "protocol" || item.SourceKind == "listenbrainz-api" ||
                item.SourceKind == "import" || item.ImportProvenance != null)
            .Select(item => new StatsPlay(item.ListenedAt!.Value, item.TrackReference, item.CanonicalRecordingId,
                item.RecordingMusicBrainzId, item.Title, item.Artist, item.Album, item.AlbumArtist, item.DurationMilliseconds))
            .ToListAsync(token);
        var knownSongs = plays.Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Artist)).ToArray();
        var songs = knownSongs.GroupBy(SongKey).Select(group =>
        {
            var song = group.OrderByDescending(item => item.At).ThenBy(item => item.Title, StringComparer.Ordinal).First();
            return new ListeningStatsSong(song.Title!.Trim(), song.Artist!.Trim(), song.Album?.Trim(), group.LongCount());
        }).OrderByDescending(item => item.PlayCount).ThenBy(item => item.Artist, StringComparer.Ordinal)
            .ThenBy(item => item.Title, StringComparer.Ordinal).ThenBy(item => item.Album, StringComparer.Ordinal).Take(10).ToArray();
        var artists = plays.Where(item => !string.IsNullOrWhiteSpace(item.Artist)).GroupBy(item => item.Artist!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ListeningStatsArtist(group.Key, group.LongCount()))
            .OrderByDescending(item => item.PlayCount).ThenBy(item => item.Artist, StringComparer.Ordinal).Take(10).ToArray();
        var albums = plays.Where(item => !string.IsNullOrWhiteSpace(item.Album) && !string.IsNullOrWhiteSpace(item.AlbumArtist ?? item.Artist))
            .GroupBy(item => (Album: Normalize(item.Album!), Artist: Normalize(item.AlbumArtist ?? item.Artist!)))
            .Select(group => new ListeningStatsAlbum(group.First().Album!.Trim(), (group.First().AlbumArtist ?? group.First().Artist)!.Trim(), group.LongCount()))
            .OrderByDescending(item => item.PlayCount).ThenBy(item => item.Artist, StringComparer.Ordinal)
            .ThenBy(item => item.Album, StringComparer.Ordinal).Take(10).ToArray();
        return new(period, plays.Count, plays.Select(item => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.At, zone).DateTime)).Distinct().Count(),
            imported, observedSince, plays.Sum(item => item.DurationMilliseconds ?? 0),
            new(plays.Count - knownSongs.Length, plays.LongCount(item => item.DurationMilliseconds == null)), songs, artists, albums);
    }

    private static string SongKey(StatsPlay item) => item.CanonicalRecordingId is { } recording ? $"recording:{recording:N}"
        : !string.IsNullOrWhiteSpace(item.RecordingMusicBrainzId) ? $"mbid:{Normalize(item.RecordingMusicBrainzId)}"
        : $"track:{item.TrackReference}";

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private sealed record StatsPlay(DateTimeOffset At, string TrackReference, Guid? CanonicalRecordingId,
        string? RecordingMusicBrainzId, string? Title, string? Artist, string? Album, string? AlbumArtist, long? DurationMilliseconds);

    public void Dispose() => cache.Dispose();
}
