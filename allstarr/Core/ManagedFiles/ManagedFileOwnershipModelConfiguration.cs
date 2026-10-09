using Microsoft.EntityFrameworkCore;
using allstarr.Core.Storage;

namespace allstarr.Core.ManagedFiles;

public static class ManagedFileOwnershipModelConfiguration
{
    // Called once from AllstarrDbContext.OnModelCreating.
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
            entity.Property(item => item.ScopeKey).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.Id, item.OwnerUserId }).IsUnique()
                .HasDatabaseName("IX_managed_file_owner_lineage");
            entity.HasIndex(item => item.CanonicalPath).IsUnique().HasDatabaseName("IX_managed_file_path");
            entity.HasIndex(item => new { item.RootId, item.ContentSha256, item.ScopeKey })
                .HasDatabaseName("IX_managed_file_fingerprint");
            entity.HasIndex(item => item.OwnerUserId).HasDatabaseName("IX_managed_file_user");
            entity.HasIndex(item => item.SourceJobId).HasDatabaseName("IX_managed_file_job");
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId)
                .HasConstraintName("FK_managed_file_user").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany()
                .HasForeignKey(item => item.SourceJobId)
                .HasConstraintName("FK_managed_file_job").OnDelete(DeleteBehavior.Restrict);
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
            entity.HasIndex(item => new { item.OwnerUserId, item.ReleasedAt })
                .HasDatabaseName("IX_managed_file_reference_owner");
            entity.HasOne<ManagedFileOwnershipEntity>().WithMany()
                .HasForeignKey(item => item.ManagedFileId)
                .HasConstraintName("FK_managed_file_reference_file").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId)
                .HasConstraintName("FK_managed_file_reference_user").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
