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
    IDurableRuntimeSettings settings, V2TrackReader tracks, ListeningLibraries libraries, V2RowIds rowIds, V2RowRefs? rowRefs = null)
{
    internal async Task<V2Result> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string resource)
    {
        var query = http.Request.Query;
        var allowed = resource switch
        {
            "history" or "history/rows" => new[] { "limit", "before" },
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
                    // A known song shows its own cover, as everywhere else; looking art up by name is for songs no longer found.
                    return new(200, new V2HistoryPage(page.Plays.Select(play => play.ItemId == null ? null : historyTracks.GetValueOrDefault(play.ItemId))
                        .Zip(page.Plays, (track, play) => new V2HistoryPlay(track, play.Title, play.Artist, play.Album, play.DurationMs,
                            track?.Artwork ?? (string.IsNullOrWhiteSpace(play.Artist) ? null : V2ListeningStats.Artwork(play.Artist, title: play.Title)),
                            play.PlayedAt, play.IsExternal, play.Provider)).ToArray(), page.Next));
                case "history/rows":
                    if (!int.TryParse(Value("limit") ?? "50", NumberStyles.None, CultureInfo.InvariantCulture, out var rowLimit)) return new(400, null);
                    return new(200, await HistoryRowsAsync(http, execution, await history.ReadAsync(execution, rowLimit, Value("before"))));
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
                    // Top songs are light rows, as history is: a recap reads them on every open.
                    var songRows = await RowsAsync(http, execution, listening.TopSongs.Select(song => song.ItemId));
                    return new(200, new V2ListeningStats(listening, listening.TopSongs.Select(song =>
                    {
                        var byName = V2ListeningStats.Artwork(song.Artist, title: song.Title);
                        IReadOnlyList<V2Ref> counted = string.IsNullOrWhiteSpace(song.Artist) ? [] : [new(null, song.Artist)];
                        if (song.ItemId == null || !songRows.TryGetValue(song.ItemId, out var row))
                            return new V2StatsSong(null, [], null, song.Title, song.Artist, counted,
                                string.IsNullOrWhiteSpace(song.Album) ? null : new(null, song.Album), song.PlayCount, byName);
                        // Names read as the plays were counted, as v1 shows them; the refs give the ids pages open by.
                        return new V2StatsSong(row.Id.Id, row.Id.Aliases, row.Id.LibraryItemId, song.Title, song.Artist,
                            row.Refs.Artists.Count > 0 ? row.Refs.Artists : counted,
                            string.IsNullOrWhiteSpace(song.Album) ? row.Refs.Album : new(row.Refs.Album?.Id, song.Album), song.PlayCount, row.Refs.Artwork ?? byName);
                    }).ToArray()));
                case "sound":
                    var profile = await sound.GetAsync(owner, token);
                    return profile == null ? new(404, null) : new(200, profile);
                case "taste":
                    var tasteProfile = await taste.GetCachedAsync(owner, token);
                    return new(200, new V2TasteProfile(ClientTasteLayer.From(tasteProfile.Lately), ClientTasteLayer.From(tasteProfile.Usually),
                        tasteProfile.LatelyWeight, tasteProfile.Paused, tasteProfile.UseImportedHistory, tasteProfile.Nudges));
                case "preferences":
                    return new(200, await PreferencesAsync(execution));
                default:
                    return new(404, null);
            }
        }
        catch (ArgumentException) { return new(400, null); }
        catch (UnauthorizedAccessException) { return new(404, null); }
    }

    /// <summary>Changes only the fields sent: the heart action, the chosen libraries, or both.</summary>
    public async Task<V2Result> UpdatePreferencesAsync(ProtocolExecutionContext execution, V2Preferences request)
    {
        if (request.HeartAction == null && request.Libraries == null ||
            request.HeartAction is not (null or "heart" or "keep" or "addToLibrary")) return new(400, null);
        try
        {
            if (request.HeartAction != null)
                await settings.UpdateHeartActionAsync(execution.Principal!.UserId, request.HeartAction, execution.CancellationToken);
            if (request.Libraries != null && await libraries.UpdateAsync(execution, request.Libraries.Selected, execution.CancellationToken) == null)
                return new(503, null);
            return new(200, await PreferencesAsync(execution));
        }
        catch (RuntimeSettingConflictException) { return new(409, null); }
        catch (ArgumentException) { return new(400, null); }
        catch (HttpRequestException) { return new(503, null); }
    }

    private async Task<V2Preferences> PreferencesAsync(ProtocolExecutionContext execution)
    {
        var token = execution.CancellationToken;
        var heart = (await settings.GetPreferencesAsync(execution.Principal!.UserId, token)).Values.HeartAction ?? "keep";
        ListeningLibraryChoice? choice;
        try { choice = await libraries.ReadAsync(execution, token); }
        catch (HttpRequestException) { choice = null; }
        return new(heart, choice == null ? null
            : new(choice.Selected, choice.Available.Select(library => new V2MusicLibrary(library.Id, library.Name)).ToArray(), choice.Revision));
    }

    // Plays as light rows: the ids a track read gives each song, beside what the play recorded, so a long history shows
    // at once and listener facts come from /tracks/facts.
    private async Task<V2HistoryRows> HistoryRowsAsync(HttpContext http, ProtocolExecutionContext execution, ClientHistoryPage page)
    {
        var rows = await RowsAsync(http, execution, page.Plays.Select(play => play.ItemId));
        return new(page.Plays.Select(play =>
        {
            var byName = string.IsNullOrWhiteSpace(play.Artist) ? null : V2ListeningStats.Artwork(play.Artist, title: play.Title);
            if (play.ItemId == null || !rows.TryGetValue(play.ItemId, out var row))
                return new V2HistoryRow(null, [], null, play.Title, string.IsNullOrWhiteSpace(play.Artist) ? [] : [new(null, play.Artist)],
                    string.IsNullOrWhiteSpace(play.Album) ? null : new(null, play.Album), play.DurationMs, byName, play.PlayedAt, play.IsExternal, play.Provider);
            var inLibrary = row.Id.LibraryItemId != null;
            // A service song's current details can lack an album or artist the play recorded; the recorded one stands in, without an id.
            IReadOnlyList<V2Ref> artists = row.Refs.Artists.Count > 0 || string.IsNullOrWhiteSpace(play.Artist) ? row.Refs.Artists : [new(null, play.Artist)];
            var album = row.Refs.Album ?? (string.IsNullOrWhiteSpace(play.Album) ? null : new V2AlbumRef(null, play.Album));
            return new V2HistoryRow(row.Id.Id, row.Id.Aliases, row.Id.LibraryItemId, play.Title, artists, album, play.DurationMs,
                inLibrary ? row.Refs.Artwork : row.Refs.Artwork ?? byName, play.PlayedAt, !inLibrary, inLibrary ? null : play.Provider);
        }).ToArray(), page.Next);
    }

    // Each song's track ids and refs as a track read gives them, without reading the tracks. Library songs take their
    // artists, album and picture from one media-server read; only the few service songs need a whole track read. Songs
    // repeat from read to read, so each song's refs are kept for a while. A song neither read returns is left out.
    private async Task<Dictionary<string, (V2RowId Id, V2RowRefs.Entry Refs)>> RowsAsync(HttpContext http, ProtocolExecutionContext execution,
        IEnumerable<string?> itemIds)
    {
        var ids = await rowIds.ResolveAsync(execution, itemIds.OfType<string>().Where(id => V2Ids.TryDecode(id, V2IdKind.Track, out _)));
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var refs = new Dictionary<string, V2RowRefs.Entry?>(StringComparer.Ordinal);
        foreach (var source in ids.Keys)
            if (rowRefs?.Get(owner, source) is { } known) refs[source] = known;
        var library = ids.Where(pair => pair.Value.LibraryItemId != null && !refs.ContainsKey(pair.Key)).ToArray();
        var read = await tracks.LibraryRefsAsync(http, execution, library.Select(pair => pair.Value.LibraryItemId!).Distinct(StringComparer.Ordinal).ToArray());
        foreach (var (source, id) in library)
            refs[source] = read.GetValueOrDefault(id.LibraryItemId!) is { } local ? new(local.Artists, local.Album, local.Artwork) : null;
        var services = ids.Where(pair => pair.Value.LibraryItemId == null && !refs.ContainsKey(pair.Key)).Select(pair => pair.Key).ToArray();
        var serviceTracks = await TrackMapAsync(http, execution, services);
        foreach (var source in services)
            refs[source] = serviceTracks.GetValueOrDefault(source) is { } track ? new(track.Artists, track.Album, track.Artwork) : null;
        var result = new Dictionary<string, (V2RowId Id, V2RowRefs.Entry Refs)>(StringComparer.Ordinal);
        foreach (var (source, entry) in refs)
        {
            if (entry == null) continue;
            rowRefs?.Set(owner, source, entry);
            result[source] = (ids[source], entry);
        }
        return result;
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
