using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Services.Common;

public sealed record ProviderCtsTrackSelection(string TrackId, int CorpusSize);

public sealed class ProviderCtsTrackSelector(
    IDbContextFactory<AllstarrDbContext> contextFactory) : IDisposable
{
    public const int CorpusLimit = 100;
    private static readonly TimeSpan RotationLifetime = TimeSpan.FromHours(24);
    private readonly MemoryCache _nextIndexes = new(new MemoryCacheOptions
    {
        SizeLimit = 256
    });
    private readonly object _rotationLock = new();

    public async Task<ProviderCtsTrackSelection?> SelectAsync(
        Guid tenantId,
        string providerId,
        Guid? providerAccountId,
        CancellationToken cancellationToken)
    {
        providerId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var recentSnapshots = await (
            from snapshot in db.ExternalMetadataSnapshots.AsNoTracking()
            join identity in db.ProviderTrackIdentities.AsNoTracking()
                on snapshot.ProviderTrackIdentityId equals identity.Id
            where snapshot.ProviderAccountId == providerAccountId &&
                  snapshot.ProviderId == providerId &&
                  identity.ProviderId == providerId &&
                  identity.ResourceKind == ProviderResourceKind.Track
            orderby snapshot.RetrievedAt descending, identity.Id
            select new { identity.Id, identity.ExternalId })
            .Take(CorpusLimit * 4)
            .ToArrayAsync(cancellationToken);
        var corpus = recentSnapshots
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .Take(CorpusLimit)
            .ToArray();
        if (corpus.Length == 0)
        {
            corpus = await db.ProviderTrackIdentities
                .AsNoTracking()
                .Where(identity =>
                    identity.TenantId == tenantId &&
                    identity.ProviderId == providerId &&
                    identity.ResourceKind == ProviderResourceKind.Track &&
                    identity.Scope == ProviderIdentityScope.Catalog &&
                    (identity.Verification == ProviderIdentityVerification.Verified ||
                     identity.Verification == ProviderIdentityVerification.Pinned))
                .OrderByDescending(identity => identity.VerifiedAt)
                .ThenBy(identity => identity.Id)
                .Select(identity => new { identity.Id, identity.ExternalId })
                .Take(CorpusLimit)
                .ToArrayAsync(cancellationToken);
        }
        if (corpus.Length == 0) return null;

        var key = $"{providerId}:{(providerAccountId.HasValue ? providerAccountId.Value.ToString("N") : "account-free")}";
        int index;
        lock (_rotationLock)
        {
            index = _nextIndexes.TryGetValue<int>(key, out var current)
                ? (current + 1) % corpus.Length
                : Random.Shared.Next(corpus.Length);
            _nextIndexes.Set(
                key,
                index,
                new MemoryCacheEntryOptions
                {
                    Size = 1,
                    SlidingExpiration = RotationLifetime
                });
        }
        var selected = corpus[index % corpus.Length];
        return new ProviderCtsTrackSelection(selected.ExternalId, corpus.Length);
    }

    public void Dispose() => _nextIndexes.Dispose();
}
