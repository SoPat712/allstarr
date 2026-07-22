namespace allstarr.Services.Common;

public enum ConnectivityMetric
{
    ApiLatency,
    ClickToStream
}

/// <summary>
/// Shared connectivity-meter policy for every provider diagnostic surface.
/// Metric-specific thresholds live here so controllers and UI endpoints cannot
/// silently assign different bar counts to the same measurement.
/// </summary>
public static class ConnectivityQuality
{
    public static int Bars(double milliseconds, bool succeeded, ConnectivityMetric metric)
    {
        if (!succeeded || !double.IsFinite(milliseconds) || milliseconds < 0) return 0;

        var (excellent, good, fair) = metric switch
        {
            ConnectivityMetric.ApiLatency => (150d, 400d, 1_000d),
            ConnectivityMetric.ClickToStream => (500d, 1_500d, 4_000d),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown connectivity metric.")
        };

        if (milliseconds <= excellent) return 4;
        if (milliseconds <= good) return 3;
        if (milliseconds <= fair) return 2;
        return 1;
    }

    public static string Label(int bars) => bars switch
    {
        4 => "excellent",
        3 => "good",
        2 => "fair",
        1 => "poor",
        _ => "unavailable"
    };
}
