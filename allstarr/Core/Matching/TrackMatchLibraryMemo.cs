using System.Collections.Concurrent;
using allstarr.Core.Storage;

namespace allstarr.Core.Matching;

// One request that reviews many songs (such as a batch of item details) loads and indexes the library once.
public sealed class TrackMatchLibraryMemo
{
    private readonly ConcurrentDictionary<string, Lazy<Task<TrackMatchLibrarySnapshot>>> loads = new(StringComparer.Ordinal);

    internal Task<TrackMatchLibrarySnapshot> GetAsync(string key, Func<Task<TrackMatchLibrarySnapshot>> load) =>
        loads.GetOrAdd(key, _ => new Lazy<Task<TrackMatchLibrarySnapshot>>(load, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}

internal sealed record TrackMatchLibrarySnapshot(IReadOnlyList<LibraryTrackRecord> Tracks,
    IReadOnlyDictionary<string, TrackMatchCandidateIndex> Indexes);
