using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ProviderDiagnosticSamplesTests
{
    [Fact]
    public async Task Check_RemovedRecordingTriesAnotherAndRemembersSuccess()
    {
        using var samples = new ProviderDiagnosticSamples();
        var tried = new List<string>();
        var curated = ProviderDiagnosticSamples.Recordings("spotify");
        var result = await samples.CheckAsync("spotify:test", curated, (sample, _) =>
        {
            tried.Add(sample.Title);
            return Task.FromResult(tried.Count == 1 ? ProviderSampleCheck.Missing : new ProviderSampleCheck(true));
        }, cancellationToken: default);
        Assert.True(result.Success);
        Assert.Equal(2, tried.Count);
        var working = tried[1];
        tried.Clear();
        await samples.CheckAsync("spotify:test", curated, (sample, _) =>
        {
            tried.Add(sample.Title);
            return Task.FromResult(new ProviderSampleCheck(true));
        }, cancellationToken: default);
        Assert.Equal(working, Assert.Single(tried));
    }

    [Fact]
    public async Task Check_RotatesTheStartingRecordingAfterAnUnavailableRun()
    {
        using var samples = new ProviderDiagnosticSamples();
        var tried = new List<string>();
        for (var i = 0; i < 2; i++)
            await samples.CheckAsync("catalog:test", ProviderDiagnosticSamples.Recordings("deezer"),
                (sample, _) => { tried.Add(sample.Title); return Task.FromResult(ProviderSampleCheck.Missing); });
        Assert.Equal(16, tried.Count);
        Assert.NotEqual(tried[0], tried[8]);
        Assert.Equal(tried.Take(8).OrderBy(item => item), tried.Skip(8).OrderBy(item => item));
    }

    [Fact]
    public async Task Check_AllCuratedRecordingsRemovedDiscoversFreshRecording()
    {
        using var samples = new ProviderDiagnosticSamples();
        var attempts = 0;
        var result = await samples.CheckAsync("catalog:test", ProviderDiagnosticSamples.Recordings("deezer"),
            (sample, _) =>
            {
                attempts++;
                return Task.FromResult(sample.Title == "Fresh song" ? new ProviderSampleCheck(true) : ProviderSampleCheck.Missing);
            }, Discover, default);
        Assert.True(result.Success);
        Assert.Equal(9, attempts);
    }

    [Fact]
    public async Task Check_OutageRequiresDistinctRecordingsAndKeepsReason()
    {
        using var samples = new ProviderDiagnosticSamples();
        var tried = new HashSet<string>();
        var result = await samples.CheckAsync("catalog:test", ProviderDiagnosticSamples.Recordings("qobuz"),
            (sample, _) =>
            {
                tried.Add(sample.Title);
                return Task.FromResult(new ProviderSampleCheck(false, "rate-limited"));
            }, cancellationToken: default);
        Assert.False(result.Success);
        Assert.Equal("rate-limited", result.ReasonCode);
        Assert.Equal(3, tried.Count);
    }

    [Fact]
    public async Task Check_OnlyRemovedSamplesAreNeutralAndAttemptsAreBounded()
    {
        using var samples = new ProviderDiagnosticSamples();
        var attempts = 0;
        var result = await samples.CheckAsync("catalog:test", ProviderDiagnosticSamples.Recordings("deezer"),
            (_, _) => { attempts++; return Task.FromResult(ProviderSampleCheck.Missing); }, DiscoverMany, default);
        Assert.Equal("sample-unavailable", result.ReasonCode);
        Assert.True(result.SampleUnavailable);
        Assert.Equal(ProviderDiagnosticSamples.MaximumAttempts, attempts);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(500, true)]
    public async Task Check_RespectsTheSourcesAttemptBudget(int milliseconds, bool succeeds)
    {
        using var samples = new ProviderDiagnosticSamples();
        var result = await samples.CheckAsync("catalog:test", ProviderDiagnosticSamples.Recordings("deezer"),
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                return new ProviderSampleCheck(true);
            }, attemptTimeout: TimeSpan.FromMilliseconds(milliseconds));
        Assert.Equal(succeeds, result.Success);
        Assert.Equal(succeeds ? null : "timeout", result.ReasonCode);
    }

    [Fact]
    public async Task Check_DeadlineStopsWithoutBlamingOneUnfinishedSample()
    {
        using var samples = new ProviderDiagnosticSamples(timeout: TimeSpan.FromMilliseconds(20));
        var pending = samples.CheckAsync("catalog:test", ProviderDiagnosticSamples.Recordings("deezer"),
            (_, _) => new TaskCompletionSource<ProviderSampleCheck>().Task, cancellationToken: default);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("timeout", result.ReasonCode);
        Assert.True(result.SampleUnavailable);
    }

    [Fact]
    public async Task Check_DiscoveryDeadlineAfterRemovedSamplesStaysNeutral()
    {
        using var samples = new ProviderDiagnosticSamples(timeout: TimeSpan.FromMilliseconds(20));
        var result = await samples.CheckAsync("catalog:test", ProviderDiagnosticSamples.Recordings("deezer"),
            (_, _) => Task.FromResult(ProviderSampleCheck.Missing), SlowDiscovery);
        Assert.True(result.SampleUnavailable);
        Assert.Equal("sample-unavailable", result.ReasonCode);
    }

    private static async IAsyncEnumerable<ProviderDiagnosticSample> SlowDiscovery(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        yield return new("Fresh song", "Artist", null, 200);
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("jiosaavn")]
    [InlineData("melon")]
    [InlineData("artistgrid")]
    public void Recordings_HaveEightDistinctSongsAndIdentifiers(string provider)
    {
        var recordings = ProviderDiagnosticSamples.Recordings(provider);
        Assert.True(recordings.Count >= 8);
        Assert.Equal(recordings.Count, recordings.Select(item => item.Title).Distinct().Count());
        Assert.All(recordings, item => Assert.Matches("^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$", item.Isrc!));
    }

    private static async IAsyncEnumerable<ProviderDiagnosticSample> Discover([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        yield return new("Fresh song", "Artist", null, 200);
    }

    private static async IAsyncEnumerable<ProviderDiagnosticSample> DiscoverMany([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        for (var i = 0; i < 100; i++)
        {
            token.ThrowIfCancellationRequested();
            yield return new($"Fresh song {i}", "Artist", null, 200);
        }
    }
}
