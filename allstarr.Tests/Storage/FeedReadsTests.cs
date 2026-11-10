using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class FeedReadsTests
{
    [Fact]
    public async Task OwnerDocumentsAndSettingsInvalidateOnlyTheirDependencies()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = User("listener");
        var other = User("other");
        db.Users.AddRange(owner, other);
        await db.SaveChangesAsync();
        var context = Context(owner);
        var otherContext = Context(other);
        var mixKey = db.FeedReadKey(context, "mixes:UTC");
        var releaseKey = db.FeedReadKey(context, "releases");
        var otherKey = db.FeedReadKey(otherContext, "mixes:UTC");
        foreach (var kind in new[] { "personal-mixes", "service-feeds", "service-feed-songs", "service-feed-prepared" })
        {
            db.ExternalMetadataSnapshots.Add(Snapshot(owner.Id, kind));
            await db.SaveChangesAsync();
            Assert.NotEqual(mixKey, db.FeedReadKey(context, "mixes:UTC"));
            mixKey = db.FeedReadKey(context, "mixes:UTC");
            Assert.Equal(releaseKey, db.FeedReadKey(context, "releases"));
            Assert.Equal(otherKey, db.FeedReadKey(otherContext, "mixes:UTC"));
        }
        var otherLibraryRevision = db.FeedReadLibraryRevisionFor(other.Id);
        var libraryRevision = db.FeedReadLibraryRevisionFor(owner.Id);
        db.ExternalMetadataSnapshots.Add(Snapshot(owner.Id, "track"));
        await db.SaveChangesAsync();
        Assert.Equal(libraryRevision + 1, db.FeedReadLibraryRevisionFor(owner.Id));
        Assert.Equal(otherLibraryRevision, db.FeedReadLibraryRevisionFor(other.Id));
        Assert.Equal(mixKey, db.FeedReadKey(context, "mixes:UTC"));
        db.RuntimeSettings.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Key = "Library:ExplicitFilter", ValueType = RuntimeSettingValueType.String, ValueJson = "\"CleanOnly\"", Source = "fixture", Revision = 1 });
        await db.SaveChangesAsync();
        Assert.NotEqual(mixKey, db.FeedReadKey(context, "mixes:UTC"));
        Assert.NotEqual(releaseKey, db.FeedReadKey(context, "releases"));
        Assert.Equal(otherKey, db.FeedReadKey(otherContext, "mixes:UTC"));
    }

    [Fact]
    public async Task OwnerInvalidationWaitsForCommitAndDiscardsRollback()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = User("listener");
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var context = Context(owner);
        var key = db.FeedReadKey(context, "mixes");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.ExternalMetadataSnapshots.Add(Snapshot(owner.Id, "personal-mixes"));
            await db.SaveChangesAsync();
            Assert.Equal(key, db.FeedReadKey(context, "mixes"));
            await transaction.RollbackAsync();
        }
        Assert.Equal(key, db.FeedReadKey(context, "mixes"));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.ExternalMetadataSnapshots.Add(Snapshot(owner.Id, "personal-mixes"));
            await db.SaveChangesAsync();
            Assert.Equal(key, db.FeedReadKey(context, "mixes"));
            await transaction.CommitAsync();
        }
        Assert.NotEqual(key, db.FeedReadKey(context, "mixes"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistEntryWritesResolveTheOwnerFromTrackedAndUntrackedParents(bool sameSave)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = User("listener");
        var other = User("other");
        var source = Snapshot(owner.Id, "track");
        var link = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            SourceProviderId = "spotify",
            SourcePlaylistId = "feed",
            SourcePlaylistIdHash = new('a', 64),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "primary"
        };
        var snapshot = new PlaylistSourceSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            PlaylistLinkId = link.Id,
            SnapshotVersion = 1,
            PayloadSha256 = new('b', 64)
        };
        db.Users.AddRange(owner, other);
        db.ExternalMetadataSnapshots.Add(source);
        await db.SaveChangesAsync();
        if (!sameSave)
        {
            db.PlaylistLinks.Add(link);
            db.PlaylistSourceSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
        }
        var context = Context(owner);
        var otherContext = Context(other);
        var key = db.FeedReadKey(context, "playlist-summaries:*");
        var otherKey = db.FeedReadKey(otherContext, "playlist-summaries:*");
        await using var writer = new AllstarrDbContext(database.Options);
        if (sameSave)
        {
            writer.PlaylistLinks.Add(link);
            writer.PlaylistSourceSnapshots.Add(snapshot);
        }
        writer.PlaylistSourceEntries.Add(new()
        {
            Id = Guid.NewGuid(),
            PlaylistSourceSnapshotId = snapshot.Id,
            ExternalMetadataSnapshotId = source.Id,
            SourceEntryIdHash = new('c', 64),
            SourcePosition = 0
        });
        await writer.SaveChangesAsync();
        Assert.NotEqual(key, db.FeedReadKey(context, "playlist-summaries:*"));
        Assert.Equal(otherKey, db.FeedReadKey(otherContext, "playlist-summaries:*"));
    }

    [Theory]
    [InlineData("playlist-fork")]
    [InlineData("playlist-membership")]
    public async Task OwnPlaylistDocumentsImmediatelyInvalidateMembershipReads(string kind)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = User("listener");
        var other = User("other");
        db.Users.AddRange(owner, other);
        await db.SaveChangesAsync();
        var context = Context(owner);
        var otherContext = Context(other);
        var key = db.FeedReadKey(context, "playlist-tracks:original");
        var otherKey = db.FeedReadKey(otherContext, "playlist-tracks:original");
        for (var edit = 0; edit < 2; edit++)
        {
            db.ExternalMetadataSnapshots.Add(Snapshot(owner.Id, kind));
            await db.SaveChangesAsync();
            var next = db.FeedReadKey(context, "playlist-tracks:original");
            Assert.NotEqual(key, next);
            Assert.Equal(otherKey, db.FeedReadKey(otherContext, "playlist-tracks:original"));
            key = next;
        }
    }

    [Fact]
    public async Task ExplicitPlaylistInvalidationWaitsForCommitAndDiscardsRollback()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = User("listener");
        var context = Context(owner);
        var key = db.FeedReadKey(context, "playlist-tracks:original");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.InvalidateFeedReads(owner.Id);
            Assert.Equal(key, db.FeedReadKey(context, "playlist-tracks:original"));
            await transaction.RollbackAsync();
        }
        Assert.Equal(key, db.FeedReadKey(context, "playlist-tracks:original"));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.InvalidateFeedReads(owner.Id);
            Assert.Equal(key, db.FeedReadKey(context, "playlist-tracks:original"));
            await transaction.CommitAsync();
        }
        Assert.NotEqual(key, db.FeedReadKey(context, "playlist-tracks:original"));
    }

    internal static UserRecord User(string principal) => new()
    {
        Id = Guid.NewGuid(),
        BackendType = "jellyfin",
        BackendInstanceId = "primary",
        BackendPrincipalId = principal,
        Enabled = true
    };

    internal static ProtocolExecutionContext Context(UserRecord user) => new(ProtocolKind.Jellyfin, user.BackendInstanceId,
        user.BackendPrincipalId, new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, "Listener", false),
        "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);

    internal static ExternalMetadataSnapshotRecord Snapshot(Guid owner, string kind) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = owner,
        ProviderId = "allstarr",
        ResourceKind = kind,
        ExternalIdHash = Guid.NewGuid().ToString("N") + new string('a', 32),
        SnapshotVersion = 1,
        PayloadSha256 = new('a', 64)
    };
}
