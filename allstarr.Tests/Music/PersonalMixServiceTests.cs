using System.Collections.Immutable;
using allstarr.Core.Enrichment;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;
using allstarr.Controllers;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Favorites;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private readonly Clock clock = new();
    private readonly Guid owner = Guid.NewGuid(), other = Guid.NewGuid();
    private readonly Mock<IProtocolProviderGateway> gateway = new();
    private readonly Mock<IDurableRuntimeSettings> settings = new();
    private readonly Mock<IEffectiveProviderPolicyResolver> policies = new();
    private readonly Mock<IBackendLibraryAccessResolver> access = new();
    private TasteService taste = null!;
    private SharedTagStore tags = null!;
    private PersonalMixService service = null!;
    private bool publish;
    private string filter = "All";
    private string protocol = "subsonic";
    private readonly Dictionary<string, Song> songs = [];

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        await using var db = factory.CreateDbContext();
        foreach (var id in new[] { owner, other }) db.Users.Add(new()
        { Id = id, BackendType = protocol, BackendInstanceId = "main", BackendPrincipalId = id.ToString(), IsAdmin = id == owner });
        await db.SaveChangesAsync();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        settings.Setup(item => item.GetAsync(PersonalMixService.PublishKey, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new EffectiveRuntimeSetting(PersonalMixService.PublishKey,
                RuntimeSettingValueType.Boolean, publish, publish.ToString(), RuntimeSettingOrigin.Durable, 1, null, null, null)));
        settings.Setup(item => item.GetPreferencesAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new PersonalListeningPreferences(new(filter), new(), false, "1")));
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<allstarr.Core.Capabilities.ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "Lossless", .07)
            { Preferences = new(filter) }));
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult(songs.GetValueOrDefault(id)));
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .Returns((ProtocolExecutionContext _, Song song) => Task.FromResult(song.Title != "Unavailable"));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        tags = new(factory, clock);
        taste = new(factory, tags, clock);
        service = NewService();
    }

    private PersonalMixService NewService() => new(factory, clock, tags, taste,
        new MusicMixService(gateway.Object), gateway.Object,
        new NewReleasesService(factory, clock, new Mock<IProviderRouter>().Object, settings.Object),
        settings.Object, policies.Object, access.Object);

    private ProtocolExecutionContext Context(Guid user) => new(Enum.Parse<ProtocolKind>(protocol, true), "main", user.ToString(),
        new(user, protocol, "main", user.ToString(), "Listener", user == owner), "mix-test", clock.UtcNow.AddMinutes(5), default);

    private async Task Seed(int count = 40, int plays = 5, int age = 1, Guid? user = null)
    {
        await using var db = factory.CreateDbContext();
        var events = new List<ListeningEventRecord>();
        for (var index = 0; index < count; index++)
        {
            var recording = Guid.NewGuid();
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = user ?? owner });
            var id = index.ToString();
            var song = new Song
            {
                Id = "ext-deezer-song-" + id,
                ExternalProvider = "deezer",
                ExternalId = id,
                CanonicalRecordingId = recording,
                Artist = "Artist " + index,
                Title = "Song " + index,
                ExplicitContentLyrics = 0,
                Album = "Album",
                Duration = 180
            };
            songs[id] = song;
            for (var play = 0; play < plays; play++) events.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user ?? owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                OccurrenceKey = Guid.NewGuid().ToString(),
                State = ListeningEventState.Completed,
                TrackReference = song.Id,
                CanonicalRecordingId = recording,
                Title = song.Title,
                Artist = song.Artist,
                ListenedAt = clock.UtcNow.AddDays(-age),
                UpdatedAt = clock.UtcNow.AddDays(-age),
                SourceKind = "import",
                ImportProvenance = "history",
                DurationMilliseconds = 180000
            });
        }
        await db.SaveChangesAsync();
        db.ListeningEvents.AddRange(events);
        await db.SaveChangesAsync();
        foreach (var song in songs.Values) await tags.SaveAsync(SharedTagStore.RecordingKey(song.CanonicalRecordingId!.Value),
            [new("provider", [new(int.Parse(song.ExternalId!) % 2 == 0 ? "rock" : "soul")], clock.UtcNow)], [], false, default);
    }

    [Fact]
    public async Task DefaultDryRun_StoresEvidenceWithoutPublishingAndIsolatesListeners()
    {
        await Seed();
        var mixes = await service.GenerateAsync(owner, token: default);
        var repeat = mixes.Single(item => item.Kind == "on-repeat");
        Assert.Equal(30, repeat.Songs.Count);
        Assert.All(repeat.Songs, item => { Assert.Equal(repeat.Id, item.MixId); Assert.Equal(repeat.Generation, item.Generation); Assert.NotEmpty(item.Reason); });
        Assert.Empty(await service.PublishedAsync(Context(owner), default));
        Assert.All(await NewService().ListAsync(other, token: default), item => Assert.Empty(item.Songs));
        Assert.Equal(repeat.Songs.Select(item => item.Song.Id), (await NewService().ListAsync(owner, token: default))
            .Single(item => item.Kind == "on-repeat").Songs.Select(item => item.Song.Id));
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.PlaylistLinks.ToArrayAsync());
        publish = true;
        Assert.Empty(await service.PublishedAsync(Context(owner), default));
        await service.SetInAppsAsync(owner, repeat.Id, true, default);
        Assert.Single(await service.PublishedAsync(Context(owner), default));
    }

    [Fact]
    public async Task NewMixes_DefaultToHiddenAndRefreshPreservesListenerChoices()
    {
        Assert.All(await service.ListAsync(owner), mix => Assert.False(mix.ShowInApps));
        await Seed();
        var generated = await service.GenerateAsync(owner);
        Assert.All(generated, mix => Assert.False(mix.ShowInApps));
        var repeat = generated.Single(mix => mix.Kind == "on-repeat");
        await service.SetInAppsAsync(owner, repeat.Id, true, default);
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var refreshed = await NewService().GenerateAsync(owner);
        Assert.True(refreshed.Single(mix => mix.Id == repeat.Id).ShowInApps);
        Assert.All(refreshed.Where(mix => mix.Id != repeat.Id), mix => Assert.False(mix.ShowInApps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyVisibility_IsResetOnceAndPersistedBeforeListOrPublishedReads(bool publishedRead)
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(mix => mix.Kind == "on-repeat");
        var customId = Guid.NewGuid();
        var hiddenCustomId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
            var document = JsonNode.Parse(row.PayloadJson)!.AsObject();
            document.Remove("AppsOptInVersion");
            var mixes = document["Mixes"]!.AsObject();
            foreach (var mix in mixes) mix.Value!["ShowInApps"] = true;
            mixes[customId.ToString()] = JsonSerializer.SerializeToNode(repeat with { Id = customId, Kind = "custom", ShowInApps = true });
            mixes[hiddenCustomId.ToString()] = JsonSerializer.SerializeToNode(repeat with { Id = hiddenCustomId, Kind = "custom", ShowInApps = false });
            row.Id = Guid.NewGuid();
            row.SnapshotVersion++;
            row.PayloadJson = document.ToJsonString();
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        publish = true;
        if (publishedRead)
            Assert.Equal(customId, Assert.Single(await NewService().PublishedAsync(Context(owner), default)).LinkId);
        var loaded = await NewService().ListAsync(owner);
        Assert.All(loaded.Where(mix => mix.Kind != "custom"), mix => Assert.False(mix.ShowInApps));
        Assert.True(loaded.Single(mix => mix.Id == customId).ShowInApps);
        Assert.False(loaded.Single(mix => mix.Id == hiddenCustomId).ShowInApps);
        await using (var db = factory.CreateDbContext())
        {
            var rows = await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "personal-mixes").OrderBy(item => item.SnapshotVersion).ToArrayAsync();
            Assert.Equal(3, rows.Length);
            var saved = JsonNode.Parse(rows[^1].PayloadJson)!;
            Assert.Equal(1, saved["AppsOptInVersion"]!.GetValue<int>());
            Assert.False(saved["Mixes"]!["on-repeat"]!["ShowInApps"]!.GetValue<bool>());
        }
        await service.SetInAppsAsync(owner, repeat.Id, true, default);
        Assert.True((await NewService().ListAsync(owner)).Single(mix => mix.Id == repeat.Id).ShowInApps);
        Assert.Contains(await service.PublishedAsync(Context(owner), default), mix => mix.LinkId == repeat.Id);
        await using var read = factory.CreateDbContext();
        Assert.Equal(4, await read.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "personal-mixes"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerResponses_ExposeAppsAllowedAndKillSwitchHidesOptedInMixes(bool allowed)
    {
        await Seed(user: other);
        var repeat = (await service.GenerateAsync(other)).Single(mix => mix.Kind == "on-repeat");
        await service.SetInAppsAsync(other, repeat.Id, true, default);
        publish = allowed;
        var controller = Controller(other, false);
        var list = Assert.IsType<OkObjectResult>(await controller.List());
        var cards = JsonSerializer.SerializeToElement(list.Value);
        Assert.All(cards.EnumerateArray(), card => Assert.Equal(allowed, card.GetProperty("appsAllowed").GetBoolean()));
        var get = Assert.IsType<OkObjectResult>(await controller.Get(repeat.Id, default));
        Assert.Equal(allowed, JsonSerializer.SerializeToElement(get.Value).GetProperty("mix").GetProperty("appsAllowed").GetBoolean());
        Assert.Equal(allowed ? 1 : 0, (await service.PublishedAsync(Context(other), default)).Count);
    }

    [Theory]
    [InlineData("subsonic")]
    [InlineData("jellyfin")]
    public async Task Publishing_UsesStableVirtualIdsAndReplacesContents(string backend)
    {
        protocol = backend;
        await using (var db = factory.CreateDbContext())
        {
            foreach (var user in await db.Users.ToArrayAsync()) user.BackendType = backend;
            await db.SaveChangesAsync();
        }
        await Seed();
        publish = true;
        var first = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        await service.SetInAppsAsync(owner, first.Id, true, default);
        var virtualization = new PlaylistVirtualizationService(factory, new(factory, access.Object), access.Object, personalMixes: service);
        var firstModel = await virtualization.ReadAsync(Context(owner), PlaylistVirtualizationService.CreateProtocolId(first.Id));
        Assert.NotNull(firstModel);
        Assert.Equal(first.Generation, firstModel.MixCover!.Generation);
        Assert.Equal(first.Id.ToString(), firstModel.MixCover.Id);
        Assert.Equal(30, firstModel.Tracks.Count);
        await AssertProtocolCount(virtualization, firstModel.ProtocolId, 30);
        clock.UtcNow += TimeSpan.FromMinutes(16);
        songs["0"].Title = "Unavailable";
        await service.GenerateAsync(owner, first.Id);
        var second = await virtualization.ReadAsync(Context(owner), firstModel.ProtocolId);
        Assert.Equal(firstModel.ProtocolId, second!.ProtocolId);
        Assert.NotEqual(MixCoverImageService.GetTag(firstModel.MixCover!), MixCoverImageService.GetTag(second.MixCover!));
        Assert.DoesNotContain(second.Tracks, item => item.BackendItemId == songs["0"].Id);
        await AssertProtocolCount(virtualization, second.ProtocolId, 30);
        Assert.Single(await virtualization.ListAsync(Context(owner)), item => item.LinkId == first.Id);
        Assert.Null(await virtualization.ReadAsync(Context(other), firstModel.ProtocolId));
        await service.SetInAppsAsync(owner, first.Id, false, default);
        Assert.Null(await virtualization.ReadAsync(Context(owner), firstModel.ProtocolId));
        await service.SetInAppsAsync(owner, first.Id, true, default);
        Assert.NotNull(await virtualization.ReadAsync(Context(owner), firstModel.ProtocolId));
        publish = false;
        Assert.Empty(await virtualization.ListAsync(Context(owner)));
    }

    private async Task AssertProtocolCount(IPlaylistVirtualizationService virtualization, string id, int count)
    {
        if (protocol == "jellyfin")
        {
            var adapter = new JellyfinVirtualPlaylistProtocolAdapter(virtualization, new Mock<IJellyfinPlaylistMutationResolver>().Object,
                providerGateway: gateway.Object);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(Context(owner), id, default)).Value));
            Assert.Equal(count, json.RootElement.GetProperty("Items").GetArrayLength());
        }
        else
        {
            var adapter = new SubsonicVirtualPlaylistProtocolAdapter(virtualization, new Mock<ISubsonicPlaylistMutationResolver>().Object, gateway.Object);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(await adapter.ReadAsync(Context(owner), id, "json", default)).Value));
            Assert.Equal(count, json.RootElement.GetProperty("subsonic-response").GetProperty("playlist").GetProperty("entry").GetArrayLength());
        }
    }

    [Fact]
    public async Task ForcedDryRun_RemainsUnpublishedEvenWithHouseholdPublishingEnabled()
    {
        await Seed();
        publish = true;
        await service.GenerateAsync(owner, dryRun: true);
        Assert.Empty(await service.PublishedAsync(Context(owner), default));
    }

    [Fact]
    public async Task Preview_PreservesPublishedContentsAndGeneration()
    {
        await Seed();
        publish = true;
        var before = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        await service.SetInAppsAsync(owner, before.Id, true, default);
        songs["0"].Title = "Unavailable";
        await service.GenerateAsync(owner, dryRun: true);
        var after = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat");
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Songs.Select(item => item.Song.Id), after.Songs.Select(item => item.Song.Id));
        Assert.Single(await service.PublishedAsync(Context(owner), default));
    }

    private async Task NewPicks(int count = 100)
    {
        var picks = new List<Song>();
        await using var db = factory.CreateDbContext();
        for (var index = 1000; index < 1000 + count; index++)
        {
            var id = Guid.NewGuid();
            db.CanonicalRecordings.Add(new() { Id = id, CreatedByUserId = owner });
            picks.Add(new()
            {
                Id = "ext-deezer-song-" + index,
                ExternalId = index.ToString(),
                ExternalProvider = "deezer",
                Title = "New " + index,
                Artist = "New artist " + index,
                CanonicalRecordingId = id,
                ExplicitContentLyrics = 0
            });
        }
        await db.SaveChangesAsync();
        foreach (var song in picks) await tags.SaveAsync(SharedTagStore.RecordingKey(song.CanonicalRecordingId!.Value),
            [new("provider", [new(int.Parse(song.ExternalId!) % 2 == 0 ? "rock" : "soul")], clock.UtcNow)], [], false, default);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), It.IsAny<int>()))
            .Returns((ProtocolExecutionContext _, string _, string id, int _) => Task.FromResult(picks.Skip(int.Parse(id)).Take(50).ToList()));
    }

    [Fact]
    public async Task DailyMixes_AreDisjointAndKeepTheirRatioOnIndividualRefresh()
    {
        await Seed();
        await NewPicks();
        var mixes = (await service.GenerateAsync(owner)).Where(item => item.Kind.StartsWith("daily-", StringComparison.Ordinal) && item.Readiness.Ready).ToArray();
        Assert.Equal(2, mixes.Length);
        var ids = mixes.SelectMany(item => item.Songs).Select(item => item.Song.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(mixes, mix => Assert.Equal(mix.Songs.Count * .7, mix.Songs.Count(item => !item.Song.Title.StartsWith("New", StringComparison.Ordinal))));
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var refreshed = (await service.GenerateAsync(owner, mixes[0].Id)).Where(item => item.Kind.StartsWith("daily-", StringComparison.Ordinal)).ToArray();
        var next = refreshed.SelectMany(item => item.Songs).Select(item => item.Song.Id).ToArray();
        Assert.Equal(next.Length, next.Distinct().Count());
    }

    [Fact]
    public async Task Discovery_DoesNotRepeatPreviousPicksOrAnyListeningOccurrence()
    {
        await Seed();
        await NewPicks();
        await using (var db = factory.CreateDbContext())
        {
            db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                OccurrenceKey = "heard",
                TrackReference = "ext-deezer-song-1000",
                State = ListeningEventState.Abandoned,
                UpdatedAt = clock.UtcNow.AddDays(-20)
            });
            await db.SaveChangesAsync();
        }
        var first = (await service.GenerateAsync(owner)).Single(item => item.Kind == "discover-weekly");
        Assert.Equal(30, first.Songs.Count);
        Assert.DoesNotContain(first.Songs, item => item.Song.ExternalId == "1000");
        clock.UtcNow += TimeSpan.FromDays(7);
        var next = (await service.GenerateAsync(owner, first.Id)).Single(item => item.Id == first.Id);
        Assert.Empty(first.Songs.Select(item => item.Song.Id).Intersect(next.Songs.Select(item => item.Song.Id)));
    }

    [Fact]
    public async Task Radar_UsesRecentAlbumsFromFrequentlyPlayedArtists()
    {
        await Seed();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist 0", 0, 0, 1, null))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult
            {
                Artists = [new Artist { Id = "ext-deezer-artist-artist",
                Name = "Artist 0", ExternalProvider = "deezer", ExternalId = "artist" }]
            });
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "artist"))
            .ReturnsAsync([new Album { Id = "fresh", ExternalId = "fresh", ReleaseDate = "2026-10-28" },
                new Album { Id = "old", ExternalId = "old", ReleaseDate = "2026-09-01" },
                new Album { Id = "future", ExternalId = "future", ReleaseDate = "2026-11-01" }]);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "fresh"))
            .ReturnsAsync(new Album
            {
                Songs = [new Song { Id = "ext-deezer-song-new", ExternalProvider = "deezer", ExternalId = "new",
                Title = "New release", Artist = "Artist 0", ExplicitContentLyrics = 0 }]
            });
        var radar = (await service.GenerateAsync(owner)).Single(item => item.Kind == "release-radar");
        Assert.Equal("New release", Assert.Single(radar.Songs).Song.Title);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "old"), Times.Never);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "future"), Times.Never);
    }

    [Fact]
    public async Task ImportedMetadata_IsResolvedThroughExistingPlayableSearch()
    {
        await Seed(count: 10);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in await db.ListeningEvents.ToArrayAsync())
            {
                play.TrackReference = "history:" + play.Title;
                play.CanonicalRecordingId = null;
            }
            await db.SaveChangesAsync();
        }
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 3))
            .Returns((ProtocolExecutionContext _, string query, int _) => Task.FromResult<IReadOnlyList<Song>>(
                songs.Values.Where(song => query == song.Title + " " + song.Artist).ToArray()));
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        Assert.Equal(10, repeat.Songs.Count);
        Assert.All(repeat.Songs, item => Assert.StartsWith("ext-deezer-song-", item.Song.Id));
    }

    [Fact]
    public async Task Rewind_IncludesOldHeartsWithoutHeavyPlayCounts()
    {
        await Seed(count: 1, plays: 1, age: 200);
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var job = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("favorite.change", "heart", new { }, owner));
        await using (var db = factory.CreateDbContext())
        {
            var accepted = new FavoriteEventRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendPrincipalId = owner.ToString(),
                ItemId = songs["0"].Id,
                Operation = FavoriteOperation.Favorite,
                EventKey = "heart",
                JobId = job.JobId,
                CreatedAt = clock.UtcNow.AddDays(-200)
            };
            db.FavoriteEvents.Add(accepted);
            db.FavoriteStates.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                ItemId = songs["0"].Id,
                LastEventId = accepted.Id,
                IsFavorite = true,
                DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, songs["0"].Id, "song", "Song 0", ["Artist 0"]),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
            await db.SaveChangesAsync();
        }
        var rewind = (await service.GenerateAsync(owner)).Single(item => item.Kind == "rewind");
        Assert.Equal(songs["0"].Id, Assert.Single(rewind.Songs).Song.Id);
    }

    [Fact]
    public async Task Refresh_IsRateLimitedAcrossRestarts()
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().GenerateAsync(owner, repeat.Id));
    }

    [Theory]
    [InlineData("notForMe", "song", "less")]
    [InlineData("neverArtist", "artist", "never")]
    [InlineData("moreLikeThis", "tag", "more")]
    public async Task Feedback_TeachesTasteAndRemovesSong(string kind, string nudgeKind, string mode)
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner, timeZoneId: "America/New_York")).Single(item => item.Kind == "on-repeat");
        var song = repeat.Songs[0].Song;
        await service.FeedbackAsync(owner, repeat.Id, new(song.Id, kind, true), default);
        var nudge = Assert.Single((await taste.GetAsync(owner)).Nudges);
        Assert.Equal(nudgeKind, nudge.Kind);
        Assert.Equal(mode, nudge.Mode);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T04:00:00Z"), nudge.ExpiresAt);
        Assert.DoesNotContain((await service.ListAsync(owner)).Single(item => item.Id == repeat.Id).Songs, item => item.Song.Id == song.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.FeedbackAsync(other, repeat.Id, new(song.Id, kind, false), default));
    }

    [Fact]
    public async Task Filters_ApplyCleanOnlyNeverUnplayableAndRecentEarlySkips()
    {
        await Seed();
        filter = "CleanOnly";
        songs["0"].ExplicitContentLyrics = 1;
        songs["1"].ExplicitContentLyrics = null;
        songs["2"].Title = "Unavailable";
        await taste.PutNudgeAsync(owner, new("artist", "Artist 3", "never"));
        await using (var db = factory.CreateDbContext())
        {
            db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                OccurrenceKey = "skip",
                TrackReference = songs["4"].Id,
                CanonicalRecordingId = songs["4"].CanonicalRecordingId,
                State = ListeningEventState.Skipped,
                UpdatedAt = clock.UtcNow,
                PositionTicks = TimeSpan.FromSeconds(10).Ticks,
                DurationMilliseconds = 180000
            });
            await db.SaveChangesAsync();
        }
        var all = (await service.GenerateAsync(owner)).SelectMany(item => item.Songs).ToArray();
        Assert.NotEmpty(all);
        Assert.DoesNotContain(all, item => new[] { "0", "1", "2", "3", "4" }.Contains(item.Song.ExternalId));
    }

    [Fact]
    public async Task ExistingGenerations_RespectNewBlocksAndCleanPolicy()
    {
        await Seed();
        publish = true;
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        await service.SetInAppsAsync(owner, repeat.Id, true, default);
        await taste.PutNudgeAsync(owner, new("artist", repeat.Songs[0].Song.Artist, "never"));
        var blocked = repeat.Songs[0].Song.Id;
        var visible = Assert.Single(await service.PublishedAsync(Context(owner), default));
        Assert.DoesNotContain(visible.Tracks, item => item.BackendItemId == blocked);
        Assert.DoesNotContain((await service.ListAsync(owner)).Single(item => item.Id == repeat.Id).Songs, item => item.Song.Id == blocked);
    }

    private MixesController Controller(Guid user, bool administrator)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = user.ToString(),
            UserName = "Listener",
            AllstarrUserId = user,
            BackendType = "Subsonic",
            BackendInstanceId = "main",
            IsAdministrator = administrator,
            JellyfinAccessToken = "",
            ExpiresAtUtc = clock.UtcNow.AddHours(1).UtcDateTime
        };
        return new(service, settings.Object, factory, clock) { ControllerContext = new() { HttpContext = http } };
    }

    [Fact]
    public async Task Api_IsListenerScopedWithExplicitAdministratorPreviewAndSettingAccess()
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        var listener = Controller(other, false);
        Assert.IsType<NotFoundResult>(await listener.Get(repeat.Id, default));
        Assert.IsType<NotFoundResult>(await listener.InApps(repeat.Id, new(false), default));
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await listener.DryRun(owner)).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await listener.SetPublishSetting(new(true), default)).StatusCode);
        Assert.IsType<OkObjectResult>(await Controller(owner, true).List());
        Assert.IsType<OkObjectResult>(await Controller(owner, true).DryRun(other));
        Assert.Equal("true", RuntimeSettingCatalog.Require(PersonalMixService.PublishKey).DefaultValue);
    }

    [Theory]
    [InlineData("2026-03-07T10:00:00Z", "2026-03-08T08:00:00Z")]
    [InlineData("2026-10-31T10:00:00Z", "2026-11-01T09:00:00Z")]
    public async Task Schedules_UseListenerFourAmAcrossDaylightSaving(string now, string next)
    {
        clock.UtcNow = DateTimeOffset.Parse(now);
        await service.ListAsync(owner, "America/New_York");
        await service.EnsureSchedulesAsync(default);
        await using var db = factory.CreateDbContext();
        var schedules = await db.JobSchedules.Where(item => item.OwnerUserId == owner).ToArrayAsync();
        Assert.Equal(10, schedules.Length);
        Assert.All(schedules.Where(item => item.CronExpression == "0 4 * * *"), item => Assert.Equal(DateTimeOffset.Parse(next), item.NextRunAt));
        Assert.Equal(7, schedules.Count(item => item.CronExpression == "0 4 * * 1"));
        Assert.Single(schedules, item => item.CronExpression == "0 4 * * 5");
        clock.UtcNow = DateTimeOffset.Parse(next);
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var result = await new DurableScheduleEngine(factory, queue, clock).TickAsync();
        Assert.True(result.Enqueued >= 2);
        Assert.Equal(2, await db.Jobs.CountAsync(item => item.OwnerUserId == owner));
        Assert.Equal(0, (await new DurableScheduleEngine(factory, queue, clock).TickAsync()).Enqueued);
    }

    public async Task DisposeAsync() => await database.DisposeAsync();
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-29T12:00:00Z"); }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
