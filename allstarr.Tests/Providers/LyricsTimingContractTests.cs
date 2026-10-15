using System.Text.Json;
using allstarr.Core.Capabilities;

namespace allstarr.Tests;

public sealed class LyricsTimingContractTests
{
    [Fact]
    public void CacheRoundTripRetainsSubMillisecondTimesAndSourceFlags()
    {
        var start = TimeSpan.FromTicks(12_345_678);
        var segment = new ProviderLyricsTimedSegment("Syn thetic ", start, null, true, false);
        var result = new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "fixture",
            ProviderLyricsFormat.LineTimed, "[00:01.235]Synthetic", "revision",
            [new("Synthetic", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), [segment])]);
        var serialized = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<ProviderLyricsResult>(serialized)!;
        var line = Assert.Single(restored.TimedLines!);
        var chunk = Assert.Single(line.Segments!);
        Assert.Equal(start, chunk.Start);
        Assert.Equal("Syn thetic ", chunk.Text);
        Assert.Null(chunk.Duration);
        Assert.True(chunk.IsBackground);
        Assert.False(chunk.Synthetic);
        Assert.Equal(TimeSpan.FromSeconds(2), line.Duration);
        Assert.Equal(result.Content, restored.Content);
        Assert.Equal(ProviderLyricsFormat.LineTimed, restored.Format);
    }

    [Fact]
    public void OlderCachedResultsRemainReadableWithoutClaimingWordTiming()
    {
        var restored = JsonSerializer.Deserialize<ProviderLyricsResult>("""
            {"Availability":0,"Source":"fixture","Format":1,"Content":"[00:01.000]Words","Revision":"old"}
            """)!;
        Assert.Null(restored.TimedLines);
        Assert.DoesNotContain("TimedLines", JsonSerializer.Serialize(restored));
        Assert.Equal("[00:01.000]Words", restored.Content);
    }

    [Fact]
    public void CallerCollectionsCannotMutateCachedTiming()
    {
        var segments = new List<ProviderLyricsTimedSegment> { new("Words", TimeSpan.FromSeconds(1)) };
        var lines = new List<ProviderLyricsTimedLine> { new("Words", TimeSpan.FromSeconds(1), segments: segments) };
        var result = new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "fixture",
            ProviderLyricsFormat.LineTimed, "[00:01.000]Words", timedLines: lines);
        segments.Clear();
        lines.Clear();
        var line = Assert.Single(result.TimedLines!);
        Assert.Single(line.Segments!);
        Assert.Throws<NotSupportedException>(() => ((IList<ProviderLyricsTimedLine>)result.TimedLines!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ProviderLyricsTimedSegment>)line.Segments!).Clear());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(864000000001, 0)]
    [InlineData(0, -1)]
    [InlineData(863990000000, 20000000)]
    public void InvalidTimesFailAtTheTypedBoundary(long startTicks, long durationTicks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderLyricsTimedSegment("Words",
            TimeSpan.FromTicks(startTicks), TimeSpan.FromTicks(durationTicks)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderLyricsTimedLine("Words",
            TimeSpan.FromTicks(startTicks), TimeSpan.FromTicks(durationTicks)));
    }

    [Theory]
    [InlineData(ProviderLyricsAvailabilityState.Unavailable)]
    [InlineData(ProviderLyricsAvailabilityState.Restricted)]
    public void UnavailableResultsCannotIncludeTimingContent(ProviderLyricsAvailabilityState availability) =>
        Assert.Throws<ArgumentException>(() => new ProviderLyricsResult(availability, "fixture",
            timedLines: [new("Words", TimeSpan.Zero)]));

    [Fact]
    public void AvailabilityOnlyResultsCannotIncludeTimingWithoutText() =>
        Assert.Throws<ArgumentException>(() => new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
            "fixture", timedLines: [new("Words", TimeSpan.Zero)]));

    [Fact]
    public void TimingPayloadHasAnAggregateTextBudget()
    {
        var text = new string('x', 1_000_000);
        var line = new ProviderLyricsTimedLine(text, TimeSpan.Zero, segments: [new(text, TimeSpan.Zero)]);
        Assert.Throws<ArgumentException>(() => new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
            "fixture", ProviderLyricsFormat.LineTimed, "[00:00.000]Words", timedLines: [line]));
    }

    [Fact]
    public void AggregateSegmentCountIsBoundedAcrossLines()
    {
        var segments = Enumerable.Repeat(new ProviderLyricsTimedSegment("x", TimeSpan.Zero), 25_001).ToArray();
        var line = new ProviderLyricsTimedLine("x", TimeSpan.Zero, segments: segments);
        Assert.Throws<ArgumentException>(() => new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
            "fixture", ProviderLyricsFormat.LineTimed, "[00:00.000]Words", timedLines: [line, line]));
    }
}
