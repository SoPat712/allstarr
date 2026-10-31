using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Controllers;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class SharedTagsTests
{
    [Theory]
    [InlineData(" Hip Hop ", "hip-hop")]
    [InlineData("rap", "hip-hop")]
    [InlineData("RNB", "r&b")]
    [InlineData("electronica", "electronic")]
    [InlineData("seen live", null)]
    [InlineData("1990s", null)]
    [InlineData("favorites", null)]
    [InlineData("albums I own", null)]
    [InlineData("Rock", null)]
    public void Normalize_UsesSmallVocabularyAndRejectsPersonalLabels(string raw, string? expected) =>
        Assert.Equal(expected, SharedTagNormalization.Canonical(raw, ["Rock"]));

    [Fact]
    public void Combine_PreservesCountsAndRawEvidenceWithoutCountingSynonymsTwice()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = SharedTagNormalization.Evidence([
            new("musicbrainz-recording", [new("Hip Hop", 4, true), new("rap", 2)], now),
            new("lastfm-artist", [new("hip-hop", 100)], now)
        ], []);
        var tag = Assert.Single(SharedTagNormalization.Combine(evidence));
        Assert.Equal("hip-hop", tag.Tag);
        Assert.Equal(3, tag.Evidence.Count);
        Assert.Contains(tag.Evidence, item => item.RawLabel == "Hip Hop" && item.Weight == 4);
        Assert.Equal(1.3, tag.Score, 6);
        Assert.True(SharedTagNormalization.SourceWeight("musicbrainz-recording", true) >
                    SharedTagNormalization.SourceWeight("musicbrainz-release-group", true));
        Assert.True(SharedTagNormalization.SourceWeight("musicbrainz-release-group", true) >
                    SharedTagNormalization.SourceWeight("musicbrainz-artist", true));
    }

    [Fact]
    public void Capture_UsesAllExistingProviderGenresAndStableArtistIds()
    {
        var captured = SharedTagMetadata.Read("apple-musickit", """
            {"Title":"Song","Artist":"Singer","ArtistId":"ext-apple-artist-17","Isrc":"USABC1234567",
             "Genre":"Hip Hop","Genres":["Hip Hop","Electronic","seen live"],"UserRating":5}
            """, DateTimeOffset.UtcNow);
        Assert.Equal("Singer", captured.Artist);
        Assert.Equal("USABC1234567", captured.Isrc);
        Assert.Equal("apple", Assert.Single(captured.Batches).Source);
        Assert.Equal(3, captured.Batches[0].Tags.Count);
        Assert.All(captured.Batches[0].Tags, tag => Assert.True(tag.IsGenre));
        Assert.Equal("ext-apple-download-artist-17", Assert.Single(captured.ArtistKeys));
        Assert.DoesNotContain(captured.Batches[0].Tags, tag => tag.RawLabel == "5");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ext-apple-musickit-artist-i.library")]
    [InlineData("ext-apple-musickit-artist-credit:name-hash")]
    public void Capture_SkipsEmptyPrivateAndNameDerivedArtistIds(string artistId)
    {
        var captured = SharedTagMetadata.Read("apple-musickit", System.Text.Json.JsonSerializer.Serialize(new
        {
            Title = "Song",
            Artist = "Singer",
            ArtistIds = new[] { artistId },
            Genres = new[] { "Rock" }
        }), DateTimeOffset.UtcNow);
        Assert.Empty(captured.ArtistKeys);
    }

    [Theory]
    [InlineData("deezer", "deezer")]
    [InlineData("apple-musickit", "apple-download")]
    public void Capture_UsesExistingArtistResourceKeysAcrossMetadataShapes(string provider, string publicProvider)
    {
        var track = new ProviderTrackMetadata(new(provider, ProviderResourceKind.Track, "12"), "Song",
            [new("Singer", new(provider, ProviderResourceKind.Artist, "17"))], genres: ["Rock"]);
        var captured = SharedTagMetadata.Read(provider, System.Text.Json.JsonSerializer.Serialize(track), DateTimeOffset.UtcNow);
        Assert.Equal($"ext-{publicProvider}-artist-17", Assert.Single(captured.ArtistKeys));
    }

    [Fact]
    public async Task Profiles_AreSharedAndArtistEvidenceIsReusedInBatchReads()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var clock = new Clock();
        var store = new SharedTagStore(factory, clock);
        var recording = Guid.NewGuid();
        var other = Guid.NewGuid();
        await store.SaveAsync(SharedTagStore.RecordingKey(recording),
            [new("deezer", [new("Rock")], clock.UtcNow)], ["musicbrainz:artist-id"], true, default);
        await store.SaveAsync(SharedTagStore.RecordingKey(other), [], ["musicbrainz:artist-id"], true, default);
        await store.SaveAsync(SharedTagStore.ArtistKey("musicbrainz:artist-id"),
            [new("musicbrainz-artist", [new("Happy", 2)], clock.UtcNow)], [], true, default);
        var tags = await store.GetTags([recording, other]);
        Assert.Contains(tags[recording].Tags, tag => tag.Tag == "rock");
        Assert.Contains(tags[recording].Tags, tag => tag.Tag == "happy");
        Assert.Equal("happy", Assert.Single(tags[other].Tags).Tag);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(3, await db.SharedTagProfiles.CountAsync());
        Assert.DoesNotContain(db.Model.FindEntityType(typeof(SharedTagProfileRecord))!.GetProperties(),
            property => property.Name.Contains("User"));
        Assert.Empty(await db.ListeningEvents.ToArrayAsync());
    }

    [Fact]
    public async Task Discovery_DeduplicatesAcrossCallsAndRefreshesAfterThirtyDays()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var clock = new Clock();
        var user = Guid.NewGuid();
        var recording = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new()
            {
                Id = user,
                DisplayName = "Listener",
                Enabled = true,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            db.CanonicalRecordings.Add(new()
            {
                Id = recording,
                CreatedByUserId = user,
                PublicId = CanonicalRecordingRecord.DefaultPublicId(recording),
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var jobs = new DurableJobQueue(factory, new DurableJobOptions(), new JobPayloadPolicy(new()), clock);
        var queue = new SharedTagQueue(factory, jobs, clock);
        Assert.Equal(1, await queue.EnqueuePendingAsync(default));
        Assert.Equal(0, await queue.EnqueuePendingAsync(default));
        clock.UtcNow += TimeSpan.FromHours(1);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.RetryScheduled;
            (await db.CanonicalRecordings.SingleAsync()).UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await queue.EnqueuePendingAsync(default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.Pending;
            await db.SaveChangesAsync();
        }
        var claim = await jobs.ClaimNextAsync("tags-worker", [SharedTagQueue.JobType]);
        await jobs.CompleteAsync(claim!, DurableJobCompletion.Success());
        var store = new SharedTagStore(factory, clock);
        await store.SaveAsync(SharedTagStore.RecordingKey(recording), [], [], true, default, capturedAt: clock.UtcNow);
        clock.UtcNow += TimeSpan.FromDays(29);
        Assert.Equal(0, await queue.EnqueuePendingAsync(default));
        clock.UtcNow += TimeSpan.FromDays(2);
        Assert.Equal(1, await queue.EnqueuePendingAsync(default));
    }

    [Fact]
    public async Task Discovery_KnownTagsAreNotFetchedAgainAfterExpiry()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var clock = new Clock();
        var owner = Guid.NewGuid();
        var recording = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new() { Id = owner, Enabled = true });
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
            await db.SaveChangesAsync();
        }
        var store = new SharedTagStore(factory, clock);
        await store.SaveAsync(SharedTagStore.RecordingKey(recording), [new("provider", [new("pop")], clock.UtcNow)], [], true, default,
            capturedAt: clock.UtcNow);
        clock.UtcNow += TimeSpan.FromDays(60);
        var jobs = new DurableJobQueue(factory, new(), new(new()), clock);
        Assert.Equal(0, await new SharedTagQueue(factory, jobs, clock).EnqueuePendingAsync(default));
    }

    [Fact]
    public async Task HistoryDiscovery_RevisitsFreshEmptyCacheOnceAndResumesWithoutDuplicates()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var clock = new Clock();
        var owner = Guid.NewGuid();
        var recording = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new() { Id = owner, Enabled = true, BackendType = "jellyfin", BackendInstanceId = "primary" });
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
            db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                SourceKind = "import",
                OccurrenceKey = "past",
                CanonicalRecordingId = recording,
                State = allstarr.Core.Intelligence.ListeningEventState.Completed,
                Artist = "Singer",
                Title = "Song"
            });
            await db.SaveChangesAsync();
        }
        var store = new SharedTagStore(factory, clock);
        await store.SaveAsync(SharedTagStore.RecordingKey(recording), [], [], true, default, capturedAt: clock.UtcNow);
        var jobs = new DurableJobQueue(factory, new(), new(new()), clock);
        Assert.Equal(1, await new SharedTagQueue(factory, jobs, clock).EnqueuePendingAsync(default));
        Assert.Equal(0, await new SharedTagQueue(factory, jobs, clock).EnqueuePendingAsync(default));
        var claim = await jobs.ClaimNextAsync("history-tags", [SharedTagQueue.JobType]);
        await store.SaveAsync(SharedTagStore.RecordingKey(recording), [], [], true, default, historyCaptured: true);
        await jobs.CompleteAsync(claim!, DurableJobCompletion.Success());
        Assert.Equal(0, await new SharedTagQueue(factory, jobs, clock).EnqueuePendingAsync(default));
    }

    [Fact]
    public void ArtistGenres_SurviveExistingMetadataMapping()
    {
        var metadata = new ProviderArtistMetadata(new("spotify", ProviderResourceKind.Artist, "17"), "Singer",
            genres: ["Hip Hop", "Soul"]);
        var artist = ProtocolProviderGateway.Map(metadata);
        Assert.Equal(["Hip Hop", "Soul"], artist.Genres);
    }

    [Fact]
    public async Task AudioMuseHook_RetainsTopMoodsAndNumericEvidence()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var clock = new Clock();
        var store = new SharedTagStore(factory, clock);
        var id = Guid.NewGuid();
        await store.CaptureAudioMuseAsync(id, [new("calm", 1), new("happy", .3), new("dreamy", .4),
            new("angry", .2), new("sad", .5), new("intense", .9)], .7, 120, default);
        var tags = (await store.GetTags(id)).Tags;
        Assert.Equal(5, tags.Count);
        Assert.Contains(tags, item => item.Tag == "intense");
        Assert.DoesNotContain(tags, item => item.Tag == "angry");
        await using var db = await factory.CreateDbContextAsync();
        var evidence = SharedTagStore.Read(await db.SharedTagProfiles.SingleAsync()).Evidence;
        Assert.Contains(evidence, item => item.Source == "audiomuse" && item.RawLabel == "energy" && item.Weight == .7);
        Assert.Contains(evidence, item => item.RawLabel == "tempo" && item.Weight == 120);
    }

    [Fact]
    public async Task Diagnostics_RequiresSessionAndAllowsAdministratorOrRecordingOwner()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var clock = new Clock();
        var owner = Guid.NewGuid();
        var recording = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new()
            {
                Id = owner,
                DisplayName = "Listener",
                Enabled = true,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            db.CanonicalRecordings.Add(new()
            {
                Id = recording,
                CreatedByUserId = owner,
                PublicId = CanonicalRecordingRecord.DefaultPublicId(recording),
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var controller = new RecordingTagsController(new(factory, clock), factory)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>(await controller.Get(recording, default));
        AdminAuthSession Session(Guid user, bool admin = false) => new()
        {
            SessionId = "fixture",
            UserId = "listener",
            UserName = "Listener",
            JellyfinAccessToken = "",
            AllstarrUserId = user,
            IsAdministrator = admin,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(Guid.NewGuid());
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Get(recording, default)).StatusCode);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(owner);
        Assert.IsType<OkObjectResult>(await controller.Get(recording, default));
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(Guid.NewGuid(), true);
        Assert.IsType<OkObjectResult>(await controller.Get(recording, default));
    }

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
