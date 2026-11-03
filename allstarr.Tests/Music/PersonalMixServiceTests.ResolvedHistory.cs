using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Theory]
    [InlineData("on-repeat", 1, 12)]
    [InlineData("rewind", 200, 12)]
    [InlineData("on-repeat", 1, 80)]
    public async Task Resolved_history_routes_fill_service_mixes_without_spending_search_reads(string kind, int age, int unresolved)
    {
        await Seed(count: 120 + unresolved, plays: 5, age: age);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult<Song?>(new()
            {
                Id = "ext-spotify-song-" + id,
                ExternalProvider = "spotify",
                ExternalId = id,
                CanonicalRecordingId = Guid.Parse(id),
                Title = "Stored display",
                Artist = "Stored artist"
            }));
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .Returns((ProtocolExecutionContext _, Song song) => Task.FromResult(song.IsLocal || song.ExternalProvider == "deezer"));
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in await db.ListeningEvents.ToArrayAsync())
            {
                play.DurationMilliseconds = null;
                if (int.Parse(play.Title!["Song ".Length..]) >= 120)
                {
                    play.TrackReference = "history:" + play.Title;
                    play.CanonicalRecordingId = null;
                    play.Album = "Album (Deluxe Edition)";
                }
            }
            for (var index = 0; index < 11; index++)
            {
                var track = new LibraryTrackRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    BackendLibraryId = "music",
                    BackendItemId = "library-" + index,
                    Title = "Library song " + index,
                    Artist = "Library artist " + index
                };
                db.LibraryTracks.Add(track);
                for (var play = 0; play < 5; play++) db.ListeningEvents.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    OccurrenceKey = Guid.NewGuid().ToString(),
                    TrackReference = track.BackendItemId,
                    LibraryTrackId = track.Id,
                    Title = track.Title,
                    Artist = track.Artist,
                    SourceKind = "protocol",
                    State = ListeningEventState.Completed,
                    ListenedAt = clock.UtcNow.AddDays(-age),
                    UpdatedAt = clock.UtcNow.AddDays(-age)
                });
            }
            await db.SaveChangesAsync();
        }
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 3))
            .Returns((ProtocolExecutionContext _, string _, string query, int _) => Task.FromResult(
                ProviderOutcome<IReadOnlyList<Song>>.Success(songs.Values.Where(song => int.Parse(song.ExternalId!) >= 120 &&
                    query == song.Title + " " + song.Artist).ToArray())));
        var logger = new MixLogger();
        service = NewService(logger);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == kind).Id;

        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);

        output.WriteLine($"{kind}: shown {mix.Songs.Count}, qualified {mix.Evidence!.Qualified}, services {mix.Evidence.FromServices}, reads {mix.Evidence.ProviderReads}; {Assert.Single(logger.Entries).Message}");
        Assert.Equal(50, mix.Songs.Count);
        var reads = Math.Min(unresolved, MixRecipes.HistoryLookupReads);
        Assert.Equal(131 + reads, mix.Evidence.Qualified);
        Assert.Equal(reads, mix.Evidence.ProviderReads);
        Assert.Equal(kind == "rewind" ? 50 : 39, mix.Evidence.FromServices);
        Assert.Equal(kind == "rewind" ? 0 : 11, mix.Evidence.FromMusic);
        Assert.All(mix.Songs.Where(item => !item.Song.IsLocal), item => Assert.Contains(songs.Values, song => song.Id == item.Song.Id));
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 3), Times.Exactly(reads));
        Assert.All(gateway.Invocations.Where(call => call.Method.Name == "SearchAutomaticProviderSongsAsync"),
            call => Assert.True(int.Parse(((string)call.Arguments[2]).Split(' ')[1]) >= 120));
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Never);
        Assert.Contains("history.resolved from history import=120", Assert.Single(logger.Entries).Message);
        Assert.Contains("history-lookups.reads=" + reads, Assert.Single(logger.Entries).Message);
    }

    [Theory]
    [InlineData("Song 0 [A]", "Artist 0", "Album", null, "Song 0", "Artist 0", "Album", 180, true)]
    [InlineData("Song 0 [E]", "Artist 0", "Album", null, "Song 0", "Artist 0", "Album", 180, true)]
    [InlineData("Song 0 (feat. Guest)", "Guest, Artist 0", "Album", 180000L, "Song 0", "Artist 0, Guest", "Album", 180, true)]
    [InlineData("Song 0", "Artist 0", "Album (Deluxe Edition)", null, "Song 0", "Artist 0", "Album", 180, true)]
    [InlineData("Song 0 - 2011 Remaster", "Artist 0", "Album", 180000L, "Song 0", "Artist 0", "Album", 180, true)]
    [InlineData("Song 0", "Artist 0", "Album", 180000L, "Song 0", "Artist 0", "Album", 185, true)]
    [InlineData("Song 0", "Artist 0", "Album", 180000L, "Song 0", "Artist 0", "Album", 0, true)]
    [InlineData("Song 0", "Artist 0", "Album", null, "Song 0 (Live)", "Artist 0", "Album", 180, false)]
    [InlineData("Song 0", "Artist 0", "Album", null, "Song 0 (Remix)", "Artist 0", "Album", 180, false)]
    [InlineData("Song 0", "Artist 0", "Album", null, "Song 0", "Other artist", "Album", 180, false)]
    [InlineData("Song 0", "Artist 0", "Album", 180000L, "Song 0", "Artist 0", "Album", 240, false)]
    [InlineData("Song 0", "Artist 0", "Album", null, "Song 0", "Artist 0", "Other album", 180, true)]
    public async Task History_search_uses_import_matching_for_decorators_editions_and_sparse_durations(
        string title, string artist, string album, long? duration, string candidateTitle, string candidateArtist,
        string candidateAlbum, int candidateDuration, bool accepted)
    {
        await Seed(count: 1, plays: 5);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in await db.ListeningEvents.ToArrayAsync())
            {
                play.TrackReference = "history:unresolved";
                play.CanonicalRecordingId = null;
                play.Title = title;
                play.Artist = artist;
                play.Album = album;
                play.DurationMilliseconds = duration;
            }
            await db.SaveChangesAsync();
        }
        var candidate = songs["0"];
        candidate.Title = candidateTitle;
        candidate.Artist = candidateArtist;
        candidate.Album = candidateAlbum;
        candidate.Duration = candidateDuration;
        var scope = new TrackMatchScope(owner, "main", null, 1, 1, new HashSet<string>());
        var source = new ExternalTrackMatchSnapshot("history", "history", "history", MusicLyrics.StripPresentationLabels(title),
            MusicLyrics.StripPresentationLabels(artist), MusicLyrics.StripPresentationLabels(album), null, duration, null, null, null);
        var decision = new TrackMatchDecisionEngine().Decide(scope, source, [TrackMatchPlayableSearch.ToCandidate(candidate, scope)]);
        Assert.Equal(accepted, decision.State == TrackMatchReviewState.Accepted && !decision.RequiresReview);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 3))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([candidate]));
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat").Id;

        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);

        Assert.Equal(accepted ? 1 : 0, mix.Songs.Count);
    }

    [Theory]
    [InlineData("found", true)]
    [InlineData("timeout", true)]
    [InlineData("transient", true)]
    [InlineData("missing", false)]
    [InlineData("not-found", false)]
    [InlineData("removed", false)]
    public async Task Resolved_history_cache_misses_keep_routes_until_a_provider_reports_them_gone(string outcome, bool kept)
    {
        await Seed(count: 1);
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0")).ReturnsAsync((Song?)null);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0"))
            .Returns(() => outcome switch
            {
                "found" => Task.FromResult<Song?>(songs["0"]),
                "timeout" => Task.FromException<Song?>(new TaskCanceledException()),
                "transient" => Task.FromException<Song?>(new HttpRequestException()),
                "not-found" => Task.FromException<Song?>(new FileNotFoundException()),
                "removed" => Task.FromException<Song?>(new HttpRequestException("Removed", null, System.Net.HttpStatusCode.Gone)),
                _ => Task.FromResult<Song?>(null)
            });
        var logger = new MixLogger();
        service = NewService(logger);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat").Id;

        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);

        Assert.Equal(kept ? 1 : 0, mix.Songs.Count);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0"), Times.Once);
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        var log = Assert.Single(logger.Entries).Message;
        Assert.Contains("playback-checks.reads=1", log);
        Assert.DoesNotContain("history-lookups.reads=", log);
        if (!kept) Assert.Contains("history.unavailable resolved route=1", log);
    }

    [Fact]
    public async Task Resolved_history_survives_exhausted_playback_checks_without_history_searches()
    {
        await Seed(count: MixRecipes.PlaybackCheckReads + 1);
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>())).ReturnsAsync((Song?)null);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>())).ThrowsAsync(new HttpRequestException());
        var logger = new MixLogger();
        service = NewService(logger);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat").Id;

        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);

        Assert.Equal(50, mix.Songs.Count);
        Assert.Equal(121, mix.Evidence!.Qualified);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.Exactly(MixRecipes.PlaybackCheckReads));
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        Assert.Contains("playback-checks.allowance exhausted=1", Assert.Single(logger.Entries).Message);
        Assert.DoesNotContain("history-lookups.reads=", Assert.Single(logger.Entries).Message);
    }

    [Theory]
    [InlineData(ProviderIdentityVerification.Verified, "history-match", ProviderIdentityScope.Catalog, true)]
    [InlineData(ProviderIdentityVerification.Verified, "superseded", ProviderIdentityScope.Catalog, false)]
    [InlineData(ProviderIdentityVerification.Verified, "automatic-suggestion", ProviderIdentityScope.Catalog, false)]
    [InlineData(ProviderIdentityVerification.Pinned, "history-match", ProviderIdentityScope.Catalog, true)]
    [InlineData(ProviderIdentityVerification.Verified, "history-match", ProviderIdentityScope.Account, false)]
    public async Task Canonical_history_uses_only_available_verified_recording_routes(
        ProviderIdentityVerification verification, string method, ProviderIdentityScope identityScope, bool playable)
    {
        await Seed(count: 1);
        var recording = songs["0"].CanonicalRecordingId!.Value;
        await using (var db = factory.CreateDbContext())
        {
            await db.ListeningEvents.ExecuteUpdateAsync(update => update.SetProperty(item => item.TrackReference, "ext-allstarr-song-" + recording));
            Guid? accountId = null;
            if (identityScope == ProviderIdentityScope.Account)
            {
                accountId = Guid.NewGuid();
                db.ProviderAccounts.Add(new() { Id = accountId.Value, ProviderId = "deezer", OwnerUserId = other, Enabled = true });
            }
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recording,
                ProviderId = "deezer",
                ExternalId = "0",
                ExternalIdHash = ListeningHistoryImportService.Hash("0"),
                ResourceKind = ProviderResourceKind.Track,
                Scope = identityScope,
                ProviderAccountId = accountId,
                Verification = verification,
                VerificationMethod = method,
                DecisionVersion = 1
            });
            await db.SaveChangesAsync();
        }
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat").Id;

        var mix = (await service.GenerateAsync(owner, id, allowProviderReads: false)).Single(item => item.Id == id);

        Assert.Equal(playable ? 1 : 0, mix.Songs.Count);
        if (playable) Assert.Equal("ext-deezer-song-0", Assert.Single(mix.Songs).Song.Id);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Resolved_service_history_prefers_its_library_recording_beyond_unrelated_library_rows()
    {
        await Seed(count: 1);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        await using (var db = factory.CreateDbContext())
        {
            for (var index = 0; index < 601; index++) db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "music",
                BackendItemId = index == 600 ? "zz-matched" : "unrelated-" + index,
                Title = index == 600 ? "Song 0" : "Unrelated song " + index,
                Artist = index == 600 ? "Artist 0" : "Unrelated artist " + index,
                CanonicalRecordingId = index == 600 ? songs["0"].CanonicalRecordingId : null
            });
            await db.SaveChangesAsync();
        }

        var mix = (await service.GenerateAsync(owner, allowProviderReads: false)).Single(item => item.Kind == "on-repeat");

        Assert.True(Assert.Single(mix.Songs).Song.IsLocal);
        Assert.Equal("zz-matched", Assert.Single(mix.Songs).Song.Id);
    }
}
