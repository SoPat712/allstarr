using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using allstarr.Controllers.ClientApi;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Xunit.Abstractions;

namespace allstarr.Tests;

[Collection("Performance")]
public sealed class RankedSearchSnapshotsPerformanceTests(ITestOutputHelper output)
{
    private const string Query = "silver river";
    private sealed record Picture((string Id, double Score)[] Songs, (string Id, double Score)[] Items);

    [Fact]
    public async Task ThirteenSnapshotsMatchPreviousRankingAndScoreEachOfTwoThousandRowsOnce()
    {
        using var process = Process.GetCurrentProcess();
        var previousRows = MakeRows();
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        var expected = PreviousSnapshots(previousRows);
        var beforeWall = watch.Elapsed.TotalMilliseconds;
        process.Refresh();
        var beforeCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var rows = MakeRows();
        var clock = new ManualTimeProvider();
        var emitted = Channel.CreateUnbounded<JsonNode>();
        var snapshots = new RankedSearchSnapshots(Query, rows.Length, ["songs"], "missing", clock,
            (value, _) => { emitted.Writer.TryWrite(JsonSerializer.SerializeToNode(value)!); return Task.CompletedTask; });
        process.Refresh();
        cpu = process.TotalProcessorTime;
        watch.Restart();
        var running = snapshots.RunAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        for (var batch = 0; batch < 13; batch++)
        {
            await snapshots.AddAsync("source-" + batch, Batch(rows, batch), CancellationToken.None);
            if (batch == 12) snapshots.Complete([]);
            var delay = TimeSpan.FromMilliseconds(batch == 0 ? 400 : 250);
            await clock.WaitForTimersAsync(delay);
            clock.Advance(delay);
            var value = await emitted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(batch + 1, value["revision"]!.GetValue<int>());
            Assert.Equal(batch == 12, value["done"]!.GetValue<bool>());
            Assert.Equal(expected[batch].Songs, ReadRows(value["sections"]!["songs"]!));
            Assert.Equal(expected[batch].Items, ReadRows(value["items"]!));
        }
        await running.WaitAsync(TimeSpan.FromSeconds(30));
        var afterWall = watch.Elapsed.TotalMilliseconds;
        process.Refresh();
        var afterCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        Assert.All(rows, row => Assert.Single(row.Scores));
        Assert.False(emitted.Reader.TryRead(out _));
        output.WriteLine($"Synthetic 2000 ordinary songs / 13 snapshots: previous CPU {beforeCpu:F1} ms, wall {beforeWall:F1} ms; current CPU {afterCpu:F1} ms, wall {afterWall:F1} ms");
    }

    [Fact]
    public async Task CancellationAfterFirstWriteStopsPublishingAndReleasesAnUnacceptedAdd()
    {
        var clock = new ManualTimeProvider();
        using var stop = new CancellationTokenSource();
        var writes = 0;
        Task? pending = null;
        RankedSearchSnapshots? snapshots = null;
        snapshots = new(Query, 10, ["songs"], "missing", clock, (_, _) =>
        {
            writes++;
            pending = snapshots!.AddAsync("unfinished", [MakeRow(1)], stop.Token);
            stop.Cancel();
            return Task.CompletedTask;
        });
        var running = snapshots.RunAsync(stop.Token);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        await snapshots.AddAsync("first", [MakeRow(0)], stop.Token);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, writes);
    }

    private static List<Picture> PreviousSnapshots(RankedSearchRow[] rows)
    {
        var sections = new List<RankedSearchRow>();
        var mixed = new List<RankedSearchRow>();
        var pictures = new List<Picture>();
        for (var batch = 0; batch < 13; batch++)
        {
            sections.AddRange(Batch(rows, batch));
            var all = sections.ToArray();
            foreach (var row in all) row.Item["score"] = SearchRelevanceRanker.ScoreSong(Query, row.Song!, hasKnownDurationVersion: true);
            PreviousRank(sections, batch == 0);
            var byId = all.ToDictionary(row => row.Song!.Id);
            var result = new MusicSearchResult<object>(all.Select(row => new MusicSearchEntry<object> { Song = row.Song }).ToArray(), [], []);
            mixed.AddRange(MusicSearchService.RankTogether(Query, result).Select(entry => byId[entry.Song!.Id]).Where(row => !mixed.Contains(row)));
            PreviousRank(mixed, batch == 0);
            pictures.Add(new(sections.Select(row => (row.Song!.Id, row.Score)).ToArray(), mixed.Select(row => (row.Song!.Id, row.Score)).ToArray()));
        }
        return pictures;
    }

    private static void PreviousRank(List<RankedSearchRow> rows, bool first)
    {
        if (first)
        {
            var ordered = rows.OrderByDescending(row => row.Score).ThenBy(row => row.Native == null).ToArray();
            rows.Clear();
            rows.AddRange(ordered);
            return;
        }
        for (var index = 1; index < rows.Count; index++)
            for (var position = index; position > 0 && (rows[position].Native != null && rows[position - 1].Native == null
                     ? rows[position].Score >= rows[position - 1].Score : rows[position].Score >= rows[position - 1].Score + 5); position--)
                (rows[position - 1], rows[position]) = (rows[position], rows[position - 1]);
    }

    private static RankedSearchRow[] MakeRows() => Enumerable.Range(0, 2000).Select(MakeRow).ToArray();
    private static RankedSearchRow MakeRow(int index)
    {
        var song = new Song
        {
            Id = "song-" + index,
            Title = (index % 5) switch
            {
                0 => "Silver River",
                1 => "Silver Rivers",
                2 => "Riverbank",
                3 => "Silver River Evening",
                _ => "Silver Night"
            },
            Artist = "North Ensemble",
            Duration = 200
        };
        return new RankedSearchRow(new JsonObject { ["Id"] = song.Id, ["score"] = 0d }, "songs", Song: song);
    }

    private static RankedSearchRow[] Batch(RankedSearchRow[] rows, int batch) => rows[(batch * rows.Length / 13)..((batch + 1) * rows.Length / 13)];
    private static (string Id, double Score)[] ReadRows(JsonNode value) => value.AsArray()
        .Select(row => (row!["Id"]!.GetValue<string>(), row["score"]!.GetValue<double>())).ToArray();
}
