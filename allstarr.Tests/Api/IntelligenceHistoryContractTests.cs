using allstarr.Controllers;

namespace allstarr.Tests;

public sealed class IntelligenceHistoryContractTests
{
    [Fact]
    public void CursorPeriodAndStreakContractsStayBoundedAndStable()
    {
        var listenedAt = DateTimeOffset.Parse("2026-08-01T12:34:56.789Z");
        var id = Guid.Parse("01988888-1111-7777-8000-000000000001");
        var encoded = new ListeningHistoryCursor(listenedAt, id).ToString();

        Assert.True(ListeningHistoryCursor.TryParse(encoded, out var cursor));
        Assert.Equal(listenedAt, cursor.ListenedAt);
        Assert.Equal(id, cursor.Id);
        Assert.False(ListeningHistoryCursor.TryParse("not-a-cursor", out _));

        Assert.True(ListeningHistoryPeriod.TryCreate(null, listenedAt, listenedAt, out var defaultPeriod));
        Assert.Equal(TimeSpan.FromDays(30), defaultPeriod.To - defaultPeriod.From);
        Assert.False(ListeningHistoryPeriod.TryCreate(
            listenedAt.AddDays(-3651), listenedAt, listenedAt, out _));

        var streaks = ListeningHistoryStreaks.Calculate(
            [new(2026, 7, 20), new(2026, 8, 1), new(2026, 8, 2), new(2026, 8, 2)],
            new(2026, 8, 2));
        Assert.Equal(2, streaks.Current);
        Assert.Equal(2, streaks.Longest);
    }
}
