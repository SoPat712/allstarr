using System.Text.Json;
using allstarr.Models.Spotify;

namespace allstarr.Services.Common;

/// <summary>
/// Computes displayed counts for injected Spotify playlists.
/// </summary>
public static class SpotifyPlaylistCountHelper
{
    public static int CountExternalMatchedTracks(IEnumerable<MatchedTrack>? matchedTracks)
    {
        if (matchedTracks == null)
        {
            return 0;
        }

        return matchedTracks.Count(t => t.MatchedSong != null && !t.MatchedSong.IsLocal);
    }

    public static int ComputeServedItemCount(
        int? exactCachedPlaylistItemsCount,
        int localTracksCount,
        IEnumerable<MatchedTrack>? matchedTracks)
    {
        if (exactCachedPlaylistItemsCount.HasValue && exactCachedPlaylistItemsCount.Value > 0)
        {
            return exactCachedPlaylistItemsCount.Value;
        }

        return Math.Max(0, localTracksCount) + CountExternalMatchedTracks(matchedTracks);
    }

    public static long SumExternalMatchedRunTimeTicks(IEnumerable<MatchedTrack>? matchedTracks)
    {
        if (matchedTracks == null)
        {
            return 0;
        }

        return matchedTracks
            .Where(t => t.MatchedSong != null && !t.MatchedSong.IsLocal)
            .Sum(t => Math.Max(0, (long)(t.MatchedSong.Duration ?? 0) * TimeSpan.TicksPerSecond));
    }

    public static long SumCachedPlaylistRunTimeTicks(IEnumerable<Dictionary<string, object?>>? cachedPlaylistItems)
    {
        if (cachedPlaylistItems == null)
        {
            return 0;
        }

        long total = 0;
        foreach (var item in cachedPlaylistItems)
        {
            item.TryGetValue("RunTimeTicks", out var runTimeTicks);
            total += ExtractRunTimeTicks(runTimeTicks);
        }

        return total;
    }

    public static long ComputeServedRunTimeTicks(
        long? exactCachedPlaylistRunTimeTicks,
        long localPlaylistRunTimeTicks,
        IEnumerable<MatchedTrack>? matchedTracks)
    {
        if (exactCachedPlaylistRunTimeTicks.HasValue)
        {
            return Math.Max(0, exactCachedPlaylistRunTimeTicks.Value);
        }

        return Math.Max(0, localPlaylistRunTimeTicks) + SumExternalMatchedRunTimeTicks(matchedTracks);
    }

    public static long ExtractRunTimeTicks(object? rawValue)
    {
        return rawValue switch
        {
            null => 0,
            long longValue => Math.Max(0, longValue),
            int intValue => Math.Max(0, intValue),
            double doubleValue => Math.Max(0, (long)doubleValue),
            decimal decimalValue => Math.Max(0, (long)decimalValue),
            string stringValue when long.TryParse(stringValue, out var parsed) => Math.Max(0, parsed),
            JsonElement jsonElement => ExtractJsonRunTimeTicks(jsonElement),
            _ => 0
        };
    }

    private static long ExtractJsonRunTimeTicks(JsonElement jsonElement)
    {
        return jsonElement.ValueKind switch
        {
            JsonValueKind.Number when jsonElement.TryGetInt64(out var longValue) => Math.Max(0, longValue),
            JsonValueKind.String when long.TryParse(jsonElement.GetString(), out var parsed) => Math.Max(0, parsed),
            _ => 0
        };
    }
}
