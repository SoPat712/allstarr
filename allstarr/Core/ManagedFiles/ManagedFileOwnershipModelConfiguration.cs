using Microsoft.EntityFrameworkCore;
using allstarr.Core.Storage;

namespace allstarr.Core.ManagedFiles;

public static class ManagedFileOwnershipModelConfiguration
{
    // Call from AllstarrDbContext.OnModelCreating during the consolidated Phase 6 migration.
    public static void ConfigureManagedFileOwnership(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagedFileOwnershipEntity>(entity =>
        {
            entity.ToTable("managed_files", table =>
            {
                table.HasCheckConstraint("CK_managed_files_sha256", "length(\"ContentSha256\") = 64");
                table.HasCheckConstraint("CK_managed_files_references", "\"ReferenceCount\" >= 0");
                table.HasCheckConstraint("CK_managed_files_owned", "\"IsManaged\" = TRUE");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.CanonicalPath).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.TargetRootPath).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.ContentSha256).HasMaxLength(64).IsRequired();
            entity.Property(item => item.FileSystemDeviceId).HasMaxLength(64);
            entity.Property(item => item.FileSystemFileId).HasMaxLength(64);
            entity.Property(item => item.PlacementMethod).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300);
            entity.Property(item => item.ScopeKey).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.CanonicalPath).IsUnique().HasDatabaseName("IX_managed_file_path");
            entity.HasIndex(item => new { item.RootId, item.ContentSha256, item.ScopeKey })
                .HasDatabaseName("IX_managed_file_fingerprint");
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId }).HasDatabaseName("IX_managed_file_user");
            entity.HasIndex(item => item.SourceJobId).HasDatabaseName("IX_managed_file_job");
            entity.HasIndex(item => new { item.Id, item.TenantId, item.OwnerUserId }).IsUnique()
                .HasDatabaseName("UX_managed_file_owner_lineage");
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .HasConstraintName("FK_managed_file_tenant_user").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany()
                .HasForeignKey(item => item.SourceJobId)
                .HasConstraintName("FK_managed_file_tenant_job").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ManagedFileReferenceEntity>(entity =>
        {
            entity.ToTable("managed_file_references");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ScopeKey).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.ReferenceKey).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ManagedFileId, item.ReferenceKey }).IsUnique()
                .HasDatabaseName("IX_managed_file_reference_key");
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.ReleasedAt })
                .HasDatabaseName("IX_managed_file_reference_owner");
            entity.HasOne<ManagedFileOwnershipEntity>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.ManagedFileId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .HasConstraintName("FK_managed_file_reference_tenant_file").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .HasConstraintName("FK_managed_file_reference_tenant_user").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
