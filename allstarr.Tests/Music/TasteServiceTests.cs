using allstarr.Controllers;
using allstarr.Core.Enrichment;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class TasteServiceTests
{
    [Fact]
    public void Decay_HalvesAtTheDocumentedIntervals()
    {
        Assert.Equal(.5, TasteService.Decay(1, TimeSpan.FromHours(6), TasteService.LatelyHalfLife), 8);
        Assert.Equal(.5, TasteService.Decay(1, TimeSpan.FromDays(30), TasteService.UsuallyHalfLife), 8);
    }

    [Theory]
    [InlineData(20, 20, 1)]
    [InlineData(20, 200, -1)]
    [InlineData(40, 200, -1)]
    [InlineData(60, 200, -.3)]
    [InlineData(120, 200, 1)]
    public async Task Playback_MapsActualListeningAndDeduplicatesTheOccurrence(int seconds, int duration, double weight)
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        await fixture.Play(song, seconds, duration);
        var before = await fixture.Service.GetAsync(fixture.User);
        Assert.Equal(weight, Assert.Single(before.Usually.Tags).Value, 6);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Stop, seconds), fixture.Track(song, duration), default);
        Assert.Equal(before.Usually.Tags[0].Value, Assert.Single((await fixture.Service.GetAsync(fixture.User)).Usually.Tags).Value, 6);
    }

    [Fact]
    public async Task Playback_RejectsStartsSeeksDropsAndUntrustedSubmissions()
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        var track = fixture.Track(song, 200);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.InferredStop, 0), track, default);
        fixture.Session = "seek";
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(2);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Stop, 150), track, default);
        fixture.Session = "drop";
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(20);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.InferredStop, 20), track, default);
        fixture.Session = "submission";
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Submission, 200) with { SourceKind = "listenbrainz-api" }, track, default);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Submission, 200) with { SourceKind = "import" }, track, default);
        fixture.Session = "failed";
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(20);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Stop, 20) with { Failed = true }, track, default);
        fixture.Session = "rejected";
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(20);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Stop, 20) with
        {
            StreamSource = new("deezer", "song", null, "jellyfin", "primary", false, fixture.Clock.UtcNow, "sequential") { RejectionReason = "failed" }
        }, track, default);
        fixture.Session = "paused";
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(100);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Progress, 100) with { Paused = true }, track, default);
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Usually.Tags);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task ImmediateStopLearnsOnlyWhenPlaybackStartedHealthy(int seconds, bool failed)
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        var track = fixture.Track(song, 200);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Stop, seconds) with { Failed = failed }, track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Stop, seconds), track, default);
        var tags = (await fixture.Service.GetAsync(fixture.User)).Usually.Tags;
        if (failed) Assert.Empty(tags);
        else Assert.Equal(-1, Assert.Single(tags).Value, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task SeekProgressDoesNotTeachDislikeOnALaterStop(int position)
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        var track = fixture.Track(song, 200);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(10);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Progress, 10), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Progress, position), track, default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Stop, position + 1), track, default);
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Usually.Tags);
    }

    [Fact]
    public async Task RestartInterruptsPendingTasteWithoutChangingLearnedPreferences()
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        await fixture.Play(song, 120, 200);
        fixture.Session = "interrupted";
        var track = fixture.Track(song, 200);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Start, 0), track, default);
        var before = (await fixture.Service.GetAsync(fixture.User)).Usually.Tags;
        await fixture.Service.InterruptPendingAsync(default);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        await fixture.Service.ObservePlaybackAsync(fixture.Payload(PlaybackTransition.Stop, 1), track, default);
        Assert.Equal(Assert.Single(before).Value, Assert.Single((await fixture.Service.GetAsync(fixture.User)).Usually.Tags).Value, 3);
    }

    [Fact]
    public async Task ProtocolCompletedSubmission_TeachesOnceWithoutInventingAStart()
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        var payload = fixture.Payload(PlaybackTransition.Submission, 0);
        await fixture.Service.ObservePlaybackAsync(payload, fixture.Track(song, 200), default);
        await fixture.Service.ObservePlaybackAsync(payload, fixture.Track(song, 200), default);
        Assert.Equal(1, Assert.Single((await fixture.Service.GetAsync(fixture.User)).Usually.Tags).Value);
    }

    [Fact]
    public async Task Repeat_AddsHalfOnlyForAnotherQualifiedPlayWithinOneWeek()
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        await fixture.Play(song);
        fixture.Session = "second";
        await fixture.Play(song);
        var value = Assert.Single((await fixture.Service.GetAsync(fixture.User)).Usually.Tags).Value;
        Assert.InRange(value, 2.49, 2.5);
        fixture.Clock.UtcNow += TimeSpan.FromDays(8);
        var previous = Assert.Single((await fixture.Service.GetAsync(fixture.User)).Usually.Tags).Value;
        fixture.Session = "after-week";
        await fixture.Play(song);
        var after = Assert.Single((await fixture.Service.GetAsync(fixture.User)).Usually.Tags).Value;
        Assert.InRange(after - previous, .999, 1);
    }

    [Fact]
    public async Task MoodSwitch_PrefersTodaysRhythmAndBluesWhileKeepingUsualRock()
    {
        await using var fixture = await Fixture.Create();
        var rock = await fixture.Song("rock", "Rock singer");
        var pop = await fixture.Song("pop", "Pop singer");
        var rhythm = await fixture.Song("r&b", "Soul singer");
        for (var i = 0; i < 16; i++)
        {
            fixture.Session = $"yesterday-{i}";
            await fixture.Play(i % 2 == 0 ? rock : pop);
        }
        fixture.Clock.UtcNow += TimeSpan.FromHours(24);
        for (var i = 0; i < 5; i++)
        {
            fixture.Session = $"today-{i}";
            await fixture.Play(rhythm);
        }
        var ranked = await fixture.Service.RankAsync(fixture.User, new[] { rock, pop, rhythm });
        Assert.Equal(rhythm.Id, ranked[0].Id);
        Assert.Contains("r&b today", ranked[0].TasteReason);
        var taste = await fixture.Service.GetAsync(fixture.User);
        Assert.True(taste.Usually.Tags.First(item => item.Name == "rock").Strength > 0);
        Assert.True(taste.LatelyWeight > .5);
    }

    [Theory]
    [InlineData("2026-03-08T05:00:00Z", "2026-03-09T04:00:00Z")]
    [InlineData("2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z")]
    public async Task TodayNudge_ExpiresAtListenersMidnightAcrossDaylightSaving(string now, string midnight)
    {
        await using var fixture = await Fixture.Create();
        fixture.Clock.UtcNow = DateTimeOffset.Parse(now);
        var song = await fixture.Song("indie rock");
        await fixture.Service.PutNudgeAsync(fixture.User, new("tag", "indie rock", "more", true, "America/New_York"));
        var nudge = Assert.Single((await fixture.Service.GetAsync(fixture.User)).Nudges);
        Assert.Equal(DateTimeOffset.Parse(midnight), nudge.ExpiresAt);
        Assert.Contains("You asked for more indie rock", (await fixture.Service.ScoreAsync(fixture.User, song)).Reasons);
        fixture.Clock.UtcNow = nudge.ExpiresAt!.Value;
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Nudges);
        Assert.Equal(0, (await fixture.Service.ScoreAsync(fixture.User, song)).Score);
    }

    [Fact]
    public async Task Nudges_AreDurableAndOnlyNeverRemovesItemsIncludingUntaggedArtists()
    {
        await using var fixture = await Fixture.Create();
        var more = await fixture.Song("indie rock", "Housewife");
        var less = await fixture.Song("electronic", "Other");
        var unknown = new Song { Id = "unknown", Artist = "Blocked artist" };
        await fixture.Service.PutNudgeAsync(fixture.User, new("tag", "indie", "more"));
        await fixture.Service.PutNudgeAsync(fixture.User, new("tag", "electronic", "less"));
        var ranked = await fixture.Service.RankAsync(fixture.User, new[] { less, unknown, more });
        Assert.Equal(3, ranked.Count);
        Assert.Equal(more.Id, ranked[0].Id);
        Assert.Equal(less.Id, ranked[^1].Id);
        await fixture.Service.PutNudgeAsync(fixture.User, new("artist", "Blocked artist", "never"));
        fixture.Clock.UtcNow += TimeSpan.FromDays(40);
        fixture.Service = new(fixture.Factory, fixture.Tags, fixture.Clock);
        ranked = await fixture.Service.RankAsync(fixture.User, new[] { less, unknown, more });
        Assert.Equal(2, ranked.Count);
        Assert.True((await fixture.Service.ScoreAsync(fixture.User, unknown)).Blocked);
        await fixture.Service.DeleteNudgeAsync(fixture.User, "artist", "Blocked artist");
        Assert.False((await fixture.Service.ScoreAsync(fixture.User, unknown)).Blocked);
        await fixture.Service.PutNudgeAsync(fixture.User, new("tag", "indie rock", "never"));
        Assert.True((await fixture.Service.ScoreAsync(fixture.User, more)).Blocked);
    }

    [Fact]
    public async Task PauseResetAndIsolation_KeepPrivateLearningAndChosenNudges()
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        await fixture.Service.PutNudgeAsync(fixture.User, new("tag", "rock", "more"));
        await fixture.Service.SetPausedAsync(fixture.User, true);
        await fixture.Play(song);
        await fixture.Service.ObserveHeartAsync(fixture.User, "paused-heart", song, fixture.Clock.UtcNow, default);
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Usually.Tags);
        await fixture.Service.SetPausedAsync(fixture.User, false);
        fixture.Session = "resumed";
        await fixture.Play(song);
        Assert.NotEmpty((await fixture.Service.GetAsync(fixture.User)).Usually.Tags);
        Assert.Empty((await fixture.Service.GetAsync(fixture.OtherUser)).Usually.Tags);
        Assert.Equal(0, (await fixture.Service.ScoreAsync(fixture.OtherUser, song)).Score);
        await fixture.Service.ResetAsync(fixture.User, false);
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Usually.Tags);
        Assert.Single((await fixture.Service.GetAsync(fixture.User)).Nudges);
        await fixture.Service.ResetAsync(fixture.User, true);
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Nudges);
    }

    [Fact]
    public async Task HeartsAndExplanations_UseOnlyAvailableInputsAndNoTagsStayNeutral()
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("r&b", "Housewife");
        await fixture.Service.ObserveHeartAsync(fixture.User, "heart", song, fixture.Clock.UtcNow, default);
        await fixture.Service.ObserveHeartAsync(fixture.User, "heart", song, fixture.Clock.UtcNow, default);
        Assert.Equal(2, Assert.Single((await fixture.Service.GetAsync(fixture.User)).Usually.Tags).Value);
        var score = await fixture.Service.ScoreAsync(fixture.User, song, "Housewife", true);
        Assert.Contains("You usually enjoy r&b", score.Reasons);
        Assert.Contains("Similar to Housewife", score.Reasons);
        Assert.Contains("New from an artist you follow", score.Reasons);
        var neutral = await fixture.Service.ScoreAsync(fixture.User, new Song { Artist = "Housewife" }, "Housewife", true);
        Assert.Equal(0, neutral.Score);
        Assert.Empty(neutral.Reasons);
        var electronic = await fixture.Song("electronic");
        fixture.Session = "skip";
        await fixture.Play(electronic, 20);
        Assert.Contains("You usually skip electronic", (await fixture.Service.ScoreAsync(fixture.User, electronic)).Reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Api_UsesOnlyTheSessionListenerEvenForAdministrators(bool administrator)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.PutNudgeAsync(fixture.OtherUser, new("tag", "rock", "never"));
        var controller = new TasteController(fixture.Service, fixture.Factory)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>(await controller.Get(default));
        var session = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "listener",
            UserName = "Listener",
            JellyfinAccessToken = "",
            AllstarrUserId = fixture.User,
            IsAdministrator = administrator,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        var response = Assert.IsType<OkObjectResult>(await controller.Get(default));
        Assert.Empty(Assert.IsType<TasteProfile>(response.Value).Nudges);
        Assert.IsType<OkObjectResult>(await controller.PutNudge(new("tag", "pop", "more"), default));
        Assert.IsType<OkObjectResult>(await controller.Pause(default));
        Assert.True((await fixture.Service.GetAsync(fixture.User)).Paused);
        Assert.IsType<OkObjectResult>(await controller.Resume(default));
        Assert.IsType<OkObjectResult>(await controller.Explain(Guid.NewGuid(), default));
        Assert.IsType<OkObjectResult>(await controller.DeleteNudge("tag", "pop", default));
        Assert.IsType<OkObjectResult>(await controller.Reset(new(true), default));
        Assert.Single((await fixture.Service.GetAsync(fixture.OtherUser)).Nudges);
        Assert.IsType<BadRequestObjectResult>(await controller.PutNudge(new("tag", "unknown", "more"), default));
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "unlinked",
            UserId = "listener",
            UserName = "Listener",
            JellyfinAccessToken = "",
            IsAdministrator = false,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Get(default)).StatusCode);
    }

    [Fact]
    public async Task ArtistNudges_UseCaseInsensitiveNamesAndNeverShareLearnedLayers()
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop", "Housewife");
        await fixture.Service.PutNudgeAsync(fixture.User, new("artist", "housewife", "more"));
        Assert.Contains("You asked for more housewife", (await fixture.Service.ScoreAsync(fixture.User, song)).Reasons);
        await fixture.Service.PutNudgeAsync(fixture.User, new("artist", "Housewife", "less"));
        Assert.True((await fixture.Service.ScoreAsync(fixture.User, song)).Score < 0);
        Assert.Equal(0, (await fixture.Service.ScoreAsync(fixture.OtherUser, song)).Score);
        Assert.Single((await fixture.Service.GetAsync(fixture.User)).Nudges);
    }

    [Fact]
    public async Task SyntheticAndOldEvents_DoNotTeachAndResetCannotReplayLearning()
    {
        await using var fixture = await Fixture.Create();
        var song = await fixture.Song("pop");
        await using (var db = fixture.Factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == fixture.User)).IsSynthetic = true;
            await db.SaveChangesAsync();
        }
        await fixture.Play(song);
        await fixture.Service.ObserveHeartAsync(fixture.User, "synthetic", song, fixture.Clock.UtcNow, default);
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Usually.Tags);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == fixture.User)).IsSynthetic = false;
            await db.SaveChangesAsync();
        }
        await fixture.Service.ObserveHeartAsync(fixture.User, "old", song, fixture.Clock.UtcNow.AddDays(-10), default);
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Usually.Tags);
        await fixture.Service.ObserveHeartAsync(fixture.User, "heart", song, fixture.Clock.UtcNow, default);
        var at = fixture.Clock.UtcNow;
        await fixture.Service.ResetAsync(fixture.User, false);
        await fixture.Service.ObserveHeartAsync(fixture.User, "heart", song, at, default);
        Assert.Empty((await fixture.Service.GetAsync(fixture.User)).Usually.Tags);
    }

    [Fact]
    public async Task SimilarAndCachedRadio_ReorderSoftlyAndApplyFreshBlocks()
    {
        await using var fixture = await Fixture.Create();
        var seed = await fixture.Song("rock");
        seed.Id = "ext-deezer-song-seed";
        seed.ExternalId = "seed";
        seed.ExternalProvider = "deezer";
        var pop = await fixture.Song("pop");
        var rhythm = await fixture.Song("r&b");
        foreach (var song in new[] { pop, rhythm })
        {
            song.ExternalId = song.Id;
            song.Id = "ext-deezer-song-" + song.Id;
            song.ExternalProvider = "deezer";
            song.Title = song.ExternalId;
        }
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
            new AllstarrPrincipal(fixture.User, "jellyfin", "primary", "listener", "Listener", false), "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Loose);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).ReturnsAsync(seed);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 10)).ReturnsAsync([pop, rhythm]);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(true);
        await fixture.Service.PutNudgeAsync(fixture.User, new("tag", "r&b", "more"));
        using var mix = new MusicMixService(gateway.Object, taste: fixture.Service);
        var similar = await mix.SimilarSongsAsync(context, "deezer", "seed", seed.Id, 10);
        Assert.Equal(new[] { rhythm.Id, pop.Id }, similar.Select(item => item.Id));
        Assert.Equal("You asked for more r&b", similar[0].TasteReason);
        var native = Mock.Of<INativeMusicSearch<string>>(MockBehavior.Strict);
        var radio = await mix.RadioAsync(context, "deezer", "song", "seed", seed.Id, 10, native);
        Assert.Equal(3, radio.Count);
        Assert.Equal(rhythm.Id, radio[0].Song!.Id);
        Assert.Equal("You asked for more r&b", radio[0].TasteReason);
        await fixture.Service.PutNudgeAsync(fixture.User, new("tag", "r&b", "never"));
        radio = await mix.RadioAsync(context, "deezer", "song", "seed", seed.Id, 10, native);
        Assert.Equal(2, radio.Count);
        Assert.DoesNotContain(radio, item => item.Song!.Id == rhythm.Id);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"), Times.Exactly(2));
    }

    private sealed class Fixture(SqliteTestDatabase database, Factory factory, Clock clock, Guid user, Guid other) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public Clock Clock { get; } = clock;
        public Guid User { get; } = user;
        public Guid OtherUser { get; } = other;
        public SharedTagStore Tags { get; } = new(factory, clock);
        public TasteService Service { get; set; } = new(factory, new(factory, clock), clock);
        public string Session { get; set; } = "first";
        public static async Task<Fixture> Create()
        {
            var database = await SqliteTestDatabase.CreateAsync();
            var factory = new Factory(database.Options);
            var clock = new Clock();
            var user = Guid.NewGuid();
            var other = Guid.NewGuid();
            await using var db = factory.CreateDbContext();
            foreach (var id in new[] { user, other }) db.Users.Add(new() { Id = id, DisplayName = "Listener", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "primary", BackendPrincipalId = id.ToString(), CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow });
            await db.SaveChangesAsync();
            return new(database, factory, clock, user, other);
        }
        public async Task<Song> Song(string tag, string artist = "Singer")
        {
            var id = Guid.NewGuid();
            await Tags.SaveAsync(SharedTagStore.RecordingKey(id), [new("provider", [new(tag)], Clock.UtcNow)], [], false, default);
            return new() { Id = id.ToString(), CanonicalRecordingId = id, Title = "Song", Artist = artist };
        }
        public PlaybackSignalPayload Payload(PlaybackTransition transition, int seconds) => new(new(User, "jellyfin", "primary"), transition,
            "song", "device", Session, TimeSpan.FromSeconds(seconds).Ticks, Clock.UtcNow, $"{Session}:{transition}", Session);
        public PlaybackTrackSnapshot Track(Song song, int duration) => new(null, song.CanonicalRecordingId, "song", song.Title, song.Artist, null, duration * 1000L);
        public async Task Play(Song song, int seconds = 120, int duration = 200)
        {
            await Service.ObservePlaybackAsync(Payload(PlaybackTransition.Start, 0), Track(song, duration), default);
            Clock.UtcNow += TimeSpan.FromSeconds(seconds);
            await Service.ObservePlaybackAsync(Payload(PlaybackTransition.Stop, seconds), Track(song, duration), default);
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
