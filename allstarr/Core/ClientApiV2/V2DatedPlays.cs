using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Plays an app heard while it could not report them, each sent once with its real time. They are recorded as a live
/// stop would record them, dated when they were heard, and never touch what is playing now.
/// </summary>
public sealed class V2DatedPlays(V2TrackReader tracks, IPlaybackSignalPipeline signals,
    IDbContextFactory<AllstarrDbContext> factory, TimeProvider clock)
{
    internal const int MaximumPlays = 50;
    private const double MaximumSeconds = 86400;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Oldest = TimeSpan.FromDays(60);

    public async Task<V2Result> RecordAsync(HttpContext http, ProtocolExecutionContext execution, V2DatedPlaysRequest? request)
    {
        if (!Valid(request, clock.GetUtcNow())) return new(400, null);
        var wanted = request!.Plays.Where(play => !TooShort(play) && V2Ids.TryDecode(play.TrackId, V2IdKind.Track, out _))
            .Select(play => play.TrackId).Distinct(StringComparer.Ordinal).ToArray();
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (wanted.Length > 0)
            {
                var (read, sources) = await tracks.ReadWithSourcesAsync(http, execution, wanted, false);
                foreach (var track in read.Tracks)
                    if (sources.TryGetValue(track.Id, out var source))
                        foreach (var alias in track.Aliases.Append(track.Id)) items.TryAdd(alias, source);
            }
            return new(200, new V2DatedPlaysResponse(await RecordResolvedAsync(execution, request.Plays, items)));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or KeyNotFoundException) { return new(404, null); }
        catch (ArgumentException) { return new(400, null); }
        catch (HttpRequestException) { return new(503, null); }
    }

    /// <summary>Records plays whose tracks are resolved to the library or service item a live report would name.</summary>
    internal async Task<IReadOnlyList<V2DatedPlayResult>> RecordResolvedAsync(ProtocolExecutionContext execution,
        IReadOnlyList<V2DatedPlay> plays, IReadOnlyDictionary<string, string> items)
    {
        var token = execution.CancellationToken;
        var owner = execution.RequireActor().EffectiveUserId ?? throw new UnauthorizedAccessException();
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<V2DatedPlayResult>(plays.Count);
        await using var db = await factory.CreateDbContextAsync(token);
        foreach (var play in plays)
        {
            string state;
            if (!sessions.Add(play.PlaySessionId)) state = "duplicate";
            else if (TooShort(play)) state = "tooShort";
            else if (!items.TryGetValue(play.TrackId, out var item)) state = "unknownTrack";
            else
            {
                var heard = (long)(play.PlayedSeconds * TimeSpan.TicksPerSecond);
                var signal = new PlaybackSignalRequest(execution, PlaybackTransition.Stop, item, null, play.PlaySessionId, heard,
                    play.StartedAt.ToUniversalTime() + TimeSpan.FromTicks(heard), Dated: true,
                    DurationMilliseconds: play.DurationSeconds is > 0 and <= MaximumSeconds ? (long)(play.DurationSeconds.Value * 1000) : null);
                // A play sent here before, or one whose end the app also reported live, is already settled. One that only
                // started live (the line dropped before its stop) is finished by this report.
                var key = PlaybackSignalPipeline.OccurrenceKey(signal);
                state = await db.ListeningEvents.AsNoTracking().AnyAsync(row => row.OwnerUserId == owner && row.OccurrenceKey == key &&
                        (row.State == ListeningEventState.Completed || row.State == ListeningEventState.Skipped), token) ||
                    !await signals.RecordAsync(signal, token) ? "duplicate" : "counted";
            }
            results.Add(new(play.PlaySessionId, state));
        }
        return results;
    }

    internal static bool Valid(V2DatedPlaysRequest? request, DateTimeOffset now) =>
        request?.Plays is { Count: > 0 and <= MaximumPlays } plays && plays.All(play => play != null &&
            !string.IsNullOrWhiteSpace(play.PlaySessionId) && play.PlaySessionId.Length <= 500 &&
            !string.IsNullOrWhiteSpace(play.TrackId) &&
            play.PlayedSeconds is >= 0 and <= MaximumSeconds &&
            play.StartedAt <= now + ClockSkew && play.StartedAt >= now - Oldest);

    // Under the time history counts, a play is not kept at all.
    private static bool TooShort(V2DatedPlay play) =>
        play.PlayedSeconds * TimeSpan.TicksPerSecond < ListeningStatsService.CountedPlayTicks;
}
