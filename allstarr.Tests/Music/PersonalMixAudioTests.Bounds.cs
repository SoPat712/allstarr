using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Moq;
using static allstarr.Tests.AudioMuseTests;
using static allstarr.Tests.AudioMuseDiscoveryTests;

namespace allstarr.Tests;

public sealed partial class PersonalMixAudioTests
{
    [Fact]
    public async Task Unchecked_radio_duplicates_do_not_consume_related_favourite_slots()
    {
        await using var f = await ReadyAsync();
        var seed = Track("seed", "Seed artist");
        seed.DurationMilliseconds = 180000;
        await AddAsync(f, seed, Track("sound", "Sound artist"));
        var radio = Enumerable.Range(0, 20).Select(index => new Song
        { Id = "ext-deezer-song-radio-" + index, ExternalProvider = "deezer", ExternalId = "radio-" + index, Title = "Radio " + index, Artist = "Radio artist" }).ToList();
        var markers = Enumerable.Range(0, 12).Select(index => new Song
        { Id = "ext-deezer-song-marker-" + index, Artist = "Related artist " + index }).ToList();
        await AddHeartsAsync(f, radio.Skip(2).Concat(Enumerable.Range(0, 24).Select(index => new Song
        { Id = "ext-deezer-song-heart-" + index, Title = "Heart " + index, Artist = "Related artist " + index / 2 })));
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/api/similar_tracks"
            ? Fixture.Json("[{\"item_id\":\"sound\"}]") : original(request);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", seed.Title + " " + seed.Artist, 3))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([new()
            { Title = seed.Title, Artist = seed.Artist, Duration = 180, ExternalProvider = "deezer", ExternalId = "seed" }]));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync(radio.Concat(markers).ToList());
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(true);
        var logger = new SoundMixLogger();

        var preview = await Service(f, gateway, logger: logger).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));

        Assert.Equal(25, preview.Songs.Count);
        Assert.Equal(25, preview.Songs.Select(item => item.Song.Id).Distinct().Count());
        Assert.Contains("related capped=56", Assert.Single(logger.Messages));
    }

    [Fact]
    public async Task Failed_favourite_routes_allow_the_same_recordings_from_the_library()
    {
        await using var f = await ReadyAsync();
        var library = Enumerable.Range(0, 40).Select(index => Track("library-" + index, "Library artist " + index)).ToArray();
        await AddAsync(f, library.Append(Track("seed", "Seed artist")).Append(Track("sound", "Sound artist")).ToArray());
        await AddHeartsAsync(f, library.Select(track => new Song
        { Id = "ext-deezer-song-" + track.BackendItemId, Title = track.Title, Artist = track.Artist }));
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/api/similar_tracks"
            ? Fixture.Json("[{\"item_id\":\"sound\"}]") : original(request);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(false);

        var preview = await Service(f, gateway).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));

        Assert.Equal(25, preview.Songs.Count);
        Assert.All(preview.Songs, item => Assert.True(item.Song.IsLocal));
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Exactly(33));
    }

    [Theory]
    [InlineData(25, 0)]
    [InlineData(50, 0)]
    [InlineData(25, 150)]
    [InlineData(50, 150)]
    public async Task Large_related_pool_qualifies_only_selected_songs_with_bounded_playback_time(int length, int delay)
    {
        await using var f = await ReadyAsync();
        var sound = Enumerable.Range(0, 40).Select(index => Track("sound-" + index, "Sound artist " + index)).ToArray();
        var seed = Track("seed", "Seed artist");
        seed.DurationMilliseconds = 180000;
        await AddAsync(f, sound.Append(seed).ToArray());
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/api/similar_tracks"
            ? Fixture.Json(JsonSerializer.Serialize(sound.Select(track => new { item_id = track.BackendItemId }))) : original(request);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", seed.Title + " " + seed.Artist, 3))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([new()
            { Title = seed.Title, Artist = seed.Artist, Duration = 180, ExternalProvider = "deezer", ExternalId = "seed" }]));
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), seed.Artist, 0, 0, 3, "deezer"))
            .ReturnsAsync(new SearchResult { Artists = [new() { Name = seed.Artist, ExternalProvider = "deezer", ExternalId = "seed-artist" }] });
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync(Enumerable.Range(0, 100).Select(index => Related("similar-" + index, "Radio artist " + index)).ToList());
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed-artist", It.IsAny<int>()))
            .ReturnsAsync(Enumerable.Range(0, 50).Select(index => Related("own-" + index, seed.Artist)).ToList());
        gateway.Setup(item => item.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed-artist", 4))
            .ReturnsAsync(Enumerable.Range(0, 10).Select(index => new Artist
            { Name = "Related artist " + index, ExternalProvider = "deezer", ExternalId = index.ToString() }).ToList());
        for (var index = 0; index < 10; index++)
        {
            var artist = index.ToString();
            gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", artist, It.IsAny<int>()))
                .ReturnsAsync(Enumerable.Range(0, 50).Select(track => Related("related-" + artist + "-" + track, "Related artist " + artist)).ToList());
        }
        var checks = 0;
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .Returns(async (ProtocolExecutionContext context, Song _) =>
            {
                Interlocked.Increment(ref checks);
                await Task.Delay(delay, context.CancellationToken);
                return true;
            });
        var logger = new SoundMixLogger();
        var timer = Stopwatch.StartNew();

        var preview = await Service(f, gateway, logger: logger).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", length));

        timer.Stop();
        var measurement = $"Checks={checks}, elapsed={timer.ElapsedMilliseconds} ms; {Assert.Single(logger.Messages)}";
        Assert.Equal(length, preview.Songs.Count);
        Assert.Contains(preview.Songs, item => item.Song.Artist == seed.Artist);
        Assert.True(preview.Songs.Count(item => item.Source != "audiomuse") >= Math.Ceiling(length * .6), measurement);
        Assert.InRange(preview.Songs.Count(item => item.Source == "audiomuse"), 1, length * 2 / 5);
        Assert.Equal(length * 2 / 5, preview.Songs.Count(item => item.Source == "audiomuse"));
        Assert.Equal(length, preview.Songs.Select(item => item.Song.Id).Distinct().Count());
        Assert.True(checks <= length + 8, measurement);
        Assert.True(checks == (length == 25 ? 23 : 38), measurement);
        Assert.Contains("related capped=60", measurement);
        Assert.Contains("playback checks=" + checks, measurement);
        Assert.True(delay == 0 || timer.Elapsed < TimeSpan.FromSeconds(6), measurement);

        static Song Related(string id, string artist) => new()
        { Id = "ext-deezer-song-" + id, ExternalProvider = "deezer", ExternalId = id, Title = "Track " + id, Artist = artist };
    }
}
