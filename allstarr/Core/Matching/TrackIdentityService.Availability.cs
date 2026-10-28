using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    public async Task RememberPlaybackMetadataAsync(ProviderExecutionContext execution, ProviderExternalResourceId track,
        allstarr.Models.Domain.Song song, CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        ThrowIfUnavailable(execution, cancellationToken);
        execution.RequireResourceOwner(track, ProviderResourceKind.Track);
        if (execution.Actor.EffectiveUserId is not { } user || execution.Actor.BackendPrincipal is not { } backend ||
            string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist)) return;
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ValidateExecutionContextAsync(db, execution, cancellationToken);
        var link = await ResolveCoreAsync(db, track, account?.Id, cancellationToken);
        if (link == null || link.Verification != ProviderIdentityVerification.Verified || link.VerificationMethod is "automatic-suggestion" or "manual-released" or "manual-replaced" or "superseded") return;
        var accountId = link.Scope == ProviderIdentityScope.Account ? account?.Id : null;
        var payload = JsonSerializer.Serialize(new
        {
            song.Title,
            song.Artist,
            song.Album,
            song.Isrc,
            DurationMilliseconds = song.PreciseDuration is { } precise ? (long?)precise.TotalMilliseconds : song.Duration is { } seconds ? seconds * 1000L : null,
            song.ExplicitContentLyrics
        });
        var hash = CanonicalCatalogKeys.Hash(track.Value);
        var previous = await db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == user &&
            item.ProviderId == track.ProviderId && item.ProviderAccountId == accountId &&
            item.ResourceKind == "playback-metadata" && item.ExternalIdHash == hash)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);
        if (previous?.PayloadJson == payload && previous.ProviderTrackIdentityId == link.LinkId) return;
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            ProviderAccountId = accountId,
            ProviderTrackIdentityId = link.LinkId,
            BackendInstanceId = backend.BackendInstanceId,
            BackendPrincipalId = backend.PrincipalId,
            Protocol = backend.BackendType,
            ProviderId = track.ProviderId,
            ResourceKind = "playback-metadata",
            ExternalIdHash = hash,
            SnapshotVersion = (previous?.SnapshotVersion ?? 0) + 1,
            ProviderRevision = "playback:" + (account?.Revision ?? 0),
            PayloadJson = payload,
            PayloadSha256 = CanonicalCatalogKeys.Hash(payload),
            CorrelationId = execution.CorrelationId,
            RetrievedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProviderRecordingAvailabilityRequest?> GetRecordingAvailabilityAsync(
        ProviderExecutionContext execution, string publicId, CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        ThrowIfUnavailable(execution, cancellationToken);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ValidateExecutionContextAsync(db, execution, cancellationToken);
        var recording = await FindRecordingByPublicIdAsync(db, execution.Actor, publicId, cancellationToken);
        if (recording == null) return null;
        var links = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
            item.CanonicalRecordingId == recording.Id && item.ResourceKind == ProviderResourceKind.Track &&
            (item.Scope == ProviderIdentityScope.Catalog || account != null && item.ProviderAccountId == account.Id))
            .ToArrayAsync(cancellationToken);
        if (await HasAvailabilityAuthorityConflictAsync(db, links, execution.Actor.EffectiveUserId, cancellationToken)) return null;
        var linkIds = links.Select(item => item.Id).ToArray();
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
            item.ProviderTrackIdentityId != null && linkIds.Contains(item.ProviderTrackIdentityId.Value) &&
            (item.ProviderAccountId == null || account != null && item.ProviderAccountId == account.Id))
            .OrderByDescending(item => item.RetrievedAt).ToArrayAsync(cancellationToken);
        var verified = links.Where(item => item.Verification == ProviderIdentityVerification.Verified &&
            CatalogIdentityPolicy.IsPlaybackEligible(item)).ToArray();
        var verifiedIds = verified.Select(item => item.Id).ToHashSet();
        var providerIds = verified.Where(item => execution.Policy.AllowsProvider(item.ProviderId) || item.Scope == ProviderIdentityScope.Catalog)
            .GroupBy(item => item.ProviderId).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().ExternalId);
        foreach (var snapshot in snapshots.Where(item => verifiedIds.Contains(item.ProviderTrackIdentityId!.Value)))
        {
            try
            {
                using var document = JsonDocument.Parse(snapshot.PayloadJson);
                var root = document.RootElement;
                JsonElement Property(string name, JsonElement element) => element.ValueKind == JsonValueKind.Object
                    ? element.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
                string? Text(string name) => Property(name, root) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
                long? Number(string name) => Property(name, root) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;
                var artists = new List<string>();
                if (Text("artist") is { Length: > 0 } artist) artists.Add(artist);
                if (Property("artists", root) is { ValueKind: JsonValueKind.Array } credits)
                    foreach (var credit in credits.EnumerateArray())
                    {
                        var name = credit.ValueKind == JsonValueKind.String ? credit.GetString() :
                            Property("name", credit) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name)) artists.Add(name);
                    }
                var title = string.IsNullOrWhiteSpace(recording.Title) ? Text("title") : recording.Title;
                if (string.IsNullOrWhiteSpace(title) || artists.Count == 0) continue;
                return new(recording.Id.ToString("D"), title, artists.Distinct().ToArray(),
                    Identifier(recording, RecordingIdentifierKinds.Isrc) ?? Text("isrc"),
                    recording.DurationMilliseconds ?? Number("durationMilliseconds") ?? Number("durationMs") ?? Number("duration") * 1000,
                    providerIds)
                {
                    ExplicitContentLyrics = recording.IsExplicit is { } isExplicit ? isExplicit ? 1 : 0 :
                        (int?)Number("explicitContentLyrics")
                };
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static async Task<bool> HasAvailabilityAuthorityConflictAsync(AllstarrDbContext db,
        IReadOnlyList<ProviderTrackIdentityRecord> links, Guid? userId, CancellationToken cancellationToken)
    {
        if (links.Any(item => item.Verification == ProviderIdentityVerification.Pinned ||
                item.VerificationMethod is ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or
                    ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod)) return true;
        var ids = links.Select(item => item.Id).ToArray();
        var hashes = links.Select(item => item.ExternalIdHash).ToArray();
        return await db.ManualTrackOverrides.AsNoTracking().AnyAsync(item => item.RevokedAt == null &&
            (item.OwnerUserId == null || item.OwnerUserId == userId) &&
            (hashes.Contains(item.SourceExternalIdHash) || db.ExternalMetadataSnapshots.Any(snapshot =>
                snapshot.Id == item.ExternalSnapshotId && snapshot.ProviderTrackIdentityId != null &&
                ids.Contains(snapshot.ProviderTrackIdentityId.Value))), cancellationToken);
    }
}
