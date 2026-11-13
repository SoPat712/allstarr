using System.Globalization;
using allstarr.Core.ClientApi;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;

namespace allstarr.Core.ClientApiV2;

public sealed class V2MeEndpoints(ClientListeningHistory history, ListeningHistoryImportService imports,
    ListeningHistoryResolution resolution, ListeningStatsService stats, TasteService taste, SoundProfileService sound,
    IDurableRuntimeSettings settings, V2TrackReader tracks)
{
    internal async Task<V2Result> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string resource)
    {
        var query = http.Request.Query;
        var allowed = resource switch
        {
            "history" => new[] { "limit", "before" },
            "stats" => ["period", "year", "timeZoneId"],
            _ => []
        };
        if (query.Any(item => allowed.Contains(item.Key) && item.Value.Count != 1)) return new(400, null);
        string? Value(string key) => query.TryGetValue(key, out var value) ? value.ToString() : null;
        var token = execution.CancellationToken;
        var owner = execution.Principal!.UserId;
        try
        {
            switch (resource)
            {
                case "history":
                    if (!int.TryParse(Value("limit") ?? "50", NumberStyles.None, CultureInfo.InvariantCulture, out var limit)) return new(400, null);
                    var page = await history.ReadAsync(execution, limit, Value("before"));
                    var historyTracks = await TrackMapAsync(http, execution, page.Plays.Select(play => play.ItemId));
                    return new(200, new V2HistoryPage(page.Plays.Select(play => new V2HistoryPlay(
                        play.ItemId == null ? null : historyTracks.GetValueOrDefault(play.ItemId), play.Title, play.Artist, play.Album,
                        play.DurationMs, string.IsNullOrWhiteSpace(play.Artist) ? null : V2ListeningStats.Artwork(play.Artist, title: play.Title),
                        play.PlayedAt, play.IsExternal, play.Provider)).ToArray(), page.Next));
                case "history/imports":
                    return new(200, await imports.ListAsync(new(owner, execution.Protocol.ToString().ToLowerInvariant(), execution.BackendInstanceId), 50, token));
                case "history/imports/recheck":
                    var recheck = await resolution.RecheckAsync(execution, token);
                    var matched = await TrackMapAsync(http, execution, recheck.Examples.Select(song => V2Ids.Track(song.RecordingId)));
                    return new(200, new V2HistoryRecheck(recheck.Songs, recheck.Unchecked, recheck.Doubtful, recheck.DoubtfulPlays,
                        recheck.Examples.Select(song => new V2HistoryRecheckSong(matched.GetValueOrDefault(V2Ids.Track(song.RecordingId)),
                            V2Ids.Track(song.RecordingId), song.Title, song.Artist, song.Album, song.MatchedTitle, song.MatchedArtist,
                            song.MatchedAlbum, song.Verdict, song.Plays)).ToArray()));
                case "stats":
                    int? year = null;
                    if (Value("year") is { } value)
                    {
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return new(400, null);
                        year = parsed;
                    }
                    var listening = await stats.GetAsync(execution, Value("period"), year, token, Value("timeZoneId"));
                    var songTracks = await TrackMapAsync(http, execution, listening.TopSongs.Select(song => song.ItemId));
                    return new(200, new V2ListeningStats(listening, listening.TopSongs.Select(song => new V2StatsSong(
                        song.ItemId == null ? null : songTracks.GetValueOrDefault(song.ItemId), song.Title, song.Artist, song.Album,
                        song.PlayCount, V2ListeningStats.Artwork(song.Artist, title: song.Title))).ToArray()));
                case "sound":
                    var profile = await sound.GetAsync(owner, token);
                    return profile == null ? new(404, null) : new(200, profile);
                case "taste":
                    var tasteProfile = await taste.GetCachedAsync(owner, token);
                    return new(200, new V2TasteProfile(ClientTasteLayer.From(tasteProfile.Lately), ClientTasteLayer.From(tasteProfile.Usually),
                        tasteProfile.LatelyWeight, tasteProfile.Paused, tasteProfile.UseImportedHistory, tasteProfile.Nudges));
                case "preferences":
                    return new(200, new V2Preferences((await settings.GetPreferencesAsync(owner, token)).Values.HeartAction ?? "keep"));
                default:
                    return new(404, null);
            }
        }
        catch (ArgumentException) { return new(400, null); }
        catch (UnauthorizedAccessException) { return new(404, null); }
    }

    public async Task<V2Result> UpdatePreferencesAsync(ProtocolExecutionContext execution, V2Preferences request)
    {
        if (request.HeartAction is not ("heart" or "keep" or "addToLibrary")) return new(400, null);
        try
        {
            await settings.UpdateHeartActionAsync(execution.Principal!.UserId, request.HeartAction, execution.CancellationToken);
            return new(200, request);
        }
        catch (RuntimeSettingConflictException) { return new(409, null); }
    }

    private async Task<Dictionary<string, V2Track>> TrackMapAsync(HttpContext http, ProtocolExecutionContext execution, IEnumerable<string?> ids)
    {
        var valid = ids.OfType<string>().Where(id => V2Ids.TryDecode(id, V2IdKind.Track, out _)).Distinct(StringComparer.Ordinal).ToArray();
        if (valid.Length == 0) return new(StringComparer.Ordinal);
        var read = await tracks.ReadAsync(http, execution, valid, false);
        return read.Tracks.SelectMany(track => track.Aliases.Append(track.Id).Select(id => (Id: id, Track: track)))
            .DistinctBy(item => item.Id, StringComparer.Ordinal).ToDictionary(item => item.Id, item => item.Track, StringComparer.Ordinal);
    }
}
