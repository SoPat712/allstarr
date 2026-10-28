using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task AcceptedFavoriteStagesAStableHouseholdInterestBeforeAnyWorkerRuns()
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, _, _, pipeline) = CreateInterestPipeline();

        var receipt = await pipeline.RecordAsync(
            Mutation(other, CanonicalItemId(), FavoriteOperation.Favorite, "accepted-other-v1"));

        await using var db = new AllstarrDbContext(database.Options);
        var state = await db.FavoriteStates.SingleAsync(item =>
            item.OwnerUserId == other && item.ItemId == CanonicalItemId());
        var favoriteEvent = await db.FavoriteEvents.SingleAsync(item => item.Id == receipt.EventId);
        var actions = await db.FavoriteActions.Where(item => item.EventId == receipt.EventId).ToArrayAsync();
        var job = await db.Jobs.SingleAsync(item => item.Id == receipt.JobId);
        var activeReferences = await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == published.ManagedFileId && item.ReleasedAt == null)
            .OrderBy(item => item.ReferenceKey).ToArrayAsync();

        Assert.True(receipt.Created);
        Assert.Equal(FavoriteEventState.Pending, favoriteEvent.State);
        Assert.Equal(FavoriteOperation.Favorite, favoriteEvent.Operation);
        Assert.Equal(2, actions.Length);
        Assert.All(actions, action => Assert.Equal(FavoriteActionState.Pending, action.State));
        Assert.Equal(FavoriteActionPipeline.JobType, job.Type);
        Assert.Equal(DurableJobState.Pending, job.State);
        Assert.True(state.IsFavorite);
        Assert.Equal(receipt.EventId, state.LastEventId);
        Assert.Contains(activeReferences, item => item.ReferenceKey == $"heart:{state.Id:N}");
        Assert.Equal(2, activeReferences.Length);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Fact]
    public async Task AcceptedUnfavoriteReleasesOnlyItsInterestAndQueuesOneLastReleaseCleanup()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, _, _, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(
            Mutation(other, CanonicalItemId(), FavoriteOperation.Favorite, "other-v1"));

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var otherReleasedAt = clock.UtcNow;
        var otherUnfavorite = await pipeline.RecordAsync(
            Mutation(other, CanonicalItemId(), FavoriteOperation.Unfavorite, "other-v2"));

        await using (var afterOther = new AllstarrDbContext(database.Options))
        {
            var state = await afterOther.FavoriteStates.SingleAsync(item =>
                item.OwnerUserId == other && item.ItemId == CanonicalItemId());
            var ownerState = await afterOther.FavoriteStates.SingleAsync(item =>
                item.OwnerUserId == owner && item.ItemId == CanonicalItemId());
            Assert.False(state.IsFavorite);
            Assert.True(ownerState.IsFavorite);
            Assert.Equal(otherUnfavorite.EventId, state.LastEventId);
            Assert.Equal(1, await afterOther.ManagedFileReferences.CountAsync(item =>
                item.ManagedFileId == published.ManagedFileId && item.ReleasedAt == null));
            Assert.Equal($"heart:{ownerState.Id:N}", await afterOther.ManagedFileReferences
                .Where(item => item.ManagedFileId == published.ManagedFileId && item.ReleasedAt == null)
                .Select(item => item.ReferenceKey).SingleAsync());
            Assert.Empty(await afterOther.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)
                .ToArrayAsync());
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var ownerUnfavorite = await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "owner-v2"));

        await using var db = new AllstarrDbContext(database.Options);
        var otherState = await db.FavoriteStates.SingleAsync(item =>
            item.OwnerUserId == other && item.ItemId == CanonicalItemId());
        var ownerStateAfter = await db.FavoriteStates.SingleAsync(item =>
            item.OwnerUserId == owner && item.ItemId == CanonicalItemId());
        var released = await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == published.ManagedFileId).ToArrayAsync();
        var cleanup = Assert.Single(await db.Jobs.Where(item =>
            item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        var payload = JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(cleanup.PayloadJson);

        Assert.NotNull(payload);
        Assert.Equal(ownerUnfavorite.EventId, ownerStateAfter.LastEventId);
        Assert.False(ownerStateAfter.IsFavorite);
        Assert.False(otherState.IsFavorite);
        Assert.Equal(clock.UtcNow, released.Single(item => item.ReferenceKey == $"heart:{ownerStateAfter.Id:N}").ReleasedAt!.Value);
        Assert.Equal(otherReleasedAt, released.Single(item => item.ReferenceKey == $"heart:{otherState.Id:N}").ReleasedAt!.Value);
        Assert.Equal(DurableJobState.Pending, cleanup.State);
        Assert.Equal(clock.UtcNow.AddHours(24), cleanup.AvailableAt);
        Assert.Equal(recordingId, payload!.RecordingId);
        Assert.Equal(published.ManagedFileId, payload.ManagedFileId);

        var duplicate = await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "owner-v2"));
        Assert.False(duplicate.Created);
        await using var afterDuplicate = new AllstarrDbContext(database.Options);
        Assert.Equal(1, await afterDuplicate.Jobs.CountAsync(item =>
            item.Type == RecordingKeptFiles.KeptCleanupJobType));
        Assert.Equal(clock.UtcNow.AddHours(24), (await afterDuplicate.Jobs.SingleAsync(item =>
            item.Id == cleanup.Id)).AvailableAt);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Fact]
    public async Task AcceptedReheartCancelsPendingGraceAndReacquiresTheSameStateReference()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, _, _, pipeline) = CreateInterestPipeline();
        var unfavorite = await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "owner-unheart-v1"));

        await using (var pending = new AllstarrDbContext(database.Options))
        {
            Assert.Single(await pending.Jobs.Where(item =>
                item.Type == RecordingKeptFiles.KeptCleanupJobType && item.State == DurableJobState.Pending)
                .ToArrayAsync());
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        var favorite = await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Favorite, "owner-reheart-v2"));

        await using var db = new AllstarrDbContext(database.Options);
        var state = await db.FavoriteStates.SingleAsync(item =>
            item.OwnerUserId == owner && item.ItemId == CanonicalItemId());
        var active = Assert.Single(await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == published.ManagedFileId && item.ReleasedAt == null).ToArrayAsync());
        var cleanup = Assert.Single(await db.Jobs.Where(item =>
            item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());

        Assert.Equal(favorite.EventId, state.LastEventId);
        Assert.True(state.IsFavorite);
        Assert.Equal($"heart:{state.Id:N}", active.ReferenceKey);
        Assert.Equal(DurableJobState.Cancelled, cleanup.State);
        Assert.NotEqual(unfavorite.EventId, favorite.EventId);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Fact]
    public async Task CanonicalAndExternalFavoriteAliasesKeepIndependentAcceptedStateReferences()
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, _, _, pipeline) = CreateInterestPipeline();
        var canonical = await pipeline.RecordAsync(
            Mutation(other, CanonicalItemId(), FavoriteOperation.Favorite, "canonical-v1"));
        var alias = await pipeline.RecordAsync(
            Mutation(other, "ext-deezer-song-source-track", FavoriteOperation.Favorite, "alias-v1"));

        await using var db = new AllstarrDbContext(database.Options);
        var canonicalState = await db.FavoriteStates.SingleAsync(item => item.Id != Guid.Empty &&
            item.OwnerUserId == other && item.ItemId == CanonicalItemId());
        var aliasState = await db.FavoriteStates.SingleAsync(item =>
            item.OwnerUserId == other && item.ItemId == "ext-deezer-song-source-track");
        var references = await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == published.ManagedFileId && item.ReleasedAt == null)
            .Select(item => item.ReferenceKey).ToArrayAsync();

        Assert.Equal(canonical.EventId, canonicalState.LastEventId);
        Assert.Equal(alias.EventId, aliasState.LastEventId);
        Assert.NotEqual(canonicalState.Id, aliasState.Id);
        Assert.Contains($"heart:{canonicalState.Id:N}", references);
        Assert.Contains($"heart:{aliasState.Id:N}", references);
        Assert.Equal(3, references.Length);
    }

    private async Task<KeptRecordingFile> PublishInitialFavoriteAsync()
    {
        var intent = await SeedFavoriteIntentAsync(owner, CanonicalItemId(), existingJobId: jobId);
        return await Service().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{intent.StateId:N}",
            Metadata = new KeptRecordingMetadata("Accepted title", ["Accepted artist"], "Accepted album", 91_234)
        });
    }

    private (InterestClock Clock, DurableJobQueue Jobs, RecordingKeptFiles Kept, FavoriteActionPipeline Pipeline)
        CreateInterestPipeline()
    {
        var clock = new InterestClock(DateTimeOffset.UtcNow);
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(factory, options, new JobPayloadPolicy(options), clock);
        var kept = new RecordingKeptFiles(factory, artifacts,
            services.GetRequiredService<IServiceScopeFactory>(), placement, identities, clock, jobs: jobs, audioVerifier: FixtureAudioVerifier.Instance);
        return (clock, jobs, kept, new FavoriteActionPipeline(factory, jobs, clock, kept));
    }

    private FavoriteMutationRequest Mutation(Guid userId, string itemId,
        FavoriteOperation operation, string revision) =>
        new(PlaybackContext(ProtocolKind.Jellyfin, userId, itemId), itemId, operation, revision);

    private sealed class InterestClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
