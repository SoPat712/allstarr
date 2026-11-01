using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Enrichment;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.MusicBrainz;
using allstarr.Core.Routing;
using allstarr.Core.Releases;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    private sealed class TagChart : HttpMessageHandler
    {
        public int Reads;
        public int Tracks = 50, Artists = 25;
        public string LastTag = "";
        public List<string> Tags = [];
        public List<(string Tag, string Query)> Searches = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Reads++;
            LastTag = Uri.UnescapeDataString(request.RequestUri!.Query.Split('&').Single(part => part.StartsWith("tag=", StringComparison.Ordinal))[4..]);
            Tags.Add(LastTag);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tracks = new
                    {
                        track = Enumerable.Range(0, Tracks)
                            .Select(i => new { name = "Fresh " + i, artist = new { name = "Fresh artist " + i % Artists } })
                    }
                }))
            });
        }
    }

    private TagChart EnableChart(bool failFirst = false, int availableArtists = 25, int tracks = 50, int artists = 25, bool scopedKey = false, bool missingKey = false)
    {
        var chart = new TagChart { Tracks = tracks, Artists = artists };
        var http = new Mock<IHttpClientFactory>();
        http.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient(chart));
        var options = new Mock<IOptionsMonitor<ScrobblingSettings>>();
        var config = new ScrobblingSettings(); config.LastFm.ApiKey = scopedKey || missingKey ? "" : "fixture-key";
        options.SetupGet(x => x.CurrentValue).Returns(config);
        tagSources = new SharedTagSources(new Mock<IMusicBrainzCatalogClient>().Object, http.Object, options.Object, clock, accounts: scopedKey ? new ChartAccounts(owner) : null);
        gateway.Setup(x => x.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failFirst ? ["broken", "deezer"] : ["deezer"]);
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "broken", It.IsAny<string>(), It.IsAny<int>()))
            .ThrowsAsync(new HttpRequestException());
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), It.IsAny<int>()))
            .Returns((ProtocolExecutionContext _, string _, string query, int _) =>
            {
                chart.Searches.Add((chart.LastTag, query));
                var number = int.Parse(query.Split(' ')[^1]);
                return Task.FromResult(ProviderOutcome<IReadOnlyList<Song>>.Success((number < availableArtists ? Enumerable.Range(0, tracks).Where(i => i % artists == number) : Enumerable.Empty<int>()).Select(i => new Song
                {
                    Id = "ext-deezer-song-fresh" + i,
                    ExternalProvider = "deezer",
                    ExternalId = "fresh" + i,
                    Title = "Fresh " + i,
                    Artist = "Fresh artist " + number,
                    ExplicitContentLyrics = i == 0 ? 1 : 0,
                    Album = "Fresh",
                    Duration = 180
                }).ToArray()));
            });
        service = NewService();
        return chart;
    }

    [Fact]
    public async Task Zero_analyzed_songs_fill_Chill_and_stop_at_target_within_its_allowance()
    {
        var chart = EnableChart();
        var initial = (await service.ListAsync(owner)).Single(x => x.Kind == "chill");
        Assert.Empty(initial.Songs);
        var result = await service.GenerateAsync(owner, initial.Id);
        var chill = result.Single(x => x.Kind == "chill");
        Assert.Equal(40, chill.Songs.Count);
        Assert.Equal(40, chill.Evidence!.FromServices);
        Assert.Equal(0, chill.Evidence.FromMusic);
        Assert.All(chill.Songs, x => Assert.Contains("Last.fm tags this chill · from Deezer", x.Reason));
        var reads = gateway.Invocations.Count(x => x.Method.Name == "SearchAutomaticProviderSongsAsync") + chart.Reads;
        Assert.Equal(21, reads);
        Assert.Equal(reads, chill.Evidence.ProviderReads);
        Assert.All(result.Where(mix => mix.Evidence != null), mix => Assert.Equal(mix.Evidence!.Candidates, mix.Evidence.Shown + mix.Evidence.Excluded.Values.Sum()));
        var saved = await NewService().GetAsync(owner, chill.Id);
        Assert.Equal(JsonSerializer.Serialize(chill.Evidence), JsonSerializer.Serialize(saved.Evidence));
        output.WriteLine($"Chill: analyzed 0, music {chill.Evidence.FromMusic}, services {chill.Evidence.FromServices}, shown {chill.Songs.Count}; generation reads {reads}/240.");
    }

    [Fact]
    public async Task Provider_failure_preserves_fill_and_clean_only_and_never_apply()
    {
        await Seed(count: 3);
        foreach (var song in songs.Values) await tags.SaveAsync(SharedTagStore.RecordingKey(song.CanonicalRecordingId!.Value),
            [new("lastfm-track", [new("calm")], clock.UtcNow)], [], false, default);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        var before = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(3, before.Songs.Count);
        await service.SetInAppsAsync(owner, id, true, default);
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var chart = EnableChart(failFirst: true);
        filter = "CleanOnly";
        await taste.PutNudgeAsync(owner, new("artist", "Fresh artist 1", "never"));
        var result = await service.GenerateAsync(owner, id);
        var chill = result.Single(x => x.Kind == "chill");
        Assert.True(chill.Added);
        Assert.Equal(before.Id, chill.Id);
        Assert.All(before.Songs, song => Assert.Contains(chill.Songs, item => item.Song.Id == song.Song.Id));
        Assert.Equal(3, chill.Evidence!.FromMusic);
        Assert.Equal(25, chill.Evidence.FromServices);
        Assert.Equal(28, chill.Songs.Count);
        Assert.DoesNotContain(chill.Songs, x => x.Song.ExplicitContentLyrics != 0 || x.Song.Artist == "Fresh artist 1");
        Assert.Equal(15, chill.Evidence!.ProviderFailures);
        Assert.InRange(gateway.Invocations.Count(x => x.Method.Name == "SearchAutomaticProviderSongsAsync") + chart.Reads, 1, MixRecipes.GenerationReads);
        output.WriteLine($"Failure/clean/never: shown {chill.Songs.Count}, provider failures {chill.Evidence.ProviderFailures}, reads {chill.Evidence.ProviderReads}/240.");
    }

    [Fact]
    public async Task Library_seed_reaches_related_artists_and_fills_Uncharted_in_real_styles()
    {
        await Seed(count: 1, plays: 10);
        await using (var db = factory.CreateDbContext())
        {
            var local = new allstarr.Core.Storage.LibraryTrackRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendItemId = "native-seed",
                Title = songs["0"].Title,
                Artist = "Artist 0",
                CanonicalRecordingId = songs["0"].CanonicalRecordingId
            };
            db.LibraryTracks.Add(local);
            foreach (var play in await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync(db.ListeningEvents)) play.LibraryTrackId = local.Id;
            await db.SaveChangesAsync();
        }
        gateway.Setup(x => x.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist 0", 0, 0, 5, "deezer"))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult { Artists = [new Artist { Name = "Artist 0", ExternalId = "seed" }] });
        gateway.Setup(x => x.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 50))
            .ReturnsAsync(Enumerable.Range(0, 26).Select(i => new Artist { Name = "Related " + i, ExternalId = i.ToString(), ExternalProvider = "deezer" }).ToList());
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 100))
            .Returns((ProtocolExecutionContext _, string _, string query, int _) => Task.FromResult(ProviderOutcome<IReadOnlyList<Song>>.Success(
                query.StartsWith("Related ", StringComparison.Ordinal) ? Enumerable.Range(0, 2).Select(i => new Song
                {
                    Id = "ext-deezer-song-" + query + i,
                    ExternalProvider = "deezer",
                    ExternalId = query + i,
                    Title = "New song " + query + i,
                    Artist = query,
                    Genre = query == "Related 0" ? "country" : null,
                    ExplicitContentLyrics = 0
                }).ToArray() : [])));
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "discover-weekly").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(50, mix.Songs.Count);
        Assert.All(mix.Songs, x => Assert.Equal("Similar to Artist 0 · via Deezer", x.Reason));
        Assert.Equal(28, mix.Evidence!.ProviderReads);
        Assert.DoesNotContain(mix.Songs, song => song.Song.Artist == "Related 0");
        Assert.All(mix.Songs.GroupBy(x => x.Song.Artist), group => Assert.Equal(2, group.Count()));
        output.WriteLine($"Library seed Uncharted: {mix.Songs.Count} tracks, {mix.Songs.Select(x => x.Song.Artist).Distinct().Count()} artists, {mix.Evidence.ProviderReads}/30 reads.");
        clock.UtcNow += TimeSpan.FromMinutes(16);
        gateway.Invocations.Clear();
        await RunRefreshJob("history-tags:related");
        var kept = await service.GetAsync(owner, id);
        Assert.Equal(mix.Songs.Select(song => song.Song.Id), kept.Songs.Select(song => song.Song.Id));
        Assert.Equal(0, kept.Evidence!.ProviderReads);
        Assert.DoesNotContain(gateway.Invocations, call => call.Method.Name is "SearchAsync" or "SearchAutomaticProviderSongsAsync" or "GetSimilarArtistsAsync");
    }

    [Fact]
    public async Task Filled_tracks_require_three_early_skips_and_ignore_interrupted_occurrences()
    {
        EnableChart(availableArtists: 4, tracks: 8, artists: 4);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var index in new[] { 4, 5, 6 })
                for (var occurrence = 0; occurrence < (index == 5 ? 1 : 3); occurrence++) db.ListeningEvents.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    OccurrenceKey = index + ":" + occurrence,
                    TrackReference = "ext-deezer-song-fresh" + index,
                    Title = "Fresh " + index,
                    Artist = "Fresh artist " + index % 4,
                    State = index == 6 ? allstarr.Core.Intelligence.ListeningEventState.Abandoned : allstarr.Core.Intelligence.ListeningEventState.Skipped,
                    UpdatedAt = clock.UtcNow,
                    PositionTicks = TimeSpan.FromSeconds(10).Ticks,
                    DurationMilliseconds = 180000
                });
            await db.SaveChangesAsync();
        }
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(7, mix.Songs.Count);
        Assert.DoesNotContain(mix.Songs, item => item.Song.ExternalId == "fresh4");
        Assert.Contains(mix.Songs, item => item.Song.ExternalId == "fresh5");
        Assert.Contains(mix.Songs, item => item.Song.ExternalId == "fresh6");
        Assert.Equal(1, mix.Evidence!.Excluded["Three early skips"]);
    }


    [Fact]
    public async Task Realistic_charts_feed_six_short_recipes_without_starving_Uncharted()
    {
        await Seed(count: 1, plays: 10);
        songs["0"].Artist = "Fresh artist 59";
        await tags.SaveAsync(SharedTagStore.RecordingKey(songs["0"].CanonicalRecordingId!.Value),
            [new("provider", [new("acoustic")], clock.UtcNow)], [], false, default);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in db.ListeningEvents) play.Artist = "Fresh artist 59";
            await db.SaveChangesAsync();
        }
        var chart = EnableChart(tracks: 100, artists: 60, availableArtists: 60);
        gateway.Setup(x => x.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Fresh artist 59", 0, 0, 5, "deezer"))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult { Artists = [new Artist { Name = "Fresh artist 59", ExternalId = "seed" }] });
        gateway.Setup(x => x.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 50))
            .ReturnsAsync(Enumerable.Range(0, 25).Select(i => new Artist { Name = "Fresh artist " + i, ExternalId = i.ToString() }).ToList());
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "Fresh artist 59", 100))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([]));
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsRegex("Fresh artist .*"), 100))
            .Returns((ProtocolExecutionContext _, string _, string query, int _) => Task.FromResult(ProviderOutcome<IReadOnlyList<Song>>.Success(
                Enumerable.Range(0, 100).Where(i => i % 60 == int.Parse(query.Split(' ')[^1])).Select(i => new Song
                {
                    Id = "ext-deezer-song-fresh" + i,
                    ExternalProvider = "deezer",
                    ExternalId = "fresh" + i,
                    Title = "Fresh " + i,
                    Artist = query,
                    Genre = "acoustic",
                    Duration = 180
                }).ToArray())));
        var result = await service.GenerateAsync(owner, scheduled: true);
        foreach (var kind in new[] { "chill", "focus", "workout", "party", "sleep", "daily-1", "discover-weekly" })
            Assert.NotEmpty(result.Single(x => x.Kind == kind).Songs);
        Assert.True(result.Single(x => x.Kind == "chill").Readiness.Ready);
        Assert.Equal("Acoustic Blend", result.Single(x => x.Kind == "daily-1").Name);
        Assert.InRange(result[0].Evidence!.ProviderReads!.Value, 1, MixRecipes.GenerationReads);
        output.WriteLine($"100 tracks / 60 artists; Chill {result.Single(x => x.Kind == "chill").Songs.Count}, Uncharted {result.Single(x => x.Kind == "discover-weekly").Songs.Count}; reads {result[0].Evidence!.ProviderReads}/240.");
    }

    [Fact]
    public async Task Not_ready_recipes_spend_before_ready_recipes_that_are_short_of_target()
    {
        await Seed(count: 25);
        foreach (var song in songs.Values) await tags.SaveAsync(SharedTagStore.RecordingKey(song.CanonicalRecordingId!.Value),
            [new("lastfm-track", [new("chill")], clock.UtcNow)], [], false, default);
        var chart = EnableChart();
        var result = await service.GenerateAsync(owner, scheduled: true);
        Assert.Equal("ambient", chart.Tags[0]);
        Assert.True(result.Single(x => x.Kind == "chill").Readiness.Ready);
    }

    [Fact]
    public async Task Large_library_handles_240_reads_of_100_results_in_seconds()
    {
        var chart = EnableChart(tracks: 100, artists: 60, availableArtists: 60);
        var cards = await service.ListAsync(owner);
        var document = new PersonalMixDocument { Mixes = cards.ToDictionary(x => x.Kind, x => x with { CoverTag = "acoustic" }) };
        var own = Enumerable.Range(0, 5000).Select(i => new MixCandidate(new Song
        {
            Id = "own" + i,
            Title = "Own " + i,
            Artist = "Own artist " + i,
            IsLocal = true,
            Duration = 180
        }, new Dictionary<string, double>(), 0, 0, null, false, "library", 0)).ToArray();
        var events = Enumerable.Range(0, 60).Select(i => new allstarr.Core.Intelligence.ListeningEventRecord
        {
            Id = Guid.NewGuid(),
            Artist = "Fresh artist " + i,
            Title = "Played " + i,
            TrackReference = "played" + i,
            State = allstarr.Core.Intelligence.ListeningEventState.Completed,
            ListenedAt = clock.UtcNow
        }).ToArray();
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 100))
            .Returns((ProtocolExecutionContext _, string _, string query, int _) => Task.FromResult(ProviderOutcome<IReadOnlyList<Song>>.Success(
                Enumerable.Range(0, 100).Select(i => new Song
                {
                    Id = query + i,
                    Title = i < 60 && query == "Fresh artist " + i ? "Fresh " + i : "Unrelated " + i,
                    Artist = query,
                    ExternalProvider = "deezer",
                    Duration = 180
                }).ToArray())));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var type = typeof(PersonalMixService).GetNestedType("GenerationInputs", System.Reflection.BindingFlags.NonPublic)!;
        var inputs = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetField("Playable", flags)!.SetValue(inputs, new[] { "deezer" });
        await using var db = factory.CreateDbContext();
        var user = await db.Users.FindAsync(owner);
        var kinds = new[] { "daily-1", "daily-2", "daily-3", "daily-4", "chill", "focus", "workout", "party", "sleep" };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var filled = await (Task<IReadOnlyList<MixCandidate>>)typeof(PersonalMixService).GetMethod("FillAsync", flags)!.Invoke(service,
            [user!, own, events, inputs, kinds, document, new Dictionary<string, allstarr.Core.AudioMuse.AudioMuseScores>(), CancellationToken.None])!;
        Assert.Equal(240, (int)type.GetField("Reads", flags)!.GetValue(inputs)!);
        var reads = (Dictionary<string, int>)type.GetField("RecipeReads", flags)!.GetValue(inputs)!;
        Assert.All(reads.Values, count => Assert.InRange(count, 1, 30));
        Assert.Equal(240, chart.Reads + gateway.Invocations.Count(x => x.Method.Name == "SearchAutomaticProviderSongsAsync"));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"Large fill took {timer.Elapsed}.");
        Assert.True(filled.Count >= 5000);
        output.WriteLine($"5,000 own songs; 240 reads × 100 results; fill completed in {timer.Elapsed.TotalSeconds:F2}s.");
    }

    [Fact]
    public async Task Chart_searches_put_played_artists_before_related_artists_then_chart_order()
    {
        await Seed(count: 1, plays: 10);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in db.ListeningEvents) play.Artist = "Fresh artist 59";
            await db.SaveChangesAsync();
        }
        var chart = EnableChart(tracks: 100, artists: 60, availableArtists: 60);
        gateway.Setup(x => x.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Fresh artist 59", 0, 0, 5, "deezer"))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult { Artists = [new Artist { Name = "Fresh artist 59", ExternalId = "seed" }] });
        gateway.Setup(x => x.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 50))
            .ReturnsAsync([new Artist { Name = "Fresh artist 58", ExternalId = "related" }]);
        await service.GenerateAsync(owner, scheduled: true);
        Assert.Equal(["Fresh artist 59", "Fresh artist 58", "Fresh artist 0"], chart.Searches.Where(x => x.Tag == "chill").Take(3).Select(x => x.Query));
    }

    [Fact]
    public async Task Mood_reasons_use_registry_display_names_for_unfamiliar_provider_ids()
    {
        EnableChart();
        const string providerId = "spotiflac-qobuz-web";
        gateway.Setup(x => x.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([providerId]);
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), providerId, It.IsAny<string>(), 100))
            .Returns((ProtocolExecutionContext context, string _, string query, int count) =>
                gateway.Object.SearchAutomaticProviderSongsAsync(context, "deezer", query, count).ContinueWith(task =>
                {
                    foreach (var song in task.Result.RequireValue()) song.ExternalProvider = providerId;
                    return task.Result;
                }, TaskScheduler.Default));
        ProviderDescriptor? descriptor = new(providerId, "Deezer", "Fixture", ProviderOrigin.Extension, "1", "1", [], new([]));
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(x => x.TryGet(providerId, out descriptor)).Returns(true);
        service = new(factory, clock, tags, taste, new MusicMixService(gateway.Object), gateway.Object,
            new NewReleasesService(factory, clock, new Mock<IProviderRouter>().Object, settings.Object),
            settings.Object, policies.Object, access.Object, tagSources: tagSources, registry: registry.Object);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.NotEmpty(mix.Songs);
        Assert.All(mix.Songs, song => { Assert.Contains("from Deezer", song.Reason); Assert.DoesNotContain(providerId, song.Reason); });
        Assert.DoesNotContain(gateway.Invocations, x => x.Method.Name is "GetSongAsync" or "CanPlaySongAsync");
    }

    [Fact]
    public async Task Scheduled_daily_blends_do_not_read_mood_charts_or_regenerate_moods()
    {
        await Seed(count: 1);
        EnableChart();
        var cards = await service.ListAsync(owner);
        clock.UtcNow = new DateTimeOffset(2026, 11, 3, 4, 0, 0, TimeSpan.Zero);
        var result = await service.GenerateAsync(owner, cards.Single(x => x.Kind == "daily-1").Id, scheduled: true);
        Assert.All(result.Where(x => PersonalMixMoods.Kinds.Contains(x.Kind)), mix => Assert.Equal(0, mix.Generation));
        Assert.DoesNotContain(gateway.Invocations, x => x.Method.Name == "SearchAutomaticProviderSongsAsync" &&
            x.Arguments[2] is string query && query.StartsWith("Fresh artist", StringComparison.Ordinal));
        Assert.All(PersonalMixMoods.Kinds, kind => Assert.Equal("0 5 * * 1", PersonalMixRules.Cron(kind)));
    }

    [Fact]
    public async Task Historical_rotation_saves_the_2025_interval_and_counts_live_contributions_once()
    {
        await Seed(count: 60, age: 365);
        await using (var db = factory.CreateDbContext())
        {
            for (var index = 0; index < 4; index++) db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                OccurrenceKey = "live-" + index,
                TrackReference = songs["0"].Id,
                Title = songs["0"].Title,
                Artist = songs["0"].Artist,
                CanonicalRecordingId = songs["0"].CanonicalRecordingId,
                SourceKind = "protocol",
                State = allstarr.Core.Intelligence.ListeningEventState.Completed,
                ListenedAt = clock.UtcNow.AddHours(-1),
                UpdatedAt = clock.UtcNow.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "on-repeat").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Equal(50, mix.Songs.Count);
        Assert.Equal(4, mix.Evidence!.PrimaryPlays);
        Assert.Equal(300, mix.Evidence.FallbackPlays);
        Assert.Contains("2025", mix.Evidence.HistoricalWindow);
        Assert.All(mix.Songs, song => { Assert.Contains("2025", song.Reason); Assert.DoesNotContain("this month", song.Reason); });
        Assert.StartsWith("Played 9 times", mix.Songs[0].Reason);
        await taste.SetUseImportedHistoryAsync(owner, false);
        clock.UtcNow += TimeSpan.FromMinutes(16);
        mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.Single(mix.Songs);
        Assert.Equal(0, mix.Evidence!.ImportedPlays);
        Assert.Null(mix.Evidence.HistoricalWindow);
    }

    [Fact]
    public async Task No_enabled_provider_keeps_the_honest_mood_message_and_saved_partial_playlist()
    {
        await Seed(count: 3);
        foreach (var song in songs.Values) await tags.SaveAsync(SharedTagStore.RecordingKey(song.CanonicalRecordingId!.Value),
            [new("provider", [new("calm")], clock.UtcNow)], [], false, default);
        var id = (await service.ListAsync(owner)).Single(x => x.Kind == "chill").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        publish = true;
        await service.SetInAppsAsync(owner, id, true, default);
        Assert.Single(await service.PublishedAsync(Context(owner), default));
        gateway.Setup(x => x.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        clock.UtcNow += TimeSpan.FromMinutes(16);
        mix = (await service.GenerateAsync(owner, id)).Single(x => x.Id == id);
        Assert.False(mix.Readiness.Ready);
        Assert.Contains("25 more", mix.Readiness.Message);
        Assert.True(mix.Added);
        Assert.Equal(id, mix.Id);
        Assert.Equal(0, mix.Evidence!.ProviderReads);
        var published = Assert.Single(await service.PublishedAsync(Context(owner), default));
        Assert.Equal(id, published.LinkId);
        Assert.Empty(published.Tracks);
    }

    [Fact]
    public async Task Page_reads_make_zero_provider_calls_and_legacy_evidence_is_unknown()
    {
        EnableChart();
        var cards = await service.ListAsync(owner);
        await service.GetAsync(owner, cards[0].Id);
        Assert.All(cards, x => Assert.Null(x.Evidence));
        Assert.DoesNotContain(gateway.Invocations, x => x.Method.Name is "SearchAutomaticProviderSongsAsync" or "GetSongAsync" or "GetSimilarArtistsAsync");
    }
}
