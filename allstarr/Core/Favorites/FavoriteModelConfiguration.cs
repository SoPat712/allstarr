using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

/// <summary>Phase 6 model slice. The consolidated Phase 6 migration wires this once all lanes land.</summary>
public static class FavoriteModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FavoriteEventRecord>(entity =>
        {
            entity.ToTable("favorite_events");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Protocol).HasMaxLength(32).IsRequired();
            entity.Property(item => item.BackendInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.BackendPrincipalId).HasMaxLength(300).IsRequired();
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300);
            entity.Property(item => item.ItemId).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Operation).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.SourceRevision).HasMaxLength(300).IsRequired();
            entity.Property(item => item.EventKey).HasMaxLength(64).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.PolicySnapshotJson).IsRequired();
            entity.HasIndex(item => item.TargetCredentialReferenceId).HasDatabaseName("IX_favorite_event_credential_reference");
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LastErrorCode).HasMaxLength(100);
            entity.Property(item => item.LastErrorMessage).HasMaxLength(1000);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasAlternateKey(item => new { item.Id, item.TenantId, item.OwnerUserId });
            entity.HasIndex(item => item.EventKey).IsUnique().HasDatabaseName("IX_favorite_event_key");
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.CreatedAt })
                .HasDatabaseName("IX_favorite_event_owner_created");
            entity.HasIndex(item => item.JobId).HasDatabaseName("IX_favorite_event_job");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.JobId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FavoriteActionRecord>(entity =>
        {
            entity.ToTable("favorite_actions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ActionType).HasMaxLength(100).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(300).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LastErrorCode).HasMaxLength(100);
            entity.Property(item => item.LastErrorMessage).HasMaxLength(1000);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.EventId, item.ActionType }).IsUnique()
                .HasDatabaseName("IX_favorite_action_type");
            entity.HasIndex(item => new { item.EventId, item.TenantId, item.OwnerUserId })
                .HasDatabaseName("IX_favorite_action_event");
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.State })
                .HasDatabaseName("IX_favorite_action_owner_state");
            entity.HasOne<FavoriteEventRecord>().WithMany()
                .HasForeignKey(item => new { item.EventId, item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.Id, item.TenantId, item.OwnerUserId })
                .HasConstraintName("FK_favorite_action_event")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FavoriteStateRecord>(entity =>
        {
            entity.ToTable("favorite_states");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Protocol).HasMaxLength(32).IsRequired();
            entity.Property(item => item.BackendInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ItemId).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new
            {
                item.TenantId,
                item.OwnerUserId,
                item.Protocol,
                item.BackendInstanceId,
                item.ItemId
            }).IsUnique().HasDatabaseName("IX_favorite_state_owner_target");
            entity.HasIndex(item => new { item.LastEventId, item.TenantId, item.OwnerUserId })
                .HasDatabaseName("IX_favorite_state_event");
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FavoriteEventRecord>().WithMany()
                .HasForeignKey(item => new { item.LastEventId, item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.Id, item.TenantId, item.OwnerUserId })
                .HasConstraintName("FK_favorite_state_event")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
