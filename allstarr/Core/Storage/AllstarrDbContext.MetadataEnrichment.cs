using Microsoft.EntityFrameworkCore;
using allstarr.Core.ManagedFiles;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    public DbSet<MetadataEnrichmentPlanRecord> MetadataEnrichmentPlans => Set<MetadataEnrichmentPlanRecord>();
    public DbSet<MetadataEnrichmentApplicationRecord> MetadataEnrichmentApplications => Set<MetadataEnrichmentApplicationRecord>();

    internal static void ConfigureMetadataEnrichment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetadataEnrichmentPlanRecord>(entity =>
        {
            entity.ToTable("metadata_enrichment_plans", table => table.HasCheckConstraint(
                "CK_enrichment_plans_fingerprint", "length(\"Fingerprint\") = 64"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(item => item.SourceRevisionsJson).IsRequired();
            entity.Property(item => item.DecisionsJson).IsRequired();
            entity.Property(item => item.TagsJson).IsRequired();
            entity.Property(item => item.PathValuesJson).IsRequired();
            entity.HasAlternateKey(item => new
            {
                item.Id,
                item.TenantId,
                item.OwnerUserId,
                item.ManagedArtifactId,
                item.LineageJobId
            }).HasName("AK_enrichment_plan_scope");
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.ManagedArtifactId, item.Fingerprint })
                .IsUnique().HasDatabaseName("IX_enrichment_plan_fingerprint");
            entity.HasIndex(item => item.LineageJobId).HasDatabaseName("IX_enrichment_plan_job");
            entity.HasIndex(item => item.ManagedArtifactId).HasDatabaseName("IX_enrichment_plan_file");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.LineageJobId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ManagedFileOwnershipEntity>().WithMany().HasForeignKey(item => item.ManagedArtifactId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MetadataEnrichmentApplicationRecord>(entity =>
        {
            entity.ToTable("metadata_enrichment_applications", table => table.HasCheckConstraint(
                "CK_enrichment_applications_sha256", "length(\"ArtifactContentSha256\") = 64"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ArtifactContentSha256).HasMaxLength(64).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ErrorCode).HasMaxLength(100);
            entity.Property(item => item.SafeErrorMessage).HasMaxLength(1000);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.PlanId, item.ManagedArtifactId, item.ArtifactContentSha256 })
                .IsUnique().HasDatabaseName("IX_enrichment_application_hash");
            entity.HasIndex(item => new { item.PlanId, item.TenantId, item.OwnerUserId, item.ManagedArtifactId, item.LineageJobId })
                .HasDatabaseName("IX_enrichment_application_plan");
            entity.HasIndex(item => item.LineageJobId).HasDatabaseName("IX_enrichment_application_job");
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MetadataEnrichmentPlanRecord>().WithMany()
                .HasForeignKey(item => new
                {
                    item.PlanId,
                    item.TenantId,
                    item.OwnerUserId,
                    item.ManagedArtifactId,
                    item.LineageJobId
                })
                .HasPrincipalKey(item => new
                {
                    item.Id,
                    item.TenantId,
                    item.OwnerUserId,
                    item.ManagedArtifactId,
                    item.LineageJobId
                }).HasConstraintName("FK_enrichment_application_plan").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.LineageJobId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
