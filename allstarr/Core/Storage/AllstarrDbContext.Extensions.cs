using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    private static void ConfigureExtensions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExtensionRegistryRecord>(entity =>
        {
            entity.ToTable("extension_registries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Name).HasMaxLength(200).IsRequired();
            entity.Property(item => item.RegistryUrl).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.RegistryUrl).IsUnique();
        });

        modelBuilder.Entity<ExtensionPackageRecord>(entity =>
        {
            entity.ToTable("extension_packages", table =>
            {
                table.HasCheckConstraint("CK_extension_packages_sha256", "length(\"Sha256\") = 64");
                table.HasCheckConstraint("CK_extension_packages_content_hash", "length(\"ContentSha256\") = 64");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ExtensionId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Version).HasMaxLength(100).IsRequired();
            entity.Property(item => item.SdkVersion).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ContentSha256).HasMaxLength(64).IsRequired();
            entity.Property(item => item.PackagePath).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.ManifestJson).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.FailureCode).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ExtensionId, item.Version, item.Sha256 });
            entity.HasIndex(item => new { item.ExtensionId, item.State });
            entity.HasOne<ExtensionRegistryRecord>().WithMany().HasForeignKey(item => item.RegistryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ExtensionPackageRecord>().WithMany().HasForeignKey(item => item.PreviousPackageId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExtensionPermissionReviewRecord>(entity =>
        {
            entity.ToTable("extension_permission_reviews");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.PermissionKind).HasMaxLength(32).IsRequired();
            entity.Property(item => item.PermissionValue).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Decision).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ExtensionPackageId, item.PermissionKind, item.PermissionValue })
                .IsUnique().HasDatabaseName("IX_extension_permission_review_key");
            entity.HasOne<ExtensionPackageRecord>().WithMany().HasForeignKey(item => item.ExtensionPackageId)
                .HasConstraintName("FK_extension_permission_review_package").OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(item => item.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExtensionLogRecord>(entity =>
        {
            entity.ToTable("extension_logs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ExtensionId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Level).HasMaxLength(20).IsRequired();
            entity.Property(item => item.EventCode).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Message).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(100).IsRequired();
            entity.HasIndex(item => new { item.ExtensionId, item.CreatedAt });
            entity.HasOne<ExtensionPackageRecord>().WithMany().HasForeignKey(item => item.ExtensionPackageId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
