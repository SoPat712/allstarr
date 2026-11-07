using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

[Collection(nameof(PlaylistTimingCollection))]
public sealed class SongPlaylistMembershipReaderTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private MembershipFactory _factory = null!;
    private readonly ReadCounter _reads = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(_reads).Options);
        await using var db = await _factory.CreateDbContextAsync();
        db.Users.AddRange(User(_owner), User(_other));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CanonicalAndProviderRoutesFindTheSameRecordingAcrossSources()
    {
        var recording = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            AddRecording(db, recording);
            var source = AddSong(db, _owner, "spotify", "source-song", recording);
            AddIdentity(db, recording, "apple-musickit", "kept-song");
            AddPlaylist(db, _owner, "Morning", [source]);
            await db.SaveChangesAsync();
        }

        var reader = new SongPlaylistMembershipReader(_factory);
        var canonical = await reader.ReadAsync(_owner, new(CanonicalRecordingId: recording));
        var provider = await reader.ReadAsync(_owner, new(ProviderId: "apple-download", ExternalId: "kept-song"));
        Assert.Equal("Morning", Assert.Single(canonical).Name);
        Assert.Equal(canonical, provider);
    }

    [Fact]
    public async Task SourceExternalIdentityWorksWithoutCanonicalIdentityAndDeduplicatesOccurrences()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = AddSong(db, _owner, "spotify", "source-song");
            AddPlaylist(db, _owner, "Morning", [source, source]);
            AddPlaylist(db, _owner, "Different source", [AddSong(db, _owner, "deezer", "source-song")]);
            await db.SaveChangesAsync();
        }

        var rows = await new SongPlaylistMembershipReader(_factory).ReadAsync(_owner,
            new(ProviderId: "spotify", ExternalId: "source-song"));
        Assert.Equal("Morning", Assert.Single(rows).Name);
    }

    [Fact]
    public async Task BackendIdentityUsesPublishedAcceptedMatchesAndRequiresTheFullBackendScope()
    {
        var recording = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            AddRecording(db, recording);
            var source = AddSong(db, _owner, "spotify", "matched");
            var track = AddLocal(db, _owner, "jellyfin", "home", "local-song");
            track.CanonicalRecordingId = recording;
            var match = AddMatch(db, source, track, TrackMatchState.Accepted);
            AddPlaylist(db, _owner, "Local match", [source], match.Id);
            var rejected = AddSong(db, _owner, "spotify", "rejected");
            var rejectedMatch = AddMatch(db, rejected, track, TrackMatchState.Rejected);
            AddPlaylist(db, _owner, "Rejected", [rejected], rejectedMatch.Id);
            await db.SaveChangesAsync();
        }

        var reader = new SongPlaylistMembershipReader(_factory);
        var rows = await reader.ReadAsync(_owner, new(Protocol: "jellyfin", BackendInstanceId: "home", BackendItemId: "local-song"));
        Assert.Equal("Local match", Assert.Single(rows).Name);
        Assert.Equal(rows, await reader.ReadAsync(_owner, new(CanonicalRecordingId: recording)));
        Assert.Empty(await reader.ReadAsync(_owner, new(Protocol: "jellyfin", BackendInstanceId: "elsewhere", BackendItemId: "local-song")));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(_owner, new(BackendItemId: "local-song")));
    }

    [Fact]
    public async Task OnlyTheOwnersLatestPublishedGenerationContributesMembership()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = AddSong(db, _owner, "spotify", "source-song");
            var otherSource = AddSong(db, _other, "spotify", "source-song");
            AddPlaylist(db, _other, "Someone else's playlist", [otherSource]);
            var updated = AddPlaylist(db, _owner, "Old name", [source]);
            AddSnapshot(db, updated, "Current name", [], 2);
            var unpublished = AddPlaylist(db, _owner, "Published name", [source]);
            AddSnapshot(db, unpublished, "Pending name", [], 2, published: false);
            await db.SaveChangesAsync();
        }

        var rows = await new SongPlaylistMembershipReader(_factory).ReadAsync(_owner,
            new(ProviderId: "spotify", ExternalId: "source-song"));
        Assert.Equal("Published name", Assert.Single(rows).Name);
    }

    [Fact]
    public async Task AnUnrelatedAccountIdentityCannotConnectTheOwnersSourceToAnotherRecording()
    {
        var recording = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            AddRecording(db, recording);
            var account = new ProviderAccountRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _other,
                ProviderId = "spotify",
                DisplayName = "Other account",
                Enabled = true,
                CreatedAt = _now,
                UpdatedAt = _now
            };
            db.ProviderAccounts.Add(account);
            AddIdentity(db, recording, "spotify", "source-song", account.Id);
            AddPlaylist(db, _owner, "Unconnected", [AddSong(db, _owner, "spotify", "source-song")]);
            await db.SaveChangesAsync();
        }

        Assert.Empty(await new SongPlaylistMembershipReader(_factory).ReadAsync(_owner, new(CanonicalRecordingId: recording)));
    }

    [Fact]
    public async Task AnOwnedAccountRouteMatchesCanonicalIdentityWithoutALinkedSnapshotIdentity()
    {
        var recording = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            AddRecording(db, recording);
            var account = new ProviderAccountRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _owner,
                ProviderId = "spotify",
                DisplayName = "Own account",
                Enabled = true,
                CreatedAt = _now,
                UpdatedAt = _now
            };
            db.ProviderAccounts.Add(account);
            AddIdentity(db, recording, "spotify", "source-song", account.Id);
            var source = AddSong(db, _owner, "spotify", "source-song");
            source.ProviderAccountId = account.Id;
            AddPlaylist(db, _owner, "Private route", [source]);
            await db.SaveChangesAsync();
        }
        var rows = await new SongPlaylistMembershipReader(_factory).ReadAsync(_owner, new(CanonicalRecordingId: recording));
        Assert.Equal("Private route", Assert.Single(rows).Name);
    }

    [Fact]
    public async Task TwentyFourPlaylistsWithOneHundredSongsUseBoundedReadsAndFinishWarmUnderThreeHundredMilliseconds()
    {
        var recording = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            AddRecording(db, recording);
            var shared = AddSong(db, _owner, "spotify", "song-42", recording);
            AddLocal(db, _owner, "jellyfin", "home", "local-song").CanonicalRecordingId = recording;
            for (var index = 0; index < 24; index++)
            {
                var playlist = index;
                var songs = Enumerable.Range(0, 100).Select(song =>
                {
                    if (song == 42) return shared;
                    var id = Guid.NewGuid();
                    AddRecording(db, id);
                    return AddSong(db, _owner, "spotify", $"playlist-{playlist}-song-{song}", id);
                }).ToArray();
                AddPlaylist(db, _owner, $"Playlist {index:00}", songs);
            }
            await db.SaveChangesAsync();
        }
        var reader = new SongPlaylistMembershipReader(_factory);
        SongPlaylistIdentity[] identities = [new(CanonicalRecordingId: recording),
            new(ProviderId: "spotify", ExternalId: "song-42"),
            new(Protocol: "jellyfin", BackendInstanceId: "home", BackendItemId: "local-song"),
            new(recording, "spotify", "song-42", "jellyfin", "home", "local-song")];
        foreach (var identity in identities)
        {
            Assert.Equal(24, (await reader.ReadAsync(_owner, identity)).Count);
            _reads.Count = 0;
            var timer = Stopwatch.StartNew();
            var rows = await reader.ReadAsync(_owner, identity);
            timer.Stop();
            Assert.Equal(24, rows.Count);
            Assert.InRange(_reads.Count, 1, 3);
            var label = identity.ProviderId != null && identity.Protocol != null ? "combined" :
                identity.CanonicalRecordingId.HasValue ? "recording" : identity.ProviderId != null ? "source" : "backend";
            Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(300), $"Warm {label} membership read took {timer.Elapsed.TotalMilliseconds:F1} ms with {_reads.Count} reads.");
        }
    }

    private UserRecord User(Guid id) => new()
    {
        Id = id,
        BackendType = "jellyfin",
        BackendInstanceId = "home",
        BackendPrincipalId = id.ToString(),
        DisplayName = id.ToString(),
        Enabled = true,
        CreatedAt = _now,
        UpdatedAt = _now,
        LastSeenAt = _now
    };

    private void AddRecording(AllstarrDbContext db, Guid id) => db.CanonicalRecordings.Add(new()
    {
        Id = id,
        CreatedByUserId = _owner,
        Title = "Song",
        PublicId = CanonicalRecordingRecord.DefaultPublicId(id),
        CreatedAt = _now,
        UpdatedAt = _now
    });

    private ProviderTrackIdentityRecord AddIdentity(AllstarrDbContext db, Guid recording, string provider, string externalId, Guid? account = null)
    {
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.NewGuid(),
            CanonicalRecordingId = recording,
            ProviderId = provider,
            ExternalId = externalId,
            ExternalIdHash = Hash(externalId),
            ResourceKind = ProviderResourceKind.Track,
            Scope = account.HasValue ? ProviderIdentityScope.Account : ProviderIdentityScope.Catalog,
            ProviderAccountId = account,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            VerifiedAt = _now,
            CreatedAt = _now,
            UpdatedAt = _now
        };
        db.ProviderTrackIdentities.Add(identity);
        return identity;
    }

    private ExternalMetadataSnapshotRecord AddSong(AllstarrDbContext db, Guid owner, string provider, string externalId, Guid? recording = null)
    {
        var identity = recording.HasValue ? AddIdentity(db, recording.Value, provider, externalId) : null;
        var song = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderId = provider,
            ResourceKind = "track",
            ProviderTrackIdentityId = identity?.Id,
            ExternalIdHash = Hash(externalId),
            SnapshotVersion = 1,
            ProviderRevision = "fixture",
            PayloadJson = "{}",
            PayloadSha256 = Hash(externalId),
            CorrelationId = "fixture",
            RetrievedAt = _now,
            Protocol = "jellyfin",
            BackendInstanceId = "home",
            BackendPrincipalId = owner.ToString()
        };
        db.ExternalMetadataSnapshots.Add(song);
        return song;
    }

    private LibraryTrackRecord AddLocal(AllstarrDbContext db, Guid owner, string protocol, string backend, string item)
    {
        var track = new LibraryTrackRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = protocol,
            BackendInstanceId = backend,
            BackendItemId = item,
            BackendLibraryId = "music",
            FilePath = $"/music/{item}.flac",
            Title = "Song",
            Artist = "Artist",
            IndexedAt = _now,
            SourceModifiedAt = _now,
            UpdatedAt = _now
        };
        db.LibraryTracks.Add(track);
        return track;
    }

    private TrackMatchRecord AddMatch(AllstarrDbContext db, ExternalMetadataSnapshotRecord source, LibraryTrackRecord track, TrackMatchState state)
    {
        var match = new TrackMatchRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = source.OwnerUserId,
            ExternalSnapshotId = source.Id,
            LibraryTrackId = state is TrackMatchState.Accepted or TrackMatchState.Pinned ? track.Id : null,
            State = state,
            Confidence = 1,
            Threshold = .8,
            DecisionVersion = 1,
            SourceSnapshotVersion = 1,
            MatcherVersion = "fixture",
            PolicyVersion = "fixture",
            CorrelationId = "fixture",
            DecidedAt = _now
        };
        db.TrackMatches.Add(match);
        return match;
    }

    private PlaylistLinkRecord AddPlaylist(AllstarrDbContext db, Guid owner, string name,
        IReadOnlyList<ExternalMetadataSnapshotRecord> songs, Guid? matchId = null)
    {
        var link = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            SourceProviderId = "spotify",
            SourcePlaylistId = Guid.NewGuid().ToString(),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "home",
            RuleVersion = "fixture",
            PolicyVersion = "fixture",
            CreatedAt = _now,
            UpdatedAt = _now
        };
        link.SourcePlaylistIdHash = Hash(link.SourcePlaylistId);
        db.PlaylistLinks.Add(link);
        AddSnapshot(db, link, name, songs, 1, matchId: matchId);
        return link;
    }

    private void AddSnapshot(AllstarrDbContext db, PlaylistLinkRecord link, string name,
        IReadOnlyList<ExternalMetadataSnapshotRecord> songs, int version, bool published = true, Guid? matchId = null)
    {
        var snapshot = new PlaylistSourceSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = link.OwnerUserId,
            PlaylistLinkId = link.Id,
            SnapshotVersion = version,
            Name = name,
            ProviderRevision = "fixture",
            PayloadSha256 = Hash(name),
            CorrelationId = "fixture",
            RetrievedAt = _now.AddMinutes(version),
            PublishedAt = published ? _now.AddMinutes(version) : null
        };
        db.PlaylistSourceSnapshots.Add(snapshot);
        for (var position = 0; position < songs.Count; position++) db.PlaylistSourceEntries.Add(new()
        {
            Id = Guid.NewGuid(),
            PlaylistSourceSnapshotId = snapshot.Id,
            ExternalMetadataSnapshotId = songs[position].Id,
            SourcePosition = position,
            SourceEntryIdHash = Hash($"{snapshot.Id}:{position}"),
            PublishedTrackMatchId = matchId
        });
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class MembershipFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Count { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
