using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class NewReleasesTests
{
    [Fact]
    public async Task LibraryIndex_artist_votes_count_recordings_once_and_require_a_strict_majority()
    {
        var first = await ArtistSongAsync("First", "winner");
        await ArtistIdentityAsync(first, "winner");
        await ArtistSongAsync("Second", "winner");
        await ArtistSongAsync("Third", "other");
        var index = ArtistIndex();

        Assert.Equal("winner", Assert.Single(await index.ReadAsync(_first, "Artist")).ArtistId);
        await ArtistSongAsync("Fourth", "other");
        Assert.Empty(await index.ReadAsync(_first, "Artist"));
        await ArtistSongAsync("Fifth", "third");
        Assert.Empty(await index.ReadAsync(_first, "Artist"));
    }

    [Fact]
    public async Task LibraryIndex_artist_links_on_one_song_when_the_service_credits_the_same_name()
    {
        await ArtistSongAsync("Only", "solo");

        Assert.Equal(new LibraryArtistLink("deezer", "solo"), Assert.Single(await ArtistIndex().ReadAsync(_first, "Artist")));
    }

    [Fact]
    public async Task LibraryIndex_artist_credited_under_another_name_needs_two_songs()
    {
        // A CD rip tagged differently from the service still links once two of its songs match.
        var first = await ArtistSongAsync("First", null);
        await ArtistIdentityAsync(first, "renamed", creditedAs: "Artiste");
        Assert.Empty(await ArtistIndex().ReadAsync(_first, "Artist"));

        var second = await ArtistSongAsync("Second", null);
        await ArtistIdentityAsync(second, "renamed", creditedAs: "Artiste");
        Assert.Equal("renamed", Assert.Single(await ArtistIndex().ReadAsync(_first, "Artist")).ArtistId);
    }

    [Fact]
    public async Task LibraryIndex_artist_skips_an_ambiguous_song_instead_of_the_whole_service()
    {
        var first = await ArtistSongAsync("First", "winner");
        await ArtistIdentityAsync(first, "conflict");
        await ArtistSongAsync("Second", "winner");
        await ArtistSongAsync("Third", "winner");

        Assert.Equal("winner", Assert.Single(await ArtistIndex().ReadAsync(_first, "Artist")).ArtistId);
    }

    [Fact]
    public async Task LibraryIndex_artist_conflicting_track_identities_leave_the_service_unlinked()
    {
        var first = await ArtistSongAsync("First", "winner");
        await ArtistSongAsync("Second", "winner");
        await ArtistIdentityAsync(first, "conflict");

        Assert.Empty(await ArtistIndex().ReadAsync(_first, "Artist"));
    }

    [Fact]
    public async Task LibraryIndex_artist_keeps_each_services_id_and_accepts_public_artist_aliases()
    {
        var first = await ArtistSongAsync("First", "deezer-artist");
        var second = await ArtistSongAsync("Second", "deezer-artist");
        await ArtistIdentityAsync(first, "apple-artist", "apple-download", "apple-download");
        await ArtistIdentityAsync(second, "apple-artist", "apple-musickit", "apple-download");

        var links = await ArtistIndex().ReadAsync(_first, "Artist");

        Assert.Equal(2, links.Count);
        Assert.Contains(new LibraryArtistLink("deezer", "deezer-artist"), links);
        Assert.Contains(new LibraryArtistLink("apple-musickit", "apple-artist"), links);
    }

    [Fact]
    public async Task LibraryIndex_album_artist_does_not_inherit_an_unrelated_performers_id()
    {
        await ArtistSongAsync("First", "performer");
        await ArtistSongAsync("Second", "performer");
        await using (var db = _factory.CreateDbContext())
        {
            foreach (var song in await db.LibraryTracks.ToArrayAsync()) song.AlbumArtist = "Compilation";
            await db.SaveChangesAsync();
        }

        Assert.Empty(await ArtistIndex().ReadAsync(_first, "Compilation"));
    }

    [Fact]
    public async Task LibraryIndex_artist_keeps_a_full_group_credit_when_it_contains_a_separator()
    {
        await ArtistSongAsync("First", "group", "Écho & South");
        await ArtistSongAsync("Second", "group", "Écho & South");
        Assert.Equal("group", Assert.Single(await ArtistIndex().ReadAsync(_first, "Écho & South")).ArtistId);
    }

    [Fact]
    public async Task LibraryIndex_artist_refreshes_after_a_song_link_or_metadata_changes_and_reuses_unchanged_snapshots()
    {
        var first = await ArtistSongAsync("First", "winner");
        var second = await ArtistSongAsync("Second", "winner");
        var index = ArtistIndex();
        Assert.Single(await index.ReadAsync(_first, "Artist"));
        Assert.Single(await index.ReadAsync(_first, "Artist"));
        await using (var db = _factory.CreateDbContext())
        {
            Assert.Single(await db.ExternalMetadataSnapshots.Where(row => row.ResourceKind == "library-artist-index").ToArrayAsync());
            foreach (var link in await db.ProviderTrackIdentities.ToArrayAsync()) link.VerificationMethod = "superseded";
            await db.SaveChangesAsync();
        }
        Assert.Empty(await index.ReadAsync(_first, "Artist"));
        await ArtistIdentityAsync(first, "winner");
        await ArtistIdentityAsync(second, "winner");
        Assert.Single(await ArtistIndex().ReadAsync(_first, "Artist"));
        await using (var db = _factory.CreateDbContext())
        {
            var old = await db.ExternalMetadataSnapshots.FirstAsync(row => row.ProviderTrackIdentityId != null &&
                row.ProviderTrackIdentityId == db.ProviderTrackIdentities.Where(link => link.CanonicalRecordingId == first.CanonicalRecordingId &&
                    link.VerificationMethod != "superseded").Select(link => link.Id).Single());
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = old.OwnerUserId,
                ProviderId = old.ProviderId,
                ResourceKind = old.ResourceKind,
                ProviderTrackIdentityId = old.ProviderTrackIdentityId,
                ExternalIdHash = old.ExternalIdHash,
                SnapshotVersion = old.SnapshotVersion + 1,
                ProviderRevision = "2",
                PayloadSha256 = new string('b', 64),
                PayloadJson = JsonSerializer.Serialize(new { Title = "First", Artist = "Artist", ArtistId = "ext-deezer-artist-changed" }),
                Protocol = old.Protocol,
                BackendInstanceId = old.BackendInstanceId,
                BackendPrincipalId = old.BackendPrincipalId,
                RetrievedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        Assert.Empty(await index.ReadAsync(_first, "Artist"));
    }

    [Fact]
    public async Task LibraryIndex_artist_shares_public_catalog_metadata_but_requires_access_and_verified_links()
    {
        await ArtistSongAsync("First", "winner");
        var second = await ArtistSongAsync("Second", "winner");
        Assert.Single(await ArtistIndex().ReadAsync(_second, "Artist"));
        await using (var db = _factory.CreateDbContext())
        {
            foreach (var link in await db.ProviderTrackIdentities.ToArrayAsync()) link.VerificationMethod = "automatic-suggestion";
            await db.SaveChangesAsync();
        }
        Assert.Empty(await ArtistIndex().ReadAsync(_first, "Artist"));
        Assert.Empty(await ArtistIndex().ReadAsync(_second, "Artist"));
        Assert.Empty(await ArtistIndex(libraries: ["private"]).ReadAsync(_first, "Artist"));
    }

    [Fact]
    public async Task LibraryIndex_artist_does_not_share_private_account_identities()
    {
        var first = await ArtistSongAsync("First", null);
        var second = await ArtistSongAsync("Second", null);
        var account = Guid.CreateVersion7();
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = _first, ProviderId = "deezer", DisplayName = "Private", Enabled = true });
            await db.SaveChangesAsync();
        }
        await ArtistIdentityAsync(first, "private-artist", accountId: account);
        await ArtistIdentityAsync(second, "private-artist", accountId: account);

        Assert.Single(await ArtistIndex().ReadAsync(_first, "Artist"));
        Assert.Empty(await ArtistIndex().ReadAsync(_second, "Artist"));
    }

    [Fact]
    public async Task LibraryIndex_verified_artist_replaces_search_follows_and_drives_release_checks_without_name_search()
    {
        await ArtistSongAsync("First", "verified");
        await ArtistSongAsync("Second", "verified");
        var service = new NewReleasesService(_factory, _clock, _router.Object, _settings.Object, artistIndex: ArtistIndex());
        var resolved = await service.ResolveLibraryArtistAsync(_first, "Artist", null, [], []);
        Assert.Equal("verified", resolved?.ArtistId);
        await service.FollowAsync(_first, new("deezer", "weaker", "Artist", LibraryArtistId: "native"));
        Assert.Equal("verified", Assert.Single(await service.ListAsync(_first)).ArtistId);
        await service.PollAsync(_first, Guid.CreateVersion7(), default);
        _metadata.Verify(value => value.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(),
            It.Is<ProviderArtistItemsRequest>(request => request.Id.Value == "verified")), Times.Once);
        _metadata.Verify(value => value.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
        await StoreLibraryArtistIdentifier(_first, "verified", Guid.NewGuid().ToString());
        _router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderMetadataCapability>(request, [],
                new(request.CorrelationId, request.Capability, "deezer", null, [])));
        await service.FollowAsync(_first, new("jellyfin", "native", "Artist", LibraryArtistId: "native",
            LibraryMusicBrainzArtistId: Guid.NewGuid().ToString()));
        Assert.NotNull(Assert.Single((await service.ReadAsync(_first)).Coverage).Reason);
    }

    [Fact]
    public async Task LibraryIndex_release_follow_stops_using_a_link_when_its_song_evidence_conflicts()
    {
        var first = await ArtistSongAsync("First", "verified");
        await ArtistSongAsync("Second", "verified");
        var service = new NewReleasesService(_factory, _clock, _router.Object, _settings.Object, artistIndex: ArtistIndex());
        await service.FollowAsync(_first, new("jellyfin", "native", "Artist", LibraryArtistId: "native"));
        Assert.True(Assert.Single(await service.ListAsync(_first)).FromSongLinks);
        await ArtistIdentityAsync(first, "conflict");
        Assert.Equal("jellyfin", Assert.Single(await service.ListAsync(_first)).ProviderId);
        Assert.Equal("library-only", Assert.Single((await service.ReadAsync(_first)).Coverage).Status);
    }

    [Fact]
    public async Task LibraryIndex_unlinked_follow_matches_songs_in_the_background_then_checks_releases()
    {
        await ArtistSongAsync("First", null);
        await ArtistSongAsync("Second", null);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(value => value.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync((ProtocolExecutionContext _, string provider, string query, int _) => ProviderOutcome<IReadOnlyList<Song>>.Success(
                new[] { "First", "Second" }.Where(query.Contains).Select(title => new Song
                {
                    Title = title,
                    Artist = "Artist",
                    Duration = 180,
                    ExternalProvider = "deezer",
                    ExternalId = title,
                    ArtistId = "ext-deezer-artist-verified"
                }).ToArray()));
        var identities = new Mock<ITrackIdentityService>();
        gateway.Setup(value => value.RememberTrackAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>()))
            .Returns(async (ProtocolExecutionContext _, string publicId) =>
            {
                await using var db = _factory.CreateDbContext();
                var local = await db.LibraryTracks.SingleAsync(row => "ext-deezer-song-" + row.Title == publicId);
                await ArtistIdentityAsync(local, "verified");
            });
        var index = ArtistIndex(gateway.Object, identities.Object);
        var service = new NewReleasesService(_factory, _clock, _router.Object, _settings.Object, artistIndex: index);
        await service.FollowAsync(_first, new("jellyfin", "native", "Artist", LibraryArtistId: "native"));

        Assert.Empty(await index.ReadAsync(_first, "Artist"));
        await service.PollAsync(_first, Guid.CreateVersion7(), default);

        Assert.Equal("verified", Assert.Single(await service.ListAsync(_first)).ArtistId);
        Assert.Equal(1, _calls);
        // The first matched song credits the same name, so the link holds and the rest of the run is skipped.
        gateway.Verify(value => value.RememberTrackAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>()), Times.Once);
        identities.Verify(value => value.LinkLibraryRecordingsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(), null,
            It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LibraryIndex_artist_backfill_is_bounded_across_continuations_of_the_same_run()
    {
        for (var i = 0; i < 8; i++) await ArtistSongAsync("Song " + i, null);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(value => value.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([]));
        var index = ArtistIndex(gateway.Object);
        var job = Guid.CreateVersion7();
        Assert.Equal(5, await index.BackfillAsync(_first, "Artist", job, default));
        Assert.Equal(0, await index.BackfillAsync(_first, "Artist", job, default));
        Assert.Equal(3, await index.BackfillAsync(_first, "Artist", Guid.CreateVersion7(), default));
        Assert.Empty(await index.ReadAsync(_first, "Artist"));
        gateway.Verify(value => value.RememberTrackAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>()), Times.Never);
    }

    private LibraryArtistIndex ArtistIndex(IProtocolProviderGateway? gateway = null, ITrackIdentityService? identities = null, string[]? libraries = null)
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(value => value.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid owner, CancellationToken _) => new BackendLibraryAccessContext(new(ProtocolKind.Subsonic, "main", owner.ToString(),
                new AllstarrPrincipal(owner, "subsonic", "main", owner.ToString(), "Listener", false), "artist-index", _clock.UtcNow.AddMinutes(10), default),
                new(true, libraries ?? ["music"])));
        gateway ??= Mock.Of<IProtocolProviderGateway>();
        return new(_factory, access.Object, _clock,
            new TrackMatchPlayableSearch(gateway, new(), NullLogger<TrackMatchPlayableSearch>.Instance),
            gateway, identities ?? Mock.Of<ITrackIdentityService>());
    }

    private async Task<LibraryTrackRecord> ArtistSongAsync(string title, string? artistId, string artist = "Artist")
    {
        await using var db = _factory.CreateDbContext();
        var recording = new CanonicalRecordingRecord
        {
            Id = Guid.CreateVersion7(),
            CreatedByUserId = _first,
            Title = title,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        db.CanonicalRecordings.Add(recording);
        var song = new LibraryTrackRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = _first,
            CanonicalRecordingId = recording.Id,
            Protocol = "subsonic",
            BackendInstanceId = "main",
            BackendLibraryId = "music",
            BackendItemId = title,
            FilePath = "/music/" + title,
            Title = title,
            Artist = artist,
            DurationMilliseconds = 180000,
            IndexedAt = _clock.UtcNow,
            SourceModifiedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        db.LibraryTracks.Add(song);
        await db.SaveChangesAsync();
        if (artistId != null) await ArtistIdentityAsync(song, artistId);
        return song;
    }

    private async Task ArtistIdentityAsync(LibraryTrackRecord song, string artistId, string provider = "deezer", string? publicProvider = null,
        Guid? accountId = null, string? creditedAs = null)
    {
        await using var db = _factory.CreateDbContext();
        var external = Guid.CreateVersion7().ToString();
        var link = new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            CanonicalRecordingId = song.CanonicalRecordingId!.Value,
            ProviderId = provider,
            ResourceKind = ProviderResourceKind.Track,
            Scope = accountId == null ? ProviderIdentityScope.Catalog : ProviderIdentityScope.Account,
            ProviderAccountId = accountId,
            ExternalId = external,
            ExternalIdHash = CanonicalCatalogKeys.Hash(external),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "availability",
            DecisionVersion = 1,
            VerifiedAt = _clock.UtcNow,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        db.ProviderTrackIdentities.Add(link);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = _first,
            ProviderId = provider,
            ResourceKind = "playback-metadata",
            ProviderAccountId = accountId,
            ProviderTrackIdentityId = link.Id,
            ExternalIdHash = link.ExternalIdHash,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            PayloadJson = JsonSerializer.Serialize(new { song.Title, Artist = creditedAs ?? song.Artist, ArtistId = $"ext-{publicProvider ?? provider}-artist-{artistId}" }),
            PayloadSha256 = new string('a', 64),
            Protocol = "subsonic",
            BackendInstanceId = "main",
            BackendPrincipalId = _first.ToString(),
            RetrievedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
