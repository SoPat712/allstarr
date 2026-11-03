using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task Final_candidate_limit_is_not_reported_as_changed_listener_choices()
    {
        EnableChart(availableArtists: 13, tracks: 26, artists: 13);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        await service.GenerateAsync(owner, allowProviderReads: false);
        await using (var db = factory.CreateDbContext())
        {
            for (var index = 0; index < 600; index++) db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "music",
                BackendItemId = "library-" + index,
                Title = "Library song " + index,
                Artist = "Library artist " + index
            });
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().OrderByDescending(item => item.SnapshotVersion).FirstAsync();
            var payload = JsonNode.Parse(row.PayloadJson)!;
            payload["FillSongs"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 500).Select(index => new MixFillSong(new Song
            {
                Id = "ext-deezer-song-saved-" + index,
                ExternalProvider = "deezer",
                ExternalId = "saved-" + index,
                Title = "Saved song " + index,
                Artist = "Saved artist " + index
            }, clock.UtcNow, "deezer", true, [], [], null, null, null, null)));
            row.Id = Guid.NewGuid(); row.SnapshotVersion++; row.PayloadJson = payload.ToJsonString();
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "workout").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);
        Assert.Equal(26, mix.Evidence!.Qualified);
        Assert.Equal(26, mix.Evidence.FromServices);
        Assert.False(mix.Evidence.Excluded.ContainsKey("Choices changed during generation"));
        Assert.Equal(126, mix.Evidence.Excluded["Candidate limit"]);
        Assert.Equal(mix.Evidence.Candidates, mix.Evidence.Shown + mix.Evidence.Excluded.Values.Sum());
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Never);
    }

    [Fact]
    public async Task Recent_inbox_singles_and_album_keep_followed_release_evidence_for_guest_led_tracks()
    {
        var logger = new MixLogger();
        await SeedReleaseInbox(logger: logger);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult<Album?>(new()
            {
                Songs = Enumerable.Range(1, id.StartsWith("kylie", StringComparison.Ordinal) ? 1 : 4).Select(track => new Song
                {
                    Id = "ext-deezer-song-" + id + track,
                    ExternalProvider = "deezer",
                    ExternalId = id + track,
                    Title = "Release track " + id + track,
                    Artist = "Guest " + id,
                    Artists = ["Guest " + id, id.StartsWith("kylie", StringComparison.Ordinal) ? "Kylie Cantrall" : "FLO"],
                    Track = track,
                    ExplicitContentLyrics = id == "flo0" && track == 1 ? 1 : 0
                }).ToList()
            }));
        filter = "CleanOnly";
        await taste.PutNudgeAsync(owner, new("song", "ext-deezer-song-flo02", "never"));
        await using (var db = factory.CreateDbContext())
        {
            for (var index = 0; index < 3; index++) db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                OccurrenceKey = "release-skip-" + index,
                TrackReference = "ext-deezer-song-flo03",
                Title = "Release track flo03",
                Artist = "Guest flo0",
                State = ListeningEventState.Skipped,
                UpdatedAt = clock.UtcNow,
                PositionTicks = TimeSpan.FromSeconds(10).Ticks,
                DurationMilliseconds = 180000
            });
            await db.SaveChangesAsync();
        }
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "release-radar").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);
        Assert.Equal(3, mix.Evidence!.ReleaseInboxEntries);
        Assert.Equal(3, mix.Songs.Count);
        Assert.Equal([1, 1, 4], mix.Songs.Select(item => item.Song.Track).Order().ToArray());
        Assert.All(mix.Songs, item => Assert.Equal(0, item.Song.ExplicitContentLyrics));
        Assert.Equal(1, mix.Evidence.Excluded["Never"]);
        Assert.Equal(1, mix.Evidence.Excluded["Three early skips"]);
        var log = Assert.Single(logger.Entries);
        Assert.Contains("inbox [entry1[", log.Message);
        Assert.Contains("entry2[", log.Message);
        Assert.Contains("entry3[", log.Message);
        Assert.Contains("Listening policy=1", log.Message);
        Assert.Contains("Never=1", log.Message);
        Assert.Contains("Three early skips=1", log.Message);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(log.Message, @"Shown=1").Count);
        foreach (var value in new[] { "Kylie", "FLO", "Guest", "flo0", "kylie0", owner.ToString() })
            Assert.DoesNotContain(value, log.Message, StringComparison.Ordinal);

        Assert.Equal(mix.Evidence.Candidates, mix.Evidence.Shown + mix.Evidence.Excluded.Values.Sum());
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Rotation_matches_38_sparse_imported_plays_alongside_11_live_library_plays()
    {
        await Seed(count: 38, plays: 1, age: 365);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        await using (var db = factory.CreateDbContext())
        {
            foreach (var play in await db.ListeningEvents.ToArrayAsync())
            {
                play.TrackReference = "history:" + play.Title;
                play.DurationMilliseconds = null;
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
                    BackendItemId = "live-" + index,
                    Title = "Live song " + index,
                    Artist = "Live artist " + index
                };
                db.LibraryTracks.Add(track);
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    OccurrenceKey = "live-" + index,
                    TrackReference = track.BackendItemId,
                    LibraryTrackId = track.Id,
                    Title = track.Title,
                    Artist = track.Artist,
                    SourceKind = "protocol",
                    State = ListeningEventState.Completed,
                    ListenedAt = clock.UtcNow.AddDays(-1),
                    UpdatedAt = clock.UtcNow.AddDays(-1)
                });
            }
            await db.SaveChangesAsync();
        }
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 3))
            .Returns((ProtocolExecutionContext _, string _, string query, int _) => Task.FromResult(
                ProviderOutcome<IReadOnlyList<Song>>.Success(songs.Values.Where(song => query == song.Title + " " + song.Artist).ToArray())));
        var logger = new MixLogger();
        service = NewService(logger);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);
        Assert.Contains("primary=11, fallback=38", Assert.Single(logger.Entries).Message);
        Assert.Equal(49, mix.Songs.Count);
        Assert.Equal(11, mix.Evidence!.PrimaryPlays);
        Assert.Equal(38, mix.Evidence.FallbackPlays);
        Assert.Equal(38, mix.Evidence.FromServices);
        Assert.Equal(11, mix.Evidence.FromMusic);
        Assert.Equal(38, mix.Evidence.ProviderReads);
    }

    [Fact]
    public async Task Rotation_uses_import_recording_matches_when_duration_is_missing()
    {
        await Seed(count: 1, plays: 1);
        await using (var db = factory.CreateDbContext())
        {
            await db.ListeningEvents.ExecuteUpdateAsync(update => update.SetProperty(item => item.TrackReference, "history:unresolved")
                .SetProperty(item => item.DurationMilliseconds, (long?)null).SetProperty(item => item.Album, "Original album"));
        }
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 3))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([
                new() { Id = "ext-deezer-song-live", ExternalProvider = "deezer", ExternalId = "live", Title = "Song 0 (Live)", Artist = "Artist 0", Duration = 180 },
                new() { Id = "ext-deezer-song-other", ExternalProvider = "deezer", ExternalId = "other", Title = "Song 0", Artist = "Artist 0", Album = "Different album", Duration = 180 }
            ]));
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat").Id;
        Assert.Equal("ext-deezer-song-other", Assert.Single((await service.GenerateAsync(owner, id)).Single(item => item.Id == id).Songs).Song.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Built_in_generations_log_each_recipe_once_with_counts_and_without_private_metadata(bool typedFailure)
    {
        EnableChart(failFirst: true, availableArtists: 3, tracks: 6, artists: 3);
        if (typedFailure)
            gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "broken", It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Failure(new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(60))));
        var logger = new MixLogger();
        service = NewService(logger);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "workout").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);
        var failures = gateway.Invocations.Count(call => call.Method.Name == "SearchAutomaticProviderSongsAsync" && Equals(call.Arguments[1], "broken"));
        Assert.Equal(failures, mix.Evidence!.ProviderFailures);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.StartsWith("Mix generation workout: shared stages [", entry.Message);
        Assert.Contains("fill.offered=6", entry.Message);
        Assert.Contains("qualified=6, shown=6, music=0, services=6", entry.Message);
        Assert.Contains("broken/" + (typedFailure ? "RateLimited" : "TransientFailure") + "=" + failures, entry.Message);
        Assert.Contains("revalidate.access=6", entry.Message);
        Assert.Contains("completed=1", entry.Message);
        Assert.Matches(@"final rank=\d+", entry.Message);
        Assert.Matches(@"total=\d+", entry.Message);
        Assert.DoesNotContain('\n', entry.Message);
        foreach (var value in new[] { owner.ToString(), "main", "Fresh 0", "Fresh artist 0", "ext-deezer-song-fresh0", "fixture-key" })
            Assert.DoesNotContain(value, entry.Message, StringComparison.Ordinal);

        logger.Entries.Clear();
        await service.GenerateAsync(owner, allowProviderReads: false);
        Assert.Equal(PersonalMixRules.Kinds.Length, logger.Entries.Count);
        foreach (var kind in PersonalMixRules.Kinds)
            Assert.Single(logger.Entries, item => item.Message.StartsWith("Mix generation " + kind + ":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inbox_keeps_library_recordings_and_excludes_releases_outside_the_recipe_window()
    {
        var logger = new MixLogger();
        await SeedReleaseInbox(logger: logger, olderSingleDays: 15);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        var recording = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendLibraryId = "music",
                BackendItemId = "native-release",
                Title = "Library release",
                Artist = "Kylie Cantrall",
                CanonicalRecordingId = recording
            });
            await db.SaveChangesAsync();
        }
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "kylie0"))
            .ReturnsAsync(new Album
            {
                Songs = [new()
            {
                Id = "ext-deezer-song-kylie01", ExternalProvider = "deezer", ExternalId = "kylie01",
                Title = "Release single", Artist = "Kylie Cantrall", Track = 1, CanonicalRecordingId = recording
            }]
            });
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "release-radar").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);
        Assert.Equal(2, mix.Songs.Count);
        Assert.Equal(1, mix.Evidence!.FromMusic);
        Assert.Equal(1, mix.Evidence.FromServices);
        Assert.Contains(mix.Songs, item => item.Song.Id == "native-release");
        Assert.Equal(3, mix.Evidence.ReleaseInboxEntries);
        var log = Assert.Single(logger.Entries);
        Assert.Contains("Outside release window=1", log.Message);
        Assert.Contains("Existing recording=1", log.Message);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "kylie1"), Times.Never);
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var refreshed = (await service.GenerateAsync(owner, id, allowProviderReads: false)).Single(item => item.Id == id);
        Assert.Equal(mix.Songs.Select(item => item.Song.Id), refreshed.Songs.Select(item => item.Song.Id));
        Assert.Equal(1, refreshed.Evidence!.FromMusic);
        Assert.Equal(0, refreshed.Evidence.ProviderReads);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Inbox_records_each_unplayable_entry_without_bypassing_playback_validation()
    {
        var logger = new MixLogger();
        await SeedReleaseInbox(logger: logger);
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["playback"]);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(false);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "release-radar").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);
        Assert.Empty(mix.Songs);
        Assert.Equal(9, mix.Evidence!.Excluded["No playable route"]);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(log.Message, "No playable route=3").Count);
        Assert.Contains("release-playback-checks.reads=6", log.Message);
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Exactly(6));
    }
    [Fact]
    public async Task Three_recent_inbox_releases_keep_library_matches_without_repeating_album_lookups()
    {
        await SeedReleaseInbox();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        var recordings = new[] { "kylie0", "kylie1", "flo0" }.ToDictionary(id => id, _ => Guid.NewGuid());
        await using (var db = factory.CreateDbContext())
        {
            foreach (var (id, recording) in recordings)
            {
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
                db.LibraryTracks.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = protocol,
                    BackendInstanceId = "main",
                    BackendLibraryId = "music",
                    BackendItemId = "native-" + id,
                    Title = "Library release " + id,
                    Artist = id.StartsWith("kylie", StringComparison.Ordinal) ? "Kylie Cantrall" : "FLO",
                    CanonicalRecordingId = recording
                });
            }
            await db.SaveChangesAsync();
        }
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult<Album?>(new()
            {
                Songs = [new()
                {
                    Id = "ext-deezer-song-" + id, ExternalProvider = "deezer", ExternalId = id, Title = "Release " + id,
                    Artist = id.StartsWith("kylie", StringComparison.Ordinal) ? "Kylie Cantrall" : "FLO", Track = 1,
                    CanonicalRecordingId = recordings[id], ExplicitContentLyrics = 0
                }]
            }));
        var mixId = (await service.ListAsync(owner)).Single(item => item.Kind == "release-radar").Id;
        var first = (await service.GenerateAsync(owner, mixId)).Single(item => item.Id == mixId);
        Assert.Equal(3, first.Songs.Count);
        Assert.Equal(3, first.Evidence!.FromMusic);
        gateway.Invocations.Clear();
        var refreshed = (await service.GenerateAsync(owner, mixId, allowProviderReads: false)).Single(item => item.Id == mixId);
        Assert.Equal(3, refreshed.Evidence!.ReleaseInboxEntries);
        Assert.Equal(first.Songs.Select(item => item.Song.Id), refreshed.Songs.Select(item => item.Song.Id));
        Assert.Equal(3, refreshed.Evidence.FromMusic);
        Assert.Equal(0, refreshed.Evidence.ProviderReads);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(false, []));
        var inaccessible = (await service.GenerateAsync(owner, mixId, allowProviderReads: false)).Single(item => item.Id == mixId);
        Assert.Empty(inaccessible.Songs);
        Assert.Equal(3, inaccessible.Evidence!.Excluded["Current access"]);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        clock.UtcNow += TimeSpan.FromDays(15);
        var aged = (await service.GenerateAsync(owner, mixId, allowProviderReads: false)).Single(item => item.Id == mixId);
        Assert.Empty(aged.Songs);
        Assert.Equal(0, aged.Evidence!.ProviderReads);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
