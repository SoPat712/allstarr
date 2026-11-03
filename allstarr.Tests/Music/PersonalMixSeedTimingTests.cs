using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Moq;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class PersonalMixSeedTimingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Uncapped_source_fixture_records_sequential_provider_cost()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = new Song { Title = "Seed", Artist = "Seed artist", Duration = 180 };
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "Seed Seed artist", 3))
            .Returns((ProtocolExecutionContext context, string _, string _, int _) => ReadAsync(context, 1100,
                ProviderOutcome<IReadOnlyList<Song>>.Success([new() { Title = seed.Title, Artist = seed.Artist, Duration = 180, ExternalProvider = "deezer", ExternalId = "seed" }])));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 100))
            .Returns((ProtocolExecutionContext context, string _, string _, int _) => ReadAsync(context, 1000,
                Enumerable.Range(0, 100).Select(index => Track("similar-" + index, "Radio artist " + index)).ToList()));
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), seed.Artist, 0, 0, 3, "deezer"))
            .Returns((ProtocolExecutionContext context, string _, int _, int _, int _, string _) => ReadAsync(context, 500,
                new SearchResult { Artists = [new() { Name = seed.Artist, ExternalProvider = "deezer", ExternalId = "root" }] }));
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext context, string _, string id) => ReadAsync(context, 450,
                Enumerable.Range(0, 50).Select(index => Track(id + "-" + index, id == "root" ? seed.Artist : "Related artist " + id)).ToList()));
        gateway.Setup(item => item.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root", 10))
            .Returns((ProtocolExecutionContext context, string _, string _, int _) => ReadAsync(context, 300,
                Enumerable.Range(0, 10).Select(index => new Artist
                { Name = "Related artist " + index, ExternalProvider = "deezer", ExternalId = index.ToString() }).ToList()));
        using var radio = new MusicMixService(gateway.Object, timeProvider: clock);
        var at = clock.GetUtcNow();
        var start = radio.SeedCandidatesAsync(MusicObservationsTests.Context(), seed, null, null, null, default);
        int[] latencies = [1100, 1000, 500, 450, 300, 450, 450, 450, 450, 450, 450, 450, 450, 450, 450];
        foreach (var latency in latencies)
        {
            await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(latency));
            clock.Advance(TimeSpan.FromMilliseconds(latency));
        }
        var found = await start.WaitAsync(TimeSpan.FromSeconds(10));
        var elapsed = clock.GetUtcNow() - at;
        Assert.Equal(TimeSpan.FromMilliseconds(7850), elapsed);
        Assert.Equal(350, found.Count);
        output.WriteLine($"Uncapped source fixture: {elapsed.TotalMilliseconds:F0} ms controlled I/O; 100 similar, 50 own, 10 related artists, 500 related tracks fetched.");

        async Task<T> ReadAsync<T>(ProtocolExecutionContext context, int milliseconds, T value)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), clock, context.CancellationToken);
            return value;
        }
    }

    [Fact]
    public async Task Artist_discovery_overlaps_seed_resolution_and_preserves_arrivals_at_the_radio_deadline()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = new Song { Title = "Seed", Artist = "Seed artist", Duration = 180 };
        var providerPending = new TaskCompletionSource<ProviderOutcome<IReadOnlyList<Song>>>();
        var artistStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "Seed Seed artist", 3))
            .Returns(providerPending.Task);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), seed.Artist, 0, 0, 3, "deezer"))
            .Returns(() =>
            {
                artistStarted.TrySetResult();
                return Task.FromResult(new SearchResult { Artists = [new() { Name = seed.Artist, ExternalProvider = "deezer", ExternalId = "root" }] });
            });
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root", 20))
            .ReturnsAsync([Track("own", seed.Artist)]);
        using var radio = new MusicMixService(gateway.Object, timeProvider: clock);
        using var cancellation = new CancellationTokenSource();
        var start = radio.SeedCandidatesAsync(MusicObservationsTests.Context(),
            seed, null, null, null, cancellation.Token, capCandidates: true);
        try
        {
            Assert.True(artistStarted.Task.IsCompleted, "Artist discovery must start before the seed search finishes.");
            await clock.WaitForTimersAsync(TimeSpan.FromSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(3));
            var found = await start.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ext-deezer-song-own", Assert.Single(found).Id);
            Assert.False(providerPending.Task.IsCompleted);
        }
        finally { cancellation.Cancel(); }
    }

    [Fact]
    public async Task Radio_deadline_keeps_completed_related_tracks_and_cancels_slow_reads()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = Track("seed", "Seed artist");
        seed.ArtistId = "ext-deezer-artist-root";
        gateway.Setup(item => item.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root", 4))
            .ReturnsAsync(Enumerable.Range(0, 4).Select(index => new Artist
            { ExternalProvider = "deezer", ExternalId = index.ToString() }).ToList());
        var active = 0;
        var cancelled = 0;
        var allCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 5))
            .Returns(async (ProtocolExecutionContext context, string _, string id, int _) =>
            {
                if (id == "0") return [Track("arrived", "Related artist")];
                Interlocked.Increment(ref active);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                finally
                {
                    if (Interlocked.Decrement(ref active) == 0 && Volatile.Read(ref cancelled) == 3) allCancelled.TrySetResult();
                }
                return [];
            });
        using var radio = new MusicMixService(gateway.Object, timeProvider: clock);
        var start = radio.SeedCandidatesAsync(MusicObservationsTests.Context(), seed, null, "deezer", "seed", default, capCandidates: true);
        Assert.Equal(3, active);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(3));
        var found = await start.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("ext-deezer-song-arrived", Assert.Single(found).Id);
        await allCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, cancelled);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_treated_as_a_successful_partial_radio_result()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = Track("seed", "Seed artist");
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 20))
            .Returns(async (ProtocolExecutionContext context, string _, string _, int _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                return [];
            });
        using var radio = new MusicMixService(gateway.Object);
        using var cancellation = new CancellationTokenSource();
        var start = radio.SeedCandidatesAsync(MusicObservationsTests.Context(), seed, null, "deezer", "seed", cancellation.Token, capCandidates: true);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Artist_discovery_uses_the_resolved_song_identity_when_name_search_misses()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = new Song { Title = "Seed", Artist = "Seed artist", Duration = 180 };
        var resolved = Track("resolved", seed.Artist);
        resolved.Title = seed.Title;
        resolved.ArtistId = "ext-deezer-artist-root";
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "Seed Seed artist", 3))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([resolved]));
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root", 20))
            .ReturnsAsync([Track("own", seed.Artist)]);
        using var radio = new MusicMixService(gateway.Object);

        var found = await radio.SeedCandidatesAsync(MusicObservationsTests.Context(), seed, null, null, null, default, capCandidates: true);

        Assert.Equal("ext-deezer-song-own", Assert.Single(found).Id);
    }

    internal static Song Track(string id, string artist) => new()
    { Id = "ext-deezer-song-" + id, ExternalProvider = "deezer", ExternalId = id, Title = "Track " + id, Artist = artist, Duration = 180 };
}
