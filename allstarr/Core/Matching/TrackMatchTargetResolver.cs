using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed class TrackMatchTarget(string providerId, string externalId)
{
    public string ProviderId { get; } = AppleMusicClient.NormalizeProviderId(providerId);
    public string ExternalId { get; } = externalId;
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string ArtworkUrl => $"/api/admin/downloads/artwork/{Uri.EscapeDataString($"ext-{ProviderId}-song-{ExternalId}")}";
    public string? Isrc { get; set; }
}

public sealed record TrackMatchTargetLookup(
    Guid? CanonicalRecordingId,
    Guid OwnerUserId,
    TrackMatchTarget Target,
    ExternalMetadataSnapshotRecord? SourceSnapshot = null,
    bool ResolveTargetIdentity = false);

public sealed class TrackMatchTargetResolver(
    IDbContextFactory<AllstarrDbContext> factory,
    IApplicationCache? cache = null,
    RecordingOdesliEnrichmentQueue? enrichment = null)
{
    public static TrackMatchTarget? Create(TrackRouteProjection projection, TrackMatchRecord? decision)
    {
        if (projection.ReviewState is not (TrackMatchState.Accepted or TrackMatchState.Pinned) ||
            projection.PrimaryProviderRoute is not { } route) return null;
        var target = new TrackMatchTarget(route.ProviderId, route.ExternalId);
        try
        {
            using var document = JsonDocument.Parse(decision?.CandidateResultsJson ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return target;
            foreach (var candidate in document.RootElement.EnumerateArray())
            {
                var ids = Property(candidate, "providerTrackIds", "ProviderTrackIds");
                if (ids is not { ValueKind: JsonValueKind.Object } ||
                    !ids.Value.EnumerateObject().Any(item =>
                        AppleMusicClient.NormalizeProviderId(item.Name) == target.ProviderId &&
                        item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() == target.ExternalId))
                    continue;
                Fill(target, candidate);
                target.Isrc ??= Text(candidate, "candidateIsrc", "CandidateIsrc");
                break;
            }
        }
        catch (JsonException) { }
        return target;
    }

    public async Task EnrichAsync(TrackMatchActor actor, IReadOnlyCollection<TrackMatchTargetLookup> lookups,
        CancellationToken cancellationToken)
    {
        var visible = lookups.Where(item => actor.IsAdministrator || item.OwnerUserId == actor.UserId).ToArray();
        if (visible.Length == 0) return;
        var recordings = new Dictionary<Guid, CanonicalRecordingRecord>();
        var snapshots = new Dictionary<(Guid RecordingId, Guid OwnerUserId), StoredMetadata[]>();
        var targetIdentities = new Dictionary<(string ProviderId, string ExternalId), ProviderTrackIdentityRecord>();
        Guid? RecordingId(TrackMatchTargetLookup lookup) =>
            targetIdentities.GetValueOrDefault((lookup.Target.ProviderId, lookup.Target.ExternalId))?.CanonicalRecordingId ??
            (lookup.ResolveTargetIdentity ? null : lookup.CanonicalRecordingId);
        if (visible.Any(item => item.CanonicalRecordingId.HasValue || item.ResolveTargetIdentity))
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var providerIds = visible.Select(item => item.Target.ProviderId).Distinct().ToList();
            if (providerIds.Contains("apple-musickit"))
                providerIds.AddRange(["apple-download", "applemusic", "apple-music", "apple_music"]);
            var externalIds = visible.Select(item => item.Target.ExternalId).Distinct().ToArray();
            var identities = await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => providerIds.Contains(item.ProviderId) && externalIds.Contains(item.ExternalId) &&
                               item.ResourceKind == ProviderResourceKind.Track && item.Scope == ProviderIdentityScope.Catalog &&
                               (item.Verification == ProviderIdentityVerification.Verified || item.Verification == ProviderIdentityVerification.Pinned))
                .ToArrayAsync(cancellationToken);
            targetIdentities = identities.OrderByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
                .ThenByDescending(item => item.DecisionVersion).ThenByDescending(item => item.VerifiedAt)
                .DistinctBy(item => (AppleMusicClient.NormalizeProviderId(item.ProviderId), item.ExternalId))
                .ToDictionary(item => (AppleMusicClient.NormalizeProviderId(item.ProviderId), item.ExternalId));
            var recordingIds = visible.Select(RecordingId).OfType<Guid>().Distinct().ToArray();
            recordings = await db.CanonicalRecordings.AsNoTracking().Include(item => item.Identifiers)
                .Where(item => recordingIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
            var ownerIds = visible.Select(item => item.OwnerUserId).Distinct().ToArray();
            var stored = await (from snapshot in db.ExternalMetadataSnapshots.AsNoTracking()
                                join identity in db.ProviderTrackIdentities.AsNoTracking()
                                    on snapshot.ProviderTrackIdentityId equals (Guid?)identity.Id
                                where recordingIds.Contains(identity.CanonicalRecordingId) &&
                                      ownerIds.Contains(snapshot.OwnerUserId) &&
                                      (actor.IsAdministrator || snapshot.OwnerUserId == actor.UserId)
                                orderby snapshot.RetrievedAt descending, snapshot.SnapshotVersion descending
                                select new { identity.CanonicalRecordingId, identity.ProviderId, identity.ExternalId, Snapshot = snapshot })
                .ToArrayAsync(cancellationToken);
            snapshots = stored.GroupBy(item => (item.CanonicalRecordingId, item.Snapshot.OwnerUserId))
                .ToDictionary(group => group.Key, group => group.Select(item =>
                    new StoredMetadata(item.Snapshot, AppleMusicClient.NormalizeProviderId(item.ProviderId), item.ExternalId)).ToArray());
        }

        var cached = new Dictionary<(string ProviderId, string ExternalId), string?>();
        var queued = new HashSet<(Guid OwnerUserId, Guid IdentityId)>();
        foreach (var lookup in visible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = lookup.Target;
            if (RecordingId(lookup) is { } recordingId)
            {
                if (recordings.TryGetValue(recordingId, out var recording))
                {
                    target.Title ??= Nonempty(recording.Title);
                    target.DurationMilliseconds ??= recording.DurationMilliseconds;
                    target.Isrc ??= recording.Identifiers.Where(item => item.Kind == RecordingIdentifierKinds.Isrc)
                        .OrderBy(item => item.Value, StringComparer.Ordinal).Select(item => item.Value).FirstOrDefault();
                }
                var stored = snapshots.GetValueOrDefault((recordingId, lookup.OwnerUserId)) ?? [];
                foreach (var item in stored.OrderByDescending(item =>
                             item.ProviderId == target.ProviderId && item.ExternalId == target.ExternalId))
                    FillJson(target, item.Snapshot.PayloadJson, includeAlbum:
                        item.ProviderId == target.ProviderId && item.ExternalId == target.ExternalId);
                if (lookup.CanonicalRecordingId == recordingId &&
                    lookup.SourceSnapshot is { } source && source.OwnerUserId == lookup.OwnerUserId)
                    FillJson(target, source.PayloadJson, includeAlbum: false);
            }
            if (cache != null && (target.Title == null || target.Artist == null || target.Album == null ||
                                  target.DurationMilliseconds == null))
            {
                var providers = target.ProviderId == "apple-musickit"
                    ? new[] { target.ProviderId, "apple-download" } : [target.ProviderId];
                foreach (var provider in providers)
                {
                    var key = (provider, target.ExternalId);
                    if (!cached.TryGetValue(key, out var json))
                    {
                        json = await cache.GetStringAsync(CacheKeyBuilder.BuildPlaybackMetadataKey(provider, target.ExternalId));
                        cached[key] = json;
                    }
                    if (json != null) FillJson(target, json, cachedMetadata: true);
                }
            }
            if (enrichment != null && target.Title == null && target.Artist == null && target.Album == null &&
                target.DurationMilliseconds == null && target.Isrc == null)
            {
                var identity = targetIdentities.GetValueOrDefault((target.ProviderId, target.ExternalId));
                if (identity?.Verification == ProviderIdentityVerification.Verified &&
                    identity.VerificationMethod != "source-snapshot-hash" && queued.Add((lookup.OwnerUserId, identity.Id)))
                    await enrichment.EnqueueAsync(lookup.OwnerUserId, identity.Id, "match-review-enrichment", cancellationToken);
            }
        }
    }

    private static void FillJson(TrackMatchTarget target, string json, bool cachedMetadata = false, bool includeAlbum = true)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (cachedMetadata)
            {
                var metadata = Property(root, "metadata", "Metadata");
                if (metadata == null) return;
                root = metadata.Value;
            }
            Fill(target, root, includeAlbum);
        }
        catch (JsonException) { }
    }

    private static void Fill(TrackMatchTarget target, JsonElement metadata, bool includeAlbum = true)
    {
        target.Title ??= Text(metadata, "title", "Title", "name", "Name");
        target.Artist ??= TrackSnapshotMetadata.Artist(metadata);
        if (includeAlbum) target.Album ??= Text(metadata, "album", "Album", "albumTitle", "AlbumTitle");
        target.Isrc ??= Text(metadata, "isrc", "Isrc", "ISRC");
        target.DurationMilliseconds ??= Number(metadata, "durationMilliseconds", "DurationMilliseconds", "durationMs", "DurationMs") ??
                                        Number(metadata, "durationSeconds", "DurationSeconds") * 1000;
    }

    private static JsonElement? Property(JsonElement root, params string[] keys)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
            if (root.TryGetProperty(key, out var value)) return value;
        return null;
    }

    private static string? Text(JsonElement root, params string[] keys) =>
        Property(root, keys) is { ValueKind: JsonValueKind.String } value ? Nonempty(value.GetString()) : null;

    private static long? Number(JsonElement root, params string[] keys) =>
        Property(root, keys) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) && number > 0
            ? number : null;

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record StoredMetadata(ExternalMetadataSnapshotRecord Snapshot, string ProviderId, string ExternalId);
}
