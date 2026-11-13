using System.Text.Json.Nodes;
using System.Threading.Channels;
using allstarr.Core.Music;
using allstarr.Core.ClientApiV2;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Controllers.ClientApi;

internal sealed record RankedSearchRow(JsonObject Item, string Section, Song? Song = null, Artist? Artist = null,
    Album? Album = null, ExternalPlaylist? Playlist = null, NativeMusicItem<object>? Native = null)
{
    internal Dictionary<string, double> Scores { get; } = new(StringComparer.Ordinal);
    private string? recordingTitle;
    internal string RecordingTitle => recordingTitle ??= SearchRelevanceRanker.RecordingTitle(Song?.Title ?? Native!.Name,
        Song?.Artist ?? Native?.ArtistText, Song?.AlbumArtist ?? Native?.AlbumArtistText);

    internal double ScoreFor(string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Scores.TryGetValue(query, out var cached)) return cached;
        var value = this switch
        {
            { Song: { } song } => SearchRelevanceRanker.ScoreSong(query, song, cancellationToken: token),
            { Native: { Kind: MusicItemKind.Song } native } => SearchRelevanceRanker.ScoreSong(query, native.Name, native.ArtistText,
                native.AlbumText, native.AlbumArtistText, native.DurationSeconds, provenRecording: true, cancellationToken: token),
            { Native: { } native } => SearchRelevanceRanker.Score(query, native.Name, native.ArtistText, cancellationToken: token),
            { Album: { } album } => SearchRelevanceRanker.ScoreAlbum(query, album, token),
            { Artist: { } artist } => SearchRelevanceRanker.Score(query, artist.Name, cancellationToken: token),
            { Playlist: { } playlist } => SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName, cancellationToken: token),
            _ => 0
        };
        Scores[query] = value;
        return value;
    }

    internal static double WithKnownDuration(double score)
    {
        var evidence = score % 5 == 1 ? 1 : 0;
        var adjusted = Math.Max(0, score - evidence - 25);
        return adjusted > 0 ? adjusted + evidence : 0;
    }

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

internal sealed record RankedSearchBatch(int Revision, IReadOnlyList<string> Providers, RankedSearchRow? Top,
    IReadOnlyList<RankedSearchRow> Items, IReadOnlyDictionary<string, RankedSearchRow[]> Sections, IReadOnlyList<string>? Late);

