using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task ImportedSpotifyHeartDoesNotAcquireInterestInExistingKeptFile()
    {
        await Service().PublishAsync(Request());
        var pipeline = new FavoriteActionPipeline(factory, queue, new SystemPlatformClock(), Service());
        var context = PlaybackContext(allstarr.Core.Protocols.ProtocolKind.Jellyfin, owner, CanonicalItemId());
        await using (var before = factory.CreateDbContext())
            Assert.Single(await before.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());

        Assert.True(await pipeline.ImportSpotifyLikeAsync(context, CanonicalItemId(), "spotify-like:kept"));

        await using var db = factory.CreateDbContext();
        Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.DoesNotContain(await db.ManagedFileReferences.ToArrayAsync(), item => item.ReferenceKey.StartsWith("heart:", StringComparison.Ordinal));
        Assert.All(await db.FavoriteActions.ToArrayAsync(), item => Assert.Equal("virtual-liked", item.ActionType));
    }

    [Fact]
    public async Task FavoriteRetentionUsesTheAcceptedEventAndIsIdempotent()
    {
        var metadata = new KeptRecordingMetadata(
            "Hearted title", ["Hearted artist"], "Hearted album", 123_457, 1);
        var intent = await SeedFavoriteIntentAsync(
            owner,
            CanonicalItemId(),
            existingJobId: jobId);
        var published = await Service().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{intent.StateId:N}",
            Metadata = metadata
        });

        var first = Assert.IsType<KeptRecordingFile>(await Service().RetainFavoriteAsync(
            new(intent.OwnerUserId, intent.JobId, intent.AttemptNumber)));
        var second = Assert.IsType<KeptRecordingFile>(await Service().RetainFavoriteAsync(
            new(intent.OwnerUserId, intent.JobId, intent.AttemptNumber)));

        Assert.Equal(first.ManagedFileId, second.ManagedFileId);
        Assert.Equal(published.ManagedFileId, first.ManagedFileId);
        await using var db = new AllstarrDbContext(database.Options);
        var state = await db.Set<FavoriteStateRecord>().SingleAsync(item => item.Id == intent.StateId);
        Assert.Equal($"heart:{state.Id:N}", Assert.Single(
            await db.ManagedFileReferences.Where(item => item.ReleasedAt == null)
                .Select(item => item.ReferenceKey).ToArrayAsync()));
        Assert.Equal(1, await db.Set<RecordingKeptRouteEntity>().CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupersededOrUnheartedFavoriteCannotPublish(bool unhearted)
    {
        var original = await SeedFavoriteIntentAsync(
            owner,
            CanonicalItemId(),
            existingJobId: jobId);
        await SeedFavoriteIntentAsync(
            owner,
            CanonicalItemId(),
            operation: unhearted ? FavoriteOperation.Unfavorite : FavoriteOperation.Favorite,
            existingJobId: null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{original.StateId:N}",
            Metadata = new KeptRecordingMetadata("Hearted title", ["Hearted artist"])
        }));
        var result = await Service().RetainFavoriteAsync(
            new(original.OwnerUserId, original.JobId, original.AttemptNumber));

        Assert.Null(result);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFiles.Where(item => item.RemovedAt == null).ToArrayAsync());
    }

    [Fact]
    public async Task FavoriteRetentionRejectsStaleAttempt()
    {
        var intent = await SeedFavoriteIntentAsync(
            owner,
            CanonicalItemId(),
            existingJobId: jobId);
        var published = await Service().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{intent.StateId:N}",
            Metadata = new KeptRecordingMetadata("Hearted title", ["Hearted artist"])
        });

        Assert.Null(await Service().RetainFavoriteAsync(
            new(intent.OwnerUserId, intent.JobId, intent.AttemptNumber + 1)));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(1, await db.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
        Assert.Equal(published.ManagedFileId,
            await db.ManagedFileReferences.Where(item => item.ReleasedAt == null)
                .Select(item => item.ManagedFileId).SingleAsync());
    }

    [Fact]
    public async Task HouseholdFavoriteReusesKeptBytesAfterSourceAccountRetirement()
    {
        await using (var sourceDb = new AllstarrDbContext(database.Options))
        {
            var identity = await sourceDb.ProviderTrackIdentities.SingleAsync();
            identity.Scope = ProviderIdentityScope.Account;
            identity.ProviderAccountId = account;
            await sourceDb.SaveChangesAsync();
        }

        var firstIntent = await SeedFavoriteIntentAsync(
            owner,
            CanonicalItemId(),
            existingJobId: jobId);
        var first = await Service().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{firstIntent.StateId:N}",
            Metadata = new KeptRecordingMetadata(
                "Household title", ["Household artist"], "Household album", 91_234, 0)
        });
        await DisableSourceAccountAsync();

        var secondIntent = await SeedFavoriteIntentAsync(other, CanonicalItemId());
        var second = Assert.IsType<KeptRecordingFile>(await Service().RetainFavoriteAsync(
            new(secondIntent.OwnerUserId, secondIntent.JobId, secondIntent.AttemptNumber)));
        var repeated = Assert.IsType<KeptRecordingFile>(await Service().RetainFavoriteAsync(
            new(secondIntent.OwnerUserId, secondIntent.JobId, secondIntent.AttemptNumber)));

        Assert.Equal(first.ManagedFileId, second.ManagedFileId);
        Assert.Equal(second.ManagedFileId, repeated.ManagedFileId);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(1, await db.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null &&
            item.ReferenceKey == $"heart:{secondIntent.StateId:N}"));
        Assert.Equal(2, await db.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
    }

    [Fact]
    public async Task FavoriteMetadataSurvivesRestartAndRetiredSourceForCanonicalAndSourceAliasReads()
    {
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var identity = await db.ProviderTrackIdentities.SingleAsync();
            identity.Scope = ProviderIdentityScope.Account;
            identity.ProviderAccountId = account;
            await db.SaveChangesAsync();
        }

        var metadata = new KeptRecordingMetadata(
            "Precise heart title", ["First artist", "Second artist"], "Precise album", 123_457, 1);
        var intent = await SeedFavoriteIntentAsync(
            owner,
            CanonicalItemId(),
            existingJobId: jobId);
        await Service().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{intent.StateId:N}",
            Metadata = metadata
        });
        await DisableSourceAccountAsync();

        var fresh = Service();
        var canonical = Assert.IsType<Song>(await fresh.FindSongAsync(Actor(other), CanonicalItemId()));
        var alias = Assert.IsType<Song>(await fresh.FindSongAsync(Actor(other), "ext-deezer-song-source-track"));

        Assert.Equal(CanonicalItemId(), canonical.Id);
        Assert.Equal(recordingId, canonical.CanonicalRecordingId);
        Assert.Equal(metadata.Title, canonical.Title);
        Assert.Equal(metadata.Artists, canonical.Artists);
        Assert.Equal(metadata.Artists[0], canonical.Artist);
        Assert.Equal(metadata.Album, canonical.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(metadata.DurationMilliseconds!.Value), canonical.PreciseDuration);
        Assert.Equal(123, canonical.Duration);
        Assert.Equal(metadata.ExplicitContentLyrics, canonical.ExplicitContentLyrics);
        Assert.Equal(CanonicalItemId(), alias.Id);
        Assert.Equal(metadata.Artists, alias.Artists);
        Assert.Equal(metadata.Album, alias.Album);
    }

    [Fact]
    public async Task FavoriteMetadataReadRequiresAHouseholdUser()
    {
        var intent = await SeedFavoriteIntentAsync(
            owner,
            CanonicalItemId(),
            existingJobId: jobId);
        await Service().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{intent.StateId:N}",
            Metadata = new KeptRecordingMetadata("Private metadata", ["Private artist"], "Private album", 1_000)
        });

        var fresh = Service();
        Assert.Null(await fresh.FindSongAsync(new(ProviderActorKind.PublicRead, null), CanonicalItemId()));
        var wrongBackend = new ProviderActorContext(
            ProviderActorKind.User,
            other,
            new("jellyfin", "other-instance", other.ToString("N")));
        Assert.Null(await fresh.FindSongAsync(wrongBackend, CanonicalItemId()));
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.Users.SingleAsync(item => item.Id == other)).Enabled = false;
            await db.SaveChangesAsync();
        }
        Assert.Null(await fresh.FindSongAsync(Actor(other), CanonicalItemId()));
    }

    [Fact]
    public async Task FavoriteRetentionRejectsInvalidMetadataBeforePlacement()
    {
        var invalid = new KeptRecordingMetadata[]
        {
            new(new string('t', 501), ["Artist"]),
            new("Title", ["Artist"], new string('a', 501)),
            new("Title", Enumerable.Repeat("Artist", 21).ToArray()),
            new("Title", [new string('a', 501)]),
            new("Title", ["Artist"], DurationMilliseconds: -1)
        };

        foreach (var metadata in invalid)
        {
            var intent = await SeedFavoriteIntentAsync(owner, CanonicalItemId(), existingJobId: jobId);
            await Assert.ThrowsAsync<ArgumentException>(() => Service().PublishAsync(Request() with
            {
                ReferenceKey = $"heart:{intent.StateId:N}",
                Metadata = metadata
            }));
        }

        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFiles.Where(item => item.RemovedAt == null).ToArrayAsync());
    }

    [Fact]
    public async Task FavoriteRetentionRechecksCancellationAtPlacementBoundary()
    {
        var intent = await SeedFavoriteIntentAsync(
            owner,
            CanonicalItemId(),
            existingJobId: jobId);
        ArmBoundary(db =>
        {
            var job = db.Jobs.Single(item => item.Id == intent.JobId);
            job.CancellationRequestedAt = DateTimeOffset.UtcNow;
            job.Revision++;
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{intent.StateId:N}",
            Metadata = new KeptRecordingMetadata("Boundary title", ["Boundary artist"])
        }));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Empty(await verify.ManagedFiles.Where(item => item.RemovedAt == null).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Fact]
    public async Task ForgedHeartReferenceCannotPublishLegacyFavoriteBytes()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(
            Request() with { ReferenceKey = "heart:forged-reference" }));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFiles.Where(item => item.RemovedAt == null).ToArrayAsync());
    }

    private string CanonicalItemId() => CanonicalRecordingRecord.DefaultPublicId(recordingId);

    private async Task<FavoriteFixture> SeedFavoriteIntentAsync(
        Guid userId,
        string itemId,
        Guid? existingJobId = null,
        FavoriteOperation operation = FavoriteOperation.Favorite,
        bool realProviderIntent = false)
    {
        var eventId = Guid.CreateVersion7();
        var activeJobId = existingJobId ?? Guid.Empty;
        var attemptNumber = 1;
        if (!existingJobId.HasValue)
        {
            activeJobId = (await queue.EnqueueAsync(new DurableJobEnqueueRequest<FavoriteJobPayload>(
                FavoriteActionPipeline.JobType,
                $"favorite-fixture:{eventId:N}",
                new FavoriteJobPayload(eventId),
                userId))).JobId;
            var claim = await queue.ClaimNextAsync(
                $"favorite-fixture:{eventId:N}",
                [FavoriteActionPipeline.JobType]);
            Assert.NotNull(claim);
            attemptNumber = claim!.AttemptNumber;
        }

        var now = DateTimeOffset.UtcNow;
        await using var db = new AllstarrDbContext(database.Options);
        var job = await db.Jobs.SingleAsync(item => item.Id == activeJobId);
        job.Type = FavoriteActionPipeline.JobType;
        job.PayloadJson = JsonSerializer.Serialize(new FavoriteJobPayload(eventId));
        job.Revision++;
        var record = new FavoriteEventRecord
        {
            Id = eventId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = userId.ToString("N"),
            ItemId = itemId,
            Operation = operation,
            IsRealProviderIntent = realProviderIntent,
            SourceRevision = $"favorite-fixture:{eventId:N}",
            EventKey = $"favorite-fixture:{eventId:N}",
            CorrelationId = $"favorite-fixture:{eventId:N}",
            PolicySnapshotJson = JsonSerializer.Serialize(new
            {
                actions = new[] { FavoriteActionPipeline.VirtualLikedAction, "download" },
                policySource = "download-only"
            }),
            JobId = activeJobId,
            State = FavoriteEventState.Running,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Set<FavoriteEventRecord>().Add(record);
        var state = await db.Set<FavoriteStateRecord>().SingleOrDefaultAsync(item =>
            item.OwnerUserId == userId && item.Protocol == "jellyfin" &&
            item.BackendInstanceId == "fixture" && item.ItemId == itemId);
        if (state == null)
        {
            state = new FavoriteStateRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = userId,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                ItemId = itemId
            };
            db.Set<FavoriteStateRecord>().Add(state);
        }
        state.IsFavorite = operation == FavoriteOperation.Favorite;
        state.LastEventId = eventId;
        state.UpdatedAt = now;
        state.Revision++;
        await db.SaveChangesAsync();
        return new(userId, activeJobId, attemptNumber, eventId, state.Id);
    }

    private sealed record FavoriteFixture(
        Guid OwnerUserId,
        Guid JobId,
        int AttemptNumber,
        Guid EventId,
        Guid StateId);
}
