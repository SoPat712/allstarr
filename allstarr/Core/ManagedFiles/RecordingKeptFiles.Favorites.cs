using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed record KeptFavoriteInterest(Guid OwnerUserId, Guid JobId, int AttemptNumber);

public sealed record KeptRecordingMetadata(string Title, string[] Artists, string? Album = null,
    long? DurationMilliseconds = null, int? ExplicitContentLyrics = null);

public sealed partial class RecordingKeptFiles
{
    public async Task<KeptRecordingFile?> RetainFavoriteAsync(KeptFavoriteInterest interest,
        CancellationToken cancellationToken = default)
    {
        if (interest.OwnerUserId == Guid.Empty || interest.JobId == Guid.Empty || interest.AttemptNumber < 1)
            return null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var proof = await FindFavoriteAsync(db, interest, cancellationToken);
            if (proof == null) return null;
            var recordingId = await ResolveKeptRecordingIdAsync(proof.Actor, proof.Event.ItemId, cancellationToken);
            if (recordingId == null) return null;
            var candidate = await FindCandidateAsync(db,
                scope.ServiceProvider.GetRequiredService<FilePlacementService>(),
                scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>(),
                recordingId.Value, true, cancellationToken);
            if (candidate == null) return null;
            await scope.ServiceProvider.GetRequiredService<IManagedFileOwnershipStore>().AddReferenceAsync(
                candidate.File.Id, new(Guid.CreateVersion7(), candidate.File.Id, null,
                    candidate.File.ScopeKey, HeartKey(proof.State.Id), clock.UtcNow), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToResult(recordingId.Value, candidate.File, candidate.MimeType);
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<Song?> FindSongAsync(ProviderActorContext actor, string itemId,
        CancellationToken cancellationToken = default)
    {
        if (actor.EffectiveUserId == null) return null;
        try
        {
            var recordingId = await ResolveKeptRecordingIdAsync(actor, itemId, cancellationToken);
            if (recordingId == null) return null;
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var recording = await db.CanonicalRecordings.AsNoTracking()
                .SingleAsync(item => item.Id == recordingId, cancellationToken);
            var snapshot = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                                  join canonical in db.CanonicalRecordings.AsNoTracking()
                                      on route.CanonicalRecordingId equals canonical.Id
                                  where (canonical.Id == recordingId || canonical.MergedIntoId == recordingId) &&
                                        route.DisplayMetadataJson != null
                                  orderby route.CanonicalRecordingId == recordingId descending, route.CreatedAt, route.ManagedFileId
                                  select route.DisplayMetadataJson).FirstOrDefaultAsync(cancellationToken);
            if (snapshot == null) return null;
            KeptRecordingMetadata? metadata;
            try { metadata = JsonSerializer.Deserialize<KeptRecordingMetadata>(snapshot); }
            catch (JsonException) { return null; }
            if (metadata == null || !ValidMetadata(metadata)) return null;
            return new()
            {
                Id = recording.PublicId,
                CanonicalRecordingId = recording.Id,
                Title = metadata.Title,
                Artist = metadata.Artists.FirstOrDefault() ?? "Unknown artist",
                Artists = metadata.Artists.ToList(),
                Album = metadata.Album ?? string.Empty,
                Duration = metadata.DurationMilliseconds is { } duration ? (int)Math.Min(duration / 1000, int.MaxValue) : null,
                PreciseDuration = metadata.DurationMilliseconds is { } precise ? TimeSpan.FromMilliseconds(precise) : null,
                ExplicitContentLyrics = metadata.ExplicitContentLyrics,
                ExternalProvider = "allstarr",
                ExternalId = recording.Id.ToString("D")
            };
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task<Guid?> ResolveKeptRecordingIdAsync(ProviderActorContext actor, string itemId,
        CancellationToken cancellationToken)
    {
        if (actor.EffectiveUserId == null) return null;
        var known = await identities.ResolveRecordingAsync(actor, itemId, cancellationToken);
        if (known != null) return known.Id;
        var parsed = LocalLibraryService.ParseExternalResource(itemId);
        if (!parsed.isExternal || parsed.type != "song") return null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var published = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                               join artifact in db.Set<allstarr.Core.Downloads.ProviderDownloadArtifactEntity>().AsNoTracking()
                                   on route.SourceArtifactId equals artifact.Id
                               join recording in db.CanonicalRecordings.AsNoTracking()
                                   on route.CanonicalRecordingId equals recording.Id
                               where artifact.ProviderId == parsed.provider && artifact.SourceExternalTrackId == parsed.externalId
                               select recording.MergedIntoId ?? recording.Id).Distinct().Take(2).ToArrayAsync(cancellationToken);
        return published.Length == 1 ? published[0] : null;
    }

    private async Task AuthorizeFavoritePublicationAsync(AllstarrDbContext db, DurableJobRecord job,
        KeptRecordingPublication request, CancellationToken cancellationToken)
    {
        if (job.Type != FavoriteActionPipeline.JobType)
        {
            if (request.ReferenceKey.StartsWith("heart:", StringComparison.Ordinal)) throw Denied();
            return;
        }
        var proof = await FindFavoriteAsync(db,
            new(request.InitiatingUserId, job.Id, request.AttemptNumber), cancellationToken);
        if (proof == null || request.ReferenceKey != HeartKey(proof.State.Id)) throw Denied();
        var favoriteRecording = await ResolveKeptRecordingIdAsync(proof.Actor, proof.Event.ItemId, cancellationToken);
        var requested = await identities.ResolveRecordingAsync(proof.Actor, request.RecordingId.ToString("D"), cancellationToken);
        if (favoriteRecording == null || requested?.Id != favoriteRecording) throw Denied();
    }

    private static async Task<FavoriteProof?> FindFavoriteAsync(AllstarrDbContext db,
        KeptFavoriteInterest interest, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == interest.JobId &&
            item.Type == FavoriteActionPipeline.JobType && item.OwnerUserId == interest.OwnerUserId &&
            item.State == DurableJobState.Running && item.AttemptCount == interest.AttemptNumber &&
            item.CancellationRequestedAt == null, cancellationToken);
        if (job == null) return null;
        FavoriteJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<FavoriteJobPayload>(job.PayloadJson); }
        catch (JsonException) { return null; }
        if (payload == null) return null;
        var favorite = await db.Set<FavoriteEventRecord>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == payload.EventId && item.JobId == job.Id && item.OwnerUserId == interest.OwnerUserId &&
            item.Operation == FavoriteOperation.Favorite && item.State != FavoriteEventState.Cancelled, cancellationToken);
        if (favorite == null || !await db.Users.AsNoTracking().AnyAsync(item =>
                item.Id == interest.OwnerUserId && item.Enabled && item.BackendType == favorite.Protocol &&
                item.BackendInstanceId == favorite.BackendInstanceId && item.BackendPrincipalId == favorite.BackendPrincipalId,
                cancellationToken)) return null;
        var state = await db.Set<FavoriteStateRecord>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.OwnerUserId == interest.OwnerUserId && item.Protocol == favorite.Protocol &&
            item.BackendInstanceId == favorite.BackendInstanceId && item.ItemId == favorite.ItemId &&
            item.IsFavorite && item.LastEventId == favorite.Id, cancellationToken);
        return state == null ? null : new(favorite, state, new(ProviderActorKind.User, favorite.OwnerUserId,
            new(favorite.Protocol, favorite.BackendInstanceId, favorite.BackendPrincipalId)));
    }

    private static string HeartKey(Guid stateId) => $"heart:{stateId:N}";
    private sealed record FavoriteProof(FavoriteEventRecord Event, FavoriteStateRecord State, ProviderActorContext Actor);

    private static bool ValidMetadata(KeptRecordingMetadata metadata) =>
        !string.IsNullOrWhiteSpace(metadata.Title) && metadata.Title.Length <= 500 &&
        metadata.Album is not { Length: > 500 } && metadata.Artists is { Length: <= 20 } &&
        metadata.Artists.All(item => !string.IsNullOrWhiteSpace(item) && item.Length <= 500) &&
        metadata.DurationMilliseconds is null or >= 0 and <= 922337203685 &&
        metadata.ExplicitContentLyrics is null or 0 or 1 or 2 or 3 or 6 or 7;

    private static string SerializeMetadata(KeptRecordingMetadata metadata)
    {
        if (!ValidMetadata(metadata)) throw new ArgumentException("The kept recording display metadata is invalid.");
        var json = JsonSerializer.Serialize(metadata);
        if (json.Length > 16000) throw new ArgumentException("The kept recording display metadata is too large.");
        return json;
    }
}
