using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class LyricsExecutionSnapshotTests
{
    [Fact]
    public async Task GetOrCreateAsyncSharesOneDependencyObservation()
    {
        var snapshot = new LyricsExecutionSnapshot();
        var calls = 0;

        async Task<string> LoadAsync(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return "ready";
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            snapshot.GetOrCreateAsync("apple-download-capabilities", LoadAsync, CancellationToken.None)));

        Assert.Equal(1, calls);
        Assert.Equal(new[] { "ready", "ready", "ready", "ready" }, results);
    }
}
