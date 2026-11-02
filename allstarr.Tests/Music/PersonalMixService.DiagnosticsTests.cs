using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task Custom_start_logs_stage_counts_once_without_song_or_listener_details()
    {
        await Seed(count: 8, plays: 0);
        filter = "CleanOnly";
        songs["1"].ExplicitContentLyrics = 1;
        await taste.PutNudgeAsync(owner, new("artist", songs["2"].Artist, "never"));
        songs["3"].Title = "Unavailable";
        songs["6"].Artist = "";
        await using (var db = factory.CreateDbContext())
        {
            for (var skip = 0; skip < 3; skip++) db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                OccurrenceKey = "skip-" + skip,
                TrackReference = songs["4"].Id,
                State = ListeningEventState.Skipped,
                UpdatedAt = clock.UtcNow,
                PositionTicks = TimeSpan.FromSeconds(10).Ticks,
                DurationMilliseconds = 180000
            });
            await db.SaveChangesAsync();
        }
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", 100))
            .ReturnsAsync(songs.Values.Append(songs["7"]).ToList());
        var logger = new MixLogger();

        var preview = await NewService(logger).StartAsync(owner, new("song", songs["0"].Id, 25));

        Assert.Equal(2, preview.Songs.Count);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("seed resolved=1, seed search found=0, provider similar=9", entry.Message);
        Assert.Contains("radio[found=9,without-seed=8,unique=7,details=6,policy=5,never=4,playable=3,skips=2,filters=2,evidence=2,revalidated-never=2,revalidated-policy=2,access=2,eligible=2,artist-checked=2,artist-kept=2,selected=2]", entry.Message);
        Assert.Contains("sound neighbours=0, preferred=0", entry.Message);
        Assert.Contains("selected seed/related/sound/general=0/2/0/0", entry.Message);
        Assert.Contains("completed=1", entry.Message);
        Assert.Contains("phases ms ", entry.Message);
        Assert.Matches(@"playback=\d+", entry.Message);
        Assert.Matches(@"total=\d+", entry.Message);
        Assert.DoesNotContain('\n', entry.Message);
        foreach (var value in songs.Values.SelectMany(song => new[] { song.Title, song.Artist, song.Id, song.CanonicalRecordingId.ToString() })
            .Append(owner.ToString()).Append(other.ToString()).Append("main").Append("deezer").Where(value => !string.IsNullOrEmpty(value)))
            Assert.DoesNotContain(value!, entry.Message, StringComparison.Ordinal);
        Assert.All(entry.Values, value => Assert.True(value is int or string));
    }

    [Fact]
    public async Task Custom_start_logs_partial_counts_once_when_seed_is_missing()
    {
        var logger = new MixLogger();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => NewService(logger).StartAsync(owner, new("song", "private-missing-seed", 25)));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("seed resolved=0", entry.Message);
        Assert.Contains("completed=0", entry.Message);
        Assert.DoesNotContain("private-missing-seed", entry.Message);
    }

    private sealed class MixLogger : ILogger<PersonalMixService>
    {
        internal List<(LogLevel Level, string Message, object?[] Values)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, formatter(state, error), Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).Select(item => item.Value).ToArray()));
    }
}
