using System.Data;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryArtistReviewPayload(Guid UserId);

public sealed class LibraryArtistReviewJob(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    IPlatformClock clock, IBackendLibraryAccessResolver libraryAccess, LibraryArtistReview review,
    LibraryArtistIndex index, ILibraryArtistLinks links, IProviderRegistry registry, IProviderRouter router,
    ILogger<LibraryArtistReviewJob> logger) : IDurableJobHandler
{
    public const string Type = "library-artist.review";
    private const string CheckpointKind = "library-artist-review-checkpoint";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private sealed record Checkpoint(string? LastId, int Completed, DateTimeOffset NextAt);
    public string JobType => Type;

    public async Task<(bool Queued, bool Running)> RequestAsync(Guid owner, CancellationToken token)
    {
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context?.Principal?.IsAdministrator != true)
            throw new UnauthorizedAccessException("An administrator is required to review library artists.");
        if (!access.Access.Succeeded) throw new BackendLibraryAccessException(access.Access);
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var active = await db.Jobs.AsNoTracking().Where(job => job.Type == Type &&
                    (job.State == DurableJobState.Pending || job.State == DurableJobState.Running ||
                     job.State == DurableJobState.RetryScheduled))
                .OrderByDescending(job => job.CreatedAt).FirstOrDefaultAsync(token);
            if (active != null) return (false, true);
            var key = $"library-artists:{clock.UtcNow.UtcDateTime:yyyy-MM-dd}";
            if (await db.Jobs.AnyAsync(job => job.Type == Type && job.IdempotencyKey == key, token))
                return (false, false);
            var result = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<LibraryArtistReviewPayload>(
                Type, key, new(owner), owner, Priority: -60, CorrelationId: "library-artist-review"), token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return (result.Created, !result.Created);
        }
        finally { Gate.Release(); }
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        LibraryArtistReviewPayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<LibraryArtistReviewPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null || payload.UserId == Guid.Empty || execution.Claim.OwnerUserId != payload.UserId)
            return DurableJobCompletion.Failure("library_artist_review_invalid", "The saved artist review request is invalid.");
        var owner = payload.UserId;
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context?.Principal?.IsAdministrator != true)
            return DurableJobCompletion.Failure("library_artist_review_denied", "The artist review administrator is unavailable.");
        if (!access.Access.Succeeded)
        {
            var error = new BackendLibraryAccessException(access.Access);
            return error.Retryable
                ? DurableJobCompletion.Defer(error.ErrorCode, error.Message, TimeSpan.FromSeconds(30))
                : DurableJobCompletion.Failure(error.ErrorCode, error.Message);
        }
        if (!await jobs.IsExecutionCurrentAsync(execution.Claim, token)) return DurableJobCompletion.Cancelled();
        var context = access.Context;
        await using var db = await factory.CreateDbContextAsync(token);
        var key = CanonicalCatalogKeys.Hash(execution.Claim.JobId.ToString("D"));
        var previous = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
                item.ProviderId == "allstarr" && item.ResourceKind == CheckpointKind && item.ExternalIdHash == key &&
                item.SourceJobId == execution.Claim.JobId)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
        Checkpoint checkpoint;
        try
        {
            checkpoint = previous == null ? new(null, 0, clock.UtcNow) :
                JsonSerializer.Deserialize<Checkpoint>(previous.PayloadJson) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return DurableJobCompletion.Failure("library_artist_review_checkpoint_invalid", "The saved artist review progress is invalid.");
        }
        var now = clock.UtcNow;
        if (checkpoint.NextAt > now) return DurableJobCompletion.Continue(checkpoint.NextAt - now);
        var artists = (await review.ReadLibraryAsync(context, token))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && item.Name.Length <= 300 && !item.Name.Any(char.IsControl))
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var artist = artists.FirstOrDefault(item => checkpoint.LastId == null || string.CompareOrdinal(item.Id, checkpoint.LastId) > 0);
        if (artist == null) return DurableJobCompletion.Success();

        // Reserve the next start before external work so retries and restarts retain the pacing.
        await SaveAsync(checkpoint with { NextAt = clock.UtcNow.Add(Interval) });
        var providers = registry.FindByCapability(ProviderCapabilityKind.Metadata).Select(item => item.Id)
            .Where(id => id is not ("jellyfin" or "subsonic" or "backend" or "allstarr")).ToArray();
        var actor = new ProviderActorContext(ProviderActorKind.SystemJob, owner, context.RequireActor().BackendPrincipal,
            execution.Claim.JobId);
        var policy = new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
            ProviderExplicitContentPolicy.Allow, false, true, false, providers);
        var plan = await router.PlanAsync<IProviderMetadataCapability>(new(ProviderCapabilityKind.Metadata, actor, policy,
            "library-artist-review", execution.Claim.CorrelationId, clock.UtcNow.AddMinutes(1), providers, cancellationToken: token));
        var candidates = new List<Artist>();
        foreach (var source in plan.Candidates.Where(item => providers.Contains(item.Provider.Id, StringComparer.Ordinal) &&
                     item.Descriptor.Hooks.Contains("searchArtists", StringComparer.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var found = await source.Implementation.SearchArtistsAsync(source.Context, new(artist.Name, new(20)));
                token.ThrowIfCancellationRequested();
                if (found.IsSuccess) candidates.AddRange(found.RequireValue().Items.Select(ProtocolProviderGateway.Map));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Artist review search timed out for {Provider}", source.Provider.Id);
            }
            catch (Exception error) when (error is HttpRequestException or TimeoutException or JsonException)
            {
                logger.LogWarning("Artist review search failed for {Provider} ({ErrorType})", source.Provider.Id, error.GetType().Name);
            }
        }
        await index.RecordCandidatesAsync(owner, [artist.Name], candidates, token);
        await links.RequestAsync(owner, artist.Name, token);
        checkpoint = new(artist.Id, checkpoint.Completed + 1, clock.UtcNow.Add(Interval));
        await SaveAsync(checkpoint);
        await execution.ReportProgressAsync(new("reviewing", $"Checked {checkpoint.Completed} of {artists.Length} library artists.",
            checkpoint.Completed, artists.Length), token);
        return artists.Any(item => string.CompareOrdinal(item.Id, artist.Id) > 0)
            ? DurableJobCompletion.Continue(Interval) : DurableJobCompletion.Success();

        async Task SaveAsync(Checkpoint state)
        {
            var json = JsonSerializer.Serialize(state);
            var snapshot = new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                SourceJobId = execution.Claim.JobId,
                Protocol = context.Protocol.ToString().ToLowerInvariant(),
                BackendInstanceId = context.BackendInstanceId,
                BackendPrincipalId = context.VerifiedBackendPrincipalId,
                ProviderId = "allstarr",
                ResourceKind = CheckpointKind,
                ExternalIdHash = key,
                SnapshotVersion = (previous?.SnapshotVersion ?? 0) + 1,
                ProviderRevision = "checkpoint",
                PayloadJson = json,
                PayloadSha256 = CanonicalCatalogKeys.Hash(json),
                CorrelationId = execution.Claim.CorrelationId,
                RetrievedAt = clock.UtcNow
            };
            db.ExternalMetadataSnapshots.Add(snapshot);
            await db.SaveChangesAsync(token);
            previous = snapshot;
        }
    }
}
