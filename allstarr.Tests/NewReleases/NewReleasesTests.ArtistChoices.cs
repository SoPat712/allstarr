using allstarr.Core.Capabilities;
using allstarr.Core.Releases;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class NewReleasesTests
{
    [Fact]
    public async Task Release_artist_choice_overrides_song_links_and_none_survives_reindexing_and_refollowing()
    {
        var song = await ArtistSongAsync("First", "automatic");
        await ArtistSongAsync("Second", "automatic");
        ConfigureLibraryArtists(LibraryArtist("chosen", albums: ["First album", "Second album"]));
        _metadata.Setup(value => value.GetArtistAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderArtistLookupRequest>(request => request.Id.Value == "automatic")))
            .ReturnsAsync(ProviderOutcome<ProviderArtistMetadata>.Success(new(new("deezer", ProviderResourceKind.Artist, "automatic"), "Current artist")));
        var service = new NewReleasesService(_factory, _clock, _router.Object, _settings.Object, artistIndex: ArtistIndex());
        await service.FollowAsync(_first, new("jellyfin", "native", "Artist", LibraryArtistId: "native"));
        var candidates = await service.ArtistCandidatesAsync(_first, "native");
        Assert.Equal("automatic", candidates.Artist.ArtistId);
        Assert.Equal("Current artist", candidates.CurrentChoice?.DisplayName);
        Assert.Equal(["First album", "Second album"], Assert.Single(candidates.Candidates).Hints);
        await service.ChooseArtistAsync(_first, "native", "deezer", "chosen");
        await ArtistIdentityAsync(song, "changed");
        service = new(_factory, _clock, _router.Object, _settings.Object, artistIndex: ArtistIndex());
        var chosen = Assert.Single(await service.ListAsync(_first));
        Assert.Equal("chosen", chosen.ArtistId);
        Assert.True(chosen.ListenerConfirmed);
        await service.ChooseArtistAsync(_first, "native", null, null);
        await service.PollAsync(_first, Guid.CreateVersion7(), default);
        Assert.Equal("library-only", Assert.Single((await service.ReadAsync(_first)).Coverage).Status);
        await service.UnfollowAsync(_first, "jellyfin", "native", libraryArtistId: "native");
        await service.FollowAsync(_first, new("deezer", "automatic", "Artist", LibraryArtistId: "native"));
        var none = Assert.Single(await service.ListAsync(_first));
        Assert.Equal("jellyfin", none.ProviderId);
        Assert.True(none.ListenerConfirmed);
    }

    [Fact]
    public async Task Release_artist_candidates_are_bounded_and_only_the_listener_can_choose_them()
    {
        ConfigureLibraryArtists(Enumerable.Range(0, 8).Select(number => LibraryArtist("candidate-" + number, songs: ["Song one", "Song two"])).ToArray());
        await _service.FollowAsync(_first, new("jellyfin", "native", "Artist", LibraryArtistId: "native"));
        await _service.FollowAsync(_second, new("jellyfin", "native", "Artist", LibraryArtistId: "native"));
        var choices = await _service.ArtistCandidatesAsync(_first, "native");
        Assert.Equal(5, choices.Candidates.Length);
        Assert.All(choices.Candidates, candidate => Assert.Equal(2, candidate.Hints.Length));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ChooseArtistAsync(_first, "native", "deezer", "candidate-7"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ChooseArtistAsync(_second, "native", "deezer", "candidate-0"));
        await _service.ChooseArtistAsync(_first, "native", "deezer", "candidate-0");
        Assert.False(Assert.Single(await _service.ListAsync(_second)).ListenerConfirmed);
        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.ExternalMetadataSnapshots.Where(row => row.ResourceKind == "library-artist-choice").ToArrayAsync());
    }

    [Fact]
    public async Task Release_legacy_library_identifier_follow_waits_for_song_evidence_and_explains_coverage()
    {
        await _service.FollowAsync(_first, new("deezer", "namesake", "FLO", Guid.NewGuid().ToString(), LibraryArtistId: "native"));
        var service = new NewReleasesService(_factory, _clock, _router.Object, _settings.Object, artistIndex: ArtistIndex());

        var coverage = Assert.Single((await service.ReadAsync(_first)).Coverage);

        Assert.Equal("library-only", coverage.Status);
        Assert.NotNull(coverage.Reason);
        Assert.Equal("jellyfin", coverage.Artist.ProviderId);
    }
}
