using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record CanonicalCatalogEntityReference(
    CanonicalCatalogEntityKind Kind,
    Guid Id);

public sealed record CanonicalCatalogAliasInput(
    string Namespace,
    string ExternalId);

public sealed record CanonicalCatalogFactInput(
    string FieldName,
    string ValueJson);

public sealed record CanonicalCatalogSourceStamp(
    string SourceId,
    string? SourceRevision,
    string PayloadSha256,
    double Confidence,
    DateTimeOffset ObservedAt,
    DateTimeOffset? RefreshAfter);

public sealed record CanonicalCatalogEvidenceResult(
    int AliasesCreated,
    int AliasesSeen,
    int FactsCreated,
    int FactsSuperseded);

public static class CanonicalCatalogKeys
{
    private const string DefaultCatalog = "default";

    public static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string ProviderTrackNamespace(
        string providerId,
        ProviderResourceKind resourceKind,
        string? catalog,
        ProviderIdentityScope scope,
        Guid? providerAccountId)
    {
        providerId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        if (resourceKind != ProviderResourceKind.Track)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resourceKind),
                "Canonical recording aliases require a track resource.");
        }

        catalog = catalog == null
            ? DefaultCatalog
            : ProviderContractValidation.Catalog(catalog, nameof(catalog));
        if (scope is not ProviderIdentityScope.Catalog and not ProviderIdentityScope.Account ||
            scope == ProviderIdentityScope.Catalog && providerAccountId.HasValue ||
            scope == ProviderIdentityScope.Account && !providerAccountId.HasValue)
        {
            throw new ArgumentException(
                "Provider alias scope and account must describe one exact identity boundary.",
                nameof(scope));
        }

        var descriptor = string.Join(
            '\n',
            providerId,
            resourceKind.ToString(),
            catalog,
            scope.ToString(),
            providerAccountId?.ToString("D") ?? string.Empty);
        return $"provider:{Hash(descriptor)}";
    }
}

internal static class CanonicalCatalogIdentityProjection
{
    public static async Task ProjectRecordingSignalsAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
        CanonicalRecordingRecord recording,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        CanonicalCatalogAliasInput[] aliases =
        [
            .. recording.Isrc == null
                ? []
                : new[] { new CanonicalCatalogAliasInput("isrc", recording.Isrc) },
            .. recording.MusicBrainzRecordingId == null
                ? []
                : new[] { new CanonicalCatalogAliasInput("musicbrainz", recording.MusicBrainzRecordingId) }
        ];
        if (aliases.Length == 0)
        {
            return;
        }

        await CanonicalCatalogEvidenceStore.RecordInContextAsync(
            db,
            actor,
            new CanonicalCatalogEntityReference(CanonicalCatalogEntityKind.Recording, recording.Id),
            new CanonicalCatalogSourceStamp(
                "canonical-signal",
                null,
                CanonicalCatalogKeys.Hash(string.Join(
                    '\n', aliases.Select(alias => $"{alias.Namespace}:{alias.ExternalId}"))),
                1,
                observedAt,
                null),
            aliases,
            [],
            cancellationToken);
    }

    public static Task ProjectProviderIdentityAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
        ProviderTrackIdentityRecord identity,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var aliasNamespace = CanonicalCatalogKeys.ProviderTrackNamespace(
            identity.ProviderId,
            identity.ResourceKind,
            identity.CatalogNamespace,
            identity.Scope,
            identity.ProviderAccountId);
        return CanonicalCatalogEvidenceStore.RecordInContextAsync(
            db,
            actor,
            new CanonicalCatalogEntityReference(
                CanonicalCatalogEntityKind.Recording,
                identity.CanonicalRecordingId),
            new CanonicalCatalogSourceStamp(
                "provider-identity",
                $"{identity.Verification}:{identity.DecisionVersion}",
                CanonicalCatalogKeys.Hash($"{aliasNamespace}\n{identity.ExternalId}"),
                1,
                observedAt,
                null),
            [new CanonicalCatalogAliasInput(aliasNamespace, identity.ExternalId)],
            [],
            cancellationToken);
    }
}

