using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    public async Task<IReadOnlyDictionary<string, CanonicalRecordingIdentity?>> ResolveRecordingsAsync(
        ProviderActorContext actor, IReadOnlyCollection<string> publicIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(publicIds);
        if (publicIds.Any(item => item == null))
            throw new ArgumentException("Recording IDs cannot be null.", nameof(publicIds));
        EnsureStorageReady();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateActorAsync(db, actor, cancellationToken);
        var records = await FindRecordingsByPublicIdsAsync(db, actor, publicIds, cancellationToken);
        return records.ToDictionary(item => item.Key,
            item => item.Value == null ? null : ToIdentity(item.Value), StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, CanonicalRecordingRecord?>> FindRecordingsByPublicIdsAsync(
        AllstarrDbContext db, ProviderActorContext actor, IReadOnlyCollection<string> publicIds,
        CancellationToken cancellationToken)
    {
        var lookups = publicIds.Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, CanonicalRecordingRecord?>(StringComparer.Ordinal);
        if (lookups.Length == 0) return result;
        var loaded = new Dictionary<Guid, CanonicalRecordingRecord>();
        var directIds = lookups.Select(item => Guid.TryParse(item, out var id) ? id : (Guid?)null)
            .Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();
        await LoadRecordingCandidatesAsync(db, lookups, directIds, loaded, cancellationToken);
        var byPublicId = loaded.Values.ToDictionary(item => item.PublicId, StringComparer.Ordinal);
        var roots = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var remaining = new List<PublicRecordingLookup>();
        foreach (var publicId in lookups)
        {
            if (byPublicId.TryGetValue(publicId, out var exact)) roots[publicId] = exact.Id;
            else if (Guid.TryParse(publicId, out var direct)) roots[publicId] = direct;
            else remaining.Add(ParseRecordingLookup(publicId));
        }
        await LoadRecordingCandidatesAsync(db,
            remaining.Select(item => item.AlternatePublicId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
            remaining.Where(item => item.DirectRecordingId.HasValue)
                .Select(item => item.DirectRecordingId!.Value).Distinct().ToArray(), loaded, cancellationToken);
        byPublicId = loaded.Values.ToDictionary(item => item.PublicId, StringComparer.Ordinal);
        var aliases = new List<PublicRecordingLookup>();
        foreach (var lookup in remaining)
        {
            if (lookup.AlternatePublicId is { } alternate && byPublicId.TryGetValue(alternate, out var record))
                roots[lookup.PublicId] = record.Id;
            else if (lookup.DirectRecordingId.HasValue) roots[lookup.PublicId] = lookup.DirectRecordingId;
            else
            {
                roots[lookup.PublicId] = null;
                if (lookup.ProviderId != null) aliases.Add(lookup);
            }
        }
        if (aliases.Count > 0)
        {
            aliases = aliases.Select(item => item with
            {
                ProviderId = _registry?.ResolveProviderId(item.ProviderId!) ?? item.ProviderId,
                ExternalIdHash = CanonicalCatalogKeys.Hash(item.ExternalId!)
            }).ToList();
            var providers = aliases.Select(item => item.ProviderId!).Distinct(StringComparer.Ordinal).ToArray();
            var hashes = aliases.Select(item => item.ExternalIdHash!).Distinct(StringComparer.Ordinal).ToArray();
            var userId = actor.EffectiveUserId;
            var links = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                EF.Parameter(providers).Contains(item.ProviderId) && item.ResourceKind == ProviderResourceKind.Track &&
                item.CatalogNamespace == DefaultCatalog && EF.Parameter(hashes).Contains(item.ExternalIdHash) &&
                item.Verification == ProviderIdentityVerification.Verified &&
                item.VerificationMethod != "automatic-suggestion" &&
                item.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
                item.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod &&
                (item.Scope == ProviderIdentityScope.Catalog || userId != null &&
                    db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId && account.Enabled &&
                        (account.OwnerUserId == null || account.OwnerUserId == userId))))
                .Select(item => new { item.ProviderId, item.ExternalIdHash, item.ExternalId, item.CanonicalRecordingId })
                .ToArrayAsync(cancellationToken);
            var targets = links.GroupBy(item => (item.ProviderId, item.ExternalIdHash, item.ExternalId))
                .ToDictionary(group => group.Key, group => group.Select(item => item.CanonicalRecordingId).Distinct().ToArray());
            foreach (var lookup in aliases)
            {
                var candidates = targets.GetValueOrDefault((lookup.ProviderId!, lookup.ExternalIdHash!, lookup.ExternalId!));
                if (candidates?.Length == 1) roots[lookup.PublicId] = candidates[0];
            }
        }

        var frontier = roots.Values.Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();
        var visited = new HashSet<Guid>();
        while (frontier.Length > 0)
        {
            var current = frontier.Where(visited.Add).ToArray();
            var missing = current.Where(item => !loaded.ContainsKey(item)).ToArray();
            await LoadRecordingCandidatesAsync(db, [], missing, loaded, cancellationToken);
            frontier = current.Where(loaded.ContainsKey).Select(item => loaded[item].MergedIntoId)
                .Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();
        }
        foreach (var (publicId, root) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[publicId] = root.HasValue
                ? await FindSurvivorAsync(db, root.Value, cancellationToken, loaded)
                : null;
        }
        return result;
    }

    private static async Task LoadRecordingCandidatesAsync(AllstarrDbContext db, string[] publicIds, Guid[] ids,
        IDictionary<Guid, CanonicalRecordingRecord> loaded, CancellationToken cancellationToken)
    {
        if (publicIds.Length == 0 && ids.Length == 0) return;
        var records = await db.CanonicalRecordings.Include(item => item.Identifiers)
            .Where(item => EF.Parameter(publicIds).Contains(item.PublicId) || EF.Parameter(ids).Contains(item.Id)).ToArrayAsync(cancellationToken);
        foreach (var record in records) loaded[record.Id] = record;
    }

    private PublicRecordingLookup ParseRecordingLookup(string publicId)
    {
        var (external, provider, kind, externalId) = LocalLibraryService.ParseExternalResource(publicId);
        if (!external || kind != "song") return new(publicId, null, null, null, null, null);
        var typed = $"ext-{provider}-song-{externalId}";
        var alternate = publicId == typed ? $"ext-{provider}-{externalId}" : typed;
        if (provider == "allstarr" && Guid.TryParse(externalId, out var direct))
            return new(publicId, direct, alternate, null, null, null);
        return new(publicId, null, alternate, provider, externalId, null);
    }

    private sealed record PublicRecordingLookup(string PublicId, Guid? DirectRecordingId,
        string? AlternatePublicId, string? ProviderId, string? ExternalId, string? ExternalIdHash);
}
