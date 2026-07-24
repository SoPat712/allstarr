namespace allstarr.Services.Spotify;

public sealed class MissingTrackExportRetryPolicy
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(6);
    private static readonly TimeSpan StateRetention = TimeSpan.FromHours(24);
    private const int MaximumTrackedPlaylists = 256;

    private readonly Dictionary<string, RetryState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public bool IsDue(string playlistName, DateTimeOffset now)
    {
        lock (_sync)
        {
            Prune(now);
            return !_states.TryGetValue(playlistName, out var state) ||
                   now >= state.NextAttemptAt;
        }
    }

    public TimeSpan RecordMiss(string playlistName, DateTimeOffset now)
    {
        lock (_sync)
        {
            Prune(now);
            var consecutiveMisses = _states.TryGetValue(playlistName, out var current)
                ? current.ConsecutiveMisses + 1
                : 1;
            var multiplier = 1L << Math.Min(consecutiveMisses - 1, 10);
            var delay = TimeSpan.FromTicks(Math.Min(
                MaximumDelay.Ticks,
                InitialDelay.Ticks * multiplier));

            _states[playlistName] = new RetryState(
                consecutiveMisses,
                now.Add(delay),
                now);
            TrimOverflow();
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

    private void Prune(DateTimeOffset now)
    {
        foreach (var playlistName in _states
                     .Where(item => now - item.Value.UpdatedAt >= StateRetention)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _states.Remove(playlistName);
        }
    }

    private void TrimOverflow()
    {
        var overflow = _states.Count - MaximumTrackedPlaylists;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var playlistName in _states
                     .OrderBy(item => item.Value.UpdatedAt)
                     .Take(overflow)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _states.Remove(playlistName);
        }
    }

    private sealed record RetryState(
        int ConsecutiveMisses,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset UpdatedAt);
}
