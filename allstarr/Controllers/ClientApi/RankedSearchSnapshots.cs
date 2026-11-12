using System.Text.Json.Nodes;
using System.Threading.Channels;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Controllers.ClientApi;

internal sealed record RankedSearchRow(JsonObject Item, string Section, Song? Song = null, Artist? Artist = null,
    Album? Album = null, ExternalPlaylist? Playlist = null, NativeMusicItem<object>? Native = null)
{
    internal double Score
    {
        get
        {
            if (!Item.TryGetPropertyValue("score", out var node) || node is null)
            {
                return 0.0;
            }
            if (node is JsonValue val)
            {
                if (val.TryGetValue<double>(out var d))
                {
                    return d;
                }
                var obj = val.GetValue<object>();
                if (obj is System.Text.Json.JsonElement elem && elem.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    return elem.GetDouble();
                }
                return Convert.ToDouble(obj);
            }
            return 0.0;
        }
    }
}

internal sealed class RankedSearchSnapshots(string query, int limit, IReadOnlyList<string> requested,
    string? preferred, TimeProvider clock, Func<object, CancellationToken, Task> write,
    Func<IReadOnlyList<RankedSearchRow>, CancellationToken, Task>? refresh = null)
{
    private sealed record Update(string? Provider, IReadOnlyList<RankedSearchRow> Rows,
        IReadOnlyList<string>? Late, TaskCompletionSource? Accepted = null, IReadOnlySet<string>? Linked = null,
        IReadOnlyList<(string Id, JsonObject Entry)>? AlsoOn = null, string? Spelled = null);
    private readonly Channel<Update> updates = Channel.CreateUnbounded<Update>(new() { SingleReader = true });
    private readonly Dictionary<string, List<RankedSearchRow>> sections = requested.ToDictionary(section => section, _ => new List<RankedSearchRow>());
    private readonly List<string> providers = [];
    private readonly List<RankedSearchRow> mixed = [];
    private RankedSearchRow? pinned;
    private RankedSearchRow? top;
    private int revision;
    // Service artists that matched songs tie to a library artist; they live in that artist's alsoOn instead.
    private IReadOnlySet<string> linked = new HashSet<string>();
    // Library artists by normalized name, so a same-named service artist can name the one it might be.
    private ILookup<string, RankedSearchRow> libraryArtists = Array.Empty<RankedSearchRow>().ToLookup(row => "");
    // The library's own spelling of the query ("Cyber Sex" for "cybersex"); every row scores against both.
    private string? spelled;

    /// <param name="alsoOn">Service artists proven to be an artist already listed; each joins that row's alsoOn.</param>
    internal async Task AddAsync(string provider, IReadOnlyList<RankedSearchRow> rows, CancellationToken token,
        IReadOnlyList<(string Id, JsonObject Entry)>? alsoOn = null)
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await updates.Writer.WriteAsync(new(provider, rows, null, accepted, AlsoOn: alsoOn), token);
        await accepted.Task.WaitAsync(token);
    }

    internal async Task AddLibraryAsync(IReadOnlyList<RankedSearchRow> rows, CancellationToken token,
        IReadOnlySet<string>? linkedArtists = null, string? spelling = null)
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await updates.Writer.WriteAsync(new(null, rows, null, accepted, linkedArtists, Spelled: spelling), token);
        await accepted.Task.WaitAsync(token);
    }

    internal void Complete(IReadOnlyList<string> late)
    {
        updates.Writer.TryWrite(new(null, [], late));
        updates.Writer.TryComplete();
    }

    internal async Task RunAsync(CancellationToken token)
    {
        var firstPaint = clock.GetUtcNow().AddMilliseconds(400);
        DateTimeOffset? last = null;
        IReadOnlyList<string>? late = null;
        var dirty = true;
        while (true)
        {
            while (updates.Reader.TryRead(out var update))
            {
                if (update.Provider is { } provider && !providers.Contains(provider, StringComparer.Ordinal)) providers.Add(provider);
                if (update.Linked is { Count: > 0 } artists) linked = artists;
                if (update.Spelled is { Length: > 0 } spelling) spelled = spelling;
                if (update.Provider == null && update.Rows.Count > 0)
                {
                    PruneExternalDuplicates(update.Rows);
                    libraryArtists = update.Rows.Where(row => row.Native?.Kind == MusicItemKind.Artist)
                        .ToLookup(row => FuzzyMatcher.NormalizeForMatching(row.Native!.Name));
                }
                foreach (var row in update.Rows)
                    if (!IsLinked(row) && sections.TryGetValue(row.Section, out var rows)) rows.Add(row);
                TagNamesakes();
                foreach (var (id, entry) in update.AlsoOn ?? [])
                    if (sections.GetValueOrDefault("artists")?.FirstOrDefault(row => row.Item["Id"]?.GetValue<string>() == id) is { } listed)
                    {
                        // The first joined part makes the row a combined entry: labelled Allstarr, listing its own part first.
                        if (listed.Item["alsoOn"] is not JsonArray also)
                        {
                            listed.Item["alsoOn"] = also = [new JsonObject
                            {
                                ["source"] = listed.Item["source"]?.DeepClone(),
                                ["sourceName"] = listed.Item["sourceName"]?.DeepClone(),
                                ["id"] = id
                            }];
                            listed.Item["source"] = SearchController.CombinedSource;
                            listed.Item["sourceName"] = SearchController.CombinedSourceName;
                        }
                        also.Add(entry);
                    }
                late = update.Late ?? late;
                dirty = true;
                update.Accepted?.TrySetResult();
            }
            var ready = providers.Count >= 2 && (preferred == null || providers.Contains(preferred, StringComparer.Ordinal));
            var due = last?.AddMilliseconds(250) ?? (ready ? clock.GetUtcNow() : firstPaint);
            if (dirty && clock.GetUtcNow() >= due)
            {
                await EmitAsync(late, token);
                last = clock.GetUtcNow();
                dirty = false;
                if (late != null) return;
            }
            if (!dirty)
            {
                await updates.Reader.WaitToReadAsync(token);
                continue;
            }
            if (late != null)
            {
                await Task.Delay(MaxDelay(due - clock.GetUtcNow()), clock, token);
                continue;
            }
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            var incoming = updates.Reader.WaitToReadAsync(wake.Token).AsTask();
            var timer = Task.Delay(MaxDelay(due - clock.GetUtcNow()), clock, wake.Token);
            await Task.WhenAny(incoming, timer);
            await wake.CancelAsync();
            token.ThrowIfCancellationRequested();
        }
    }

    // A service artist that shares a library artist's name without being tied to it lists that library artist, so an app can ask "Same artist?".
    private void TagNamesakes()
    {
        if (!sections.TryGetValue("artists", out var rows)) return;
        foreach (var row in rows.Where(row => row.Native == null && row.Artist != null && row.Item["namesakes"] == null))
            if (libraryArtists[FuzzyMatcher.NormalizeForMatching(row.Artist!.Name)].ToArray() is { Length: > 0 } namesakes)
                row.Item["namesakes"] = new JsonArray(namesakes.Select(library => (JsonNode)new JsonObject
                {
                    ["id"] = library.Native!.Id,
                    ["name"] = library.Native.Name
                }).ToArray());
    }

    private bool IsLinked(RankedSearchRow row) => row.Native == null && row.Artist != null && MusicSearchService.IsLinked(row.Artist, linked);

    private void PruneExternalDuplicates(IReadOnlyList<RankedSearchRow> libraryRows)
    {
        if (linked.Count > 0)
        {
            if (sections.TryGetValue("artists", out var artistRows)) artistRows.RemoveAll(IsLinked);
            mixed.RemoveAll(IsLinked);
            if (pinned != null && IsLinked(pinned)) pinned = null;
            if (top != null && IsLinked(top)) top = null;
        }
        // Same-named artists and albums stay separate rows; only a matching recording of a library song is a duplicate.
        var nativeSongs = libraryRows.Where(r => r.Native?.Kind == MusicItemKind.Song).Select(r => new Song
        {
            Title = r.Native!.Name,
            Artist = r.Native.ArtistText ?? "",
            Album = r.Native.AlbumText ?? "",
            PreciseDuration = r.Native.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            ExplicitContentLyrics = r.Native.ExplicitContentLyrics
        }).ToArray();
        if (nativeSongs.Length > 0 && sections.TryGetValue("songs", out var songRows))
        {
            songRows.RemoveAll(r => r.Native == null && r.Song != null &&
                nativeSongs.Any(known => MusicSearchService.MetadataRecordingsMatch(known, r.Song)));
            mixed.RemoveAll(r => r.Native == null && r.Song != null &&
                nativeSongs.Any(known => MusicSearchService.MetadataRecordingsMatch(known, r.Song)));
        }
    }


    private async Task EmitAsync(IReadOnlyList<string>? late, CancellationToken token)
    {
        var all = sections.Values.SelectMany(rows => rows).ToArray();
        if (refresh != null) await refresh(all, token);
        var knownDurationTitles = all.Where(row => (row.Song?.PreciseDuration?.TotalSeconds ?? row.Song?.Duration ?? row.Native?.DurationSeconds) is > 0)
            .Select(row => SearchRelevanceRanker.RecordingTitle(row.Song?.Title ?? row.Native!.Name, row.Song?.Artist ?? row.Native?.ArtistText, row.Song?.AlbumArtist ?? row.Native?.AlbumArtistText)).ToHashSet(StringComparer.Ordinal);
        double Best(Func<string, double> score) => spelled == null ? score(query) : Math.Max(score(query), score(spelled));
        foreach (var row in all)
        {
            if (row.Song != null)
                row.Item["score"] = Best(text => SearchRelevanceRanker.ScoreSong(text, row.Song!, knownDurationTitles.Contains(
                    SearchRelevanceRanker.RecordingTitle(row.Song!.Title, row.Song.Artist, row.Song.AlbumArtist))));
            else if (row.Native is { Kind: MusicItemKind.Song } native)
                row.Item["score"] = Best(text => SearchRelevanceRanker.ScoreSong(text, native.Name, native.ArtistText,
                    native.AlbumText, native.AlbumArtistText, native.DurationSeconds, provenRecording: true,
                    hasKnownDurationVersion: knownDurationTitles.Contains(
                        SearchRelevanceRanker.RecordingTitle(native.Name, native.ArtistText, native.AlbumArtistText))));
        }
        foreach (var rows in sections.Values) Rank(rows);
        var byEntry = new Dictionary<MusicSearchEntry<object>, RankedSearchRow>(ReferenceEqualityComparer.Instance);
        foreach (var row in all)
            byEntry.Add(new() { Song = row.Song, Album = row.Album, Artist = row.Artist, Playlist = row.Playlist, Native = row.Native }, row);
        MusicSearchEntry<object>[] Kind(string section) => byEntry.Where(pair => pair.Value.Section == section).Select(pair => pair.Key).ToArray();
        var merged = new MusicSearchResult<object>(Kind("songs"), Kind("albums").Concat(Kind("playlists")).ToArray(), Kind("artists"));
        var rankedMixed = MusicSearchService.RankTogether(query, merged).Select(entry => byEntry[entry]);
        mixed.AddRange(rankedMixed.Where(row => !mixed.Contains(row)));
        Rank(mixed);
        string? ArtistName(RankedSearchRow row) => row.Artist?.Name ?? (row.Native?.Kind == MusicItemKind.Artist ? row.Native.Name : null);
        bool Exact(RankedSearchRow row) => ArtistName(row) is { } name &&
            (FuzzyMatcher.NormalizeForMatching(name) == FuzzyMatcher.NormalizeForMatching(query) ||
             spelled != null && FuzzyMatcher.NormalizeForMatching(name) == FuzzyMatcher.NormalizeForMatching(spelled));
        var exact = all.Where(Exact).OrderByDescending(row => row.Score).ThenBy(row => row.Native == null).FirstOrDefault();
        if (exact != null && (pinned == null || exact.Native != null && pinned.Native == null))
            pinned = exact;
        pinned ??= exact;
        var nearArtists = all.Where(row => ArtistName(row) != null && row.Score >= 95).ToArray();
        var candidates = nearArtists.Length > 0 ? nearArtists : all;
        var best = candidates.OrderByDescending(row => row.Score).ThenBy(row => row.Native == null).FirstOrDefault();
        top = pinned ?? (top != null && candidates.Contains(top) && (best == null || !ShouldMoveUp(best, top)) ? top : best);
        var visible = sections.ToDictionary(pair => pair.Key, pair => pair.Value.Take(limit).Select(row => row.Item).ToArray());
        if (top != null && !visible[top.Section].Contains(top.Item))
            visible[top.Section] = visible[top.Section].Take(limit - 1).Append(top.Item).ToArray();
        var value = new Dictionary<string, object?>
        {
            ["revision"] = ++revision,
            ["done"] = late != null,
            ["providers"] = providers.ToArray(),
            ["top"] = top?.Item,
            ["items"] = mixed.Take(limit).Select(row => row.Item).ToArray(),
            ["sections"] = visible
        };
        if (late != null) value["late"] = late;
        await write(value, token);
    }

    private void Rank(List<RankedSearchRow> rows)
    {
        if (revision == 0)
        {
            var ranked = rows.OrderByDescending(row => row.Score).ThenBy(row => row.Native == null).ToArray();
            rows.Clear();
            rows.AddRange(ranked);
            return;
        }
        // Five points is one band in the shared scorer; smaller evidence changes keep their place.
        for (var index = 1; index < rows.Count; index++)
            for (var position = index; position > 0 && ShouldMoveUp(rows[position], rows[position - 1]); position--)
                (rows[position - 1], rows[position]) = (rows[position], rows[position - 1]);
    }

    private static bool ShouldMoveUp(RankedSearchRow current, RankedSearchRow previous)
    {
        if (current.Native != null && previous.Native == null)
            return current.Score >= previous.Score;
        return current.Score >= previous.Score + 5;
    }

    private static TimeSpan MaxDelay(TimeSpan delay) => delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
}
