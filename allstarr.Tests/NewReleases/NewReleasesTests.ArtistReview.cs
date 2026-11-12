using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class NewReleasesTests
{
    [Fact]
    public async Task LibraryArtist_candidates_are_shared_and_written_only_when_changed()
    {
        var index = ArtistIndex(administrator: _first);
        Artist[] artists = [new() { Name = "ARTIST", ExternalProvider = "deezer", ExternalId = "namesake" }];
        var search = new MusicSearchService(Mock.Of<IProtocolProviderGateway>(), artistLinks: new LibraryArtistLinks(index, _queue, _clock));
        await search.RecordArtistCandidatesAsync(ArtistReviewContext(_first), ["Artist"], artists, default);
        await index.RecordCandidatesAsync(_second, ["Artist"], artists);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.ExternalMetadataSnapshots.CountAsync(row => row.ResourceKind == "library-artist-candidate"));
        Assert.Single((await index.ReadReviewAsync(_second, ["Artist"]))["Artist"].Candidates);
        Assert.Equal("needs-look", ArtistReviewRow.StatusFor(["candidate"]));
        Assert.False(await index.SetFixAsync(_second, "Artist", "deezer", "namesake", true, true));

        await index.SetFixAsync(_first, "Artist", "deezer", "namesake", false, false);
        Assert.Empty((await index.ReadReviewAsync(_first, ["Artist"]))["Artist"].Candidates);
        Assert.Single((await index.ReadReviewAsync(_second, ["Artist"]))["Artist"].Candidates);
        await index.SetFixAsync(_first, "Artist", "deezer", "namesake", null, false);
        Assert.Single((await index.ReadReviewAsync(_first, ["Artist"]))["Artist"].Candidates);
        await index.SetFixAsync(_first, "Artist", "deezer", "namesake", true, true);
        Assert.Empty((await index.ReadReviewAsync(_second, ["Artist"]))["Artist"].Candidates);
        await index.SetFixAsync(_first, "Artist", "deezer", "namesake", null, true);
        await ArtistSongAsync("Shared", "namesake");
        var linked = (await index.ReadReviewAsync(_second, ["Artist"]))["Artist"];
        Assert.Empty(linked.Candidates);
        Assert.Single(linked.Songs);
    }

    [Fact]
    public async Task LibraryArtist_candidates_ignore_empty_sources_and_other_names()
    {
        var empty = new EmptyServiceArtists();
        for (var number = 0; number < 10; number++) empty.Record("deezer", number.ToString(), 0);
        var index = ArtistIndex(empty: empty);
        await index.RecordCandidatesAsync(_first, ["Artist"],
        [
            new() { Name = "Artist", ExternalProvider = "deezer", ExternalId = "new" },
            new() { Name = "Different", ExternalProvider = "qobuz", ExternalId = "other" },
            new() { Name = "Artist", ExternalProvider = "qobuz", ExternalId = "credit:placeholder" }
        ]);
        Assert.Empty((await index.ReadReviewAsync(_first, ["Artist"]))["Artist"].Candidates);
    }

    [Fact]
    public async Task LibraryArtist_new_candidates_from_different_listeners_share_one_household_state()
    {
        var index = ArtistIndex();
        Artist[] first = [new() { Name = "Artist", ExternalProvider = "deezer", ExternalId = "first" }];
        Artist[] second = [new() { Name = "Artist", ExternalProvider = "qobuz", ExternalId = "second" }];
        await index.RecordCandidatesAsync(_first, ["Artist"], first);
        await index.RecordCandidatesAsync(_second, ["Artist"], second);
        Assert.Equal(2, (await index.ReadReviewAsync(_first, ["Artist"]))["Artist"].Candidates.Count);
        Assert.Equal(2, (await index.ReadReviewAsync(_second, ["Artist"]))["Artist"].Candidates.Count);
        await index.RecordCandidatesAsync(_first, ["Artist"], first.Concat(second).ToArray());
        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.ExternalMetadataSnapshots.CountAsync(row => row.ResourceKind == "library-artist-candidate"));
    }

    [Fact]
    public async Task LibraryArtist_observations_do_not_save_candidates_for_song_links_or_household_fixes()
    {
        await ArtistSongAsync("Shared", "linked");
        var index = ArtistIndex(administrator: _first);
        await index.SetFixAsync(_first, "Artist", "deezer", "fixed", true);
        await index.RecordCandidatesAsync(_first, ["Artist"],
        [
            new() { Name = "Artist", ExternalProvider = "deezer", ExternalId = "linked" },
            new() { Name = "Artist", ExternalProvider = "deezer", ExternalId = "fixed" }
        ]);
        await using var db = _factory.CreateDbContext();
        Assert.False(await db.ExternalMetadataSnapshots.AnyAsync(row => row.ResourceKind == "library-artist-candidate"));
    }

    [Fact]
    public async Task LibraryArtist_detail_uses_the_song_link_rules_for_reasons_and_titles()
    {
        var track = await ArtistSongAsync("Shared title", null);
        await ArtistIdentityAsync(track, "renamed", creditedAs: "Artiste");
        var index = ArtistIndex();
        var detail = (await index.ReadReviewAsync(_first, ["Artist"], detail: true))["Artist"];
        var evidence = Assert.Single(detail.Evidence);
        Assert.Equal("name differs, 1 shared song (needs 2)", evidence.Reason);
        Assert.Equal(["Shared title"], evidence.SongTitles);
        await index.RecordCandidatesAsync(_first, ["Artist"], [new() { Name = "Artist", ExternalProvider = "deezer", ExternalId = "namesake" }]);
        Assert.Equal("same name, no shared songs", Assert.Single((await index.ReadReviewAsync(_first, ["Artist"]))["Artist"].Candidates).Reason);
    }

    [Fact]
    public async Task LibraryArtist_shared_song_examples_are_capped_at_five()
    {
        for (var number = 0; number < 6; number++) await ArtistSongAsync("Shared " + number, "linked");
        var detail = (await ArtistIndex().ReadReviewAsync(_first, ["Artist"], detail: true))["Artist"];
        var evidence = Assert.Single(detail.Evidence);
        Assert.Equal(6, evidence.SharedSongs);
        Assert.Equal(5, evidence.SongTitles.Count);
    }

    [Fact]
    public async Task LibraryArtist_detail_keeps_ambiguous_song_credits_out_of_the_proof_count()
    {
        var song = await ArtistSongAsync("Ambiguous", "first");
        await ArtistIdentityAsync(song, "second");
        var detail = (await ArtistIndex().ReadReviewAsync(_first, ["Artist"], detail: true))["Artist"];
        Assert.Empty(detail.Songs);
        Assert.Equal(2, detail.Evidence.Count);
        Assert.All(detail.Evidence, evidence =>
        {
            Assert.Equal(0, evidence.SharedSongs);
            Assert.Empty(evidence.SongTitles);
            Assert.Equal("song credits disagree", evidence.Reason);
        });
    }
}
