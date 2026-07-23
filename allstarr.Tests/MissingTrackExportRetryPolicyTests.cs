using allstarr.Services.Spotify;

namespace allstarr.Tests;

public sealed class MissingTrackExportRetryPolicyTests
{
    [Fact]
    public void RecordMiss_ExponentiallyBacksOffAndCapsAtSixHours()
    {
        var policy = new MissingTrackExportRetryPolicy();
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var expectedDelays = new[]
        {
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(4),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(6)
        };

        foreach (var expectedDelay in expectedDelays)
        {
            var actualDelay = policy.RecordMiss("Release Radar", now);

            Assert.Equal(expectedDelay, actualDelay);
            Assert.False(policy.IsDue("Release Radar", now.Add(expectedDelay).AddTicks(-1)));
            Assert.True(policy.IsDue("Release Radar", now.Add(expectedDelay)));
            now = now.Add(expectedDelay);
        }
    }

    [Fact]
    public void RecordSuccess_ClearsTheBackoff()
    {
        var policy = new MissingTrackExportRetryPolicy();
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

        policy.RecordMiss("Release Radar", now);
        policy.RecordSuccess("Release Radar");

        Assert.True(policy.IsDue("Release Radar", now));
        Assert.Equal(
            MissingTrackExportRetryPolicy.InitialDelay,
            policy.RecordMiss("Release Radar", now));
    }

    [Fact]
    public void Playlists_HaveIndependentBackoffWindows()
    {
        var policy = new MissingTrackExportRetryPolicy();
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

        policy.RecordMiss("Release Radar", now);

        Assert.False(policy.IsDue("Release Radar", now));
        Assert.True(policy.IsDue("Discover Weekly", now));
    }
}
