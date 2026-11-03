using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Moq;
using Xunit.Abstractions;
using static allstarr.Tests.AudioMuseTests;
using static allstarr.Tests.AudioMuseDiscoveryTests;

namespace allstarr.Tests;

public sealed partial class PersonalMixAudioTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Local_start_with_provider_latencies_finishes_within_six_seconds_and_keeps_composition()
    {
        await using var f = await ReadyAsync();
        var sound = Enumerable.Range(0, 25).Select(index => Track("sound-" + index, "Sound artist " + index)).ToArray();
        var own = Enumerable.Range(0, 4).Select(index => Track("own-" + index, "Seed artist")).ToArray();
        var library = Enumerable.Range(0, 60).Select(index => Track("radio-" + index.ToString("D2"), "Radio artist " + index / 3)).ToArray();
        var seed = Track("seed", "Seed artist");
        seed.DurationMilliseconds = 180000;
        await AddAsync(f, sound.Concat(own).Concat(library).Append(seed).ToArray());
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/api/similar_tracks"
            ? Fixture.Json(JsonSerializer.Serialize(sound.Select(track => new { item_id = track.BackendItemId }))) : original(request);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var accessReads = 0;
        f.Access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref accessReads) == 2)
                    await Task.Delay(TimeSpan.FromMilliseconds(300), clock, token);
                return new BackendLibraryAccess(true, ["allowed"]);
            });
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", seed.Title + " " + seed.Artist, 3))
            .Returns((ProtocolExecutionContext context, string _, string _, int _) => ReadAsync(context, 1100,
                ProviderOutcome<IReadOnlyList<Song>>.Success([new()
                { Title = seed.Title, Artist = seed.Artist, Duration = 180, ExternalProvider = "deezer", ExternalId = "seed" }])));
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), seed.Artist, 0, 0, 3, "deezer"))
            .Returns((ProtocolExecutionContext context, string _, int _, int _, int _, string _) => ReadAsync(context, 500,
                new SearchResult { Artists = [new() { Name = seed.Artist, ExternalProvider = "deezer", ExternalId = "root" }] }));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .Returns((ProtocolExecutionContext context, string _, string _, int limit) => ReadAsync(context, 1000,
                Enumerable.Range(0, limit).Select(index => PersonalMixSeedTimingTests.Track("similar-" + index, "Radio artist " + index)).ToList()));
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root", 20))
            .Returns((ProtocolExecutionContext context, string _, string _, int _) => ReadAsync(context, 450,
                own.Select(track => PersonalMixSeedTimingTests.Track(track.BackendItemId, seed.Artist)).ToList()));
        gateway.Setup(item => item.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root", 4))
            .Returns((ProtocolExecutionContext context, string _, string _, int _) => ReadAsync(context, 300,
                Enumerable.Range(0, 4).Select(index => new Artist
                { Name = "Related artist " + index, ExternalProvider = "deezer", ExternalId = index.ToString() }).ToList()));
        var active = 0;
        var peak = 0;
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.Is<string>(id => id != "root"), 5))
            .Returns(async (ProtocolExecutionContext context, string _, string id, int limit) =>
            {
                var current = Interlocked.Increment(ref active);
                peak = Math.Max(peak, current);
                try { return await ReadAsync(context, 450, Enumerable.Range(0, limit).Select(index => PersonalMixSeedTimingTests.Track(id + "-" + index, "Related artist " + id)).ToList()); }
                finally { Interlocked.Decrement(ref active); }
            });
        var logger = new SoundMixLogger();
        var timer = Stopwatch.StartNew();
        var start = Service(f, gateway, logger: logger, timeProvider: clock).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1100));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(300));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(450));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(1000));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(450));
        clock.Advance(TimeSpan.FromMilliseconds(450));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(1000));
        clock.Advance(TimeSpan.FromMilliseconds(900));
        var preview = await start.WaitAsync(TimeSpan.FromSeconds(10));
        timer.Stop();
        var elapsed = TimeSpan.FromSeconds(3) + timer.Elapsed;
        var message = Assert.Single(logger.Messages);
        output.WriteLine($"Provider fixture: 3000 ms controlled I/O + {timer.ElapsedMilliseconds} ms local work = {elapsed.TotalMilliseconds:F0} ms. {message}");

        Assert.True(elapsed <= TimeSpan.FromSeconds(6), message);
        Assert.Equal(25, preview.Songs.Count);
        Assert.Equal(2, preview.Songs.Count(row => row.Song.Artist == seed.Artist));
        Assert.Equal(10, preview.Songs.Count(row => row.Source == "audiomuse"));
        Assert.Equal(13, preview.Songs.Count(row => row.Source != "audiomuse" && row.Song.Artist != seed.Artist));
        Assert.All(preview.Songs, row => Assert.True(row.Song.IsLocal));
        Assert.Equal(3, peak);
        Assert.Equal(0, active);
        Assert.Contains("related capped=60; playback checks=0", message);
        Assert.Contains("audiomuse[found=25,without-seed=25,unique=25", message);
        Assert.Contains("library[found=65,without-seed=64,unique=60", message);
        Assert.Contains("selected seed/related/sound/general=2/13/10/0", message);
        gateway.Verify(item => item.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root", 4), Times.Once);
        gateway.Verify(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.Is<string>(id => id != "root"), 5), Times.Exactly(4));

        async Task<T> ReadAsync<T>(ProtocolExecutionContext context, int milliseconds, T value)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), clock, context.CancellationToken);
            return value;
        }
    }
}
