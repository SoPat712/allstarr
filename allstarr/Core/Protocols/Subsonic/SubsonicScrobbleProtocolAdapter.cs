using allstarr.Core.Playback;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Protocols.Subsonic;

public sealed record SubsonicScrobbleSignal(
    string ItemId,
    PlaybackTransition Transition,
    DateTimeOffset ObservedAt,
    string EventKey,
    int Index);

public sealed class SubsonicScrobbleProtocolAdapter
{
    public IReadOnlyList<SubsonicScrobbleSignal> Parse(
        SubsonicRequestParameters parameters,
        DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var ids = parameters.GetAllValues("id");
        var times = parameters.GetAllValues("time");
        var submissions = parameters.GetAllValues("submission");
        var signals = new List<SubsonicScrobbleSignal>(ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            var itemId = ids[index];
            if (string.IsNullOrWhiteSpace(itemId)) continue;

            var suppliedTime = ParseTime(times, index);
            var observedAt = suppliedTime ?? receivedAt;
            var eventKey = suppliedTime?.ToUnixTimeMilliseconds().ToString()
                ?? (receivedAt.ToUnixTimeSeconds() / 30).ToString();
            signals.Add(new SubsonicScrobbleSignal(
                itemId,
                ParseSubmission(submissions, index)
                    ? PlaybackTransition.Submission
                    : PlaybackTransition.Start,
                observedAt,
                eventKey,
                index));
        }

        return signals;
    }

    private static DateTimeOffset? ParseTime(IReadOnlyList<string> values, int index)
    {
        if (index >= values.Count || !long.TryParse(values[index], out var milliseconds)) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool ParseSubmission(IReadOnlyList<string> values, int index)
    {
        if (values.Count == 0) return true;
        var value = values.Count == 1 ? values[0] : index < values.Count ? values[index] : values[^1];
        return !bool.TryParse(value, out var submission) || submission;
    }
}