internal sealed partial class RankedSearchSnapshots(string query, int limit, IReadOnlyList<string> requested,
    string? preferred, TimeProvider clock, Func<object, CancellationToken, Task> write,
    Func<IReadOnlyList<RankedSearchRow>, CancellationToken, Task>? refresh = null,
    Func<RankedSearchBatch, CancellationToken, Task>? publish = null)
{
    private sealed record Update(string? Provider, IReadOnlyList<RankedSearchRow> Rows,
        IReadOnlyList<string>? Late, TaskCompletionSource? Accepted = null, IReadOnlySet<string>? Linked = null,
        IReadOnlyList<(string Id, JsonObject Entry)>? AlsoOn = null, string? Spelled = null);
    private readonly Channel<Update> updates = Channel.CreateUnbounded<Update>(new() { SingleReader = true });
    private readonly Dictionary<string, List<RankedSearchRow>> sections = requested.ToDictionary(section => section, _ => new List<RankedSearchRow>());
    private readonly List<string> providers = [];
    private readonly List<RankedSearchRow> mixed = [];
    private readonly List<RankedSearchRow> fresh = [];
    private readonly Dictionary<string, ScoreNode?> sectionOrder = new(StringComparer.Ordinal);
    private ScoreNode? mixedOrder;
    private bool resetOrder;
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
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                while (updates.Reader.TryRead(out var update))
                {
                    token.ThrowIfCancellationRequested();
                    if (update.Provider is { } provider && !providers.Contains(provider, StringComparer.Ordinal)) providers.Add(provider);
                    if (update.Linked is { Count: > 0 } artists) linked = artists;
                    if (update.Spelled is { Length: > 0 } spelling) spelled = spelling;
                    if (update.Provider == null && update.Rows.Count > 0)
                    {
                        PruneExternalDuplicates(update.Rows, token);
                        libraryArtists = update.Rows.Where(row => row.Native?.Kind == MusicItemKind.Artist)
                            .ToLookup(row => FuzzyMatcher.NormalizeForMatching(row.Native!.Name));
                    }
                    foreach (var row in update.Rows)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!IsLinked(row) && sections.TryGetValue(row.Section, out var rows)) { rows.Add(row); fresh.Add(row); }
                    }
                    TagNamesakes(token);
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
                                listed.Item["source"] = V2SearchStream.CombinedSource;
                                listed.Item["sourceName"] = V2SearchStream.CombinedSourceName;
                            }
                            also.Add(entry);
                        }
                    late = update.Late ?? late;
                    dirty = true;
                    update.Accepted?.TrySetResult();
                }
                var ready = providers.Count >= 2 && (preferred == null || providers.Contains(preferred, StringComparer.Ordinal));
                var due = last?.AddMilliseconds(250) ?? (ready ? clock.GetUtcNow() : firstPaint);
                if (dirty && (late != null || clock.GetUtcNow() >= due))
                {
                    await EmitAsync(late, token);
                    last = clock.GetUtcNow();
                    dirty = false;
                    if (late != null) return;
                }
                if (!dirty)
                {
                    if (!await updates.Reader.WaitToReadAsync(token)) return;
                    continue;
                }
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
                var incoming = updates.Reader.WaitToReadAsync(wake.Token).AsTask();
                var timer = Task.Delay(MaxDelay(due - clock.GetUtcNow()), clock, wake.Token);
                await Task.WhenAny(incoming, timer);
                if (incoming.IsCompletedSuccessfully && !incoming.Result) await timer;
                await wake.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            updates.Writer.TryComplete();
            while (updates.Reader.TryRead(out var waiting)) waiting.Accepted?.TrySetCanceled();
        }
    }

    // A service artist that shares a library artist's name without being tied to it lists that library artist, so an app can ask "Same artist?".
    private void TagNamesakes(CancellationToken token)
    {
        if (!sections.TryGetValue("artists", out var rows)) return;
        foreach (var row in rows.Where(row => row.Native == null && row.Artist != null && row.Item["namesakes"] == null))
        {
            token.ThrowIfCancellationRequested();
            if (libraryArtists[FuzzyMatcher.NormalizeForMatching(row.Artist!.Name)].ToArray() is { Length: > 0 } namesakes)
                row.Item["namesakes"] = new JsonArray(namesakes.Select(library => (JsonNode)new JsonObject
                {
                    ["id"] = library.Native!.Id,
                    ["name"] = library.Native.Name
                }).ToArray());
        }
    }

    private bool IsLinked(RankedSearchRow row) => row.Native == null && row.Artist != null && MusicSearchService.IsLinked(row.Artist, linked);

    private void PruneExternalDuplicates(IReadOnlyList<RankedSearchRow> libraryRows, CancellationToken token)
    {
        resetOrder = true;
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
            var duplicates = songRows.Where(row =>
            {
                token.ThrowIfCancellationRequested();
                return row.Native == null && row.Song != null && nativeSongs.Any(known =>
                {
                    token.ThrowIfCancellationRequested();
                    return MusicSearchService.MetadataRecordingsMatch(known, row.Song);
                });
            }).ToHashSet(ReferenceEqualityComparer.Instance);
            songRows.RemoveAll(duplicates.Contains);
            mixed.RemoveAll(duplicates.Contains);
        }
    }


    private async Task EmitAsync(IReadOnlyList<string>? late, CancellationToken token)
    {
        var all = sections.Values.SelectMany(rows => rows).ToArray();
        if (refresh != null) await refresh(all, token);
        token.ThrowIfCancellationRequested();
        var knownDurationTitles = all.Where(row => (row.Song?.PreciseDuration?.TotalSeconds ?? row.Song?.Duration ?? row.Native?.DurationSeconds) is > 0)
            .Select(row => row.RecordingTitle).ToHashSet(StringComparer.Ordinal);
        var added = fresh.ToHashSet(ReferenceEqualityComparer.Instance);
        double DurationScore(RankedSearchRow row, double score)
        {
            if ((row.Song?.PreciseDuration?.TotalSeconds ?? row.Song?.Duration ?? row.Native?.DurationSeconds) is > 0 ||
                !knownDurationTitles.Contains(row.RecordingTitle)) return score;
            return RankedSearchRow.WithKnownDuration(score);
        }
        foreach (var row in all)
        {
            token.ThrowIfCancellationRequested();
            if (row.Song == null && row.Native?.Kind != MusicItemKind.Song) continue;
            var score = row.ScoreFor(query, token);
            if (spelled != null) score = Math.Max(score, row.ScoreFor(spelled, token));
            score = DurationScore(row, score);
            if (row.Score != score && !added.Contains(row)) resetOrder = true;
            row.Item["score"] = score;
        }
        foreach (var (section, rows) in sections)
        {
            sectionOrder.TryGetValue(section, out var order);
            if (revision == 0 || resetOrder) order = null;
            foreach (var row in revision == 0 || resetOrder ? rows : fresh.Where(row => row.Section == section))
            {
                token.ThrowIfCancellationRequested();
                order = InsertScore(order, row, revision == 0);
            }
            sectionOrder[section] = order;
            rows.Clear();
            rows.AddRange(Ordered(order));
        }
        var included = mixed.ToHashSet(ReferenceEqualityComparer.Instance);
        var newcomers = all.Where(row => !included.Contains(row))
            .OrderByDescending(row => row.Song != null || row.Native?.Kind == MusicItemKind.Song
                ? DurationScore(row, row.ScoreFor(query, token)) : row.ScoreFor(query, token))
            .ThenBy(row => row.Native == null)
            .ThenBy(row => row.Section switch { "artists" => 0, "albums" => 1, "playlists" => 2, _ => 3 }).ToArray();
        if (revision == 0 || resetOrder) mixedOrder = null;
        foreach (var row in revision == 0 || resetOrder ? mixed.Concat(newcomers) : newcomers)
        {
            token.ThrowIfCancellationRequested();
            mixedOrder = InsertScore(mixedOrder, row, revision == 0);
        }
        mixed.Clear();
        mixed.AddRange(Ordered(mixedOrder));
        fresh.Clear();
        resetOrder = false;
        string? ArtistName(RankedSearchRow row) => row.Artist?.Name ?? (row.Native?.Kind == MusicItemKind.Artist ? row.Native.Name : null);
        bool Exact(RankedSearchRow row)
        {
            token.ThrowIfCancellationRequested();
            return ArtistName(row) is { } name &&
                (FuzzyMatcher.NormalizeForMatching(name) == FuzzyMatcher.NormalizeForMatching(query) ||
                 spelled != null && FuzzyMatcher.NormalizeForMatching(name) == FuzzyMatcher.NormalizeForMatching(spelled));
        }
        var exact = all.Where(Exact).OrderByDescending(row => row.Score).ThenBy(row => row.Native == null).FirstOrDefault();
        if (exact != null && (pinned == null || exact.Native != null && pinned.Native == null))
            pinned = exact;
        pinned ??= exact;
        var nearArtists = all.Where(row => ArtistName(row) != null && row.Score >= 95).ToArray();
        var candidates = nearArtists.Length > 0 ? nearArtists : all;
        var best = candidates.OrderByDescending(row => row.Score).ThenBy(row => row.Native == null).FirstOrDefault();
        top = pinned ?? (top != null && candidates.Contains(top) && (best == null || !ShouldMoveUp(best, top)) ? top : best);
        var visible = sections.ToDictionary(pair => pair.Key, pair => pair.Value.Take(limit).ToArray());
        if (top != null && !visible[top.Section].Contains(top))
            visible[top.Section] = visible[top.Section].Take(limit - 1).Append(top).ToArray();
        ++revision;
        if (publish != null)
        {
            await publish(new(revision, providers.ToArray(), top, mixed.Take(limit).ToArray(), visible, late), token);
            return;
        }
        var value = new Dictionary<string, object?>
        {
            ["revision"] = revision,
            ["done"] = late != null,
            ["providers"] = providers.ToArray(),
            ["top"] = top?.Item,
            ["items"] = mixed.Take(limit).Select(row => row.Item).ToArray(),
            ["sections"] = visible.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => row.Item).ToArray())
        };
        if (late != null) value["late"] = late;
        token.ThrowIfCancellationRequested();
        await write(value, token);
    }

    private static bool ShouldMoveUp(RankedSearchRow current, RankedSearchRow previous)
    {
        if (current.Native != null && previous.Native == null)
            return current.Score >= previous.Score;
        return current.Score >= previous.Score + 5;
    }

    private static TimeSpan MaxDelay(TimeSpan delay) => delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
}
