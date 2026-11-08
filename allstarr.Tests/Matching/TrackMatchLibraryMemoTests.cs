using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class TrackMatchLibraryMemoTests
{
    [Fact]
    public async Task ConcurrentReviewsInOneRequestLoadEachLibraryOnce()
    {
        var memo = new TrackMatchLibraryMemo();
        var loads = 0;
        var release = new TaskCompletionSource();
        async Task<TrackMatchLibrarySnapshot> Load()
        {
            Interlocked.Increment(ref loads);
            await release.Task;
            return new([], new Dictionary<string, TrackMatchCandidateIndex>());
        }
        var first = Enumerable.Range(0, 4).Select(_ => memo.GetAsync("listener|library", Load)).ToArray();
        var other = memo.GetAsync("listener|other-library", Load);
        release.SetResult();
        var snapshots = await Task.WhenAll(first);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        Assert.NotSame(snapshots[0], await other);
        Assert.Equal(2, loads);
    }
}
