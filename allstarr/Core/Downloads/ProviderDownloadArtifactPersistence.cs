using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Downloads;

public static class ProviderDownloadArtifactModelConfiguration
{
    public static void ConfigureProviderDownloadArtifacts(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderDownloadWorkspaceEntity>(entity =>
        {
            entity.ToTable("provider_download_workspaces");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.WorkspaceId).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ProviderId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(300).IsRequired();
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.WorkspaceId).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.DurableJobId, item.ProviderId, item.ProviderAccountId, item.IdempotencyKey }).IsUnique().HasDatabaseName("IX_download_workspace_idempotency");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id }).HasConstraintName("FK_download_workspace_user").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.DurableJobId)
                .HasConstraintName("FK_download_workspace_job").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderAccountRecord>().WithMany().HasForeignKey(item => new { item.ProviderAccountId, item.ProviderId })
                .HasPrincipalKey(item => new { item.Id, item.ProviderId }).HasConstraintName("FK_download_workspace_account").OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProviderDownloadArtifactEntity>(entity =>
        {
            entity.ToTable("provider_download_artifacts", table =>
            {
                table.HasCheckConstraint("CK_download_artifact_sha", "length(\"ContentSha256\") = 64");
                table.HasCheckConstraint("CK_download_artifact_length", "\"Length\" > 0");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.WorkspaceId).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ProviderId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.ProviderArtifactId).HasMaxLength(500).IsRequired();
            entity.Property(item => item.RelativePath).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300);
            entity.Property(item => item.ContentSha256).HasMaxLength(64).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.WorkspaceRecordId, item.ProviderArtifactId }).IsUnique().HasDatabaseName("IX_download_artifact_identity");
            entity.HasIndex(item => new { item.TenantId, item.DurableJobId, item.ProviderId }).IsUnique().HasDatabaseName("IX_download_artifact_job_provider");
            entity.HasOne<ProviderDownloadWorkspaceEntity>().WithMany().HasForeignKey(item => item.WorkspaceRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ManagedFileOwnershipEntity>().WithMany().HasForeignKey(item => item.ManagedFileId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class EfProviderDownloadArtifactStore(IDbContextFactory<AllstarrDbContext> factory) : IProviderDownloadArtifactStore
{
    public async Task<ProviderDownloadWorkspaceEntity> CreateWorkspaceAsync(ProviderDownloadWorkspaceEntity workspace, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<ProviderDownloadWorkspaceEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.WorkspaceId == workspace.WorkspaceId, cancellationToken);
        if (existing is not null) return existing;
        db.Add(workspace);
        try { await db.SaveChangesAsync(cancellationToken); return workspace; }
        catch (DbUpdateException)
        {
            db.Entry(workspace).State = EntityState.Detached;
            return await db.Set<ProviderDownloadWorkspaceEntity>().AsNoTracking().SingleAsync(item => item.WorkspaceId == workspace.WorkspaceId, cancellationToken);
        }
    }

    public async Task<ProviderDownloadWorkspaceEntity?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    { await using var db = await factory.CreateDbContextAsync(cancellationToken); return await db.Set<ProviderDownloadWorkspaceEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId, cancellationToken); }

    public async Task<ProviderDownloadArtifactEntity> AddVerifiedAsync(ProviderDownloadArtifactEntity artifact, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.WorkspaceRecordId == artifact.WorkspaceRecordId && item.ProviderArtifactId == artifact.ProviderArtifactId, cancellationToken);
        if (existing is not null)
        {
            if (existing.ContentSha256 != artifact.ContentSha256 || existing.Length != artifact.Length) throw new InvalidOperationException("A provider artifact identity was reused for different content.");
            return existing;
        }
        db.Add(artifact);
        try { await db.SaveChangesAsync(cancellationToken); return artifact; }
        catch (DbUpdateException)
        {
            db.Entry(artifact).State = EntityState.Detached;
            var winner = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleAsync(item => item.WorkspaceRecordId == artifact.WorkspaceRecordId && item.ProviderArtifactId == artifact.ProviderArtifactId, cancellationToken);
            if (winner.ContentSha256 != artifact.ContentSha256 || winner.Length != artifact.Length) throw new InvalidOperationException("A provider artifact identity was reused for different content.");
            return winner;
        }
    }

    public async Task<ProviderDownloadArtifactEntity?> FindByJobAsync(Guid tenantId, Guid durableJobId, string providerId, CancellationToken cancellationToken)
    { await using var db = await factory.CreateDbContextAsync(cancellationToken); return await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == tenantId && item.DurableJobId == durableJobId && item.ProviderId == providerId, cancellationToken); }

    public async Task MarkPlacedAsync(Guid artifactId, Guid managedFileId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var item = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync(value => value.Id == artifactId, cancellationToken);
        if (item.State == ProviderDownloadArtifactState.Placed)
        { if (item.ManagedFileId != managedFileId) throw new InvalidOperationException("The artifact is already linked to another managed file."); return; }
        if (item.State != ProviderDownloadArtifactState.Verified) throw new InvalidOperationException("Only a verified download artifact can be placed.");
        var exactManagedScope = await db.Set<ManagedFileOwnershipEntity>().AsNoTracking().AnyAsync(file =>
            file.Id == managedFileId &&
            file.TenantId == item.TenantId &&
            file.OwnerUserId == item.OwnerUserId &&
            file.LibraryScopeId == item.LibraryScopeId &&
            file.RemovedAt == null,
            cancellationToken);
        if (!exactManagedScope)
            throw new UnauthorizedAccessException("The managed file is outside the verified artifact ownership or library scope.");
        item.State = ProviderDownloadArtifactState.Placed; item.ManagedFileId = managedFileId; item.PlacedAt = DateTimeOffset.UtcNow; item.Revision++;
        await db.SaveChangesAsync(cancellationToken);
    }
}
