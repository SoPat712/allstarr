using System.Reflection;
using System.Text.Json;
using allstarr.Core.AudioMuse;
using allstarr.Core.Capabilities;
using allstarr.Core.Enrichment;
using allstarr.Core.Intelligence;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    private async Task SeedReleaseInbox(bool includeArtistAlbums = false)
    {
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.Setup(x => x.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .Returns((ProviderExecutionContext context, ProviderArtistItemsRequest request) => Task.FromResult(
                ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new(context.ProviderId,
                    Enumerable.Range(0, request.Id.Value == "kylie" ? 2 : 1).Select(i => new ProviderAlbumMetadata(
                        new("deezer", ProviderResourceKind.Album, request.Id.Value + i), "Release " + i,
                        [new(request.Id.Value == "kylie" ? "Kylie Cantrall" : "FLO", request.Id)],
                        releaseDate: clock.UtcNow.AddDays(-i - 1).ToString("yyyy-MM-dd"), isExplicit: false,
                        releaseType: request.Id.Value == "kylie" ? "single" : "album")).ToArray()))));
        var router = new Mock<IProviderRouter>();
        router.Setup(x => x.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .Returns((ProviderRouteRequest request) =>
            {
                var descriptor = new ProviderDescriptor("deezer", "Deezer", "Catalog fixture", ProviderOrigin.BuiltIn, "1", "1",
                    [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                        "1", ["getArtistAlbums"])], new());
                var context = new ProviderExecutionContext(request.Actor, "deezer", null, request.Policy,
                    request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
                return Task.FromResult(new ProviderRoutePlan<IProviderMetadataCapability>(request,
                    [new(0, descriptor, descriptor.Capabilities.Single(), metadata.Object, context, null)],
                    new(request.CorrelationId, request.Capability, "deezer", null, [])));
            });
        var releases = new NewReleasesService(factory, clock, router.Object, settings.Object);
        await releases.FollowAsync(owner, new("deezer", "kylie", "Kylie Cantrall"));
        await releases.FollowAsync(owner, new("deezer", "flo", "FLO"));
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        clock.UtcNow += TimeSpan.FromSeconds(3);
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        Assert.Equal(3, (await releases.ReadAsync(owner)).Entries.Count);
        gateway.Setup(x => x.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult<Album?>(new()
            {
                Songs = Enumerable.Range(1, 3).Select(track => new Song
                {
                    Id = "ext-deezer-song-" + id + track,
                    ExternalProvider = "deezer",
                    ExternalId = id + track,
                    Title = "Release " + id + " track " + track,
                    Artist = id.StartsWith("kylie", StringComparison.Ordinal) ? "Kylie Cantrall" : "FLO",
                    Track = track,
                    ExplicitContentLyrics = track == 1 ? 1 : 0
                }).ToList()
            }));
        if (includeArtistAlbums)
            gateway.Setup(x => x.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
                .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult(Enumerable.Range(0, id == "kylie" ? 2 : 1)
                    .Select(i => new Album { ExternalId = id + i, ReleaseDate = clock.UtcNow.AddDays(-i - 1).ToString("yyyy-MM-dd") }).ToList()));
        service = new(factory, clock, tags, taste, new(gateway.Object), gateway.Object, releases,
            settings.Object, policies.Object, access.Object);
    }

    [Fact]
    public async Task Release_inbox_resolves_three_entries_before_frequent_artist_searches()
    {
        await Seed(count: 40, plays: 10);
        await SeedReleaseInbox();
        filter = "CleanOnly";
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "release-radar").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(3, mix.Songs.Count);
        Assert.All(mix.Songs, x => Assert.Equal(2, x.Song.Track));
        Assert.Equal(3, mix.Evidence!.FromServices);
        Assert.Equal(0, mix.Evidence.FromMusic);
        Assert.True(mix.Songs[0].Reason.Contains(clock.UtcNow.AddDays(-1).ToString("dd MMM yyyy"), StringComparison.Ordinal));
        gateway.Verify(x => x.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Release_inbox_does_not_read_its_albums_again_from_artist_catalogues()
    {
        await SeedReleaseInbox(includeArtistAlbums: true);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "release-radar").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(3, mix.Songs.Count);
        gateway.Verify(x => x.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Release_inbox_can_use_a_track_not_selected_from_an_earlier_album()
    {
        await SeedReleaseInbox();
        var shared = new Song
        {
            Id = "ext-deezer-song-shared",
            ExternalProvider = "deezer",
            ExternalId = "shared",
            Title = "Shared song",
            Artist = "Kylie Cantrall",
            Track = 3
        };
        gateway.Setup(x => x.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "kylie0"))
            .ReturnsAsync(new Album
            {
                Songs = [new Song { Id = "ext-deezer-song-first", ExternalProvider = "deezer", ExternalId = "first",
                Title = "First song", Artist = "Kylie Cantrall", Track = 1 }, shared]
            });
        gateway.Setup(x => x.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "kylie1"))
            .ReturnsAsync(new Album { Songs = [shared] });
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "release-radar").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(3, mix.Songs.Count);
        Assert.Contains(mix.Songs, x => x.Song.Id == shared.Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Release_inbox_has_its_own_bounded_reads_when_generation_reads_are_spent(bool alreadyFull, bool routed)
    {
        await SeedReleaseInbox();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(PersonalMixService).GetNestedType("GenerationInputs", BindingFlags.NonPublic)!;
        var inputs = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetField("Reads", flags)!.SetValue(inputs, MixRecipes.GenerationReads);
        type.GetField("Playable", flags)!.SetValue(inputs, new[] { routed ? "playback" : "deezer" });
        var cards = await service.ListAsync(owner);
        var document = new PersonalMixDocument { Mixes = cards.ToDictionary(x => x.Kind) };
        await using var db = factory.CreateDbContext();
        var user = await db.Users.FindAsync(owner);
        var own = alreadyFull ? Enumerable.Range(0, 50).Select(i => new MixCandidate(new Song
        { Id = "old-" + i, Title = "Old " + i, Artist = i < 2 ? "Kylie Cantrall" : i == 2 ? "FLO" : "Old artist " + i, ExternalProvider = "deezer", ExternalId = "old-" + i },
            new Dictionary<string, double>(), 0, 0, null, false, "deezer", 0, clock.UtcNow.AddDays(-7), ReleaseKey: "old-" + i)).ToArray() : [];
        var rows = await (Task<IReadOnlyList<MixCandidate>>)typeof(PersonalMixService).GetMethod("FillAsync", flags)!.Invoke(service,
            [user!, own, Array.Empty<ListeningEventRecord>(), inputs, new[] { "release-radar" }, document,
                new Dictionary<string, AudioMuseScores>(), CancellationToken.None])!;
        var selected = PersonalMixRules.Select("release-radar", rows, clock.UtcNow, []);
        Assert.Equal(alreadyFull ? 50 : 3, selected.Count);
        Assert.Equal(3, selected.Count(row => !row.Song.Id.StartsWith("old-", StringComparison.Ordinal)));
        gateway.Verify(x => x.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.Exactly(3));
    }

    [Theory]
    [InlineData("on-repeat", 1, false)]
    [InlineData("on-repeat", 1, true)]
    [InlineData("rewind", 300, false)]
    [InlineData("rewind", 300, true)]
    public async Task History_uses_playable_matches_when_the_original_service_is_unavailable(string kind, int age, bool stored)
    {
        await Seed(count: 40, plays: 10, age: age);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in await db.ListeningEvents.ToArrayAsync())
                play.TrackReference = "ext-spotify-song-" + play.Title![5..];
            await db.SaveChangesAsync();
        }
        if (stored)
            gateway.Setup(x => x.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "spotify", It.IsAny<string>()))
                .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult<Song?>(new()
                {
                    Id = "ext-spotify-song-" + id,
                    ExternalProvider = "spotify",
                    ExternalId = id,
                    Title = songs[id].Title,
                    Artist = songs[id].Artist,
                    CanonicalRecordingId = songs[id].CanonicalRecordingId
                }));
        gateway.Setup(x => x.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .Returns((ProtocolExecutionContext _, Song song) => Task.FromResult(song.ExternalProvider == "deezer"));
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 3))
            .Returns((ProtocolExecutionContext _, string _, string query, int _) => Task.FromResult(
                ProviderOutcome<IReadOnlyList<Song>>.Success(songs.Values.Where(song => query == song.Title + " " + song.Artist).ToArray())));
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == kind).Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(40, mix.Songs.Count);
        Assert.Equal(40, mix.Evidence!.FromServices);
        Assert.All(mix.Songs, x => Assert.Contains("10", x.Reason));
        gateway.Verify(x => x.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "spotify", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Rewind_resolves_older_service_favourites_beyond_the_recent_history_limit()
    {
        await Seed(count: 340, plays: 5);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in await db.ListeningEvents.ToArrayAsync())
                if (int.Parse(play.Title![5..]) >= 300) play.ListenedAt = play.UpdatedAt = clock.UtcNow.AddDays(-300);
            await db.SaveChangesAsync();
        }
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "rewind").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(40, mix.Songs.Count);
        Assert.All(mix.Songs, x => Assert.False(x.Song.IsLocal));
    }

    [Fact]
    public async Task Workout_includes_saved_service_songs_with_fitting_styles()
    {
        var jobs = new DurableJobQueue(factory, new(), new(new()), clock);
        var job = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<object>("favorite.change", "saved-style", new { }, owner));
        await using (var db = factory.CreateDbContext())
        {
            var accepted = new FavoriteEventRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendPrincipalId = owner.ToString(),
                ItemId = "ext-deezer-song-saved",
                Operation = FavoriteOperation.Favorite,
                EventKey = "saved-style",
                JobId = job.JobId,
                CreatedAt = clock.UtcNow
            };
            db.FavoriteEvents.Add(accepted);
            db.FavoriteStates.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                ItemId = accepted.ItemId,
                LastEventId = accepted.Id,
                IsFavorite = true,
                DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, accepted.ItemId, "song", "Saved song", ["Saved artist"])
                { Genres = ["pop", "hip-hop"], IsExplicit = false }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
            await db.SaveChangesAsync();
        }
        var mix = (await service.GenerateAsync(owner)).Single(x => x.Kind == "workout");
        Assert.Equal("ext-deezer-song-saved", Assert.Single(mix.Songs).Song.Id);
        Assert.Equal(1, mix.Evidence!.FromServices);
        Assert.StartsWith("Tagged hip-hop", mix.Songs[0].Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_ineligible_library_copy_does_not_hide_playable_service_history(bool missingDetails)
    {
        await Seed(count: 1, plays: 10);
        access.Setup(x => x.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "on-repeat").Id;
        Assert.Single((await service.GenerateAsync(owner, id)).Single(x => x.Id == id).Songs);
        await using (var db = factory.CreateDbContext())
        {
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "music",
                BackendItemId = "local",
                Title = missingDetails ? "" : "Song 0",
                Artist = "Artist 0",
                CanonicalRecordingId = songs["0"].CanonicalRecordingId
            });
            await db.SaveChangesAsync();
        }
        filter = missingDetails ? "All" : "CleanOnly";
        gateway.Setup(x => x.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["replacement"]);
        gateway.Setup(x => x.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(false);
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "replacement", "Song 0 Artist 0", 3))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([new Song
            { Id = "ext-replacement-song-song", ExternalProvider = "replacement", ExternalId = "song", Title = "Song 0", Artist = "Artist 0",
                Duration = 180, ExplicitContentLyrics = 0, CanonicalRecordingId = songs["0"].CanonicalRecordingId }]));
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal("replacement", Assert.Single(mix.Songs).Song.ExternalProvider);
        await taste.PutNudgeAsync(owner, new("artist", "Artist 0", "never"));
        clock.UtcNow += TimeSpan.FromMinutes(16);
        Assert.Empty((await service.GenerateAsync(owner, id)).Single(x => x.Id == id).Songs);
        gateway.Verify(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "replacement", "Song 0 Artist 0", 3), Times.Once);
    }

    [Fact]
    public async Task Mood_reads_keep_the_generation_shortfall_until_policy_actually_removes_songs()
    {
        await Seed(count: 1);
        await tags.SaveAsync(SharedTagStore.RecordingKey(songs["0"].CanonicalRecordingId!.Value),
            [new("provider", [new("ambient")], clock.UtcNow)], [], false, default);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "focus").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Single(mix.Songs);
        Assert.Equal(mix.Readiness, (await service.GetAsync(owner, id)).Readiness);
        await taste.PutNudgeAsync(owner, new("artist", "Artist 0", "never"));
        var filtered = await service.GetAsync(owner, id);
        Assert.Empty(filtered.Songs);
        Assert.Contains("current listening policy", filtered.Readiness.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stored_history_metadata_can_use_a_different_playback_provider(bool playable)
    {
        await Seed(count: 40, plays: 10);
        gateway.Setup(x => x.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["playback"]);
        gateway.Setup(x => x.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(playable);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "on-repeat").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(playable ? 40 : 0, mix.Songs.Count);
        Assert.Equal(playable ? 40 : 0, mix.Evidence!.FromServices);
        gateway.Verify(x => x.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.AtLeastOnce);
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromMinutes(16);
        mix = (await service.GenerateAsync(owner, id, allowProviderReads: false)).Single(x => x.Id == id);
        Assert.Equal(playable ? 40 : 0, mix.Songs.Count);
        gateway.Verify(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }
}
