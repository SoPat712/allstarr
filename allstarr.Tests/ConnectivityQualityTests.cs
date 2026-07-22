using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ConnectivityQualityTests
{
    [Theory]
    [InlineData(0, 4)]
    [InlineData(150, 4)]
    [InlineData(150.1, 3)]
    [InlineData(400, 3)]
    [InlineData(400.1, 2)]
    [InlineData(1000, 2)]
    [InlineData(1000.1, 1)]
    public void ApiLatency_UsesSharedBoundaries(double milliseconds, int expected) =>
        Assert.Equal(expected, ConnectivityQuality.Bars(milliseconds, true, ConnectivityMetric.ApiLatency));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(500, 4)]
    [InlineData(500.1, 3)]
    [InlineData(1500, 3)]
    [InlineData(1500.1, 2)]
    [InlineData(4000, 2)]
    [InlineData(4000.1, 1)]
    public void ClickToStream_UsesSharedBoundaries(double milliseconds, int expected) =>
        Assert.Equal(expected, ConnectivityQuality.Bars(milliseconds, true, ConnectivityMetric.ClickToStream));

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, -1)]
    [InlineData(true, double.NaN)]
    [InlineData(true, double.PositiveInfinity)]
    public void InvalidOrFailedMeasurements_ShowZeroBars(bool succeeded, double milliseconds) =>
        Assert.Equal(0, ConnectivityQuality.Bars(milliseconds, succeeded, ConnectivityMetric.ApiLatency));

    [Theory]
    [InlineData(4, "excellent")]
    [InlineData(3, "good")]
    [InlineData(2, "fair")]
    [InlineData(1, "poor")]
    [InlineData(0, "unavailable")]
    public void Labels_AreDerivedFromBars(int bars, string expected) =>
        Assert.Equal(expected, ConnectivityQuality.Label(bars));
}
