using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task SavedMembership_UsesContinuousUnionOfHeartsAndPlaylistSnapshots()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = storage.CreateDbContext())
        {
            await db.Set<FavoriteEventRecord>().Where(row => row.ItemId == "ext-deezer-song-saved")
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.CreatedAt, start));
            await AddMembershipSnapshotAsync(db, "mixed", 1, start.AddDays(1), true);
            await AddMembershipSnapshotAsync(db, "mixed", 2, start.AddDays(2), true);
            await db.SaveChangesAsync();
        }
        var execution = SavedMembershipContext();
        var reader = new SavedLibraryReader(storage);
        var first = await reader.ReadAsync(execution);
        Assert.Equal(start, Assert.Single(first, item => item.ItemId == "ext-deezer-song-saved").FavoritedAt);
        Assert.Equal(start, Assert.Single(first, item => item.ItemId == "ext-deezer-album-parent").FavoritedAt);
        await using (var db = storage.CreateDbContext())
        {
            var state = await db.Set<FavoriteStateRecord>().SingleAsync(row => row.ItemId == "ext-deezer-song-saved");
            var last = await db.Set<FavoriteEventRecord>().SingleAsync(row => row.Id == state.LastEventId);
            var removed = new FavoriteEventRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = FavoriteViewer,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "user-1",
                ItemId = state.ItemId,
                Operation = FavoriteOperation.Unfavorite,
                CreatedAt = start.AddDays(3),
                UpdatedAt = start.AddDays(3),
                EventKey = "removed",
                JobId = last.JobId
            };
            db.Add(removed);
            state.IsFavorite = false;
            state.LastEventId = removed.Id;
            await db.SaveChangesAsync();
        }
        var unhearted = await reader.ReadAsync(execution);
        Assert.Equal(start, Assert.Single(unhearted, item => item.ItemId == "ext-deezer-song-saved").FavoritedAt);
        Assert.Equal(start, Assert.Single(unhearted, item => item.ItemId == "ext-deezer-album-parent").FavoritedAt);
        await using (var db = storage.CreateDbContext())
        {
            await AddMembershipSnapshotAsync(db, "mixed", 3, start.AddDays(4), false);
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain(await reader.ReadAsync(execution), item => item.ItemId == "ext-deezer-song-saved");
        await using (var db = storage.CreateDbContext())
        {
            await AddMembershipSnapshotAsync(db, "mixed", 4, start.AddDays(5), true);
            await db.SaveChangesAsync();
        }
        var readded = await reader.ReadAsync(execution);
        Assert.Equal(start.AddDays(5), Assert.Single(readded, item => item.ItemId == "ext-deezer-song-saved").FavoritedAt);
        Assert.Equal(readded.Select(item => (item.ItemId, item.FavoritedAt)),
            (await reader.ReadAsync(execution)).Select(item => (item.ItemId, item.FavoritedAt)));
        Assert.DoesNotContain(readded, item => item.ItemId.Contains("foreign", StringComparison.Ordinal));
        await using var unchanged = storage.CreateDbContext();
        Assert.Equal(4, await unchanged.ExternalMetadataSnapshots.CountAsync(row => row.ResourceKind == "playlist-membership"));
    }

    [Fact]
    public async Task SavedMembership_ImportsPublishedSourceSongsWithoutWritesOrProviderLookups()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var added = new DateTimeOffset(2023, 7, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = storage.CreateDbContext())
        {
            var recording = Guid.CreateVersion7();
            var identity = Guid.CreateVersion7();
            var metadata = Guid.CreateVersion7();
            var link = Guid.CreateVersion7();
            var snapshot = Guid.CreateVersion7();
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = FavoriteViewer, PublicId = CanonicalRecordingRecord.DefaultPublicId(recording), CreatedAt = added, UpdatedAt = added });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = identity,
                CanonicalRecordingId = recording,
                ProviderId = "deezer",
                ResourceKind = ProviderResourceKind.Track,
                Verification = ProviderIdentityVerification.Verified,
                Scope = ProviderIdentityScope.Catalog,
                DecisionVersion = 1,
                ExternalId = "source-song",
                ExternalIdHash = SavedMembershipHash("source-song"),
                CreatedAt = added,
                UpdatedAt = added
            });
            db.PlaylistLinks.Add(new()
            {
                Id = link,
                OwnerUserId = FavoriteViewer,
                SourceProviderId = "deezer",
                SourcePlaylistId = "source",
                SourcePlaylistIdHash = SavedMembershipHash("source"),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "primary",
                Mode = PlaylistLinkMode.Virtual,
                CreatedAt = added,
                UpdatedAt = added
            });
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = metadata,
                OwnerUserId = FavoriteViewer,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "user-1",
                ProviderId = "deezer",
                ResourceKind = "track",
                ProviderTrackIdentityId = identity,
                ExternalIdHash = SavedMembershipHash("source-song"),
                SnapshotVersion = 1,
                PayloadSha256 = SavedMembershipHash("source-payload"),
                RetrievedAt = added,
                PayloadJson = """{"Title":"Source song","Artists":["Source artist"],"Album":"Source album","DurationMilliseconds":123456,"ArtistIds":["ext-deezer-artist-source"],"AlbumId":"ext-deezer-album-source","Year":1999,"Genre":"Jazz"}"""
            });
            db.PlaylistSourceSnapshots.Add(new()
            {
                Id = snapshot,
                OwnerUserId = FavoriteViewer,
                PlaylistLinkId = link,
                SnapshotVersion = 1,
                RetrievedAt = added,
                PublishedAt = added,
                Name = "Imported",
                PayloadSha256 = SavedMembershipHash("source-snapshot")
            });
            db.PlaylistSourceEntries.Add(new()
            {
                Id = Guid.CreateVersion7(),
                PlaylistSourceSnapshotId = snapshot,
                ExternalMetadataSnapshotId = metadata,
                SourcePosition = 0,
                SourceEntryIdHash = SavedMembershipHash("source-row")
            });
            await db.SaveChangesAsync();
        }
        var readOnly = new FavoriteViewFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(new SongReadWriteGuard()).Options);
        var rows = await new SavedLibraryReader(readOnly).ReadAsync(SavedMembershipContext());
        var song = Assert.Single(rows, row => row.ItemId == "ext-deezer-song-source-song");
        Assert.Equal(added, song.FavoritedAt);
        Assert.Equal(123456, song.Display.DurationMilliseconds);
        Assert.Contains(rows, row => row.ItemId == "ext-deezer-album-source");
        Assert.Contains(rows, row => row.ItemId == "ext-deezer-artist-source");
        await using (var db = storage.CreateDbContext())
        {
            var accepted = new FavoriteEventRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = FavoriteViewer,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "user-1",
                ItemId = song.ItemId,
                Operation = FavoriteOperation.Favorite,
                CreatedAt = added.AddDays(1),
                UpdatedAt = added.AddDays(1),
                EventKey = SavedMembershipHash("overlap"),
                SourceRevision = "overlap",
                JobId = await db.Set<FavoriteEventRecord>().Select(item => item.JobId).FirstAsync()
            };
            db.Add(accepted);
            db.Add(new FavoriteStateRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = FavoriteViewer,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                ItemId = song.ItemId,
                IsFavorite = true,
                LastEventId = accepted.Id,
                DisplayMetadataJson = JsonSerializer.Serialize(song.Display, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                UpdatedAt = accepted.CreatedAt
            });
            await db.SaveChangesAsync();
        }
        await using (var db = storage.CreateDbContext())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            var persistence = new PlaylistPersistenceService(storage, null!, new SystemPlatformClock(), null!, null!);
            await persistence.DeleteLinkAsync(SavedMembershipContext(), link.Id, link.Revision);
        }
        var afterDelete = await new SavedLibraryReader(readOnly).ReadAsync(SavedMembershipContext());
        Assert.Equal(added, Assert.Single(afterDelete, row => row.ItemId == song.ItemId).FavoritedAt);
        await using var archived = storage.CreateDbContext();
        Assert.Empty(await archived.PlaylistLinks.ToArrayAsync());
        Assert.Single(await archived.ExternalMetadataSnapshots.Where(row => row.ResourceKind == "library-membership-history").ToArrayAsync());
    }

    [Fact]
    public async Task SavedMembership_PreservesDistinctAlbumArtistIdentityAndName()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
        {
            await AddMembershipSnapshotAsync(db, "guest", 1, DateTimeOffset.UnixEpoch, true);
            var row = db.ChangeTracker.Entries<ExternalMetadataSnapshotRecord>().Single().Entity;
            var state = JsonSerializer.Deserialize<WritablePlaylistState>(row.PayloadJson)!;
            state.Members[0].Song!.AlbumArtists = [new Artist { Id = "ext-deezer-artist-host", Name = "Host artist" }];
            row.PayloadJson = JsonSerializer.Serialize(state);
            row.PayloadSha256 = SavedMembershipHash(row.PayloadJson);
            await db.SaveChangesAsync();
        }
        var items = await new SavedLibraryReader(storage).ReadAsync(SavedMembershipContext());
        var host = Assert.Single(items, item => item.ItemId == "ext-deezer-artist-host");
        Assert.Equal("Host artist", host.Display.Title);
        Assert.Equal("ext-deezer-artist-host", Assert.Single(items, item => item.ItemId == "ext-deezer-album-parent").Album().ArtistId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedMembership_UsesNativeVisibilityForSharedPlaylistsAndKeepsHeartsPrivate(bool visible)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
        {
            var foreign = await db.Users.SingleAsync(user => user.Id != FavoriteViewer);
            await AddMembershipSnapshotAsync(db, "shared", 1, DateTimeOffset.UnixEpoch, true);
            var row = db.ChangeTracker.Entries<ExternalMetadataSnapshotRecord>().Single().Entity;
            row.OwnerUserId = foreign.Id;
            row.BackendPrincipalId = foreign.BackendPrincipalId;
            row.ExternalIdHash = WritablePlaylistMembership.Key(SavedMembershipContext(), "shared-playlist");
            var state = JsonSerializer.Deserialize<WritablePlaylistState>(row.PayloadJson)!;
            var member = state.Members[0];
            member.Song!.Id = "ext-deezer-song-shared";
            row.PayloadJson = JsonSerializer.Serialize(new WritablePlaylistState([member with { ItemId = member.Song.Id }]));
            row.PayloadSha256 = SavedMembershipHash(row.PayloadJson);
            await db.SaveChangesAsync();
        }
        var target = new Mock<IBackendPlaylistTarget>(MockBehavior.Strict);
        target.Setup(item => item.ListPageAsync(It.IsAny<BackendPlaylistTargetContext>(), null, 0, 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>(BackendPlaylistTargetStatus.Success,
                visible ? [new("shared-playlist", "Shared")] : []));
        var targets = new Mock<IBackendPlaylistTargetResolver>(MockBehavior.Strict);
        targets.Setup(item => item.Resolve("jellyfin")).Returns(target.Object);
        var items = await new SavedLibraryReader(storage, targets.Object).ReadAsync(SavedMembershipContext());
        Assert.Equal(visible, items.Any(item => item.ItemId == "ext-deezer-song-shared"));
        Assert.DoesNotContain(items, item => item.ItemId.Contains("foreign-track", StringComparison.Ordinal));
        target.VerifyAll();
        target.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SavedMembership_CurrentAcceptedHeartWinsWhenEventTimestampsTie()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var at = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);
        await using (var db = storage.CreateDbContext())
        {
            var state = await db.Set<FavoriteStateRecord>().SingleAsync(row => row.ItemId == "ext-deezer-song-saved");
            var previous = await db.Set<FavoriteEventRecord>().SingleAsync(row => row.Id == state.LastEventId);
            previous.CreatedAt = at.AddDays(-1);
            foreach (var favorite in new[] { false, true })
            {
                var id = Guid.Parse(favorite ? "00000000-0000-0000-0000-000000000001" : "ffffffff-ffff-ffff-ffff-ffffffffffff");
                db.Add(new FavoriteEventRecord
                {
                    Id = id,
                    OwnerUserId = FavoriteViewer,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    BackendPrincipalId = "user-1",
                    ItemId = state.ItemId,
                    Operation = favorite ? FavoriteOperation.Favorite : FavoriteOperation.Unfavorite,
                    CreatedAt = at,
                    UpdatedAt = at,
                    EventKey = SavedMembershipHash("tie-" + favorite),
                    JobId = previous.JobId
                });
                if (favorite) state.LastEventId = id;
            }
            await db.SaveChangesAsync();
        }
        var items = await new SavedLibraryReader(storage).ReadAsync(SavedMembershipContext());
        Assert.Equal(at, Assert.Single(items, item => item.ItemId == "ext-deezer-song-saved").FavoritedAt);
    }

    [Fact]
    public async Task SavedMembership_InferredAlbumRetainsGenresFromEveryMemberSong()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
        {
            await AddMembershipSnapshotAsync(db, "genres", 1, DateTimeOffset.UnixEpoch, true);
            var row = db.ChangeTracker.Entries<ExternalMetadataSnapshotRecord>().Single().Entity;
            var state = JsonSerializer.Deserialize<WritablePlaylistState>(row.PayloadJson)!;
            var other = JsonSerializer.Deserialize<Song>(JsonSerializer.Serialize(state.Members[0].Song))!;
            other.Id = "ext-deezer-song-jazz";
            other.Genre = "Jazz";
            row.PayloadJson = JsonSerializer.Serialize(new WritablePlaylistState([state.Members[0], new("two", other.Id, other)]));
            row.PayloadSha256 = SavedMembershipHash(row.PayloadJson);
            await db.SaveChangesAsync();
        }
        var items = await new SavedLibraryReader(storage).ReadAsync(SavedMembershipContext());
        var album = Assert.Single(items, item => item.ItemId == "ext-deezer-album-parent");
        Assert.Equal(2, album.Display.Count);
        Assert.Equal(["Jazz", "Rock"], album.Display.Genres.Order(StringComparer.Ordinal));
        var artist = Assert.Single(items, item => item.ItemId == "ext-deezer-artist-parent");
        Assert.Equal(["Jazz", "Rock"], artist.Display.Genres.Order(StringComparer.Ordinal));
    }

    private static string SavedMembershipHash(string text) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static ProtocolExecutionContext SavedMembershipContext() => new(ProtocolKind.Jellyfin, "primary", "user-1",
        new AllstarrPrincipal(FavoriteViewer, "jellyfin", "primary", "user-1", "Viewer", false),
        "saved-library", DateTimeOffset.UtcNow.AddMinutes(5), default);

    private static Task AddMembershipSnapshotAsync(AllstarrDbContext db, string key, int version, DateTimeOffset at, bool present)
    {
        var song = new Song
        {
            Id = "ext-deezer-song-saved",
            Title = "Saved song",
            Artist = "Saved artist",
            Artists = ["Saved artist"],
            ArtistId = "ext-deezer-artist-parent",
            ArtistIds = ["ext-deezer-artist-parent"],
            Album = "Saved album",
            AlbumId = "ext-deezer-album-parent",
            Genre = "Rock",
            Year = 2001
        };
        var payload = JsonSerializer.Serialize(new WritablePlaylistState(present ? [new("one", song.Id, song)] : []));
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = FavoriteViewer,
            Protocol = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = "user-1",
            ProviderId = "allstarr",
            ResourceKind = "playlist-membership",
            ExternalIdHash = SavedMembershipHash(key),
            SnapshotVersion = version,
            PayloadJson = payload,
            PayloadSha256 = SavedMembershipHash(payload),
            RetrievedAt = at
        });
        return Task.CompletedTask;
    }
}
