using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class NewReleasesTests
{
    [Fact]
    public async Task Admin_unfollow_removes_the_native_release_follow_and_preserves_a_direct_service_follow()
    {
        await Follow(_first);
        await _service.FollowAsync(_first, new("deezer", "a", "Artist", LibraryArtistId: "native-artist"));
        Assert.Equal(2, (await _service.ListAsync(_first)).Count);
        var controller = ReleaseController(Mock.Of<IProtocolProviderGateway>());

        Assert.IsType<NoContentResult>(await controller.Unfollow("deezer", "a", default, "native-artist"));

        var remaining = Assert.Single(await _service.ListAsync(_first));
        Assert.Null(remaining.LibraryArtistId);
        Assert.True(remaining.IsDirectFollow);
    }

    [Fact]
    public async Task Nine_followed_artists_finish_daily_polling_across_spacing_and_temporary_route_deferrals()
    {
        var account = new ProviderAccountContext(Guid.CreateVersion7(), "deezer", ProviderAccountScope.Shared, 1);
        var accounts = new Mock<IProviderRouteAccountResolver>();
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderRouteAccountResolution(account, 1));
        _metadata.SetupGet(item => item.ProviderId).Returns("deezer");
        _metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var descriptor = new ProviderDescriptor("deezer", "Deezer", "Catalog fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                "1", ["searchTracks", "getTrack", "getArtistAlbums"], allowedAccountScopes: [ProviderAccountScope.Shared])], new());
        var health = new ProviderRuntimeHealth(clock: _clock);
        var router = new ProviderRouter(new ProviderRegistry([new(descriptor, [_metadata.Object])]), accounts.Object,
            new RuntimeProviderRouteHealthSource(health), Mock.Of<IProviderRouteSidecarSource>(), Mock.Of<ITrackIdentityService>(), health);
        var calls = new List<(string Artist, DateTimeOffset At)>();
        ProviderExecutionContext? lastContext = null;
        _metadata.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync((ProviderExecutionContext context, ProviderArtistItemsRequest request) =>
            {
                lastContext = context;
                calls.Add((request.Id.Value, _clock.UtcNow));
                return ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer", [Album(id: request.Id.Value)]));
            });
        for (var index = 0; index < 9; index++) await Follow(_first, artist: index.ToString());
        var jobId = Guid.CreateVersion7();
        var service = new NewReleasesService(_factory, _clock, router, _settings.Object);
        for (var day = 0; day < 2; day++)
        {
            var result = await service.PollAsync(_first, jobId, default);
            Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
            Assert.Equal(TimeSpan.FromSeconds(2), result.RetryDelay);
            health.Observe(lastContext!, ProviderCapabilityKind.Metadata,
                new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)), TimeSpan.Zero);
            _clock.UtcNow += result.RetryDelay!.Value;
            result = await service.PollAsync(_first, jobId, default);
            Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
            for (var attempt = 0; result.Kind == DurableJobCompletionKind.Deferred && attempt < 12; attempt++)
            {
                _clock.UtcNow += result.RetryDelay!.Value;
                service = new(_factory, _clock, router, _settings.Object);
                result = await service.PollAsync(_first, jobId, default);
            }
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            Assert.Equal((day + 1) * 9, calls.Count);
            Assert.All((await service.ReadAsync(_first)).Coverage, item => Assert.Equal("known", item.Status));
            _clock.UtcNow = new(_clock.UtcNow.UtcDateTime.Date.AddDays(1).AddHours(4), TimeSpan.Zero);
        }
        Assert.All(calls.Zip(calls.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromSeconds(2)));
        Assert.All(calls.GroupBy(item => item.Artist), group => Assert.Equal(2, group.Count()));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(9, await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "release-catalog")
            .Select(item => item.ExternalIdHash).Distinct().CountAsync());
    }

    [Fact]
    public async Task Library_follow_without_a_verified_service_has_its_own_coverage()
    {
        await _service.FollowAsync(_first, new("jellyfin", "native-artist", "Artist", LibraryArtistId: "native-artist", LibraryImageTag: "native-picture"));
        Assert.Equal("library-only", Assert.Single((await _service.ReadAsync(_first)).Coverage).Status);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Poll(_first)).Kind);
        Assert.Equal(0, _calls);
    }

    [Fact]
    public async Task Library_artist_identifier_alone_does_not_follow_a_namesake_with_different_songs()
    {
        var wrong = Guid.NewGuid().ToString();
        ConfigureLibraryArtists(LibraryArtist("namesake", mbid: wrong, songs: ["Other song"]));

        Assert.Null(await _service.ResolveLibraryArtistAsync(_first, "FLO", wrong, [], ["Side Effects", "Cry Ugly"]));
        Assert.Null(await _service.ResolveLibraryArtistAsync(_first, "FLO", wrong, [], []));
    }

    [Fact]
    public async Task Library_artist_with_the_same_name_and_no_title_overlap_is_rejected()
    {
        ConfigureLibraryArtists(LibraryArtist("different", albums: ["Other album"], songs: ["Other song"]));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", null, ["Library album"], ["Library song"]);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Library_artist_identifier_match_is_preferred_over_more_title_overlap()
    {
        var mbid = Guid.NewGuid().ToString();
        ConfigureLibraryArtists(
            LibraryArtist("titles", albums: ["First album", "Second album"], songs: ["Song"]),
            LibraryArtist("identity", mbid: mbid.ToUpperInvariant(), identifierKey: "mUsIcBrAiNzArTiSt", songs: ["Song"]));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", mbid,
            ["First album", "Second album"], ["Song"]);

        Assert.Equal("identity", resolved?.ArtistId);
    }

    [Fact]
    public async Task Library_artist_can_match_an_identifier_supplied_by_artist_details()
    {
        var mbid = Guid.NewGuid().ToString();
        var sample = LibraryArtist("details", songs: ["Song"]);
        ConfigureLibraryArtists(sample with
        {
            Details = new(sample.Artist.Id, "Artist", providerIds: new Dictionary<string, string> { ["MusicBrainzArtist"] = mbid })
        });

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", mbid, [], ["Song"]);

        Assert.Equal("details", resolved?.ArtistId);
        _metadata.Verify(value => value.GetArtistAsync(It.IsAny<ProviderExecutionContext>(),
            It.Is<ProviderArtistLookupRequest>(request => request.Id.Value == "details")), Times.Once);
    }

    [Fact]
    public async Task Library_artist_matches_normalized_album_titles()
    {
        ConfigureLibraryArtists(LibraryArtist("album", albums: ["CAFÉ & MOON!"]));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", null, ["  Cafe and Moon  "], []);

        Assert.Equal("album", resolved?.ArtistId);
    }

    [Fact]
    public async Task Library_artist_matches_a_top_track_title()
    {
        ConfigureLibraryArtists(LibraryArtist("song", songs: ["Library song"]));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", null, [], ["Library song"]);

        Assert.Equal("song", resolved?.ArtistId);
    }

    [Fact]
    public async Task Library_artist_with_the_most_distinct_title_matches_is_selected()
    {
        ConfigureLibraryArtists(
            LibraryArtist("duplicates", albums: ["First album", "First album", "First album"]),
            LibraryArtist("more-evidence", albums: ["First album"], songs: ["Library song"]));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", null,
            ["First album", "First album"], ["Library song"]);

        Assert.Equal("more-evidence", resolved?.ArtistId);
    }

    [Fact]
    public async Task Library_artists_with_equal_title_evidence_are_rejected()
    {
        ConfigureLibraryArtists(
            LibraryArtist("first", albums: ["Library album"]),
            LibraryArtist("second", songs: ["Library song"]));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", null, ["Library album"], ["Library song"]);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Library_artist_uses_song_evidence_when_the_library_identifier_is_contradicted()
    {
        ConfigureLibraryArtists(LibraryArtist("conflict", mbid: Guid.NewGuid().ToString(),
            albums: ["Library album"], songs: ["Library song"]));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", Guid.NewGuid().ToString(),
            ["Library album"], ["Library song"]);

        Assert.Equal("conflict", resolved?.ArtistId);
        Assert.Contains("library artist ID", resolved?.ResolutionReason);
    }

    [Fact]
    public async Task Library_artist_can_use_a_stored_identifier_from_the_same_listener()
    {
        var mbid = Guid.NewGuid().ToString();
        ConfigureLibraryArtists(LibraryArtist("stored", songs: ["Song"]), LibraryArtist("other", songs: ["Song"]));
        await StoreLibraryArtistIdentifier(_first, "stored", mbid);

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", mbid, [], ["Song"]);

        Assert.Equal("stored", resolved?.ArtistId);
    }

    [Fact]
    public async Task Library_artist_does_not_use_another_listeners_stored_identifier()
    {
        var mbid = Guid.NewGuid().ToString();
        ConfigureLibraryArtists(LibraryArtist("stored", songs: ["Song"]), LibraryArtist("other", songs: ["Song"]));
        await StoreLibraryArtistIdentifier(_second, "stored", mbid);

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", mbid, [], ["Song"]);

        Assert.Null(resolved);
    }

    private void ConfigureLibraryArtists(params LibraryArtistSample[] samples)
    {
        _router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .Returns((ProviderRouteRequest request) =>
            {
                var descriptor = new ProviderDescriptor("deezer", "deezer", "Catalog fixture", ProviderOrigin.BuiltIn, "1", "1",
                    [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                        "1", ["searchArtists", "getArtist", "getArtistAlbums", "getArtistTracks"])], new());
                var context = new ProviderExecutionContext(request.Actor, "deezer", null, request.Policy,
                    request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
                return Task.FromResult(new ProviderRoutePlan<IProviderMetadataCapability>(request,
                    [new(0, descriptor, descriptor.Capabilities.Single(), _metadata.Object, context, null)],
                    new(request.CorrelationId, request.Capability, "deezer", null, [])));
            });
        _metadata.Setup(value => value.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer", samples.Select(item => item.Artist))));
        _metadata.Setup(value => value.GetArtistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistLookupRequest>()))
            .Returns((ProviderExecutionContext _, ProviderArtistLookupRequest request) =>
            {
                var sample = samples.Single(item => item.Artist.Id == request.Id);
                return Task.FromResult(ProviderOutcome<ProviderArtistMetadata>.Success(sample.Details ?? sample.Artist));
            });
        _metadata.Setup(value => value.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .Returns((ProviderExecutionContext _, ProviderArtistItemsRequest request) =>
            {
                var sample = samples.Single(item => item.Artist.Id == request.Id);
                var albums = sample.Albums.Select((title, index) => new ProviderAlbumMetadata(
                    new("deezer", ProviderResourceKind.Album, sample.Artist.Id.Value + "-album-" + index), title,
                    [new("Artist", sample.Artist.Id)]));
                return Task.FromResult(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer", albums)));
            });
        _metadata.Setup(value => value.GetArtistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .Returns((ProviderExecutionContext _, ProviderArtistItemsRequest request) =>
            {
                var sample = samples.Single(item => item.Artist.Id == request.Id);
                var songs = sample.Songs.Select((title, index) => new ProviderTrackMetadata(
                    new("deezer", ProviderResourceKind.Track, sample.Artist.Id.Value + "-song-" + index), title,
                    [new("Artist", sample.Artist.Id)]));
                return Task.FromResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", songs)));
            });
    }

    private static LibraryArtistSample LibraryArtist(string id, string? mbid = null, string identifierKey = "MusicBrainzArtist",
        string[]? albums = null, string[]? songs = null) => new(
        new(new("deezer", ProviderResourceKind.Artist, id), "Artist",
            providerIds: mbid == null ? null : new Dictionary<string, string> { [identifierKey] = mbid }),
        albums ?? [], songs ?? []);

    private async Task StoreLibraryArtistIdentifier(Guid owner, string artistId, string mbid)
    {
        await using var db = _factory.CreateDbContext();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(artistId))).ToLowerInvariant();
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ProviderId = "deezer",
            ResourceKind = "artist",
            ExternalIdHash = hash,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            PayloadJson = JsonSerializer.Serialize(new { ProviderIds = new Dictionary<string, string> { ["musicbrainzartist"] = mbid } }),
            PayloadSha256 = new string('a', 64),
            Protocol = "subsonic",
            BackendInstanceId = "main",
            BackendPrincipalId = owner.ToString(),
            RetrievedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private sealed record LibraryArtistSample(ProviderArtistMetadata Artist, string[] Albums, string[] Songs,
        ProviderArtistMetadata? Details = null);
}
