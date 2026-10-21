using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, null)]
    [InlineData(ProtocolKind.Subsonic, null)]
    [InlineData(ProtocolKind.Jellyfin, "deezer")]
    [InlineData(ProtocolKind.Subsonic, "deezer")]
    [InlineData(ProtocolKind.Jellyfin, "spotiflac-soundcloud")]
    [InlineData(ProtocolKind.Subsonic, "spotiflac-soundcloud")]
    public async Task SongAliases_UseOneCompleteDisplaySource(ProtocolKind kind, string? playingProvider)
    {
        var songs = await SeedDisplaySourcesAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == _userA)).BackendType = kind.ToString().ToLowerInvariant();
            await db.SaveChangesAsync();
        }
        using var activity = new PlaybackDeliveryActivityStore();
        var protocol = DisplayProtocol(kind);
        if (playingProvider != null)
        {
            var lease = new ProviderStreamLease("display", new Uri("https://media.example.test/song"),
                protocol.Deadline, true, true, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry);
            using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            activity.StreamOpened(protocol, "ext-spotiflac-soundcloud-song-1886663583", ProviderAudioQuality.Any,
                new(response, lease, playingProvider, playingProvider == "deezer" ? "2925172991" : "1886663583"));
        }
        var gateway = new ProtocolProviderGateway(Mock.Of<IProviderRouter>(MockBehavior.Strict),
            new ProviderRegistry([]), Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            playbackActivity: activity, identities: _service);
        var expected = songs[playingProvider == "deezer" ? 1 : 3];
        foreach (var alias in songs)
        {
            var song = await gateway.GetSongAsync(protocol,
                alias.ExternalProvider == "apple-download" ? "apple-musickit" : alias.ExternalProvider!, alias.ExternalId!);
            Assert.NotNull(song);
            Assert.Equal(songs[0].Id, song.Id);
            Assert.Equal(expected.ExternalProvider, song.ExternalProvider);
            Assert.Equal(expected.ExternalId, song.ExternalId);
            Assert.Equal(expected.Title, song.Title);
            Assert.Equal(expected.Artist, song.Artist);
            Assert.Equal(expected.Album, song.Album);
            Assert.Equal(expected.AlbumArtist, song.AlbumArtist);
            Assert.Equal(expected.AlbumId, song.AlbumId);
            Assert.Equal(expected.Year, song.Year);
            Assert.Equal(expected.ReleaseDate, song.ReleaseDate);
            Assert.Equal(expected.Size, song.Size);
            Assert.Equal(expected.SampleRate, song.SampleRate);
            Assert.Equal(expected.CoverArtUrl, song.CoverArtUrl);
            Assert.Equal(expected.Composer, song.Composer);
            Assert.Equal("GBUM72403956", song.Isrc);
            var local = await gateway.GetStoredSongAsync(protocol, alias.ExternalProvider!, alias.ExternalId!);
            Assert.Equal(song.AlbumId, local!.AlbumId);
            if (kind == ProtocolKind.Jellyfin)
            {
                var item = new JellyfinResponseBuilder().ConvertSongToJellyfinItem(song, null, null, true);
                Assert.Equal(expected.AlbumId, item["AlbumId"]);
                Assert.Equal(expected.AlbumId, item["ParentId"]);
                Assert.Equal(expected.ReleaseDate + "T00:00:00.0000000Z", item["PremiereDate"]);
                var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(item["MediaSources"]));
                Assert.Equal(expected.Size, source["Size"]);
                Assert.Equal(expected.Bitrate, source["Bitrate"]);
                var providers = Assert.IsType<Dictionary<string, string>>(item["ProviderIds"]);
                Assert.Equal(expected.ExternalId, providers[expected.ExternalProvider!]);
                Assert.Equal(2, providers.Count);
            }
            else
            {
                var response = Assert.IsType<JsonResult>(new SubsonicResponseBuilder().CreateSongResponse("json", song));
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
                var item = json.RootElement.GetProperty("subsonic-response").GetProperty("song");
                Assert.Equal(expected.AlbumId, item.GetProperty("albumId").GetString());
                Assert.Equal(songs[0].Id, item.GetProperty("id").GetString());
            }
        }
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task ProtocolSearch_DoesNotWriteToDatabaseAndCachesRecentMetadata(ProtocolKind kind)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == _userA)).BackendType = kind.ToString().ToLowerInvariant();
            await db.SaveChangesAsync();
        }
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Track, "unplayed"), "Unplayed", [new("Artist")],
                    albumTitle: "Album", albumId: new("deezer", ProviderResourceKind.Album, "release"))])));
        capability.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Album, "release"), "Album", [new("Artist")])])));
        capability.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "performer"), "Artist")])));
        var descriptor = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata,
            ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1.0", ["searchTracks", "getTrack"]);
        var registration = new ProviderRegistration(new ProviderDescriptor("deezer", "Deezer", "Fixture",
            ProviderOrigin.BuiltIn, "1", "1.0", [descriptor], new ProviderPermissionDescriptor()), [capability.Object]);
        var registry = new ProviderRegistry([registration]);
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderMetadataCapability>(request,
                [new(0, registration.Descriptor, descriptor, capability.Object,
                    new(request.Actor, "deezer", null, request.Policy, request.OperationId, request.CorrelationId,
                        request.Deadline, request.CancellationToken), null)],
                new(request.CorrelationId, ProviderCapabilityKind.Metadata, "deezer", null, [])));
        using var cache = new MemoryApplicationCache(_clock);
        var gateway = new ProtocolProviderGateway(router.Object, registry, Mock.Of<IHttpClientFactory>(),
            identities: _service, applicationCache: cache);
        var protocol = DisplayProtocol(kind);
        await using var before = await _factory.CreateDbContextAsync();
        var snapshots = await before.ExternalMetadataSnapshots.CountAsync();
        var links = await before.ProviderTrackIdentities.CountAsync();
        var recordings = await before.CanonicalRecordings.CountAsync();
        var audits = await before.AuditEvents.CountAsync();

        for (var attempt = 0; attempt < 2; attempt++)
            await gateway.SearchAsync(protocol, "Album", 10, 10, 10);

        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(snapshots, await after.ExternalMetadataSnapshots.CountAsync());
        Assert.Equal(links, await after.ProviderTrackIdentities.CountAsync());
        Assert.Equal(recordings, await after.CanonicalRecordings.CountAsync());
        Assert.Equal(audits, await after.AuditEvents.CountAsync());
        var stored = await gateway.GetStoredSongAsync(protocol, "deezer", "unplayed");
        Assert.Equal("ext-deezer-song-unplayed", stored!.Id);
        Assert.Equal("ext-deezer-album-release", stored.AlbumId);
        Assert.Equal("ext-deezer-album-release", (await gateway.GetStoredAlbumAsync(protocol, "deezer", "release"))!.Id);
        Assert.Equal("ext-deezer-artist-performer", (await gateway.GetStoredArtistAsync(protocol, "deezer", "performer"))!.Id);
        Assert.Null(await gateway.GetStoredSongAsync(DisplayProtocol(kind), "deezer", "missing"));
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(await gateway.GetStoredSongAsync(protocol, "deezer", "unplayed"));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task SongAliases_SparseEvidenceFetchesChosenSourceOnce(ProtocolKind kind, bool hasUploadEvidence)
    {
        var songs = await SeedDisplaySourcesAsync(retainSnapshots: false);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == _userA)).BackendType = kind.ToString().ToLowerInvariant();
            if (hasUploadEvidence)
            {
                var link = await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "spotiflac-soundcloud");
                var payload = JsonSerializer.Serialize(songs[0]);
                db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = _userA,
                    ProviderId = link.ProviderId,
                    ProviderTrackIdentityId = link.Id,
                    ExternalIdHash = link.ExternalIdHash,
                    ResourceKind = "track",
                    SnapshotVersion = 1,
                    ProviderRevision = "identity-evidence",
                    PayloadJson = payload,
                    PayloadSha256 = Hash(payload),
                    RetrievedAt = _clock.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(item => item.ProviderId).Returns("apple-musickit");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(
                new("apple-musickit", ProviderResourceKind.Track, "1780787517"), "Check",
                [new("FLO", new("apple-musickit", ProviderResourceKind.Artist, "artist"))],
                albumTitle: "Access All Areas", albumId: new("apple-musickit", ProviderResourceKind.Album, "1780787475"), year: 2024)));
        var descriptor = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata,
            ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1.0", ["searchTracks", "getTrack"]);
        var registration = new ProviderRegistration(new ProviderDescriptor("apple-musickit", "Apple", "Fixture",
            ProviderOrigin.BuiltIn, "1", "1.0", [descriptor], new ProviderPermissionDescriptor()), [metadata.Object]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderMetadataCapability>(request,
                [new(0, registration.Descriptor, descriptor, metadata.Object,
                    new(request.Actor, "apple-musickit", null, request.Policy, request.OperationId, request.CorrelationId,
                        request.Deadline, request.CancellationToken), new("apple-musickit", ProviderResourceKind.Track, "1780787517"))],
                new(request.CorrelationId, ProviderCapabilityKind.Metadata, "apple-musickit", null, [])));
        using var cache = new MemoryApplicationCache(_clock);
        var gateway = new ProtocolProviderGateway(router.Object, new ProviderRegistry([registration]), Mock.Of<IHttpClientFactory>(),
            identities: _service, applicationCache: cache);
        foreach (var alias in songs.Concat(songs))
        {
            var song = await gateway.GetSongAsync(DisplayProtocol(kind), alias.ExternalProvider!, alias.ExternalId!);
            Assert.Equal(songs[0].Id, song!.Id);
            Assert.Equal("apple-download", song.ExternalProvider);
            Assert.Equal("Access All Areas", song.Album);
            Assert.Equal("ext-apple-download-album-1780787475", song.AlbumId);
            Assert.Equal(2024, song.Year);
        }
        metadata.Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Once);
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(hasUploadEvidence ? 1 : 0, await after.ExternalMetadataSnapshots.CountAsync());
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task DisplaySources_UseOnlyCurrentOwnedAcceptedMatchEvidence(bool otherOwner, bool rejected, bool expected)
    {
        var songs = await SeedDisplaySourcesAsync(retainSnapshots: false);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var recordingId = (await db.ProviderTrackIdentities.FirstAsync()).CanonicalRecordingId;
            var snapshot = new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = otherOwner ? _userB : _userA,
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = Hash("source"),
                SnapshotVersion = 1,
                ProviderRevision = "fixture",
                PayloadJson = "{}",
                PayloadSha256 = Hash("{}"),
                RetrievedAt = _clock.UtcNow
            };
            db.ExternalMetadataSnapshots.Add(snapshot);
            var evidence = JsonSerializer.Serialize(new[] { new
            {
                ProviderTrackIds = new Dictionary<string, string> { ["deezer"] = "2925172991" },
                Title = "Check", Artist = "FLO", Album = "Check", DurationMilliseconds = 180000, CandidateIsrc = "GBUM72403956"
            } });
            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = snapshot.OwnerUserId,
                ExternalSnapshotId = snapshot.Id,
                CanonicalRecordingId = recordingId,
                State = TrackMatchState.Accepted,
                Confidence = 1,
                Threshold = 0.9,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                CandidateResultsJson = evidence,
                DecidedAt = _clock.UtcNow
            });
            if (rejected)
                db.TrackMatches.Add(new TrackMatchRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = snapshot.OwnerUserId,
                    ExternalSnapshotId = snapshot.Id,
                    State = TrackMatchState.Rejected,
                    DecisionVersion = 2,
                    SourceSnapshotVersion = 1,
                    CandidateResultsJson = evidence,
                    DecidedAt = _clock.UtcNow.AddSeconds(1)
                });
            await db.SaveChangesAsync();
        }
        var gateway = new ProtocolProviderGateway(Mock.Of<IProviderRouter>(MockBehavior.Strict), new ProviderRegistry([]),
            Mock.Of<IHttpClientFactory>(), identities: _service);
        foreach (var alias in songs)
        {
            var song = await gateway.GetStoredSongAsync(DisplayProtocol(ProtocolKind.Jellyfin), alias.ExternalProvider!, alias.ExternalId!);
            if (!expected) { Assert.Null(song); continue; }
            Assert.Equal(songs[0].Id, song!.Id);
            Assert.Equal("deezer", song.ExternalProvider);
            Assert.Equal("2925172991", song.ExternalId);
            Assert.Equal("Check", song.Album);
            Assert.Equal("FLO", song.Artist);
            Assert.Equal("GBUM72403956", song.Isrc);
        }
    }

    [Fact]
    public async Task PublicSongAliases_UseTheSameStoredDisplaySourceAndArtwork()
    {
        var songs = await SeedDisplaySourcesAsync();
        var gateway = new ProtocolProviderGateway(Mock.Of<IProviderRouter>(MockBehavior.Strict),
            new ProviderRegistry([]), Mock.Of<IHttpClientFactory>(MockBehavior.Strict), identities: _service);
        foreach (var alias in songs)
        {
            var song = await gateway.GetPublicSongAsync(alias.ExternalProvider!, alias.ExternalId!);
            Assert.Equal(songs[3].AlbumId, song!.AlbumId);
            Assert.Equal(songs[3].CoverArtUrl, song.CoverArtUrl);
            Assert.Equal(songs[3].CoverArtUrl, (await gateway.GetPublicArtworkUriAsync(alias.ExternalProvider!,
                ProviderResourceKind.Track, alias.ExternalId!))!.AbsoluteUri);
        }
    }

    [Fact]
    public async Task StoredDisplaySources_ExcludeAnotherListenersSnapshots()
    {
        var songs = await SeedDisplaySourcesAsync(appleOwnedByAnotherListener: true);
        var gateway = new ProtocolProviderGateway(Mock.Of<IProviderRouter>(MockBehavior.Strict),
            new ProviderRegistry([]), Mock.Of<IHttpClientFactory>(MockBehavior.Strict), identities: _service);
        var song = await gateway.GetStoredSongAsync(DisplayProtocol(ProtocolKind.Jellyfin), "spotiflac-soundcloud", "1886663583");
        Assert.Equal("deezer", song!.ExternalProvider);
        Assert.Equal(songs[1].AlbumId, song.AlbumId);
        Assert.Null(await gateway.GetStoredSongAsync(DisplayProtocol(ProtocolKind.Jellyfin), "deezer", "unknown"));
    }

    private ProtocolExecutionContext DisplayProtocol(ProtocolKind kind) => new(kind, "fixture", _userA.ToString("N"),
        new AllstarrPrincipal(_userA, kind.ToString().ToLowerInvariant(), "fixture", _userA.ToString("N"), "User", false),
        "display", DateTimeOffset.UtcNow.AddMinutes(1), default, new("client", "device"));

    private async Task<Song[]> SeedDisplaySourcesAsync(bool appleOwnedByAnotherListener = false, bool retainSnapshots = true)
    {
        const string publicId = "ext-allstarr-song-01a1540a-0842-7777-969e-50303caad5c3";
        var recording = (await _service.CreateRecordingAsync(Actor(_userA), "display", "GBUM72403956", null, publicId)).Recording;
        var sources = new[]
        {
            ("spotiflac-soundcloud", "1886663583", "Check", (string?)null),
            ("deezer", "2925172991", "Check", "ext-deezer-album-622458821"),
            ("spotiflac-tidal-web", "tidal:378292110", "Check", "ext-spotiflac-tidal-web-album-tidal:378292109"),
            ("apple-musickit", "1780787517", "Access All Areas", "ext-apple-download-album-1780787475")
        };
        var songs = new List<Song>();
        foreach (var (provider, externalId, album, albumId) in sources)
        {
            await Link(recording.Id, Context(Actor(_userA), provider), externalId);
            var song = new Song
            {
                Id = publicId,
                ExternalProvider = provider == "apple-musickit" ? "apple-download" : provider,
                ExternalId = externalId,
                Title = "Check",
                Artist = "FLO",
                Artists = ["FLO"],
                Album = album,
                AlbumId = albumId,
                AlbumArtist = "FLO",
                Year = provider == "apple-musickit" ? 2024 : 2023,
                ReleaseDate = provider == "apple-musickit" ? "2024-11-15" : "2023-08-04",
                Size = 12_000_000,
                Bitrate = 900_000,
                Codec = "flac",
                SampleRate = 96_000,
                BitDepth = 24,
                Channels = 2,
                CoverArtUrl = $"https://images.example.test/{provider}",
                Isrc = "GBUM72403956",
                Duration = 180,
                Composer = $"Composer from {provider}"
            };
            songs.Add(song);
            if (!retainSnapshots) continue;
            await using var db = await _factory.CreateDbContextAsync();
            var link = await db.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == externalId);
            var payload = JsonSerializer.Serialize(song);
            db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = provider == "apple-musickit" && appleOwnedByAnotherListener ? _userB : _userA,
                ProviderId = provider,
                ProviderTrackIdentityId = link.Id,
                ExternalIdHash = link.ExternalIdHash,
                ResourceKind = "track",
                SnapshotVersion = 1,
                ProviderRevision = "fixture",
                PayloadJson = payload,
                PayloadSha256 = Hash(payload),
                RetrievedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        return songs.ToArray();
    }
}