public interface ICanonicalCatalogEvidenceStore
{
    Task<CanonicalCatalogEvidenceResult> RecordAsync(
        ProviderActorContext actor,
        CanonicalCatalogEntityReference target,
        CanonicalCatalogSourceStamp source,
        IReadOnlyCollection<CanonicalCatalogAliasInput> aliases,
        IReadOnlyCollection<CanonicalCatalogFactInput> facts,
        CancellationToken cancellationToken = default);
}

public sealed class CanonicalCatalogEvidenceStore(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableStorageState storageState) : ICanonicalCatalogEvidenceStore
{
    public async Task<CanonicalCatalogEvidenceResult> RecordAsync(
        ProviderActorContext actor,
        CanonicalCatalogEntityReference target,
        CanonicalCatalogSourceStamp source,
        IReadOnlyCollection<CanonicalCatalogAliasInput> aliases,
        IReadOnlyCollection<CanonicalCatalogFactInput> facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(facts);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStorageReady();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var result = await RecordInContextAsync(
            db, actor, target, source, aliases, facts, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    internal static async Task<CanonicalCatalogEvidenceResult> RecordInContextAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
        CanonicalCatalogEntityReference target,
        CanonicalCatalogSourceStamp source,
        IReadOnlyCollection<CanonicalCatalogAliasInput> aliases,
        IReadOnlyCollection<CanonicalCatalogFactInput> facts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(facts);
        cancellationToken.ThrowIfCancellationRequested();
        source = NormalizeSource(source);
        await ValidateActorAndTargetAsync(db, actor, target, cancellationToken);
        var createdAliases = 0;
        var seenAliases = 0;
        foreach (var alias in NormalizeAliases(aliases))
        {
            var hash = CanonicalCatalogKeys.Hash(alias.ExternalId);
            var existing = db.CanonicalCatalogAliases.Local.SingleOrDefault(item =>
                    item.TenantId == actor.TenantId &&
                    item.Namespace == alias.Namespace &&
                    item.EntityKind == target.Kind &&
                    item.ExternalIdHash == hash) ??
                await db.CanonicalCatalogAliases.SingleOrDefaultAsync(item =>
                    item.TenantId == actor.TenantId &&
                    item.Namespace == alias.Namespace &&
                    item.EntityKind == target.Kind &&
                    item.ExternalIdHash == hash,
                    cancellationToken);
            if (existing != null)
            {
                if (!existing.ExternalId.Equals(alias.ExternalId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A catalog alias hash collision was detected.");
                }

                if (existing.CanonicalEntityId != target.Id)
                {
                    throw new InvalidOperationException("The catalog alias already identifies another canonical entity.");
                }

                existing.LastSeenAt = source.ObservedAt;
                seenAliases++;
                continue;
            }

            db.CanonicalCatalogAliases.Add(new CanonicalCatalogAliasRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                EntityKind = target.Kind,
                CanonicalEntityId = target.Id,
                Namespace = alias.Namespace,
                ExternalId = alias.ExternalId,
                ExternalIdHash = hash,
                CreatedAt = source.ObservedAt,
                LastSeenAt = source.ObservedAt
            });
            createdAliases++;
        }

        var createdFacts = 0;
        var supersededFacts = 0;
        foreach (var fact in NormalizeFacts(facts))
        {
            var persisted = await db.CatalogFacts.Where(item =>
                    item.TenantId == actor.TenantId &&
                    item.EntityKind == target.Kind &&
                    item.CanonicalEntityId == target.Id &&
                    item.FieldName == fact.FieldName &&
                    item.SourceId == source.SourceId &&
                    item.SupersededAt == null)
                .ToListAsync(cancellationToken);
            var active = db.CatalogFacts.Local.Where(item =>
                    item.TenantId == actor.TenantId &&
                    item.EntityKind == target.Kind &&
                    item.CanonicalEntityId == target.Id &&
                    item.FieldName == fact.FieldName &&
                    item.SourceId == source.SourceId &&
                    item.SupersededAt == null)
                .Concat(persisted)
                .DistinctBy(item => item.Id)
                .ToList();
            if (active.Any(item =>
                    item.PayloadSha256.Equals(source.PayloadSha256, StringComparison.Ordinal) &&
                    JsonEquivalent(item.ValueJson, fact.ValueJson)))
            {
                continue;
            }

            foreach (var previous in active)
            {
                previous.SupersededAt = source.ObservedAt;
                supersededFacts++;
            }

            db.CatalogFacts.Add(new CatalogFactRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                EntityKind = target.Kind,
                CanonicalEntityId = target.Id,
                FieldName = fact.FieldName,
                ValueJson = fact.ValueJson,
                SourceId = source.SourceId,
                SourceRevision = source.SourceRevision,
                PayloadSha256 = source.PayloadSha256,
                Confidence = source.Confidence,
                ObservedAt = source.ObservedAt,
                RefreshAfter = source.RefreshAfter
            });
            createdFacts++;
        }

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            ActorUserId = actor.UserId,
            Category = "canonical-catalog",
            Action = "evidence.record",
            Outcome = createdAliases + createdFacts + supersededFacts == 0 ? "unchanged" : "updated",
            CorrelationId = $"catalog:{source.SourceId}:{source.PayloadSha256[..12]}",
            DetailsJson = JsonSerializer.Serialize(new
            {
                entityKind = target.Kind.ToString(),
                canonicalEntityId = target.Id,
                source.SourceId,
                aliasesCreated = createdAliases,
                aliasesSeen = seenAliases,
                factsCreated = createdFacts,
                factsSuperseded = supersededFacts
            }),
            CreatedAt = source.ObservedAt
        });
        return new CanonicalCatalogEvidenceResult(
            createdAliases,
            seenAliases,
            createdFacts,
            supersededFacts);
    }

    private static IEnumerable<CanonicalCatalogAliasInput> NormalizeAliases(
        IEnumerable<CanonicalCatalogAliasInput> aliases) => aliases
        .Select(alias => new CanonicalCatalogAliasInput(
            Required(alias.Namespace, nameof(alias.Namespace), 100).ToLowerInvariant(),
            Required(alias.ExternalId, nameof(alias.ExternalId), 500)))
        .Distinct();

    private static IEnumerable<CanonicalCatalogFactInput> NormalizeFacts(
        IEnumerable<CanonicalCatalogFactInput> facts)
    {
        var normalized = facts.Select(fact => new CanonicalCatalogFactInput(
                Required(fact.FieldName, nameof(fact.FieldName), 100),
                NormalizeJson(fact.ValueJson)))
            .Distinct()
            .ToArray();
        if (normalized.GroupBy(fact => fact.FieldName, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException("One source payload cannot assert conflicting values for the same field.", nameof(facts));
        }

        return normalized;
    }

    private static async Task ValidateActorAndTargetAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
        CanonicalCatalogEntityReference target,
        CancellationToken cancellationToken)
    {
        if (target.Id == Guid.Empty || !Enum.IsDefined(target.Kind))
        {
            throw new ArgumentException("A valid canonical catalog target is required.", nameof(target));
        }

        var actorExists = actor.UserId == null
            ? db.Tenants.Local.Any(item => item.Id == actor.TenantId) ||
              await db.Tenants.AnyAsync(item => item.Id == actor.TenantId, cancellationToken)
            : db.Users.Local.Any(item =>
                  item.TenantId == actor.TenantId &&
                  item.Id == actor.UserId &&
                  item.Status == PlatformUserStatus.Active) ||
              await db.Users.AnyAsync(item =>
                item.TenantId == actor.TenantId &&
                item.Id == actor.UserId &&
                item.Status == PlatformUserStatus.Active,
                cancellationToken);
        if (!actorExists)
        {
            throw new UnauthorizedAccessException("The catalog actor is not active in the requested tenant.");
        }

        var trackedTargetExists = target.Kind switch
        {
            CanonicalCatalogEntityKind.Artist => db.CanonicalArtists.Local.Any(
                item => item.TenantId == actor.TenantId && item.Id == target.Id),
            CanonicalCatalogEntityKind.ReleaseGroup => db.CanonicalReleaseGroups.Local.Any(
                item => item.TenantId == actor.TenantId && item.Id == target.Id),
            CanonicalCatalogEntityKind.Release => db.CanonicalReleases.Local.Any(
                item => item.TenantId == actor.TenantId && item.Id == target.Id),
            CanonicalCatalogEntityKind.ReleaseTrack => db.CanonicalReleaseTracks.Local.Any(
                item => item.TenantId == actor.TenantId && item.Id == target.Id),
            CanonicalCatalogEntityKind.Recording => db.CanonicalRecordings.Local.Any(
                item => item.TenantId == actor.TenantId && item.Id == target.Id),
            _ => false
        };
        var targetExists = trackedTargetExists || (target.Kind switch
        {
            CanonicalCatalogEntityKind.Artist => await db.CanonicalArtists.AnyAsync(
                item => item.TenantId == actor.TenantId && item.Id == target.Id,
                cancellationToken),
            CanonicalCatalogEntityKind.ReleaseGroup => await db.CanonicalReleaseGroups.AnyAsync(
                item => item.TenantId == actor.TenantId && item.Id == target.Id,
                cancellationToken),
            CanonicalCatalogEntityKind.Release => await db.CanonicalReleases.AnyAsync(
                item => item.TenantId == actor.TenantId && item.Id == target.Id,
                cancellationToken),
            CanonicalCatalogEntityKind.ReleaseTrack => await db.CanonicalReleaseTracks.AnyAsync(
                item => item.TenantId == actor.TenantId && item.Id == target.Id,
                cancellationToken),
            CanonicalCatalogEntityKind.Recording => await db.CanonicalRecordings.AnyAsync(
                item => item.TenantId == actor.TenantId && item.Id == target.Id,
                cancellationToken),
            _ => false
        });
        if (!targetExists)
        {
            throw new KeyNotFoundException("The canonical catalog target does not exist in the actor tenant.");
        }
    }

    private static CanonicalCatalogSourceStamp NormalizeSource(CanonicalCatalogSourceStamp source)
    {
        var sourceId = Required(source.SourceId, nameof(source.SourceId), 100).ToLowerInvariant();
        if (source.SourceRevision is { Length: > 500 })
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Source revisions are limited to 500 characters.");
        }

        if (source.PayloadSha256.Length != 64 || source.PayloadSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The source payload requires a SHA-256 hash.", nameof(source));
        }

        if (source.Confidence is < 0 or > 1 || double.IsNaN(source.Confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Catalog confidence must be between zero and one.");
        }

        if (source.RefreshAfter < source.ObservedAt)
        {
            throw new ArgumentException("Catalog refresh cannot precede observation.", nameof(source));
        }

        return source with
        {
            SourceId = sourceId,
            SourceRevision = source.SourceRevision?.Trim(),
            PayloadSha256 = source.PayloadSha256.ToLowerInvariant()
        };
    }

    private static string NormalizeJson(string value)
    {
        using var document = JsonDocument.Parse(Required(value, nameof(value), 1_048_576));
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static bool JsonEquivalent(string first, string second)
    {
        using var firstDocument = JsonDocument.Parse(first);
        using var secondDocument = JsonDocument.Parse(second);
        return JsonElement.DeepEquals(firstDocument.RootElement, secondDocument.RootElement);
    }

    private static string Required(string value, string name, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", name)
            : value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : throw new ArgumentOutOfRangeException(name, $"Values are limited to {maxLength} characters.");
    }

    private void EnsureStorageReady()
    {
        if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
        {
            throw new InvalidOperationException("Durable storage is not ready.");
        }
    }
}
