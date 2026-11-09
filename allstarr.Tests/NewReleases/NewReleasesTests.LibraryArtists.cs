using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using Moq;

namespace allstarr.Tests;

public sealed partial class NewReleasesTests
{
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
            LibraryArtist("identity", mbid: mbid.ToUpperInvariant(), identifierKey: "mUsIcBrAiNzArTiSt"));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", mbid,
            ["First album", "Second album"], ["Song"]);

        Assert.Equal("identity", resolved?.ArtistId);
    }

    [Fact]
    public async Task Library_artist_can_match_an_identifier_supplied_by_artist_details()
    {
        var mbid = Guid.NewGuid().ToString();
        var sample = LibraryArtist("details");
        ConfigureLibraryArtists(sample with
        {
            Details = new(sample.Artist.Id, "Artist", providerIds: new Dictionary<string, string> { ["MusicBrainzArtist"] = mbid })
        });

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", mbid, [], []);

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
    public async Task Library_artist_with_a_conflicting_known_identifier_is_rejected_despite_title_overlap()
    {
        ConfigureLibraryArtists(LibraryArtist("conflict", mbid: Guid.NewGuid().ToString(),
            albums: ["Library album"], songs: ["Library song"]));

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", Guid.NewGuid().ToString(),
            ["Library album"], ["Library song"]);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Library_artist_can_use_a_stored_identifier_from_the_same_listener()
    {
        var mbid = Guid.NewGuid().ToString();
        ConfigureLibraryArtists(LibraryArtist("stored"));
        await StoreLibraryArtistIdentifier(_first, "stored", mbid);

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", mbid, [], []);

        Assert.Equal("stored", resolved?.ArtistId);
    }

    [Fact]
    public async Task Library_artist_does_not_use_another_listeners_stored_identifier()
    {
        var mbid = Guid.NewGuid().ToString();
        ConfigureLibraryArtists(LibraryArtist("stored"));
        await StoreLibraryArtistIdentifier(_second, "stored", mbid);

        var resolved = await _service.ResolveLibraryArtistAsync(_first, "Artist", mbid, [], []);

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
