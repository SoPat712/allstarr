using allstarr.Controllers;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ListeningStatsTests : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private ListeningStatsService stats = null!;
    private readonly Clock clock = new() { UtcNow = DateTimeOffset.Parse("2026-10-28T16:00:00Z") };
    private readonly Guid owner = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        stats = new(factory, clock);
        await using var db = factory.CreateDbContext();
        db.Users.Add(new()
        {
            Id = owner,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
            LastSeenAt = clock.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task EmptyHistoryReturnsCoverageAndEmptyTops()
    {
        var result = await stats.GetAsync(Session(), "all", null, default);
        Assert.Equal(0, result.PlayCount);
        Assert.Equal(0, result.ActiveDays);
        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(0, result.EstimatedTimeFromSongLengthsMilliseconds);
        Assert.Null(result.ObservedSince);
        Assert.Equal(0, result.Coverage.UnknownSongIdentityCount);
        Assert.Equal(0, result.Coverage.UnknownDurationCount);
        Assert.Empty(result.TopSongs);
        Assert.Empty(result.TopArtists);
        Assert.Empty(result.TopAlbums);
        Assert.Equal(TimeZoneInfo.Local.Id, result.Period.TimeZoneId);
    }

    [Fact]
    public async Task ImportsContributeToTotalsAndTopsAlongsideObservedPlays()
    {
        var first = Play("2026-10-01T12:00:00Z");
        var repeat = Play("2026-10-02T12:00:00Z");
        var unknown = Play("2026-10-03T12:00:00Z");
        unknown.Title = null;
        unknown.Artist = null;
        unknown.Album = null;
        unknown.DurationMilliseconds = null;
        unknown.SourceKind = "listenbrainz-api";
        var import = Play("2020-01-01T12:00:00Z");
        import.SourceKind = "import";
        import.ImportProvenance = "fixture";
        import.TrackReference = "imported-track";
        import.Title = "Imported song";
        import.Artist = "Imported artist";
        import.Album = "Imported album";
        var provenance = Play("2026-10-04T12:00:00Z");
        provenance.ImportProvenance = "fixture";
        await Add(first, repeat, unknown, import, provenance);

        var result = await stats.GetAsync(Session(), "all", null, default);

        Assert.Equal(5, result.PlayCount);
        Assert.Equal(5, result.ActiveDays);
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(first.ListenedAt, result.ObservedSince);
        Assert.Equal(480000, result.EstimatedTimeFromSongLengthsMilliseconds);
        Assert.Equal(1, result.Coverage.UnknownSongIdentityCount);
        Assert.Equal(1, result.Coverage.UnknownDurationCount);
        Assert.Collection(result.TopSongs,
            song => { Assert.Equal("Song", song.Title); Assert.Equal(3, song.PlayCount); },
            song => { Assert.Equal("Imported song", song.Title); Assert.Equal(1, song.PlayCount); });
        Assert.Collection(result.TopArtists,
            artist => { Assert.Equal("Artist", artist.Artist); Assert.Equal(3, artist.PlayCount); },
            artist => { Assert.Equal("Imported artist", artist.Artist); Assert.Equal(1, artist.PlayCount); });
        Assert.Collection(result.TopAlbums,
            album => { Assert.Equal("Album", album.Album); Assert.Equal(3, album.PlayCount); },
            album => { Assert.Equal("Imported album", album.Album); Assert.Equal(1, album.PlayCount); });
    }

    [Fact]
    public async Task ImportedPlaysUseOriginalDatesAndDisappearAfterRemovalAndCacheExpiry()
    {
        var import = Play("2020-01-01T12:00:00Z");
        import.SourceKind = "import";
        import.TrackReference = "imported-track";
        import.Title = "Imported song";
        import.Artist = "Imported artist";
        import.Album = "Imported album";
        var observed = Play("2026-10-01T12:00:00Z");
        await Add(import, observed);

        var historical = await stats.GetAsync(Session(), null, 2020, default);
        Assert.Equal(1, historical.PlayCount);
        Assert.Equal(1, historical.ActiveDays);
        Assert.Equal(1, historical.ImportedCount);
        Assert.Equal("Imported song", Assert.Single(historical.TopSongs).Title);
        Assert.Equal("Imported artist", Assert.Single(historical.TopArtists).Artist);
        Assert.Equal(observed.ListenedAt, historical.ObservedSince);
        var current = await stats.GetAsync(Session(), "year", null, default);
        Assert.Equal(1, current.PlayCount);
        Assert.Equal(0, current.ImportedCount);
        Assert.Equal("Song", Assert.Single(current.TopSongs).Title);
        Assert.Equal(2, (await stats.GetAsync(Session(), "all", null, default)).PlayCount);

        await using (var db = factory.CreateDbContext())
        {
            db.ListeningEvents.Remove(await db.ListeningEvents.SingleAsync(item => item.Id == import.Id));
            await db.SaveChangesAsync();
        }
        clock.UtcNow = clock.UtcNow.AddSeconds(31);

        var removed = await stats.GetAsync(Session(), null, 2020, default);
        Assert.Equal(0, removed.PlayCount);
        Assert.Equal(0, removed.ActiveDays);
        Assert.Equal(0, removed.ImportedCount);
        Assert.Equal(0, removed.EstimatedTimeFromSongLengthsMilliseconds);
        Assert.Empty(removed.TopSongs);
        Assert.Empty(removed.TopArtists);
        Assert.Empty(removed.TopAlbums);
        var remaining = await stats.GetAsync(Session(), "all", null, default);
        Assert.Equal(1, remaining.PlayCount);
        Assert.Equal("Song", Assert.Single(remaining.TopSongs).Title);
        Assert.Equal("Artist", Assert.Single(remaining.TopArtists).Artist);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerAndAdministratorGetOnlyTheirExactScope(bool administrator)
    {
        var own = Play("2026-10-01T12:00:00Z");
        var other = Play("2026-10-01T12:00:00Z");
        other.OwnerUserId = Guid.CreateVersion7();
        var protocol = Play("2026-10-01T12:00:00Z");
        protocol.Protocol = "subsonic";
        var backend = Play("2026-10-01T12:00:00Z");
        backend.BackendInstanceId = "other";
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = other.OwnerUserId,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "other",
                DisplayName = "Other",
                Enabled = true,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
                LastSeenAt = clock.UtcNow
            });
            (await db.Users.SingleAsync(user => user.Id == owner)).IsAdmin = administrator;
            await db.SaveChangesAsync();
        }
        await Add(own, other, protocol, backend);

        var result = await stats.GetAsync(Session(administrator), "all", null, default);
        Assert.Equal(1, result.PlayCount);
        var otherSession = new AdminAuthSession
        {
            SessionId = "other-fixture",
            UserId = "other",
            UserName = "Other",
            IsAdministrator = false,
            BackendType = "Jellyfin",
            BackendInstanceId = "backend",
            AllstarrUserId = other.OwnerUserId,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = clock.UtcNow.AddHours(1).UtcDateTime
        };
        Assert.Equal(1, (await stats.GetAsync(otherSession, "all", null, default)).PlayCount);

        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(administrator);
        http.Request.QueryString = new("?period=all&ownerUserId=" + other.OwnerUserId);
        var controller = new ListeningStatsController(stats) { ControllerContext = new() { HttpContext = http } };
        var response = Assert.IsType<OkObjectResult>(await controller.Get("all", null, default));
        Assert.Equal(1, Assert.IsType<ListeningStats>(response.Value).PlayCount);
        Assert.Equal("private, no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Single(typeof(ListeningStatsController).GetCustomAttributes(typeof(ServiceFilterAttribute), false)
            .Cast<ServiceFilterAttribute>(), attribute => attribute.ServiceType == typeof(AdminPortFilter));
    }

    [Fact]
    public async Task SyntheticAndUnqualifiedHistoricalRowsDoNotCount()
    {
        var playing = Play("2026-10-01T12:00:00Z");
        playing.State = ListeningEventState.Playing;
        var skipped = Play("2026-10-01T12:00:00Z");
        skipped.State = ListeningEventState.Skipped;
        var abandoned = Play("2026-10-01T12:00:00Z");
        abandoned.State = ListeningEventState.Abandoned;
        var undated = Play("2026-10-01T12:00:00Z");
        undated.ListenedAt = null;
        await Add(playing, skipped, abandoned, undated);
        Assert.Equal(0, (await stats.GetAsync(Session(), "all", null, default)).PlayCount);
        await Add(Play("2026-10-02T12:00:00Z"));
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync()).IsSynthetic = true;
            await db.SaveChangesAsync();
        }
        var result = await stats.GetAsync(Session(), "all", null, default);
        Assert.Equal(0, result.PlayCount);
        Assert.Null(result.ObservedSince);
    }

    [Fact]
    public async Task CachedRecapExpiresAndRechecksTheSessionBeforeReturning()
    {
        await Add(Play("2025-01-01T12:00:00Z"));
        var first = await stats.GetAsync(Session(), null, 2025, default);
        await Add(Play("2025-02-01T12:00:00Z"));
        Assert.Same(first, await stats.GetAsync(Session(), null, 2025, default));
        clock.UtcNow = clock.UtcNow.AddSeconds(31);
        Assert.Equal(2, (await stats.GetAsync(Session(), null, 2025, default)).PlayCount);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stats.GetAsync(Session(), null, 2025, default));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("principal")]
    [InlineData("backend")]
    [InlineData("protocol")]
    [InlineData("unlinked")]
    public async Task InvalidSessionsCannotReadStats(string mismatch)
    {
        var session = Session(mismatch: mismatch);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stats.GetAsync(session, "all", null, default));
        var controller = new ListeningStatsController(stats) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>(await controller.Get(null, null, default));
    }

    [Theory]
    [InlineData("2026-03-08T16:00:00Z", "2026-03-02T05:00:00Z", "2026-03-09T04:00:00Z", 167)]
    [InlineData("2026-11-01T16:00:00Z", "2026-10-26T04:00:00Z", "2026-11-02T05:00:00Z", 169)]
    public async Task LocalCalendarWindowsUseHalfOpenDstBoundaries(string now, string from, string to, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var period = ListeningStatsService.CreatePeriod("7d", null, DateTimeOffset.Parse(now), zone);
        Assert.Equal(DateTimeOffset.Parse(from), period.From);
        Assert.Equal(DateTimeOffset.Parse(to), period.To);
        Assert.Equal(hours, (period.To - period.From!.Value).TotalHours);
        await Add(Play(from), Play(DateTimeOffset.Parse(to).AddTicks(-1).ToString("O")), Play(to),
            Play(DateTimeOffset.Parse(from).AddTicks(-1).ToString("O")));
        await using var db = factory.CreateDbContext();
        var result = await ListeningStatsService.ReadAsync(db, new(owner, "jellyfin", "backend"), period, zone, default);
        Assert.Equal(2, result.PlayCount);
        Assert.Equal(2, result.ActiveDays);
    }

    [Fact]
    public async Task RecapUsesLocalYearAndAllTimeObservedSince()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var period = ListeningStatsService.CreatePeriod(null, 2025, clock.UtcNow, zone);
        Assert.Equal(DateTimeOffset.Parse("2025-01-01T05:00:00Z"), period.From);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T05:00:00Z"), period.To);
        await Add(Play("2024-01-01T12:00:00Z"), Play("2025-01-01T04:59:59Z"), Play("2025-01-01T05:00:00Z"),
            Play("2026-01-01T04:59:59Z"), Play("2026-01-01T05:00:00Z"));
        await using var db = factory.CreateDbContext();
        var result = await ListeningStatsService.ReadAsync(db, new(owner, "jellyfin", "backend"), period, zone, default);
        Assert.Equal(2, result.PlayCount);
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T12:00:00Z"), result.ObservedSince);
        var thirty = ListeningStatsService.CreatePeriod("30d", null, clock.UtcNow, zone);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T04:00:00Z"), thirty.From);
        Assert.Equal(ListeningStatsService.CreatePeriod("year", null, clock.UtcNow, zone),
            ListeningStatsService.CreatePeriod(null, 2026, clock.UtcNow, zone));
    }

    [Fact]
    public async Task TopsAreLimitedAndKeepDistinctSongAndAlbumIdentities()
    {
        var plays = Enumerable.Range(0, 12).Select(index =>
        {
            var play = Play("2026-10-01T12:00:00Z");
            play.Title = "Song";
            play.TrackReference = $"track-{index}";
            play.Artist = $"Artist {index:00}";
            play.AlbumArtist = play.Artist;
            return play;
        }).ToArray();
        await Add(plays);
        var result = await stats.GetAsync(Session(), "all", null, default);
        Assert.Equal(10, result.TopSongs.Count);
        Assert.Equal(10, result.TopArtists.Count);
        Assert.Equal(10, result.TopAlbums.Count);
        Assert.Equal("Artist 00", result.TopSongs[0].Artist);
    }

    [Theory]
    [InlineData("bad", null)]
    [InlineData("7d", 2025)]
    [InlineData(null, 0)]
    [InlineData(null, 9999)]
    public async Task InvalidPeriodsReturnBadRequest(string? period, int? year)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session();
        var controller = new ListeningStatsController(stats) { ControllerContext = new() { HttpContext = http } };
        Assert.IsType<BadRequestObjectResult>(await controller.Get(period, year, default));
    }

    private ListeningEventRecord Play(string at) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = owner,
        Protocol = "jellyfin",
        BackendInstanceId = "backend",
        OccurrenceKey = Guid.NewGuid().ToString("N"),
        State = ListeningEventState.Completed,
        ListenedAt = DateTimeOffset.Parse(at),
        UpdatedAt = clock.UtcNow,
        SourceKind = "protocol",
        TrackReference = "track",
        Title = "Song",
        Artist = "Artist",
        Album = "Album",
        DurationMilliseconds = 120000
    };

    private async Task Add(params ListeningEventRecord[] plays)
    {
        await using var db = factory.CreateDbContext();
        db.ListeningEvents.AddRange(plays);
        await db.SaveChangesAsync();
    }

    private AdminAuthSession Session(bool administrator = false, string? mismatch = null) => new()
    {
        SessionId = "fixture",
        UserId = mismatch == "principal" ? "other" : "listener",
        UserName = "Listener",
        IsAdministrator = administrator,
        BackendType = mismatch == "protocol" ? "Subsonic" : "Jellyfin",
        BackendInstanceId = mismatch == "backend" ? "other" : "backend",
        AllstarrUserId = mismatch == "unlinked" ? null : owner,
        JellyfinAccessToken = "fixture",
        ExpiresAtUtc = clock.UtcNow.AddHours(mismatch == "expired" ? -1 : 1).UtcDateTime
    };

    public async Task DisposeAsync()
    {
        stats.Dispose();
        await database.DisposeAsync();
    }

    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
}
