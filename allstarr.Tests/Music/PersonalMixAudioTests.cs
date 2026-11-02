using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.AudioMuse;
using allstarr.Core.Enrichment;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using static allstarr.Tests.AudioMuseTests;
using static allstarr.Tests.AudioMuseDiscoveryTests;

namespace allstarr.Tests;

public sealed class PersonalMixAudioTests
{
    private static PersonalMixService Service(Fixture f, Mock<IProtocolProviderGateway> gateway, IPlatformClock? clock = null, ILogger<PersonalMixService>? logger = null)
    {
        clock ??= new SystemPlatformClock();
        var tags = new SharedTagStore(f.Factory, clock);
        var policy = new Mock<IEffectiveProviderPolicyResolver>();
        policy.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<allstarr.Core.Capabilities.ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "Lossless", .07)
            { Preferences = new("All") });
        var settings = new Mock<IDurableRuntimeSettings>();
        settings.Setup(item => item.GetPreferencesAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new("All"), new(), false, "1"));
        return new(f.Factory, clock, tags, new(f.Factory, tags, clock), new(gateway.Object), gateway.Object,
            new NewReleasesService(f.Factory, clock, Mock.Of<IProviderRouter>(), settings.Object),
            settings.Object, policy.Object, f.Access.Object, f.Discovery, logger: logger);
    }

    [Fact]
    public async Task Local_start_logs_sound_shortfall_and_library_artist_limits()
    {
        await using var f = await ReadyAsync();
        var sound = Enumerable.Range(0, 25).Select(index => Track("sound-" + index, "Sound artist " + index)).ToArray();
        var own = Enumerable.Range(0, 3).Select(index => Track("own-" + index, "Artist A")).ToArray();
        await AddAsync(f, sound.Concat(own).Append(Track("seed", "Artist A")).ToArray());
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/api/similar_tracks"
            ? Fixture.Json(JsonSerializer.Serialize(sound.Select(track => new { item_id = track.BackendItemId }))) : original(request);
        var logger = new SoundMixLogger();

        var preview = await Service(f, new(), logger: logger).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));

        Assert.Equal(25, preview.Songs.Count);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("seed resolved=0, seed search found=0, provider similar=0", message);
        Assert.Contains("sound neighbours=25, preferred=0", message);
        Assert.Contains("audiomuse[found=25,without-seed=25,unique=25,details=25,policy=25,never=25,playable=25,skips=25,filters=25,evidence=0,revalidated-never=25,revalidated-policy=25,access=25,eligible=25,artist-checked=23,artist-kept=23,selected=23]", message);
        Assert.Contains("library[found=4,without-seed=3,unique=3,details=3,policy=3,never=3,playable=3,skips=3,filters=3,evidence=3,revalidated-never=3,revalidated-policy=3,access=3,eligible=3,artist-checked=3,artist-kept=2,selected=2]", message);
        Assert.Contains("selected seed/related/sound/general=2/0/23/0; sound allowance=10, related shortfall=13", message);
        Assert.Matches(@"sound=\d+,radio=\d+", message);
        foreach (var track in sound.Concat(own))
        {
            Assert.DoesNotContain(track.Title, message, StringComparison.Ordinal);
            Assert.DoesNotContain(track.BackendItemId, message, StringComparison.Ordinal);
        }
    }

    private sealed class SoundMixLogger : ILogger<PersonalMixService>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Information, level);
            Messages.Add(formatter(state, error));
        }
    }

    [Theory]
    [InlineData("song", false)]
    [InlineData("song", true)]
    [InlineData("artist", true)]
    public async Task Starts_keep_seed_sound_radio_and_related_library_ahead_of_general_favourites(string kind, bool external)
    {
        await using var f = await ReadyAsync();
        var sound = Enumerable.Range(0, 41).Select(index => Track(index == 40 ? "zz-compatible" : "00-sound-" + index.ToString("D2"), "Sound artist " + index)).ToArray();
        var own = Enumerable.Range(0, 3).Select(index => Track("own-" + index, "Artist A")).ToArray();
        var related = Enumerable.Range(0, 6).Select(index => Track("related-" + index, "Related artist " + index)).ToArray();
        var unrelated = Enumerable.Range(0, 80).Select(index => Track("unrelated-" + index, "Unrelated artist " + index)).ToArray();
        var seedTrack = Track("seed", "Artist A");
        seedTrack.DurationMilliseconds = 180000;
        seedTrack.CanonicalRecordingId = Guid.NewGuid();
        sound[^1].CanonicalRecordingId = Guid.NewGuid();
        await using (var db = f.Factory.CreateDbContext())
        {
            foreach (var recording in new[] { seedTrack.CanonicalRecordingId.Value, sound[^1].CanonicalRecordingId!.Value })
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = f.Actor.AllstarrUserId!.Value });
            await db.SaveChangesAsync();
        }
        await AddAsync(f, sound.Concat(own).Concat(related).Concat(unrelated).Append(seedTrack).ToArray());
        var clock = new SystemPlatformClock();
        var tags = new SharedTagStore(f.Factory, clock);
        foreach (var recording in new[] { seedTrack.CanonicalRecordingId.Value, sound[^1].CanonicalRecordingId!.Value })
            await tags.SaveAsync(SharedTagStore.RecordingKey(recording), [new("provider", [new("hip-hop")], clock.UtcNow)], [], false, default);
        var radioSongs = Enumerable.Range(0, 20).Select(index => new Song
        {
            Id = "ext-deezer-song-radio-" + index,
            ExternalProvider = "deezer",
            ExternalId = "radio-" + index,
            Title = "Radio " + index,
            Artist = "Related artist " + index
        }).ToArray();
        await AddHeartsAsync(f, Enumerable.Range(0, 80).Select(index => new Song
        { Id = "ext-deezer-song-heart-" + index, Title = "Heart " + index, Artist = "Unrelated artist " + index }));
        var seed = new Song
        {
            Id = "ext-deezer-song-seed",
            ExternalProvider = "deezer",
            ExternalId = "seed",
            Title = "Library seed",
            Artist = "Artist A",
            CanonicalRecordingId = seedTrack.CanonicalRecordingId,
            Duration = 180
        };
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).ReturnsAsync(seed);
        gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(new Artist { Name = "Artist A" });
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).ReturnsAsync([seed]);
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Library seed Artist A", 3)).ReturnsAsync([seed]);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 100)).ReturnsAsync(radioSongs.ToList());
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(true);
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/api/similar_tracks"
            ? Fixture.Json(JsonSerializer.Serialize(sound.Select(track => new { item_id = track.BackendItemId }))) : original(request);
        var taste = new TasteService(f.Factory, tags, clock);
        await taste.PutNudgeAsync(f.Actor.AllstarrUserId!.Value, new("artist", "Unrelated artist 0", "more"));

        var preview = await Service(f, gateway).StartAsync(f.Actor.AllstarrUserId.Value,
            new(kind, external ? "ext-deezer-" + kind + "-seed" : "seed", 25));

        Assert.Equal(25, preview.Songs.Count);
        Assert.DoesNotContain(preview.Songs, item => item.Song.Artist.StartsWith("Unrelated", StringComparison.Ordinal));
        Assert.Contains(preview.Songs, item => item.Source == "library" && item.Song.Artist == "Artist A");
        Assert.Contains(preview.Songs, item => item.Source == "library" && item.Song.Artist.StartsWith("Related", StringComparison.Ordinal));
        if (!external)
        {
            Assert.Equal(8, preview.Songs.Count(item => item.Source == "audiomuse"));
            Assert.Contains(preview.Songs, item => item.Song.Id == "zz-compatible");
            Assert.True(preview.Songs.Take(3).Select(item => item.Source).Distinct().Count() > 1);
        }
        if (kind == "song") Assert.DoesNotContain(preview.Songs, item => item.Song.Title == seed.Title);
        Assert.All(preview.Songs.GroupBy(item => item.Song.Artist), group => Assert.True(group.Count() <= 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_start_tops_up_only_after_eligible_related_songs_and_artist_limits(bool generalHearts)
    {
        await using var f = await ReadyAsync();
        var own = Enumerable.Range(0, 4).Select(index => Track("own-" + index, "Artist A")).ToArray();
        var unrelated = Enumerable.Range(0, 40).Select(index => Track("unrelated-" + index, "Unrelated artist " + index)).ToArray();
        var denied = Track("denied", "Artist A");
        denied.BackendLibraryId = "restricted";
        var seedTrack = Track("seed", "Artist A");
        seedTrack.DurationMilliseconds = 180000;
        await AddAsync(f, own.Concat(unrelated).Append(seedTrack).Append(Track("sound", "Sound artist"))
            .Append(Track("related", "Related artist")).Append(denied).ToArray());
        await AddHeartsAsync(f, Enumerable.Range(0, generalHearts ? 40 : 0).Select(index => new Song
        { Id = "ext-deezer-song-heart-" + index, Title = "Heart " + index, Artist = "Favourite artist " + index })
            .Append(new Song { Id = "ext-deezer-song-companion", Title = "Companion", Artist = "Secondary related artist" }));
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = new Song { Id = "ext-deezer-song-seed", ExternalProvider = "deezer", ExternalId = "seed", Title = "Library seed", Artist = "Artist A", Duration = 180 };
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Library seed Artist A", 3)).ReturnsAsync([seed]);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 100))
            .ReturnsAsync([seed, new Song { Id = "ext-deezer-song-radio", Title = "Radio", Artist = "Related artist", ExternalProvider = "deezer", ExternalId = "radio" },
                new Song { Id = "ext-deezer-song-secondary", Title = "Secondary", Artist = "Secondary related artist", ExternalProvider = "deezer", ExternalId = "secondary" }]);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(true);
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/api/similar_tracks"
            ? Fixture.Json("[{\"item_id\":\"sound\"},{\"item_id\":\"denied\"}]") : original(request);

        var preview = await Service(f, gateway).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));

        Assert.Equal(25, preview.Songs.Count);
        Assert.All(preview.Songs.Take(7), item => Assert.Contains(item.Song.Artist, new[] { "Artist A", "Sound artist", "Related artist", "Secondary related artist" }));
        Assert.All(preview.Songs.Skip(7), item => Assert.DoesNotContain(item.Song.Artist, new[] { "Artist A", "Sound artist", "Related artist", "Secondary related artist" }));
        Assert.Equal(2, preview.Songs.Count(item => item.Song.Artist == "Artist A"));
        Assert.Contains(preview.Songs.Take(7), item => item.Source == "heart" && item.Song.Artist == "Secondary related artist");
        Assert.All(preview.Songs.Skip(7), item => Assert.Equal(generalHearts ? "heart" : "library", item.Source));
        Assert.DoesNotContain(preview.Songs, item => item.Song.Id is "seed" or "denied" || item.Song.Title == seed.Title);
        Assert.Equal(preview.Songs.Count, preview.Songs.Select(item => item.Song.Id).Distinct().Count());
        Assert.All(preview.Songs.GroupBy(item => item.Song.Artist), group => Assert.True(group.Count() <= 2));
    }

    [Fact]
    public async Task Sound_filled_start_does_not_check_playback_for_unrelated_hearts()
    {
        await using var f = await ReadyAsync();
        var sound = Enumerable.Range(0, 25).Select(index => Track("sound-" + index, "Sound artist " + index)).ToArray();
        await AddAsync(f, sound.Append(Track("seed", "Artist A")).ToArray());
        await AddHeartsAsync(f, Enumerable.Range(0, 40).Select(index => new Song
        { Id = "ext-deezer-song-heart-" + index, Title = "Heart " + index, Artist = "Favourite artist " + index }));
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/api/similar_tracks"
            ? Fixture.Json(JsonSerializer.Serialize(sound.Select(track => new { item_id = track.BackendItemId }))) : original(request);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(true);

        var preview = await Service(f, gateway).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));

        Assert.Equal(25, preview.Songs.Count);
        Assert.All(preview.Songs, item => Assert.Equal("audiomuse", item.Source));
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Never);
    }

    [Fact]
    public async Task Local_start_overlaps_sound_and_radio_reads()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed", "Artist A"));
        var clock = new SeedClock();
        var startedAt = clock.UtcNow;
        var soundStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soundRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var radioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var radioRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        f.Access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref reads) == 2)
                {
                    var at = clock.UtcNow;
                    soundStarted.SetResult();
                    await soundRelease.Task.WaitAsync(token);
                    clock.Complete(at.AddSeconds(2));
                }
                return new BackendLibraryAccess(true, ["allowed"]);
            });
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Library seed Artist A", 3))
            .Returns(async (ProtocolExecutionContext context, string _, int _) =>
            {
                var at = clock.UtcNow;
                radioStarted.SetResult();
                await radioRelease.Task.WaitAsync(context.CancellationToken);
                clock.Complete(at.AddSeconds(3));
                return new List<Song>();
            });
        var start = Service(f, gateway, clock).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));
        bool overlapped;
        try
        {
            await soundStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            overlapped = radioStarted.Task.IsCompleted;
            soundRelease.TrySetResult();
            await radioStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { soundRelease.TrySetResult(); radioRelease.TrySetResult(); }
        await start.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(overlapped, $"Sound and radio reads ran in sequence; source time was {(clock.UtcNow - startedAt).TotalSeconds} seconds.");
        Assert.Equal(TimeSpan.FromSeconds(3), clock.UtcNow - startedAt);
    }

    private sealed class SeedClock : IPlatformClock
    {
        private readonly object gate = new();
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public DateTimeOffset UtcNow { get { lock (gate) return now; } }
        internal void Complete(DateTimeOffset at) { lock (gate) { if (at > now) now = at; } }
    }

    private static async Task AddHeartsAsync(Fixture f, IEnumerable<Song> songs)
    {
        var clock = new SystemPlatformClock();
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(f.Factory, options, new(options), clock);
        var owner = f.Actor.AllstarrUserId!.Value;
        var job = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("favorite.change", "hearts", new { }, owner));
        await using var db = f.Factory.CreateDbContext();
        foreach (var song in songs)
        {
            var accepted = new FavoriteEventRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "listener",
                ItemId = song.Id,
                Operation = FavoriteOperation.Favorite,
                EventKey = Guid.NewGuid().ToString(),
                JobId = job.JobId,
                CreatedAt = clock.UtcNow
            };
            db.FavoriteEvents.Add(accepted);
            db.FavoriteStates.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                ItemId = song.Id,
                LastEventId = accepted.Id,
                IsFavorite = true,
                DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, song.Id, "song", song.Title, [song.Artist]),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
        }
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("chill", .7, .4, .7, .7, .7, .6, .5, 100)]
    [InlineData("focus", .8, .2, .4, .2, .3, .5, .3, 90)]
    [InlineData("workout", .3, .8, .8, .6, .7, .2, .8, 140)]
    [InlineData("party", .6, .6, .8, .8, .8, .4, .8, 125)]
    [InlineData("sleep", .8, .2, .2, .1, .2, .8, .2, 65)]
    public async Task Moods_use_library_analysis_payloads(string kind, double relaxed, double aggressive, double danceable,
        double party, double happy, double sad, double energy, double tempo)
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Enumerable.Range(0, 25).Select(index => Track(index.ToString(), "Artist " + index)).ToArray());
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/external/get_score" ? Fixture.Json(JsonSerializer.Serialize(new
        {
            item_id = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["id"].ToString(),
            energy,
            tempo,
            mood_vector = "ambient:0.6,electronic:0.5",
            other_features = FormattableString.Invariant($"danceable:{danceable},aggressive:{aggressive},happy:{happy},party:{party},relaxed:{relaxed},sad:{sad}")
        })) : original(request);
        var service = Service(f, new());
        var mix = (await service.GenerateAsync(f.Actor.AllstarrUserId!.Value)).Single(item => item.Kind == kind);
        Assert.True(mix.Readiness.Ready);
        Assert.Equal(25, mix.Songs.Count);
        Assert.Equal("moods-v3", mix.RecipeVersion);
        Assert.All(mix.Songs, item => { Assert.True(item.Song.IsLocal); Assert.Equal(kind switch { "workout" => "Energetic sound", "party" => "Party sound", _ => "Calm sound" }, item.Reason); });
    }

    [Fact]
    public async Task Local_seed_shared_with_another_listener_starts_from_the_listeners_own_copy()
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed", "Seed"));
        var other = Guid.NewGuid();
        await using (var db = f.Factory.CreateDbContext())
        {
            db.Users.Add(new allstarr.Core.Storage.UserRecord
            {
                Id = other,
                BackendType = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "other",
                DisplayName = "other",
                Enabled = true
            });
            var copy = Track("seed", "Seed");
            copy.OwnerUserId = other;
            db.LibraryTracks.Add(copy);
            await db.SaveChangesAsync();
        }

        var preview = await Service(f, new()).StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));

        Assert.Equal("Library seed", preview.SeedName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_seed_adds_analyzed_neighbors_only_when_indexed_and_rechecks_library_access(bool analyzed)
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed", "Seed"), Track("neighbor", "Neighbor"));
        var denied = Track("denied", "Denied");
        denied.BackendLibraryId = "restricted";
        await AddAsync(f, denied);
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/external/get_embedding" when !analyzed => Fixture.Json("{}", System.Net.HttpStatusCode.NotFound),
            "/api/similar_tracks" => Fixture.Json("[{\"item_id\":\"seed\"},{\"item_id\":\"neighbor\"},{\"item_id\":\"denied\"}]"),
            _ => original(request)
        };
        var service = Service(f, new());
        var preview = await service.StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));
        if (analyzed)
        {
            var song = Assert.Single(preview.Songs);
            Assert.Equal("neighbor", song.Song.Id);
            Assert.Equal("Similar sound", song.Reason);
            await using var db = f.Factory.CreateDbContext();
            (await db.LibraryTracks.SingleAsync(item => item.BackendItemId == "neighbor")).BackendLibraryId = "restricted";
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveCustomAsync(f.Actor.AllstarrUserId.Value, preview.Id, "Saved"));
        }
        else Assert.Empty(preview.Songs);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "denied", 25)));
    }
}
