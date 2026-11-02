using System.Data.Common;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task Custom_selection_bounds_overlapping_playback_reads_and_checks_each_recording_once()
    {
        await Seed(count: 176, plays: 0);
        filter = "CleanOnly";
        songs["1"].ExplicitContentLyrics = 1;
        songs["4"].Artist = "";
        await taste.PutNudgeAsync(owner, new("artist", songs["2"].Artist, "never"));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", 100))
            .ReturnsAsync(songs.Values.Append(songs["5"]).ToList());
        var reads = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlapped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .Returns(async (ProtocolExecutionContext context, Song song) =>
            {
                reads.AddOrUpdate(song.Id, 1, (_, count) => count + 1);
                var current = Interlocked.Increment(ref active);
                lock (reads) peak = Math.Max(peak, current);
                if (current == 4) overlapped.TrySetResult();
                try { await release.Task.WaitAsync(context.CancellationToken); return song.ExternalId != "3"; }
                finally { Interlocked.Decrement(ref active); }
            });
        var probe = new MixReadProbe();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(probe).Options);
        taste = new(factory, tags, clock);
        var logger = new MixLogger();
        var generation = NewService(logger).StartAsync(owner, new("song", songs["0"].Id, 25));
        var startedTogether = await Task.WhenAny(overlapped.Task, Task.Delay(TimeSpan.FromSeconds(2))) == overlapped.Task;
        release.TrySetResult();
        var preview = await generation;
        output.WriteLine($"Custom ranking: playback reads {reads.Count}, peak {peak}, database reads {probe.Reads}; " +
            System.Text.RegularExpressions.Regex.Match(Assert.Single(logger.Entries).Message, @"rank=\d+,playback=\d+").Value);

        Assert.True(startedTogether, "Four independent playback reads should start before any finishes.");
        Assert.Equal(4, peak);
        Assert.InRange(reads.Count, 25, 33);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.DoesNotContain(preview.Songs, row => row.Song.ExternalId is "0" or "1" or "2" or "3" or "4");
        Assert.Equal(25, preview.Songs.Count);
        Assert.Equal(songs.Values.Take(61).Where(song => song.ExternalId is not ("0" or "1" or "2" or "3" or "4"))
            .OrderBy(song => song.Id, StringComparer.Ordinal).Take(25).Select(song => song.Id), preview.Songs.Select(row => row.Song.Id));
        Assert.True(probe.Reads < 100, $"Expected batched metadata reads, observed {probe.Reads}.");
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    public async Task Custom_selection_borrows_unused_source_slots_to_fill_from_radio_alone(int length)
    {
        await Seed(count: 90, plays: 0);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", 100))
            .ReturnsAsync(songs.Values.ToList());
        var logger = new MixLogger();

        var preview = await NewService(logger).StartAsync(owner, new("song", songs["0"].Id, length));

        Assert.Equal(length, preview.Songs.Count);
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Exactly(length + 8));
        Assert.Contains("related capped=60; playback checks=" + (length + 8), Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public async Task Custom_selection_keeps_spare_qualified_songs_for_final_rules()
    {
        await Seed(count: 90, plays: 0);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", 100))
            .ReturnsAsync(songs.Values.ToList());
        var checks = 0;
        string? blocked = null;
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .Returns(async (ProtocolExecutionContext _, Song song) =>
            {
                if (Interlocked.Increment(ref checks) == 1)
                {
                    blocked = song.Id;
                    await taste.PutNudgeAsync(owner, new("song", PersonalMixRules.Key(song), "never"));
                }
                return true;
            });

        var preview = await service.StartAsync(owner, new("song", songs["0"].Id, 25));

        Assert.Equal(25, preview.Songs.Count);
        Assert.DoesNotContain(preview.Songs, row => row.Song.Id == blocked);
        Assert.Equal(33, checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_selection_stops_at_the_playback_budget_when_routes_fail(bool throws)
    {
        await Seed(count: 90, plays: 0);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", 100))
            .ReturnsAsync(songs.Values.ToList());
        var checks = 0;
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .Returns((ProtocolExecutionContext _, Song _) =>
            {
                Interlocked.Increment(ref checks);
                return throws ? Task.FromException<bool>(new InvalidOperationException("Unavailable")) : Task.FromResult(false);
            });
        var logger = new MixLogger();
        var start = NewService(logger).StartAsync(owner, new("song", songs["0"].Id, 25));

        if (throws) await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        else Assert.Empty((await start).Songs);

        Assert.Equal(33, checks);
        Assert.Contains("playback checks=33", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public async Task Custom_selection_cancels_pending_playback_and_releases_the_next_start()
    {
        await Seed(count: 60, plays: 0);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", 100))
            .ReturnsAsync(songs.Values.ToList());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .Returns(async (ProtocolExecutionContext context, Song _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                return true;
            });
        using var cancellation = new CancellationTokenSource();
        var start = service.StartAsync(owner, new("song", songs["0"].Id, 25), cancellation.Token);
        await entered.Task.WaitAsync(CompletionDeadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(CompletionDeadline));
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(true);

        Assert.Equal(25, (await service.StartAsync(owner, new("song", songs["0"].Id, 25)).WaitAsync(CompletionDeadline)).Songs.Count);
    }

    [Fact]
    public async Task StartWithEnoughSeedSongs_SkipsFavouriteReads()
    {
        await Seed();
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", It.IsAny<int>()))
            .ReturnsAsync(songs.Values.ToList());
        var probe = new MixReadProbe();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(probe).Options);
        taste = new(factory, tags, clock);
        service = NewService();

        var preview = await service.StartAsync(owner, new("song", songs["0"].Id, 25));

        Assert.Equal(25, preview.Songs.Count);
        Assert.Equal(0, probe.FavouriteReads);
        output.WriteLine($"Custom preview: favourite reads {probe.FavouriteReads}, total reads {probe.Reads}.");
    }

    [Fact]
    public async Task SavedMixList_ReadsOneDocumentWithoutHistoryReplayOrWrites()
    {
        await Seed();
        await service.GenerateAsync(owner);
        await ExternalLink();
        await ExternalLink(selection: "similar");
        await service.ListAsync(owner, "UTC");
        var probe = new MixReadProbe();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(probe).Options);
        taste = new(factory, tags, clock);
        service = NewService();
        var cards = await service.ListAsync(owner, "UTC");
        Assert.Equal(15, cards.Count);
        Assert.Equal(1, probe.MixDocuments);
        Assert.Equal(0, probe.HistoryReads);
        Assert.Equal(0, probe.FullHistoryReads);
        Assert.Equal(0, probe.Writes);
        output.WriteLine($"Saved mix list: documents {probe.MixDocuments}, history reads {probe.HistoryReads}, writes {probe.Writes}, total reads {probe.Reads}.");
    }

    private sealed class MixReadProbe : DbCommandInterceptor
    {
        public int MixDocuments, HistoryReads, FullHistoryReads, Writes, Reads, FavouriteReads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)) Reads++;
            else Writes++;
            if (sql.Contains("FROM \"listening_events\"", StringComparison.Ordinal)) HistoryReads++;
            if (sql.Contains("FROM \"favorite_states\"", StringComparison.Ordinal)) FavouriteReads++;
            if (sql.Contains("FROM \"listening_events\"", StringComparison.Ordinal) && sql.Contains("MusicBrainzFactsJson", StringComparison.Ordinal)) FullHistoryReads++;
            if (sql.Contains("PayloadJson", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(item => Equals(item.Value, "personal-mixes"))) MixDocuments++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
