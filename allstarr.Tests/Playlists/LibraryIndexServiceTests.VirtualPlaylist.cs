using System.Diagnostics;
using System.Net;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _recordingOutput;

    public LibraryIndexServiceTests(Xunit.Abstractions.ITestOutputHelper output) => _recordingOutput = output;

    [Fact]
    public async Task VirtualPlaylistMatchesReuseIndividualSongsAndRematchCurrentLibraryAndListenerChoices()
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "lookup", null, null);
        var identity = ReadIdentity(recording.Recording.Id, "spotify", "known-source");
        var snapshot = ReadSnapshot(identity);
        var track = SelfLoveMatchFixture.Local(_userA, "backend");
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderTrackIdentities.Add(identity);
            db.ExternalMetadataSnapshots.Add(snapshot);
            db.LibraryTracks.Add(track);
            await db.SaveChangesAsync();
        }
        var reads = new RecordingReadCounter();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(reads).Options);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        using var cache = new MemoryApplicationCache(_clock);
        var service = new LibraryIndexService(factory, state, _clock, _identities, _libraryAccess.Object, recordingCache: cache);
        LibraryRecordingLookup lookup = new("external-alias", recording.Recording.Id, null, "apple-musickit", "alias");
        var baseline = await _service.FindKnownRecordingTracksAsync(viewer, [lookup]);
        Assert.Equal(SelfLoveMatchFixture.BackendItemId, Assert.Single(baseline).Value);
        Assert.Equal(baseline, await service.FindKnownRecordingTracksAsync(viewer, [lookup]));
        Assert.NotEmpty(reads.Commands);
        reads.Commands.Clear();
        Assert.Equal(baseline, await service.FindKnownRecordingTracksAsync(viewer, [lookup]));
        Assert.Empty(reads.Commands);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.FindKnownRecordingTracksAsync(viewer, [lookup], cancellation.Token));

        // Adding an unrelated playlist member leaves the song's library choice reusable.
        await using (var db = _factory.CreateDbContext())
        {
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _userA,
                ProviderId = "allstarr",
                ResourceKind = "playlist-membership",
                ExternalIdHash = new string('b', 64),
                SnapshotVersion = 1,
                PayloadSha256 = new string('c', 64)
            });
            await db.SaveChangesAsync();
        }
        await service.FindKnownRecordingTracksAsync(viewer, [lookup]);
        Assert.Empty(reads.Commands);

        await using (var db = _factory.CreateDbContext())
        {
            (await db.LibraryTracks.SingleAsync()).BackendItemId = "replacement";
            await db.SaveChangesAsync();
        }
        Assert.Equal("replacement", Assert.Single(await service.FindKnownRecordingTracksAsync(viewer, [lookup])).Value);
        Assert.NotEmpty(reads.Commands);
        reads.Commands.Clear();
        await using (var db = _factory.CreateDbContext())
        {
            db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _userA,
                ExternalSnapshotId = snapshot.Id,
                SourceProviderId = identity.ProviderId,
                SourceExternalIdHash = identity.ExternalIdHash,
                Decision = ManualOverrideDecision.Reject,
                DecisionVersion = 1,
                CreatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        Assert.Empty(await service.FindKnownRecordingTracksAsync(viewer, [lookup]));
        Assert.NotEmpty(reads.Commands);
        reads.Commands.Clear();
        Assert.Empty(await service.FindKnownRecordingTracksAsync(viewer, [lookup]));
        Assert.Empty(reads.Commands);
        _libraryAccess.Setup(item => item.ResolveAsync(viewer, Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        Assert.Empty(await service.FindKnownRecordingTracksAsync(viewer, [lookup]));
    }

    [Fact]
    public async Task VirtualPlaylistOneHundredSongsUseOneMatchingBatchAndReuseMissesAcrossPages()
    {
        var viewer = Context(_userA, "principal-a");
        var lookups = Enumerable.Range(0, 100).Select(index =>
            new LibraryRecordingLookup("song-" + index, Guid.NewGuid(), null, "deezer", index.ToString())).ToArray();
        await using (var db = _factory.CreateDbContext())
        {
            foreach (var lookup in lookups)
            {
                db.CanonicalRecordings.Add(new() { Id = lookup.RecordingId!.Value, CreatedByUserId = _userA });
                if (lookup.PublicId == "song-99") continue;
                var track = SelfLoveMatchFixture.Local(_userA, "backend");
                track.Id = Guid.NewGuid();
                track.BackendItemId = "native-" + lookup.PublicId;
                track.CanonicalRecordingId = lookup.RecordingId;
                db.LibraryTracks.Add(track);
            }
            await db.SaveChangesAsync();
        }
        var reads = new RecordingReadCounter(150);
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(reads).Options);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        using var cache = new MemoryApplicationCache(_clock);
        var service = new LibraryIndexService(factory, state, _clock, _identities, _libraryAccess.Object, recordingCache: cache);
        var uncached = new LibraryIndexService(factory, state, _clock, _identities, _libraryAccess.Object);
        var watch = Stopwatch.StartNew();
        var expected = await uncached.FindKnownRecordingTracksAsync(viewer, lookups);
        var baseline = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(99, expected.Count);
        var baselineReads = reads.Commands.Count;
        reads.Commands.Clear();
        watch.Restart();
        Assert.Equal(expected, await service.FindKnownRecordingTracksAsync(viewer, lookups));
        var cold = watch.Elapsed.TotalMilliseconds;
        Assert.Single(reads.Commands, sql => sql.Contains("FROM \"library_tracks\"", StringComparison.Ordinal));
        Assert.InRange(reads.Commands.Count, 1, 8);
        reads.Commands.Clear();
        watch.Restart();
        Assert.Equal(expected, await service.FindKnownRecordingTracksAsync(viewer, lookups));
        var warm = watch.Elapsed.TotalMilliseconds;
        Assert.Empty(reads.Commands);
        Assert.Equal(expected.Where(item => item.Key is "song-0" or "song-1").ToDictionary(),
            await service.FindKnownRecordingTracksAsync(viewer, [lookups[1], lookups[99], lookups[0]]));
        Assert.Empty(reads.Commands);
        _recordingOutput.WriteLine($"100-song matching with delayed database reads: uncached {baseline:F1} ms / {baselineReads} queries; cold {cold:F1} ms; warm {warm:F1} ms / 0 queries; next page 0 queries; identical library choices including a cached miss.");

        using var backend = new VirtualPlaylistItemBackend();
        using var client = new HttpClient(backend);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var proxy = new JellyfinProxyService(clients.Object, Options.Create(new JellyfinSettings { Url = "http://backend.test" }),
            new HttpContextAccessor(), NullLogger<JellyfinProxyService>.Instance, cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new ConfigurationBuilder().Build());
        var linkId = Guid.NewGuid();
        var protocolId = PlaylistVirtualizationService.CreateProtocolId(linkId);
        var membershipReads = 0;
        async Task<string> PageAsync(LibraryIndexService matcher)
        {
            var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
            playlists.Setup(item => item.ReadAsync(viewer, protocolId, It.IsAny<CancellationToken>())).Returns(async () =>
            {
                membershipReads++;
                var matches = await matcher.FindKnownRecordingTracksAsync(viewer, lookups);
                return new VirtualPlaylistReadModel(protocolId, linkId, linkId, "Saved songs", null, null,
                    "spotify", "source", "1", PlaylistLinkMode.Virtual,
                    lookups.Select((lookup, index) => new VirtualPlaylistTrack(index,
                        matches.GetValueOrDefault(lookup.PublicId) ?? "allstarr-unresolved-" + index,
                        "Song " + index, "Artist", "Album", null, 180000, null, TrackMatchState.Accepted,
                        RouteKind: matches.ContainsKey(lookup.PublicId) ? TrackRouteKind.Local : TrackRouteKind.Unresolved)).ToArray());
            });
            var adapter = new JellyfinVirtualPlaylistProtocolAdapter(playlists.Object, Mock.Of<IJellyfinPlaylistMutationResolver>(),
                proxyService: proxy, responseBuilder: new JellyfinResponseBuilder(), readContexts: factory, readCache: cache);
            var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            { ["Limit"] = "100", ["Fields"] = "MediaSources" });
            return JsonSerializer.Serialize(Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(viewer, protocolId, null, query, default)).Value);
        }
        watch.Restart();
        var previousPage = await PageAsync(uncached);
        var pageBefore = watch.Elapsed.TotalMilliseconds;
        reads.Commands.Clear();
        var backendBefore = backend.Reads;
        watch.Restart();
        var currentPage = await PageAsync(service);
        var pageAfter = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(previousPage, currentPage);
        Assert.Equal(1, backend.Reads - backendBefore);
        Assert.Empty(reads.Commands);
        Assert.Equal(2, membershipReads);
        using var page = JsonDocument.Parse(currentPage);
        Assert.Equal(100, page.RootElement.GetProperty("Items").GetArrayLength());
        _recordingOutput.WriteLine($"100-song served page with real matching and 250 ms fake Jellyfin: before {pageBefore:F1} ms; warm {pageAfter:F1} ms; one batched Jellyfin hydration; zero matching queries; exact response JSON/order equal; membership read each time.");
    }

    private sealed class VirtualPlaylistItemBackend : HttpMessageHandler
    {
        public int Reads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/Items", request.RequestUri!.AbsolutePath);
            Reads++;
            var ids = QueryHelpers.ParseQuery(request.RequestUri.Query)["Ids"].ToString().Split(',');
            Assert.Equal(99, ids.Length);
            await Task.Delay(250, cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    Items = ids.Reverse().Select(id => new { Id = id, Type = "Audio", Name = id, ServerId = "fixture", MediaSources = Array.Empty<object>() }),
                    TotalRecordCount = ids.Length
                }))
            };
        }
    }
}
