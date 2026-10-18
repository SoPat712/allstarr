using System.Text.Json;
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

    public async Task<IReadOnlyList<ProviderRecordingAvailabilityRequest>> SelectRecordingSamplesAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        string[] sourceProviders = ["deezer", "spotify", "tidal", "qobuz"];
        var identities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => sourceProviders.Contains(item.ProviderId) && item.ResourceKind == ProviderResourceKind.Track &&
                item.Scope == ProviderIdentityScope.Catalog && item.ProviderAccountId == null &&
                (item.Verification == ProviderIdentityVerification.Verified || item.Verification == ProviderIdentityVerification.Pinned))
            .OrderByDescending(item => item.VerifiedAt).ThenBy(item => item.Id)
            .Take(CorpusLimit * 4).ToArrayAsync(cancellationToken);
        var recordingIds = identities.Select(item => item.CanonicalRecordingId).Distinct().Take(CorpusLimit).ToArray();
        var recordings = await db.CanonicalRecordings.AsNoTracking().Include(item => item.Identifiers)
            .Where(item => recordingIds.Contains(item.Id) && item.MergedIntoId == null)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var identityIds = identities.Select(item => item.Id).ToArray();
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.ProviderTrackIdentityId != null && identityIds.Contains(item.ProviderTrackIdentityId.Value))
            .OrderByDescending(item => item.RetrievedAt).ThenBy(item => item.Id)
            .Take(CorpusLimit * 4).ToArrayAsync(cancellationToken);
        var result = new List<ProviderRecordingAvailabilityRequest>();
        foreach (var group in identities.GroupBy(item => item.CanonicalRecordingId))
        {
            if (!recordings.TryGetValue(group.Key, out var recording)) continue;
            var groupIds = group.Select(item => item.Id).ToHashSet();
            foreach (var snapshot in snapshots.Where(item => groupIds.Contains(item.ProviderTrackIdentityId!.Value)))
            {
                try
                {
                    using var document = JsonDocument.Parse(snapshot.PayloadJson);
                    var root = document.RootElement;
                    JsonElement Property(string name) => root.ValueKind == JsonValueKind.Object
                        ? root.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
                    string? Text(string name) => Property(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
                    long? Number(string name) => Property(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;
                    var artists = new List<string>();
                    if (Text("artist") is { Length: > 0 } artist) artists.Add(artist);
                    var credits = Property("artists");
                    if (credits.ValueKind == JsonValueKind.Array)
                        foreach (var credit in credits.EnumerateArray())
                        {
                            var name = credit.ValueKind == JsonValueKind.String ? credit.GetString() :
                                credit.ValueKind == JsonValueKind.Object && credit.TryGetProperty("name", out var value) ? value.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(name)) artists.Add(name);
                        }
                    var title = string.IsNullOrWhiteSpace(recording.Title) ? Text("title") : recording.Title;
                    if (string.IsNullOrWhiteSpace(title) || artists.Count == 0) continue;
                    result.Add(new(recording.Id.ToString("N"), title, artists.Distinct().ToArray(),
                        recording.Identifiers.FirstOrDefault(item => item.Kind == RecordingIdentifierKinds.Isrc)?.Value ?? Text("isrc"),
                        recording.DurationMilliseconds ?? Number("durationMilliseconds") ?? Number("durationMs"),
                        group.GroupBy(item => item.ProviderId).ToDictionary(items => items.Key, items => items.First().ExternalId)));
                    break;
                }
                catch (JsonException) { }
            }
        }
        if (result.Count == 0) return result;
        var offset = Random.Shared.Next(result.Count);
        return result.Skip(offset).Concat(result.Take(offset)).ToArray();
    }

    public void Dispose() => _nextIndexes.Dispose();
}
