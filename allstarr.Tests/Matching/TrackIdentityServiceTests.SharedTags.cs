using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Models.Domain;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task PlaybackMetadata_RetainsProviderGenresWithoutStartingTagWork()
    {
        var context = Context(Actor(_userA), "deezer");
        var track = Track("deezer", "tagged-track");
        await _service.RememberSourceAsync(context, track, "ext-deezer-song-tagged-track", ProviderIdentityScope.Catalog);
        await _service.RememberPlaybackMetadataAsync(context, track, new Song
        {
            Title = "Song",
            Artist = "Singer",
            ArtistId = "ext-deezer-artist-12",
            Genre = "Rock",
            Genres = ["Rock", "Blues"]
        });
        await using var db = await _factory.CreateDbContextAsync();
        var snapshot = await db.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "playback-metadata");
        using var payload = JsonDocument.Parse(snapshot.PayloadJson);
        Assert.Equal(2, payload.RootElement.GetProperty("Genres").GetArrayLength());
        Assert.Equal("ext-deezer-artist-12", payload.RootElement.GetProperty("ArtistId").GetString());
        Assert.Empty(await db.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task SharedTagJob_ReusesSavedGenresWithoutEnrichmentRequests()
    {
        var context = Context(Actor(_userA), "deezer");
        var track = Track("deezer", "retry-tagged-track");
        var id = await _service.RememberSourceAsync(context, track, "ext-deezer-song-retry-tagged-track",
            ProviderIdentityScope.Catalog, "USABC1234567");
        await _service.RememberPlaybackMetadataAsync(context, track,
            new Song { Title = "Song", Artist = "Singer", Genres = ["Rock"] });
        var settings = new Mock<IOptionsMonitor<ScrobblingSettings>>();
        settings.SetupGet(item => item.CurrentValue).Returns(new ScrobblingSettings());
        var catalog = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        catalog.Setup(item => item.LookupByIsrcAsync("USABC1234567", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MusicBrainzLookupException("catalog_source_temporarily_unavailable", "Try later.", true));
        var store = new SharedTagStore(_factory, _clock);
        var jobs = new DurableJobQueue(_factory, new DurableJobOptions(), new JobPayloadPolicy(new()), _clock);
        var queue = new SharedTagQueue(_factory, jobs, _clock);
        var handler = new SharedTagJobHandler(_factory, store,
            new(catalog.Object, Mock.Of<IHttpClientFactory>(), settings.Object, _clock), _clock);
        Assert.Equal(1, await queue.EnqueuePendingAsync(default));
        var claim = await jobs.ClaimNextAsync("tags-worker", [SharedTagQueue.JobType]);

        var result = await handler.ExecuteAsync(new(claim!, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal("rock", Assert.Single((await store.GetTags(id)).Tags).Tag);
        catalog.Verify(item => item.LookupByIsrcAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(DateTimeOffset.MinValue, (await db.SharedTagProfiles.SingleAsync()).RefreshedAt);
    }

    [Fact]
    public async Task SharedTagDiscovery_IgnoresMetadataThatCollectionCannotUse()
    {
        var context = Context(Actor(_userA), "deezer");
        var track = Track("deezer", "unverified-tags");
        var recording = await _service.RememberSourceAsync(context, track, "ext-deezer-song-unverified-tags", ProviderIdentityScope.Catalog);
        var store = new SharedTagStore(_factory, _clock);
        await store.SaveAsync(SharedTagStore.RecordingKey(recording), [new("provider", [new("Pop")], _clock.UtcNow)], [], true, default,
            capturedAt: _clock.UtcNow);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _service.RememberPlaybackMetadataAsync(context, track, new Song { Title = "Song", Artist = "Singer", Genres = ["Rock"] });
        await using (var db = _factory.CreateDbContext())
        {
            (await db.ProviderTrackIdentities.SingleAsync()).VerificationMethod = "automatic-suggestion";
            await db.SaveChangesAsync();
        }
        var jobs = new DurableJobQueue(_factory, new(), new(new()), _clock);
        Assert.Equal(0, await new SharedTagQueue(_factory, jobs, _clock).EnqueuePendingAsync(default));
    }

    [Fact]
    public async Task SharedTagJob_CapturesSavedGenresAndFreshMetadataWithoutSourceRequests()
    {
        var context = Context(Actor(_userA), "deezer");
        var track = Track("deezer", "tagged-track");
        var id = await _service.RememberSourceAsync(context, track, "ext-deezer-song-tagged-track", ProviderIdentityScope.Catalog);
        var song = new Song { Title = "Song", Artist = "Singer", ArtistId = "ext-deezer-artist-12", Genres = ["Rock", "Blues"] };
        var settings = new Mock<IOptionsMonitor<ScrobblingSettings>>();
        settings.SetupGet(item => item.CurrentValue).Returns(new ScrobblingSettings());
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var catalog = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        var store = new SharedTagStore(_factory, _clock);
        var sources = new SharedTagSources(catalog.Object, clients.Object, settings.Object, _clock);
        var jobs = new DurableJobQueue(_factory, new DurableJobOptions(), new JobPayloadPolicy(new()), _clock);
        var queue = new SharedTagQueue(_factory, jobs, _clock);
        var handler = new SharedTagJobHandler(_factory, store, sources, _clock);
        async Task Collect()
        {
            Assert.Equal(1, await queue.EnqueuePendingAsync(default));
            var claim = await jobs.ClaimNextAsync("tags-worker", [SharedTagQueue.JobType]);
            var result = await handler.ExecuteAsync(new(claim!, Mock.Of<IServiceProvider>()), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await jobs.CompleteAsync(claim!, result);
        }
        await Collect();
        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Equal(_clock.UtcNow, (await db.SharedTagProfiles.SingleAsync()).RefreshedAt);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _service.RememberPlaybackMetadataAsync(context, track, song);
        await Collect();
        Assert.Equal(["blues", "rock"], (await store.GetTags(id)).Tags.Select(item => item.Tag).Order());
        Assert.Equal(0, await queue.EnqueuePendingAsync(default));
        _clock.Advance(TimeSpan.FromDays(1));
        context = Context(Actor(_userA), "deezer");
        song.Genres = ["Soul"];
        await _service.RememberPlaybackMetadataAsync(context, track, song);
        await Collect();
        Assert.Equal("soul", Assert.Single((await store.GetTags(id)).Tags).Tag);
        Assert.Equal(0, await queue.EnqueuePendingAsync(default));
        var artist = new Artist { Id = "ext-deezer-artist-12", Name = "Singer", Genres = ["Happy"] };
        Assert.True((await queue.CaptureArtistAsync(_userB, artist, "deezer", default)).Created);
        Assert.False((await queue.CaptureArtistAsync(_userB, artist, "deezer", default)).Created);
        var capturedAt = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromHours(1));
        var artistClaim = await jobs.ClaimNextAsync("tags-worker", [SharedTagQueue.JobType]);
        var artistResult = await handler.ExecuteAsync(new(artistClaim!, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, artistResult.Kind);
        await jobs.CompleteAsync(artistClaim!, artistResult);
        Assert.Contains((await store.GetTags(id)).Tags, item => item.Tag == "happy" &&
            item.Evidence.Single().Source == "deezer-artist" && item.Evidence.Single().FetchedAt == capturedAt && item.Evidence.Single().IsGenre);
        _clock.Advance(TimeSpan.FromSeconds(1));
        context = Context(Actor(_userA), "deezer");
        await _service.RememberPlaybackMetadataAsync(context, track, new Song { Title = "Song", Artist = "Singer" });
        await Collect();
        Assert.Equal(["happy", "soul"], (await store.GetTags(id)).Tags.Select(item => item.Tag).Order());
        clients.VerifyNoOtherCalls();
        catalog.VerifyNoOtherCalls();
    }
}
