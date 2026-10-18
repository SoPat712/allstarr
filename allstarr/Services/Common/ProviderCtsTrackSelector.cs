using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
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
        CancellationToken cancellationToken,
        Guid? requestingAdministratorId = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        string[] sourceProviders = ["deezer", "spotify", "tidal", "qobuz", "spotiflac-tidal-web", "spotiflac-deezer", "spotiflac-qobuz-web"];
        var identities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => sourceProviders.Contains(item.ProviderId) && item.ResourceKind == ProviderResourceKind.Track &&
                item.Scope == ProviderIdentityScope.Catalog && item.ProviderAccountId == null &&
                (item.Verification == ProviderIdentityVerification.Verified || item.Verification == ProviderIdentityVerification.Pinned))
            .OrderByDescending(item => item.VerifiedAt).ThenBy(item => item.Id)
            .Take(CorpusLimit * 4).ToArrayAsync(cancellationToken);
        var recordingIds = identities.Select(item => item.CanonicalRecordingId).Distinct().Take(CorpusLimit).ToArray();
        if (recordingIds.Length == 0) return [];
        var recordings = await db.CanonicalRecordings.AsNoTracking().Include(item => item.Identifiers)
            .Where(item => recordingIds.Contains(item.Id) && item.MergedIntoId == null)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var metadataQuery = from snapshot in db.ExternalMetadataSnapshots.AsNoTracking()
                            join identity in db.ProviderTrackIdentities.AsNoTracking()
                                on snapshot.ProviderTrackIdentityId equals (Guid?)identity.Id
                            where recordingIds.Contains(identity.CanonicalRecordingId) &&
                                  identity.ResourceKind == ProviderResourceKind.Track
                            select new { identity.CanonicalRecordingId, identity.Scope, IdentityAccountId = identity.ProviderAccountId, Snapshot = snapshot };
        var catalogSnapshots = await metadataQuery
            .Where(item => item.Scope == ProviderIdentityScope.Catalog && item.IdentityAccountId == null && item.Snapshot.ProviderAccountId == null)
            .OrderByDescending(item => item.Snapshot.RetrievedAt).ThenByDescending(item => item.Snapshot.SnapshotVersion).ThenBy(item => item.Snapshot.Id)
            .Select(item => new StoredMetadata(item.CanonicalRecordingId, item.Snapshot.PayloadJson))
            .Take(CorpusLimit * 4).ToArrayAsync(cancellationToken);
        var decisions = await db.TrackMatches.AsNoTracking()
            .Where(item => item.CanonicalRecordingId != null && recordingIds.Contains(item.CanonicalRecordingId.Value))
            .OrderByDescending(item => item.DecidedAt).ThenByDescending(item => item.DecisionVersion).ThenBy(item => item.Id)
            .Select(item => new StoredMetadata(item.CanonicalRecordingId!.Value, item.CandidateResultsJson))
            .Take(CorpusLimit * 4).ToArrayAsync(cancellationToken);
        var accountSnapshots = requestingAdministratorId.HasValue
            ? await metadataQuery
                .Where(item => item.Snapshot.OwnerUserId == requestingAdministratorId.Value &&
                    (item.Scope == ProviderIdentityScope.Account || item.IdentityAccountId != null || item.Snapshot.ProviderAccountId != null))
                .OrderByDescending(item => item.Snapshot.RetrievedAt).ThenByDescending(item => item.Snapshot.SnapshotVersion).ThenBy(item => item.Snapshot.Id)
                .Select(item => new StoredMetadata(item.CanonicalRecordingId, item.Snapshot.PayloadJson))
                .Take(CorpusLimit * 4).ToArrayAsync(cancellationToken)
            : [];
        var catalogByRecording = catalogSnapshots.ToLookup(item => item.RecordingId);
        var decisionsByRecording = decisions.ToLookup(item => item.RecordingId);
        var accountByRecording = accountSnapshots.ToLookup(item => item.RecordingId);
        var result = new List<ProviderRecordingAvailabilityRequest>();
        foreach (var group in identities.GroupBy(item => item.CanonicalRecordingId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!recordings.TryGetValue(group.Key, out var recording)) continue;
            var metadata = new SampleMetadata
            {
                Title = Nonempty(recording.Title),
                DurationMilliseconds = recording.DurationMilliseconds,
                Isrc = recording.Identifiers.Where(item => item.Kind == RecordingIdentifierKinds.Isrc)
                    .OrderBy(item => item.Value, StringComparer.Ordinal).Select(item => Nonempty(item.Value)).FirstOrDefault(item => item != null)
            };
            foreach (var snapshot in catalogByRecording[group.Key]) metadata.FillJson(snapshot.Json);
            foreach (var decision in decisionsByRecording[group.Key])
            {
                try
                {
                    using var document = JsonDocument.Parse(decision.Json);
                    foreach (var identity in group)
                        if (TrackMatchTargetResolver.FindCandidate(document.RootElement, identity.ProviderId, identity.ExternalId) is { } candidate)
                        {
                            metadata.Fill(candidate);
                            break;
                        }
                }
                catch (JsonException) { }
            }
            foreach (var snapshot in accountByRecording[group.Key]) metadata.FillJson(snapshot.Json);
            if (metadata.Title == null || metadata.Artists.Length == 0) continue;
            var providerIds = group.Select(AvailabilityId).Where(item => !string.IsNullOrWhiteSpace(item.ExternalId))
                .GroupBy(item => item.ProviderId).ToDictionary(items => items.Key, items => items.First().ExternalId);
            if (providerIds.Count == 0) continue;
            result.Add(new(recording.Id.ToString("N"), metadata.Title, metadata.Artists,
                metadata.Isrc, metadata.DurationMilliseconds, providerIds));
        }
        if (result.Count == 0) return result;
        var offset = Random.Shared.Next(result.Count);
        return result.Skip(offset).Concat(result.Take(offset)).ToArray();
    }

    private static (string ProviderId, string ExternalId) AvailabilityId(ProviderTrackIdentityRecord identity)
    {
        var provider = identity.ProviderId switch
        {
            "spotiflac-tidal-web" => "tidal",
            "spotiflac-deezer" => "deezer",
            "spotiflac-qobuz-web" => "qobuz",
            _ => identity.ProviderId
        };
        var prefix = $"{provider}:";
        var externalId = provider != identity.ProviderId && identity.ExternalId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? identity.ExternalId[prefix.Length..] : identity.ExternalId;
        return (provider, externalId);
    }

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record StoredMetadata(Guid RecordingId, string Json);

    private sealed class SampleMetadata
    {
        public string? Title { get; set; }
        public string[] Artists { get; private set; } = [];
        public string? Isrc { get; set; }
        public long? DurationMilliseconds { get; set; }

        public void FillJson(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                Fill(document.RootElement);
            }
            catch (JsonException) { }
        }

        public void Fill(JsonElement root)
        {
            Title ??= Text(root, "title") ?? Text(root, "name");
            Isrc ??= Text(root, "isrc") ?? Text(root, "candidateIsrc");
            DurationMilliseconds ??= Number(root, "durationMilliseconds") ?? Number(root, "durationMs") ??
                                      Number(root, "durationSeconds") * 1000;
            if (Artists.Length > 0) return;
            if (Property(root, "artists") is { ValueKind: JsonValueKind.Array } credits)
                Artists = credits.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? Nonempty(item.GetString()) : Text(item, "name"))
                    .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (Artists.Length == 0 && (Text(root, "artist") ?? Text(root, "primaryArtist")) is { } artist)
                Artists = [artist];
        }

        private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;

        private static string? Text(JsonElement root, string name) =>
            Property(root, name) is { ValueKind: JsonValueKind.String } value ? Nonempty(value.GetString()) : null;

        private static long? Number(JsonElement root, string name) =>
            Property(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) && number > 0
                ? number : null;
    }

    public void Dispose() => _nextIndexes.Dispose();
}
