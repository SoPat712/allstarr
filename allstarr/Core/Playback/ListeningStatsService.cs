using System.Collections.Concurrent;
using allstarr.Core.Enrichment;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.Playback;

public sealed record ListeningStatsPeriod(DateTimeOffset? From, DateTimeOffset To, string TimeZoneId);
public sealed record ListeningStatsCoverage(long UnknownSongIdentityCount, long UnknownDurationCount);
public sealed record ListeningStatsSong(string Title, string Artist, string? Album, long PlayCount)
{
    public string? ArtworkUrl { get; init; }
}
public sealed record ListeningStatsArtist(string Artist, long PlayCount)
{
    public string? ArtworkUrl { get; init; }
}
public sealed record ListeningStatsAlbum(string Album, string Artist, long PlayCount)
{
    public string? ArtworkUrl { get; init; }
}
public sealed record ListeningStatsPoint(string Key, long Plays);
public sealed record ListeningStatsGenre(string Genre, long Plays);
public sealed record ListeningStats(ListeningStatsPeriod Period, long PlayCount, int ActiveDays, long ImportedCount,
    DateTimeOffset? ObservedSince, long EstimatedTimeFromSongLengthsMilliseconds, ListeningStatsCoverage Coverage,
    IReadOnlyList<ListeningStatsSong> TopSongs, IReadOnlyList<ListeningStatsArtist> TopArtists,
    IReadOnlyList<ListeningStatsAlbum> TopAlbums)
{
    public long ShortPlayCount { get; init; }
    public DateTimeOffset? FirstPlayAt { get; init; }
    public int UniqueSongs { get; init; }
    public int UniqueArtists { get; init; }
    public string TimelineUnit { get; init; } = "month";
    public IReadOnlyList<ListeningStatsPoint> Timeline { get; init; } = [];
    public IReadOnlyList<long> ByHour { get; init; } = [];
    public ListeningStatsPoint? BiggestDay { get; init; }
    public int LongestStreakDays { get; init; }
    public IReadOnlyList<ListeningStatsGenre> TopGenres { get; init; } = [];
    public long NewArtistCount { get; init; }
    public IReadOnlyList<ListeningStatsArtist> NewArtists { get; init; } = [];
}

