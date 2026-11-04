using System.Text.Json;
using allstarr.Core.Protocols;

namespace allstarr.Core.Music;

public sealed partial class MusicSearchService
{
    private readonly Dictionary<string, SearchPagePrefix> searchPages = new(StringComparer.Ordinal);
    private readonly object searchPagesLock = new();

    internal IReadOnlyList<T> PreserveSearchOrder<T>(ProtocolExecutionContext context, string query, string scope,
        int offset, int count, IReadOnlyList<T> items, Func<T, string> identity, bool continueSearch = false)
    {
        if (count <= 0) return items.DistinctBy(identity, StringComparer.Ordinal).ToArray();
        var key = SearchPageKey<T>(context, query, scope);
        var continuation = offset > 0 || continueSearch;
        lock (searchPagesLock)
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            foreach (var expired in searchPages.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
                searchPages.Remove(expired);
            searchPages.TryGetValue(key, out var previous);
            var rows = new List<T>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = items.DistinctBy(identity, StringComparer.Ordinal).ToDictionary(identity, StringComparer.Ordinal);
            if (continuation && previous?.Items is IReadOnlyList<T> prefix)
                foreach (var item in prefix)
                    if (seen.Add(identity(item))) rows.Add(current.GetValueOrDefault(identity(item), item));
            foreach (var item in items)
                if (seen.Add(identity(item))) rows.Add(item);

            // Retain only the prefix already sent to the listener; late answers can still rank on the next page.
            var returned = (int)Math.Min(2000L, Math.Max(offset, 0L) + Math.Max(count, 0));
            var keep = continuation && previous?.Items is IReadOnlyList<T> known ? Math.Max(known.Count, returned) : returned;
            if (!searchPages.ContainsKey(key) && searchPages.Count >= 256)
                searchPages.Remove(searchPages.MinBy(item => item.Value.ExpiresAt).Key);
            searchPages[key] = new(now.AddMinutes(2), rows.Take(keep).ToArray(), continuation ? previous?.Windows : null);
            return rows;
        }
    }

    internal IReadOnlyList<T> ApplySubsonicSearchWindow<T>(ProtocolExecutionContext context, string query, string scope,
        int offset, int count, IReadOnlyList<T> items, Func<T, string> identity)
    {
        if (count <= 0) return [];
        var key = SearchPageKey<T>(context, query, scope);
        lock (searchPagesLock)
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var windows = offset > 0 && searchPages.TryGetValue(key, out var previous) && previous.ExpiresAt > now
                ? previous.Windows?.ToList() ?? [] : new List<SearchPageWindow>();
            var known = windows.FirstOrDefault(item => item.Offset == offset);
            var start = known?.Start ?? offset;
            if (known == null && offset > 0 && windows.Where(item => item.Offset < offset).MaxBy(item => item.Offset) is { } earlier)
            {
                var distance = offset - earlier.Offset;
                start = earlier.Start + (distance >= earlier.Count
                    ? earlier.Returned + distance - earlier.Count : Math.Min(distance, earlier.Returned));
            }
            // The first merge can exceed the requested count; remember each window's actual start for retries and overlaps.
            var ordered = PreserveSearchOrder(context, query, scope, start,
                offset == 0 ? Math.Max(1, items.Count) : count, items, identity, continueSearch: offset > 0);
            var page = offset == 0 ? ordered : ordered.Skip(start).Take(count).ToArray();
            windows.RemoveAll(item => item.Offset == offset);
            windows.Add(new(offset, Math.Max(count, known?.Count ?? 0), start, Math.Max(page.Count, known?.Returned ?? 0)));
            if (windows.Count > 128)
                windows.Remove(windows.Where(item => item.Offset != 0).MinBy(item => item.Offset)!);
            searchPages[key] = searchPages[key] with { Windows = windows };
            return page;
        }
    }

    private static string SearchPageKey<T>(ProtocolExecutionContext context, string query, string scope) => JsonSerializer.Serialize(new
    {
        context.Protocol,
        context.BackendInstanceId,
        context.VerifiedBackendPrincipalId,
        context.Actor,
        context.Client,
        context.Policy,
        Query = query,
        Scope = scope,
        Payload = typeof(T).FullName
    });

    private sealed record SearchPagePrefix(DateTimeOffset ExpiresAt, object Items, IReadOnlyList<SearchPageWindow>? Windows = null);
    private sealed record SearchPageWindow(int Offset, int Count, int Start, int Returned);
}
