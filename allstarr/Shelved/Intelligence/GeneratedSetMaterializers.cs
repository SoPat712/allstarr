using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public abstract class BackendGeneratedSetMaterializer(
    IDbContextFactory<AllstarrDbContext> factory,
    IBackendPlaylistTargetResolver targets) : IGeneratedSetMaterializer
{
    public abstract string Protocol { get; }

    public async Task<GeneratedSetMaterializationResult> MaterializeAsync(
        GeneratedSetMaterializationRequest request, CancellationToken cancellationToken)
    {
        IntelligencePolicyService.ValidateScope(request.Scope);
        if (!request.Scope.Protocol.Equals(Protocol, StringComparison.Ordinal) || request.GeneratedSetId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 300)
            return new(false, false, "generated_set_scope_invalid");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var set = await db.GeneratedSets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.GeneratedSetId &&
            item.TenantId == request.Scope.TenantId && item.OwnerUserId == request.Scope.OwnerUserId &&
            item.Protocol == request.Scope.Protocol && item.BackendInstanceId == request.Scope.BackendInstanceId &&
            item.LibraryScopeId == request.Scope.LibraryScopeId, cancellationToken);
        if (set == null) return new(false, false, "generated_set_scope_unavailable");
        var identity = await db.BackendIdentities.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == request.Scope.TenantId && item.UserId == request.Scope.OwnerUserId &&
            item.BackendType == request.Scope.Protocol && item.BackendInstanceId == request.Scope.BackendInstanceId,
            cancellationToken);
        if (identity == null) return new(false, false, "generated_set_backend_identity_unavailable");

        string? credentialReference = null;
        if (Protocol == "subsonic")
        {
            if (set.TargetCredentialReferenceId is not { } credentialId || !await db.SecretReferences.AsNoTracking().AnyAsync(item =>
                    item.Id == credentialId && item.TenantId == set.TenantId &&
                    item.BackendIdentityId == identity.Id && item.Purpose == BackendCredentialScope.SubsonicPurpose &&
                    item.RevokedAt == null,
                    cancellationToken))
                return new(false, false, "generated_set_subsonic_credential_unavailable");
            credentialReference = credentialId.ToString();
        }
        else if (set.TargetCredentialReferenceId.HasValue)
            return new(false, false, "generated_set_jellyfin_credential_not_allowed");

        var entries = await db.GeneratedSetEntries.Where(item => item.GeneratedSetId == set.Id &&
            item.TenantId == set.TenantId && item.OwnerUserId == set.OwnerUserId).OrderBy(item => item.Position)
            .ToListAsync(cancellationToken);
        var candidates = request.OrderedCandidates.GroupBy(item => item.TrackKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Score).First(), StringComparer.Ordinal);
        var orderedIds = new List<string>();
        foreach (var entry in entries)
        {
            if (!candidates.TryGetValue(entry.TrackKey, out var candidate))
            {
                AddResult(entry, "materialization-skipped-missing-candidate", 0,
                    "Skipped because the generated candidate facts are unavailable.");
                continue;
            }
            var local = await ResolveLocalAsync(db, request.Scope, candidate.Identity, cancellationToken);
            if (local == null)
            {
                AddResult(entry, "materialization-skipped-unmatched", 0,
                    "Skipped because no exact accepted item exists in this local backend library.");
                continue;
            }
            orderedIds.Add(local.BackendItemId);
            AddResult(entry, "materialization-local-match", 1,
                "Matched an exact item in the selected local backend library.");
        }
        await db.SaveChangesAsync(cancellationToken);
        if (orderedIds.Count == 0) return new(false, false, "generated_set_has_no_local_matches");

        var target = targets.Resolve(Protocol);
        var targetContext = new BackendPlaylistTargetContext(set.BackendInstanceId, identity.PrincipalId,
            credentialReference, set.TenantId);
        var name = SafeName(set.Name, set.ScheduleId ?? set.Id);
        BackendPlaylistSnapshot? before = null;
        if (set.ScheduleId is { } scheduleId)
        {
            var previousTargetId = await db.GeneratedSets.AsNoTracking().Where(item => item.Id != set.Id &&
                    item.ScheduleId == scheduleId && item.TenantId == set.TenantId &&
                    item.OwnerUserId == set.OwnerUserId && item.Protocol == set.Protocol &&
                    item.BackendInstanceId == set.BackendInstanceId && item.LibraryScopeId == set.LibraryScopeId &&
                    item.MaterializationState == GeneratedSetMaterializationState.Succeeded &&
                    item.BackendPlaylistId != null).OrderByDescending(item => item.MaterializedAt)
                .Select(item => item.BackendPlaylistId).FirstOrDefaultAsync(cancellationToken);
            if (previousTargetId != null)
            {
                var previous = await target.ReadAsync(targetContext, previousTargetId, cancellationToken);
                if (previous.IsSuccess) before = previous.Value;
                else if (previous.Status != BackendPlaylistTargetStatus.NotFound)
                    return Failure(previous.Status, previous.ErrorCode);
            }
        }
        if (before == null)
        {
            var found = await target.FindByNameAsync(targetContext, name, cancellationToken);
            if (!found.IsSuccess) return Failure(found.Status, found.ErrorCode);
            before = found.Value;
        }
        var description = "Generated by Allstarr from explained recommendations. Only exact local library matches are included.";
        var write = await target.WriteAsync(targetContext, new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile, new BackendPlaylistMetadata(name, description), orderedIds,
            request.IdempotencyKey, before?.BackendPlaylistId, before?.NativeRevision, before?.Fingerprint,
            before?.Members.Select(item => item.BackendItemId), removeStaleSyncOwnedItems: true), cancellationToken);
        if (!write.IsSuccess || write.Value == null) return Failure(write.Status, write.ErrorCode);

        if (write.Value.UnsupportedMetadataFields.Count > 0)
        {
            foreach (var entry in entries.Where(item => item.ExplanationJson.Contains("materialization-local-match", StringComparison.Ordinal)))
                AddResult(entry, "materialization-metadata-limited", 0,
                    $"The backend did not support: {string.Join(", ", write.Value.UnsupportedMetadataFields)}.");
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(true, BackendPlaylistId: write.Value.Snapshot.BackendPlaylistId,
            TargetRevision: write.Value.Snapshot.NativeRevision ?? write.Value.Snapshot.Fingerprint);
    }

    private static async Task<LibraryTrackRecord?> ResolveLocalAsync(AllstarrDbContext db, IntelligenceScope scope,
        RecommendationTrackIdentity? identity, CancellationToken cancellationToken)
    {
        if (identity == null) return null;
        var local = db.LibraryTracks.AsNoTracking().Where(item => item.TenantId == scope.TenantId &&
            item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
            item.BackendInstanceId == scope.BackendInstanceId && item.LibraryScopeId == scope.LibraryScopeId);
        if (!string.IsNullOrWhiteSpace(identity.BackendItemId))
            return await local.SingleOrDefaultAsync(item => item.BackendItemId == identity.BackendItemId, cancellationToken);
        if (identity.LibraryTrackId is { } libraryTrackId)
            return await local.SingleOrDefaultAsync(item => item.Id == libraryTrackId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(identity.MusicBrainzRecordingId))
            return await UniqueAsync(local.Where(item => item.MusicBrainzRecordingId == identity.MusicBrainzRecordingId), cancellationToken);
        if (!string.IsNullOrWhiteSpace(identity.Isrc))
            return await UniqueAsync(local.Where(item => item.Isrc == identity.Isrc), cancellationToken);
        if (!string.IsNullOrWhiteSpace(identity.ProviderId) && !string.IsNullOrWhiteSpace(identity.ProviderTrackId))
        {
            var canonicalIds = await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.TenantId == scope.TenantId &&
                item.ProviderId == identity.ProviderId && item.ExternalId == identity.ProviderTrackId &&
                item.Verification != ProviderIdentityVerification.Unknown).Select(item => (Guid?)item.CanonicalRecordingId)
                .Distinct().Take(2).ToListAsync(cancellationToken);
            if (canonicalIds.Count == 1) return await UniqueAsync(local.Where(item => item.CanonicalRecordingId == canonicalIds[0]), cancellationToken);
        }
        return null;
    }

    private static async Task<LibraryTrackRecord?> UniqueAsync(IQueryable<LibraryTrackRecord> query, CancellationToken token)
    { var values = await query.Take(2).ToListAsync(token); return values.Count == 1 ? values[0] : null; }

    private static void AddResult(GeneratedSetEntryRecord entry, string code, double weight, string explanation)
    {
        RecommendationSignal[] signals;
        try { signals = JsonSerializer.Deserialize<RecommendationSignal[]>(entry.ExplanationJson) ?? []; }
        catch (JsonException) { signals = []; }
        var retained = code == "materialization-metadata-limited"
            ? signals.Where(item => item.Code != code)
            : signals.Where(item => item.Code is not ("materialization-local-match" or "materialization-skipped-unmatched" or "materialization-skipped-missing-candidate"));
        entry.ExplanationJson = JsonSerializer.Serialize(retained
            .Append(new RecommendationSignal(code, weight, explanation)).ToArray());
    }

    private static string SafeName(string value, Guid id)
    {
        value = new string((value ?? "Generated recommendations").Trim().Where(character => !char.IsControl(character)).ToArray());
        var suffix = $" [Allstarr {id.ToString("N")[..8]}]";
        if (value.Length > 180 - suffix.Length) value = value[..(180 - suffix.Length)].TrimEnd();
        return (value.Length == 0 ? "Generated recommendations" : value) + suffix;
    }

    private static GeneratedSetMaterializationResult Failure(BackendPlaylistTargetStatus status, string? code) => status switch
    {
        BackendPlaylistTargetStatus.BackendFailure => new(false, true, code ?? "generated_set_backend_failure"),
        BackendPlaylistTargetStatus.Cancelled => new(false, true, "generated_set_cancelled"),
        BackendPlaylistTargetStatus.Unauthorized => new(false, false, "generated_set_backend_unauthorized"),
        BackendPlaylistTargetStatus.Conflict => new(false, true, "generated_set_backend_conflict"),
        _ => new(false, false, code ?? "generated_set_target_unsupported")
    };

}

public sealed class JellyfinGeneratedSetMaterializer(IDbContextFactory<AllstarrDbContext> factory,
    IBackendPlaylistTargetResolver targets) : BackendGeneratedSetMaterializer(factory, targets)
{ public override string Protocol => "jellyfin"; }

public sealed class SubsonicGeneratedSetMaterializer(IDbContextFactory<AllstarrDbContext> factory,
    IBackendPlaylistTargetResolver targets) : BackendGeneratedSetMaterializer(factory, targets)
{ public override string Protocol => "subsonic"; }

public static class GeneratedSetMaterializerRegistration
{
    public static IServiceCollection AddGeneratedSetMaterializers(this IServiceCollection services)
    {
        services.AddSingleton<IGeneratedSetMaterializer, JellyfinGeneratedSetMaterializer>();
        services.AddSingleton<IGeneratedSetMaterializer, SubsonicGeneratedSetMaterializer>();
        return services;
    }
}
