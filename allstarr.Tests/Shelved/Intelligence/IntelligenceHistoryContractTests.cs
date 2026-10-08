using allstarr.Controllers;
using allstarr.Core.Intelligence;

namespace allstarr.Tests;

public sealed class IntelligenceHistoryContractTests
{
    [Fact]
    public void CursorPeriodAndStreakContractsStayValidAndStable()
    {
        var listenedAt = DateTimeOffset.Parse("2026-08-01T12:34:56.789Z");
        var id = Guid.Parse("01988888-1111-7777-8000-000000000001");
        var encoded = new ListeningHistoryCursor(listenedAt, id).ToString();

        Assert.True(ListeningHistoryCursor.TryParse(encoded, out var cursor));
        Assert.Equal(listenedAt, cursor.ListenedAt);
        Assert.Equal(id, cursor.Id);
        Assert.False(ListeningHistoryCursor.TryParse("not-a-cursor", out _));

        Assert.True(ListeningHistoryPeriod.TryCreate(null, null, listenedAt, out var defaultPeriod));
        Assert.Equal(DateTimeOffset.MinValue, defaultPeriod.From);
        Assert.Equal(listenedAt, defaultPeriod.To);
        Assert.True(ListeningHistoryPeriod.TryCreate(
            listenedAt.AddYears(-20), listenedAt, listenedAt, out _));
        Assert.False(ListeningHistoryPeriod.TryCreate(
            listenedAt, listenedAt, listenedAt, out _));

        var streaks = ListeningHistoryStreaks.Calculate(
            [new(2026, 7, 20), new(2026, 8, 1), new(2026, 8, 2), new(2026, 8, 2)],
            new(2026, 8, 2));
        Assert.Equal(2, streaks.Current);
        Assert.Equal(2, streaks.Longest);
    }

    [Fact]
    public void ListeningHistoryDefaultsToUnlimitedUntilTheUserChoosesALimit()
    {
        Assert.Equal(0, new IntelligencePolicyRequest().RetentionDays);
        Assert.Equal(0, new IntelligencePolicyRecord().RetentionDays);
    }
}
