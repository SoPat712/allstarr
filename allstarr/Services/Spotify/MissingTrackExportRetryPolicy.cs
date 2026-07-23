namespace allstarr.Services.Spotify;

public sealed class MissingTrackExportRetryPolicy
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(6);

    private readonly Dictionary<string, RetryState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public bool IsDue(string playlistName, DateTimeOffset now)
    {
        lock (_sync)
        {
            return !_states.TryGetValue(playlistName, out var state) ||
                   now >= state.NextAttemptAt;
        }
    }

    public TimeSpan RecordMiss(string playlistName, DateTimeOffset now)
    {
        lock (_sync)
        {
            var consecutiveMisses = _states.TryGetValue(playlistName, out var current)
                ? current.ConsecutiveMisses + 1
                : 1;
            var multiplier = 1L << Math.Min(consecutiveMisses - 1, 10);
            var delay = TimeSpan.FromTicks(Math.Min(
                MaximumDelay.Ticks,
                InitialDelay.Ticks * multiplier));

            _states[playlistName] = new RetryState(
                consecutiveMisses,
                now.Add(delay));
            return delay;
        }
    }

    public void RecordSuccess(string playlistName)
    {
        lock (_sync)
        {
            _states.Remove(playlistName);
        }
    }

    private sealed record RetryState(
        int ConsecutiveMisses,
        DateTimeOffset NextAttemptAt);
}
