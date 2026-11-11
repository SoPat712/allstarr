using System.Text.Json.Nodes;
using System.Threading.Channels;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Controllers.ClientApi;

internal sealed record RankedSearchRow(JsonObject Item, string Section, Song? Song = null, Artist? Artist = null,
    Album? Album = null, ExternalPlaylist? Playlist = null)
{
    internal double Score => Item["score"]!.GetValue<double>();
}

internal sealed class RankedSearchSnapshots(string query, int limit, IReadOnlyList<string> requested,
    string? preferred, TimeProvider clock, Func<object, CancellationToken, Task> write,
    Func<IReadOnlyList<RankedSearchRow>, CancellationToken, Task>? refresh = null)
{
    private sealed record Update(string? Provider, IReadOnlyList<RankedSearchRow> Rows,
        IReadOnlyList<string>? Late, TaskCompletionSource? Accepted = null);
    private readonly Channel<Update> updates = Channel.CreateUnbounded<Update>(new() { SingleReader = true });
    private readonly Dictionary<string, List<RankedSearchRow>> sections = requested.ToDictionary(section => section, _ => new List<RankedSearchRow>());
    private readonly List<string> providers = [];
    private readonly List<RankedSearchRow> mixed = [];
    private RankedSearchRow? pinned;
    private RankedSearchRow? top;
    private int revision;

    internal async Task AddAsync(string provider, IReadOnlyList<RankedSearchRow> rows, CancellationToken token)
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await updates.Writer.WriteAsync(new(provider, rows, null, accepted), token);
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
                foreach (var row in update.Rows)
                    if (sections.TryGetValue(row.Section, out var rows)) rows.Add(row);
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

    private async Task EmitAsync(IReadOnlyList<string>? late, CancellationToken token)
    {
        var all = sections.Values.SelectMany(rows => rows).ToArray();
        if (refresh != null) await refresh(all, token);
        var knownDurationTitles = all.Where(row => (row.Song?.PreciseDuration?.TotalSeconds ?? row.Song?.Duration) is > 0)
            .Select(row => SearchRelevanceRanker.RecordingTitle(row.Song!.Title, row.Song.Artist, row.Song.AlbumArtist)).ToHashSet(StringComparer.Ordinal);
        foreach (var row in all.Where(row => row.Song != null))
            row.Item["score"] = SearchRelevanceRanker.ScoreSong(query, row.Song!, knownDurationTitles.Contains(
                SearchRelevanceRanker.RecordingTitle(row.Song!.Title, row.Song.Artist, row.Song.AlbumArtist)));
        foreach (var rows in sections.Values) Rank(rows);
        var byEntry = new Dictionary<MusicSearchEntry<object>, RankedSearchRow>(ReferenceEqualityComparer.Instance);
        foreach (var row in all)
            byEntry.Add(new() { Song = row.Song, Album = row.Album, Artist = row.Artist, Playlist = row.Playlist }, row);
        MusicSearchEntry<object>[] Kind(string section) => byEntry.Where(pair => pair.Value.Section == section).Select(pair => pair.Key).ToArray();
        var merged = new MusicSearchResult<object>(Kind("songs"), Kind("albums").Concat(Kind("playlists")).ToArray(), Kind("artists"));
        var rankedMixed = MusicSearchService.RankTogether(query, merged).Select(entry => byEntry[entry]);
        mixed.AddRange(rankedMixed.Where(row => !mixed.Contains(row)));
        Rank(mixed);
        bool Exact(RankedSearchRow row) => row.Artist != null &&
            FuzzyMatcher.NormalizeForMatching(row.Artist.Name) == FuzzyMatcher.NormalizeForMatching(query);
        pinned ??= all.Where(Exact).OrderByDescending(row => row.Score).FirstOrDefault();
        var nearArtists = all.Where(row => row.Artist != null && row.Score >= 95).ToArray();
        var candidates = nearArtists.Length > 0 ? nearArtists : all;
        var best = candidates.OrderByDescending(row => row.Score).FirstOrDefault();
        top = pinned ?? (top != null && candidates.Contains(top) && (best == null || best.Score < top.Score + 5) ? top : best);
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
            var ranked = rows.OrderByDescending(row => row.Score).ToArray();
            rows.Clear();
            rows.AddRange(ranked);
            return;
        }
        // Five points is one band in the shared scorer; smaller evidence changes keep their place.
        for (var index = 1; index < rows.Count; index++)
            for (var position = index; position > 0 && rows[position].Score >= rows[position - 1].Score + 5; position--)
                (rows[position - 1], rows[position]) = (rows[position], rows[position - 1]);
    }

    private static TimeSpan MaxDelay(TimeSpan delay) => delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
}
