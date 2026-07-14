using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public static class IntelligenceModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureOwned<IntelligencePolicyRecord>(modelBuilder, "intelligence_policies", e => e.Id,
            (entity) => { entity.Property(x => x.AllowedSignalTypesJson).IsRequired(); entity.Property(x => x.EnabledProvidersJson).IsRequired(); entity.Property(x => x.Revision).IsConcurrencyToken();
                entity.HasIndex(x => x.TargetCredentialReferenceId).HasDatabaseName("IX_intelligence_policy_credential_reference");
                entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.Protocol, x.BackendInstanceId, x.LibraryScopeId }).IsUnique().HasDatabaseName("IX_intelligence_policy_scope"); });
        ConfigureOwned<ListeningSignalRecord>(modelBuilder, "listening_signals", e => e.Id,
            entity => { entity.Property(x => x.SignalType).HasMaxLength(32).IsRequired(); entity.Property(x => x.TrackKeyHash).HasMaxLength(64).IsRequired(); entity.Property(x => x.TrackReference).HasMaxLength(100).IsRequired(); entity.Property(x => x.SignalKey).HasMaxLength(64); entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.ExpiresAt }); entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.SignalKey }).IsUnique().HasFilter("\"SignalKey\" IS NOT NULL").HasDatabaseName("IX_listening_signal_idempotency"); entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(x => x.SourceJobId).HasConstraintName("FK_listening_signal_job").OnDelete(DeleteBehavior.Restrict); });
        ConfigureOwned<ListeningProfileRecord>(modelBuilder, "listening_profiles", e => e.Id,
            entity => { entity.Property(x => x.ProfileJson).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.CreatedAt }); });
        ConfigureOwned<RecommendationRunRecord>(modelBuilder, "recommendation_runs", e => e.Id,
            entity => { entity.Property(x => x.IdempotencyKey).HasMaxLength(300).IsRequired(); entity.Property(x => x.PolicySnapshotJson).IsRequired(); entity.Property(x => x.SeedTrackKeysJson).IsRequired(); entity.Property(x => x.State).HasConversion<string>().HasMaxLength(32); entity.Property(x => x.ErrorCode).HasMaxLength(100); entity.Property(x => x.Revision).IsConcurrencyToken(); entity.HasAlternateKey(x => new { x.Id, x.TenantId, x.OwnerUserId }); entity.HasIndex(x => x.TargetCredentialReferenceId).HasDatabaseName("IX_recommendation_run_credential_reference"); entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.IdempotencyKey }).IsUnique().HasDatabaseName("IX_recommendation_run_idempotency"); entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict); });
        modelBuilder.Entity<RecommendationCandidateRecord>(entity => { entity.ToTable("recommendation_candidates"); entity.HasKey(x => x.Id); entity.Property(x => x.Id).ValueGeneratedNever(); entity.Property(x => x.TrackKey).HasMaxLength(500).IsRequired(); entity.Property(x => x.Source).HasMaxLength(100).IsRequired(); entity.Property(x => x.SignalsJson).IsRequired(); entity.Property(x => x.IdentityJson).IsRequired(); entity.HasIndex(x => new { x.RunId, x.Position }).IsUnique(); entity.HasOne<RecommendationRunRecord>().WithMany().HasForeignKey(x => new { x.RunId, x.TenantId, x.OwnerUserId }).HasPrincipalKey(x => new { x.Id, x.TenantId, x.OwnerUserId }).OnDelete(DeleteBehavior.Cascade); });
        ConfigureOwned<GeneratedSetRecord>(modelBuilder, "generated_sets", e => e.Id,
            entity => { entity.Property(x => x.Name).HasMaxLength(200).IsRequired(); entity.Property(x => x.MaterializationState).HasConversion<string>().HasMaxLength(32); entity.Property(x => x.BackendPlaylistId).HasMaxLength(500); entity.Property(x => x.TargetRevision).HasMaxLength(300); entity.Property(x => x.LastErrorCode).HasMaxLength(100); entity.Property(x => x.Revision).IsConcurrencyToken(); entity.HasAlternateKey(x => new { x.Id, x.TenantId, x.OwnerUserId }); entity.HasIndex(x => x.TargetCredentialReferenceId).HasDatabaseName("IX_generated_set_credential_reference"); entity.HasIndex(x => x.RunId).IsUnique(); entity.HasOne<RecommendationRunRecord>().WithMany().HasForeignKey(x => new { x.RunId, x.TenantId, x.OwnerUserId }).HasPrincipalKey(x => new { x.Id, x.TenantId, x.OwnerUserId }).OnDelete(DeleteBehavior.Cascade); });
        modelBuilder.Entity<GeneratedSetEntryRecord>(entity => { entity.ToTable("generated_set_entries"); entity.HasKey(x => x.Id); entity.Property(x => x.Id).ValueGeneratedNever(); entity.Property(x => x.TrackKey).HasMaxLength(500).IsRequired(); entity.Property(x => x.Source).HasMaxLength(100).IsRequired(); entity.Property(x => x.ExplanationJson).IsRequired(); entity.Property(x => x.IdentityJson).IsRequired(); entity.HasIndex(x => new { x.GeneratedSetId, x.Position }).IsUnique(); entity.HasOne<GeneratedSetRecord>().WithMany().HasForeignKey(x => new { x.GeneratedSetId, x.TenantId, x.OwnerUserId }).HasPrincipalKey(x => new { x.Id, x.TenantId, x.OwnerUserId }).OnDelete(DeleteBehavior.Cascade); });
    }

    private static void ConfigureOwned<T>(ModelBuilder modelBuilder, string table,
        System.Linq.Expressions.Expression<Func<T, Guid>> key, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T>> extra) where T : class
    {
        var entity = modelBuilder.Entity<T>(); entity.ToTable(table); entity.HasKey("Id"); entity.Property<Guid>("Id").ValueGeneratedNever();
        entity.Property("Protocol").HasMaxLength(32).IsRequired(); entity.Property("BackendInstanceId").HasMaxLength(200).IsRequired(); entity.Property("LibraryScopeId").HasMaxLength(300).IsRequired();
        entity.HasOne<TenantRecord>().WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey("TenantId", "OwnerUserId").HasPrincipalKey(nameof(PlatformUserRecord.TenantId), nameof(PlatformUserRecord.Id)).OnDelete(DeleteBehavior.Restrict); extra(entity);
    }
}
