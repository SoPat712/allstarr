using allstarr.Core.Favorites;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    public DbSet<FavoriteActionPolicyRecord> FavoriteActionPolicies => Set<FavoriteActionPolicyRecord>();
    internal static void ConfigureFavoriteActionPolicies(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FavoriteActionPolicyRecord>(entity =>
        {
            entity.ToTable("favorite_action_policies"); entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever(); entity.Property(item => item.Scope).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Protocol).HasMaxLength(32).IsRequired(); entity.Property(item => item.BackendInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300); entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.TargetCredentialReferenceId).HasDatabaseName("IX_favorite_policy_credential_reference");
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.Scope, item.Protocol, item.BackendInstanceId, item.LibraryScopeId })
                .IsUnique().HasDatabaseName("IX_favorite_policy_scope");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(item => new { item.TenantId, item.UpdatedByUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
