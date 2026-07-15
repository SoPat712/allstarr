using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record DurableEnrichmentPlanRequest(Guid TenantId, Guid OwnerUserId, Guid LineageJobId,
    Guid ManagedArtifactId, MetadataEnrichmentPlan Plan);
public sealed record DurableEnrichmentApplicationRequest(Guid TenantId, Guid OwnerUserId, Guid LineageJobId,
    Guid ManagedArtifactId, Guid PlanId, string ArtifactContentSha256);

/// <summary>Persists explainable plans and idempotent applications without storing media bytes.</summary>
public sealed class DurableMetadataEnrichmentService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock)
{
    public async Task<MetadataEnrichmentPlanRecord> SavePlanAsync(DurableEnrichmentPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.Plan);
        if (request.TenantId == Guid.Empty || request.OwnerUserId == Guid.Empty || request.LineageJobId == Guid.Empty ||
            request.ManagedArtifactId == Guid.Empty || request.Plan.Fingerprint.Length != 64)
            throw new ArgumentException("The durable enrichment plan request is invalid.", nameof(request));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Jobs.AsNoTracking().AnyAsync(item => item.Id == request.LineageJobId &&
            item.TenantId == request.TenantId && item.OwnerUserId == request.OwnerUserId, cancellationToken))
            throw new InvalidOperationException("The enrichment plan lineage job is outside the requested tenant and user scope.");
        var existing = await db.MetadataEnrichmentPlans.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == request.TenantId && item.OwnerUserId == request.OwnerUserId &&
            item.ManagedArtifactId == request.ManagedArtifactId && item.Fingerprint == request.Plan.Fingerprint, cancellationToken);
        if (existing != null) return existing;
        var record = new MetadataEnrichmentPlanRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId,
            OwnerUserId = request.OwnerUserId,
            LineageJobId = request.LineageJobId,
            ManagedArtifactId = request.ManagedArtifactId,
            Fingerprint = request.Plan.Fingerprint,
            PlanVersion = request.Plan.Version,
            SourceRevisionsJson = JsonSerializer.Serialize(request.Plan.SourceRevisions),
            DecisionsJson = JsonSerializer.Serialize(request.Plan.Decisions),
            TagsJson = JsonSerializer.Serialize(request.Plan.Tags),
            PathValuesJson = JsonSerializer.Serialize(request.Plan.PathValues),
            CreatedAt = clock.UtcNow
        };
        db.MetadataEnrichmentPlans.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task<MetadataEnrichmentApplicationRecord> BeginApplicationAsync(DurableEnrichmentApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ArtifactContentSha256.Length != 64 || !request.ArtifactContentSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The artifact checksum is invalid.", nameof(request));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var plan = await db.MetadataEnrichmentPlans.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.PlanId &&
            item.TenantId == request.TenantId && item.OwnerUserId == request.OwnerUserId &&
            item.ManagedArtifactId == request.ManagedArtifactId && item.LineageJobId == request.LineageJobId, cancellationToken);
        if (plan == null) throw new InvalidOperationException("The enrichment plan is outside the managed artifact or job scope.");
        var checksum = request.ArtifactContentSha256.ToLowerInvariant();
        var existing = await db.MetadataEnrichmentApplications.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == request.TenantId && item.OwnerUserId == request.OwnerUserId && item.PlanId == request.PlanId &&
            item.ManagedArtifactId == request.ManagedArtifactId && item.LineageJobId == request.LineageJobId &&
            item.ArtifactContentSha256 == checksum, cancellationToken);
        if (existing != null) return existing;
        // A crash can leave this application pending after the atomic file swap,
        // including after managed ownership has advanced to the output checksum.
        // Reuse only the pending application in the exact plan/job scope. Its old
        // input checksum is intentional: the writer must prove the matching
        // input/output/operation journal before accepting recovery.
        var recoverable = await db.MetadataEnrichmentApplications.AsNoTracking()
            .Where(item => item.TenantId == request.TenantId && item.OwnerUserId == request.OwnerUserId &&
                           item.PlanId == request.PlanId && item.ManagedArtifactId == request.ManagedArtifactId &&
                           item.LineageJobId == request.LineageJobId &&
                           item.State == MetadataEnrichmentApplicationState.Pending)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (recoverable != null) return recoverable;
        var record = new MetadataEnrichmentApplicationRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId,
            OwnerUserId = request.OwnerUserId,
            PlanId = request.PlanId,
            ManagedArtifactId = request.ManagedArtifactId,
            LineageJobId = request.LineageJobId,
            ArtifactContentSha256 = checksum,
            State = MetadataEnrichmentApplicationState.Pending,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
        db.MetadataEnrichmentApplications.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public Task MarkAppliedAsync(Guid tenantId, Guid ownerUserId, Guid applicationId, CancellationToken cancellationToken = default) =>
        UpdateAsync(tenantId, ownerUserId, applicationId, MetadataEnrichmentApplicationState.Applied, null, null, cancellationToken);

    public Task MarkFailedAsync(Guid tenantId, Guid ownerUserId, Guid applicationId, string errorCode, string safeMessage,
        CancellationToken cancellationToken = default) => UpdateAsync(tenantId, ownerUserId, applicationId,
            MetadataEnrichmentApplicationState.Failed, Required(errorCode, 100), Required(safeMessage, 1000), cancellationToken);

    private async Task UpdateAsync(Guid tenantId, Guid ownerUserId, Guid id, MetadataEnrichmentApplicationState state,
        string? errorCode, string? safeMessage, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.MetadataEnrichmentApplications.SingleOrDefaultAsync(item => item.Id == id &&
            item.TenantId == tenantId && item.OwnerUserId == ownerUserId, cancellationToken)
            ?? throw new KeyNotFoundException("The enrichment application was not found in this scope.");
        if (record.State == MetadataEnrichmentApplicationState.Applied && state != MetadataEnrichmentApplicationState.Applied)
            throw new InvalidOperationException("An applied enrichment record cannot be changed to a failed state.");
        record.State = state; record.ErrorCode = errorCode; record.SafeErrorMessage = safeMessage;
        record.UpdatedAt = clock.UtcNow; record.Revision++;
        await db.SaveChangesAsync(cancellationToken);
    }
    private static string Required(string value, int max) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > max
        ? throw new ArgumentException("A bounded safe error value is required.") : value.Trim();
}