public sealed class ListeningStatsService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock, SharedTagStore? tags = null,
    IEffectiveProviderPolicyResolver? policies = null) : IDisposable
{
    private const int TopLimit = 50;
    public static readonly TimeSpan WarmEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);
    // Recaps opened lately are read again in the background; the list is bounded so odd years cannot pile up work.
    private const int RememberedRecaps = 24;
    private static readonly TimeSpan RememberedFor = TimeSpan.FromDays(1);
    private sealed record OpenedRecap(IntelligenceScope Scope, string? Period, int? Year, TimeZoneInfo Zone);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly ConcurrentDictionary<OpenedRecap, DateTimeOffset> opened = new();

    public async Task<ListeningStats> GetAsync(AdminAuthSession session, string? period, int? year, CancellationToken token, string? timeZoneId = null)
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
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("The time zone is invalid."); }
        }
        var interval = CreatePeriod(period, year, clock.UtcNow, zone);
        opened[new(scope, period, year, zone)] = clock.UtcNow;
        if (opened.Count > RememberedRecaps) opened.TryRemove(opened.MinBy(item => item.Value));
        return await CachedAsync(db, scope, interval, zone, actor.IsSynthetic, TimeSpan.Zero, token);
    }

    // A recap opened lately is read again once the listener's plays changed or its answer is about to expire, so the next
    // visit answers from memory even right after listening. A visit before that still reads it fresh.
    public async Task WarmAsync(CancellationToken token)
    {
        foreach (var (recap, at) in opened)
        {
            if (at <= clock.UtcNow - RememberedFor)
            {
                opened.TryRemove(KeyValuePair.Create(recap, at));
                continue;
            }
            await using var db = await factory.CreateDbContextAsync(token);
            var synthetic = await db.Users.AsNoTracking().Where(user => user.Id == recap.Scope.OwnerUserId && user.Enabled)
                .Select(user => (bool?)user.IsSynthetic).SingleOrDefaultAsync(token);
            if (synthetic == null)
            {
                opened.TryRemove(KeyValuePair.Create(recap, at));
                continue;
            }
            var interval = CreatePeriod(recap.Period, recap.Year, clock.UtcNow, recap.Zone);
            await CachedAsync(db, recap.Scope, interval, recap.Zone, synthetic.Value, 2 * WarmEvery, token);
        }
    }

    private async Task<ListeningStats> CachedAsync(AllstarrDbContext db, IntelligenceScope scope, ListeningStatsPeriod interval,
        TimeZoneInfo zone, bool synthetic, TimeSpan ahead, CancellationToken token)
    {
        // Reading a long history takes a moment, so an answer is kept until the listener's plays change.
        var owner = scope.OwnerUserId;
        var policy = policies == null ? null : await policies.ResolveForUserAsync(owner, token);
        var cleanOnly = policy?.Preferences.ExplicitFilter == "CleanOnly";
        var plays = await db.ListeningEvents.AsNoTracking().LongCountAsync(item => item.OwnerUserId == owner, token);
        var finished = await db.ListeningEvents.AsNoTracking().LongCountAsync(item => item.OwnerUserId == owner && item.State == ListeningEventState.Completed, token);
        var key = (scope, interval, synthetic, plays, finished, cleanOnly, policy?.PreferenceRevision);
        if (!cleanOnly && cache.TryGetValue(key, out (ListeningStats Stats, DateTimeOffset ExpiresAt) cached) && cached.ExpiresAt > clock.UtcNow + ahead)
            return cached.Stats;
        var result = await ReadAsync(db, scope, interval, zone, token, clock.UtcNow, tags, cleanOnly);
        cache.Set(key, (result, clock.UtcNow + KeptFor), new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = KeptFor
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

    private const long CountedPlayTicks = 30 * TimeSpan.TicksPerSecond;

    internal static async Task<ListeningStats> ReadAsync(AllstarrDbContext db, IntelligenceScope scope,
        ListeningStatsPeriod period, TimeZoneInfo zone, CancellationToken token, DateTimeOffset? now = null, SharedTagStore? tags = null,
        bool cleanOnly = false)
    {
        var listened = db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
            (item.State == ListeningEventState.Completed ? item.ListenedAt != null :
                (item.State == ListeningEventState.Abandoned || item.State == ListeningEventState.Skipped) && item.StartedAt != null) &&
            db.Users.Any(user => user.Id == scope.OwnerUserId && !user.IsSynthetic));
        if (cleanOnly) listened = listened.Where(item => db.CanonicalRecordings.Any(recording =>
            recording.Id == item.CanonicalRecordingId && recording.IsExplicit == false));
        // A play counts once it finished or reached 30 seconds, the rule streaming services use.
        // Only finished plays carry a listen date, so the rest are dated from when they started.
        var qualified = listened.Where(item => item.State == ListeningEventState.Completed || item.PositionTicks >= CountedPlayTicks);
        var observed = qualified.Where(item => item.ImportProvenance == null &&
            (item.SourceKind == "protocol" || item.SourceKind == "listenbrainz-api"));
        var observedSince = await observed.OrderBy(item => item.ListenedAt ?? item.StartedAt).Select(item => item.ListenedAt ?? item.StartedAt).FirstOrDefaultAsync(token);
        var firstPlayAt = await qualified.OrderBy(item => item.ListenedAt ?? item.StartedAt).Select(item => item.ListenedAt ?? item.StartedAt).FirstOrDefaultAsync(token);
        var selected = qualified.Where(item => (item.ListenedAt ?? item.StartedAt) < period.To);
        if (period.From is { } from) selected = selected.Where(item => (item.ListenedAt ?? item.StartedAt) >= from);
        var uncounted = listened.Where(item => (item.ListenedAt ?? item.StartedAt) < period.To && item.State != ListeningEventState.Completed && !(item.PositionTicks >= CountedPlayTicks));
        if (period.From is { } start) uncounted = uncounted.Where(item => (item.ListenedAt ?? item.StartedAt) >= start);
        var shortPlays = await uncounted.LongCountAsync(token);
        // Every imported play is among these, so they are counted from this one read instead of another pass over the history.
        var plays = await selected.Where(item => item.SourceKind == "protocol" || item.SourceKind == "listenbrainz-api" ||
                item.SourceKind == "import" || item.ImportProvenance != null)
            .Select(item => new StatsPlay((item.ListenedAt ?? item.StartedAt)!.Value, item.TrackReference, item.CanonicalRecordingId,
                item.RecordingMusicBrainzId, item.Title, item.Artist, item.Album, item.AlbumArtist,
                item.DurationMilliseconds ?? item.PositionTicks / TimeSpan.TicksPerMillisecond,
                item.SourceKind == "import" || item.ImportProvenance != null))
            .ToListAsync(token);
        var imported = plays.LongCount(item => item.Imported);
        var knownSongs = plays.Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Artist)).ToArray();
        var songGroups = knownSongs.GroupBy(SongKey).ToArray();
        var songs = songGroups.Select(group =>
        {
            var song = group.OrderByDescending(item => item.At).ThenBy(item => item.Title, StringComparer.Ordinal).First();
            return new ListeningStatsSong(song.Title!.Trim(), song.Artist!.Trim(), song.Album?.Trim(), group.LongCount()) { ArtworkUrl = ArtworkByName(song.Artist!.Trim(), title: song.Title!.Trim()) };
        }).OrderByDescending(item => item.PlayCount).ThenBy(item => item.Artist, StringComparer.Ordinal)
            .ThenBy(item => item.Title, StringComparer.Ordinal).ThenBy(item => item.Album, StringComparer.Ordinal).Take(TopLimit).ToArray();
        var artistGroups = plays.Where(item => !string.IsNullOrWhiteSpace(item.Artist)).GroupBy(item => item.Artist!.Trim(), StringComparer.OrdinalIgnoreCase).ToArray();
        var artists = artistGroups.Select(group => new ListeningStatsArtist(group.Key, group.LongCount()) { ArtworkUrl = ArtworkByName(group.Key) })
            .OrderByDescending(item => item.PlayCount).ThenBy(item => item.Artist, StringComparer.Ordinal).Take(TopLimit).ToArray();
        var albums = plays.Where(item => !string.IsNullOrWhiteSpace(item.Album) && !string.IsNullOrWhiteSpace(item.AlbumArtist ?? item.Artist))
            .GroupBy(item => (Album: Normalize(item.Album!), Artist: Normalize(item.AlbumArtist ?? item.Artist!)))
            .Select(group => new ListeningStatsAlbum(group.First().Album!.Trim(), (group.First().AlbumArtist ?? group.First().Artist)!.Trim(), group.LongCount())
            {
                ArtworkUrl = ArtworkByName((group.First().AlbumArtist ?? group.First().Artist)!.Trim(), album: group.First().Album!.Trim())
            })
            .OrderByDescending(item => item.PlayCount).ThenBy(item => item.Artist, StringComparer.Ordinal)
            .ThenBy(item => item.Album, StringComparer.Ordinal).Take(TopLimit).ToArray();

        var local = plays.Select(item => TimeZoneInfo.ConvertTime(item.At, zone).DateTime).ToArray();
        var days = local.GroupBy(DateOnly.FromDateTime).ToDictionary(group => group.Key, group => group.LongCount());
        var byHour = new long[24];
        foreach (var at in local) byHour[at.Hour]++;
        var biggest = days.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Select(item => new ListeningStatsPoint(item.Key.ToString("yyyy-MM-dd"), item.Value)).FirstOrDefault();

        var newArtists = Array.Empty<ListeningStatsArtist>();
        var newArtistCount = 0L;
        if (period.From is { } since)
        {
            var earlier = (await qualified.Where(item => (item.ListenedAt ?? item.StartedAt) < since && item.Artist != null).Select(item => item.Artist!).Distinct().ToListAsync(token))
                .Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (earlier.Count > 0)
            {
                var fresh = artistGroups.Where(group => !earlier.Contains(group.Key)).ToArray();
                newArtistCount = fresh.Length;
                newArtists = fresh.Select(group => new ListeningStatsArtist(group.Key, group.LongCount()))
                    .OrderByDescending(item => item.PlayCount).ThenBy(item => item.Artist, StringComparer.Ordinal).Take(12).ToArray();
            }
        }

        var (unit, timeline) = Timeline(period, zone, days, now);
        return new(period, plays.Count, days.Count,
            imported, observedSince, plays.Sum(item => item.DurationMilliseconds ?? 0),
            new(plays.Count - knownSongs.Length, plays.LongCount(item => item.DurationMilliseconds == null)),
            songs, artists, albums)
        {
            ShortPlayCount = shortPlays,
            FirstPlayAt = firstPlayAt,
            UniqueSongs = songGroups.Length,
            UniqueArtists = artistGroups.Length,
            TimelineUnit = unit,
            Timeline = timeline,
            ByHour = byHour,
            BiggestDay = biggest,
            LongestStreakDays = LongestStreak(days.Keys),
            TopGenres = tags == null ? [] : await GenresAsync(tags, plays, token),
            NewArtistCount = newArtistCount,
            NewArtists = newArtists
        };
    }

    private static string ArtworkByName(string artist, string? title = null, string? album = null) =>
        $"/api/admin/stats/art?artist={Uri.EscapeDataString(artist)}" +
        (title != null ? $"&title={Uri.EscapeDataString(title)}" : album != null ? $"&album={Uri.EscapeDataString(album)}" : "");

    // A long span reads best by month, a short one by day.
    private static (string Unit, IReadOnlyList<ListeningStatsPoint> Points) Timeline(ListeningStatsPeriod period, TimeZoneInfo zone,
        IReadOnlyDictionary<DateOnly, long> days, DateTimeOffset? now)
    {
        if (days.Count == 0) return ("day", []);
        var end = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(period.To.AddTicks(-1), zone).DateTime);
        if (now.HasValue && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.Value, zone).DateTime) is var today && today < end) end = today;
        if (!now.HasValue) end = days.Keys.Max();
        var first = period.From.HasValue ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(period.From.Value, zone).DateTime) : days.Keys.Min();
        if (end.DayNumber - first.DayNumber <= 62)
            return ("day", Enumerable.Range(0, end.DayNumber - first.DayNumber + 1).Select(first.AddDays)
                .Select(day => new ListeningStatsPoint(day.ToString("yyyy-MM-dd"), days.GetValueOrDefault(day))).ToArray());
        var months = days.GroupBy(item => (item.Key.Year, item.Key.Month)).ToDictionary(group => group.Key, group => group.Sum(item => item.Value));
        var points = new List<ListeningStatsPoint>();
        for (var month = new DateOnly(first.Year, first.Month, 1); month <= end; month = month.AddMonths(1))
            points.Add(new(month.ToString("yyyy-MM"), months.GetValueOrDefault((month.Year, month.Month))));
        return ("month", points);
    }

    private static int LongestStreak(IEnumerable<DateOnly> days)
    {
        var (longest, current, previous) = (0, 0, default(DateOnly?));
        foreach (var day in days.OrderBy(item => item))
        {
            current = previous is { } last && last.AddDays(1) == day ? current + 1 : 1;
            longest = Math.Max(longest, current);
            previous = day;
        }
        return longest;
    }

    private static async Task<IReadOnlyList<ListeningStatsGenre>> GenresAsync(SharedTagStore tags, IReadOnlyList<StatsPlay> plays, CancellationToken token)
    {
        var recordings = plays.Where(item => item.CanonicalRecordingId != null).GroupBy(item => item.CanonicalRecordingId!.Value)
            .Select(group => (Id: group.Key, Plays: group.LongCount())).OrderByDescending(item => item.Plays).ThenBy(item => item.Id).Take(3000).ToArray();
        if (recordings.Length == 0) return [];
        var profiles = await tags.GetTags(recordings.Select(item => item.Id).ToArray(), token);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var recording in recordings)
            if (profiles.TryGetValue(recording.Id, out var profile))
                foreach (var tag in profile.Tags) totals[tag.Tag] = totals.GetValueOrDefault(tag.Tag) + recording.Plays;
        return totals.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).Take(10)
            .Select(item => new ListeningStatsGenre(item.Key, item.Value)).ToArray();
    }

    private static string SongKey(StatsPlay item) => item.CanonicalRecordingId is { } recording ? $"recording:{recording:N}"
        : !string.IsNullOrWhiteSpace(item.RecordingMusicBrainzId) ? $"mbid:{Normalize(item.RecordingMusicBrainzId)}"
        : $"track:{item.TrackReference}";

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private sealed record StatsPlay(DateTimeOffset At, string TrackReference, Guid? CanonicalRecordingId,
        string? RecordingMusicBrainzId, string? Title, string? Artist, string? Album, string? AlbumArtist, long? DurationMilliseconds, bool Imported);

    public void Dispose() => cache.Dispose();
}
