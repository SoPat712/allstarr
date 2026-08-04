using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using allstarr.Core.Intelligence;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Downloads;
using allstarr.Core.Playback;
using allstarr.Core.Routing;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext(DbContextOptions<AllstarrDbContext> options) : DbContext(options)
{
    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();
    public DbSet<PlatformUserRecord> Users => Set<PlatformUserRecord>();
    public DbSet<BackendIdentityRecord> BackendIdentities => Set<BackendIdentityRecord>();
    public DbSet<OnboardingStateRecord> OnboardingStates => Set<OnboardingStateRecord>();
    public DbSet<AdminAuthSessionRecord> AdminAuthSessions => Set<AdminAuthSessionRecord>();
    public DbSet<ProviderAccountRecord> ProviderAccounts => Set<ProviderAccountRecord>();
    public DbSet<SecretReferenceRecord> SecretReferences => Set<SecretReferenceRecord>();
    public DbSet<SecretVersionRecord> SecretVersions => Set<SecretVersionRecord>();
    public DbSet<DurableJobRecord> Jobs => Set<DurableJobRecord>();
    public DbSet<JobAttemptRecord> JobAttempts => Set<JobAttemptRecord>();
    public DbSet<OutboxMessageRecord> OutboxMessages => Set<OutboxMessageRecord>();
    public DbSet<ProviderHealthSampleRecord> ProviderHealthSamples => Set<ProviderHealthSampleRecord>();
    public DbSet<ProviderHealthRollupRecord> ProviderHealthRollups => Set<ProviderHealthRollupRecord>();
    public DbSet<ProviderCircuitRecord> ProviderCircuits => Set<ProviderCircuitRecord>();
    public DbSet<CanonicalRecordingRecord> CanonicalRecordings => Set<CanonicalRecordingRecord>();
    public DbSet<ProviderTrackIdentityRecord> ProviderTrackIdentities => Set<ProviderTrackIdentityRecord>();
    public DbSet<LibraryTrackRecord> LibraryTracks => Set<LibraryTrackRecord>();
    public DbSet<ExternalMetadataSnapshotRecord> ExternalMetadataSnapshots => Set<ExternalMetadataSnapshotRecord>();
    public DbSet<TrackMatchRecord> TrackMatches => Set<TrackMatchRecord>();
    public DbSet<ManualTrackOverrideRecord> ManualTrackOverrides => Set<ManualTrackOverrideRecord>();
    public DbSet<JobScheduleRecord> JobSchedules => Set<JobScheduleRecord>();
    public DbSet<PlaylistLinkRecord> PlaylistLinks => Set<PlaylistLinkRecord>();
    public DbSet<PlaylistSourceSnapshotRecord> PlaylistSourceSnapshots => Set<PlaylistSourceSnapshotRecord>();
    public DbSet<PlaylistSourceEntryRecord> PlaylistSourceEntries => Set<PlaylistSourceEntryRecord>();
    public DbSet<PlaylistSyncRunRecord> PlaylistSyncRuns => Set<PlaylistSyncRunRecord>();
    public DbSet<PlaylistSyncEntryResultRecord> PlaylistSyncEntryResults => Set<PlaylistSyncEntryResultRecord>();
    public DbSet<PlaylistTargetMembershipRecord> PlaylistTargetMemberships => Set<PlaylistTargetMembershipRecord>();
    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();
    public DbSet<LegacyEnvImportRecord> LegacyEnvImports => Set<LegacyEnvImportRecord>();
    public DbSet<BackupRecord> Backups => Set<BackupRecord>();
    public DbSet<ExtensionRegistryRecord> ExtensionRegistries => Set<ExtensionRegistryRecord>();
    public DbSet<ExtensionPackageRecord> ExtensionPackages => Set<ExtensionPackageRecord>();
    public DbSet<ExtensionPermissionReviewRecord> ExtensionPermissionReviews => Set<ExtensionPermissionReviewRecord>();
    public DbSet<ExtensionLogRecord> ExtensionLogs => Set<ExtensionLogRecord>();
    public DbSet<FavoriteEventRecord> FavoriteEvents => Set<FavoriteEventRecord>();
    public DbSet<FavoriteActionRecord> FavoriteActions => Set<FavoriteActionRecord>();
    public DbSet<FavoriteStateRecord> FavoriteStates => Set<FavoriteStateRecord>();
    public DbSet<ManagedFileOwnershipEntity> ManagedFiles => Set<ManagedFileOwnershipEntity>();
    public DbSet<ManagedFileReferenceEntity> ManagedFileReferences => Set<ManagedFileReferenceEntity>();
    public DbSet<ProviderDownloadWorkspaceEntity> ProviderDownloadWorkspaces => Set<ProviderDownloadWorkspaceEntity>();
    public DbSet<ProviderDownloadArtifactEntity> ProviderDownloadArtifacts => Set<ProviderDownloadArtifactEntity>();
    public DbSet<DownloadedSongMappingEntity> DownloadedSongMappings => Set<DownloadedSongMappingEntity>();
    public DbSet<PlaybackDeliveryCheckpointEntity> PlaybackDeliveryCheckpoints => Set<PlaybackDeliveryCheckpointEntity>();
    public DbSet<ProviderRouteDecisionEntity> ProviderRouteDecisions => Set<ProviderRouteDecisionEntity>();
    public DbSet<ProviderRouteOutcomeEntity> ProviderRouteOutcomes => Set<ProviderRouteOutcomeEntity>();
    public DbSet<ApplicationCacheEntryRecord> ApplicationCacheEntries => Set<ApplicationCacheEntryRecord>();
    public DbSet<ManualLyricsMappingRecord> ManualLyricsMappings => Set<ManualLyricsMappingRecord>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceImmutableSnapshots();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnforceImmutableSnapshots();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureTenant(modelBuilder);
        ConfigureOnboarding(modelBuilder);
        ConfigureAdminAuthSessions(modelBuilder);
        ConfigureProviderAccounts(modelBuilder);
        ConfigureSecrets(modelBuilder);
        ConfigureJobs(modelBuilder);
        ConfigureProviderHealth(modelBuilder);
        ConfigureTrackIdentity(modelBuilder);
        ConfigurePhase4LibraryAndPlaylists(modelBuilder);
        ConfigureExtensions(modelBuilder);
        FavoriteModelConfiguration.Configure(modelBuilder);
        modelBuilder.ConfigureManagedFileOwnership();
        modelBuilder.ConfigureProviderDownloadArtifacts();
        modelBuilder.ConfigureDownloadedSongMappings();
        ConfigurePhase6Enrichment(modelBuilder);
        ConfigureFavoriteActionPolicies(modelBuilder);
        IntelligenceModelConfiguration.Configure(modelBuilder);
        modelBuilder.ConfigurePlaybackDeliveryCheckpoints();
        ConfigureOperations(modelBuilder);
        ConfigureRuntimeSettings(modelBuilder);
        modelBuilder.ConfigureProviderRouteDecisions();
        ConfigureApplicationCache(modelBuilder);
        modelBuilder.ConfigureManualLyricsMappings();
        ConfigurePortableDateTimeOffsets(modelBuilder);
        // Keep the checked-in snapshot provider-neutral. Neither convention is
        // required because Allstarr assigns durable identifiers explicitly.
        modelBuilder.Model.RemoveAnnotation("Relational:MaxIdentifierLength");
        modelBuilder.Model.RemoveAnnotation("Npgsql:ValueGenerationStrategy");
    }

    private static void ConfigureTenant(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantRecord>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Slug).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(item => item.Slug).IsUnique();
        });

        modelBuilder.Entity<PlatformUserRecord>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BackendIdentityRecord>(entity =>
        {
            entity.ToTable("backend_identities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.BackendType).HasMaxLength(32).IsRequired();
            entity.Property(item => item.BackendInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.PrincipalId).HasMaxLength(300).IsRequired();
            entity.Property(item => item.DisplayName).HasMaxLength(200);
            entity.HasIndex(item => new
            {
                item.BackendType,
                item.BackendInstanceId,
                item.PrincipalId
            }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId });
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureOnboarding(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OnboardingStateRecord>(entity =>
        {
            entity.ToTable("onboarding_states");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.SchemaVersion).HasMaxLength(100).IsRequired();
            entity.Property(item => item.CompletedStepsJson).IsRequired();
            entity.Property(item => item.CompletionSource).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.UserId }).IsUnique();
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.UserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAdminAuthSessions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdminAuthSessionRecord>(entity =>
        {
            entity.ToTable("admin_auth_sessions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(64);
            entity.Property(item => item.ProtectedPayload).IsRequired();
            entity.HasIndex(item => item.ExpiresAt);
        });
    }

    private static void ConfigureProviderAccounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderAccountRecord>(entity =>
        {
            entity.ToTable("provider_accounts", table => table.HasCheckConstraint(
                "CK_provider_accounts_scope_shape",
                "(\"Scope\" = 'Global' AND \"TenantId\" IS NULL AND \"OwnerUserId\" IS NULL AND \"LibraryScopeId\" IS NULL) OR " +
                "(\"Scope\" = 'User' AND \"TenantId\" IS NOT NULL AND \"OwnerUserId\" IS NOT NULL AND \"LibraryScopeId\" IS NULL) OR " +
                "(\"Scope\" = 'Library' AND \"TenantId\" IS NOT NULL AND \"OwnerUserId\" IS NULL AND \"LibraryScopeId\" IS NOT NULL)"));
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.Id, item.ProviderId });
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ProviderId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Scope).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ProviderId, item.TenantId, item.OwnerUserId });
            entity.HasIndex(item => item.CreatedByUserId);
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .HasConstraintName("FK_provider_account_tenant_owner")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => item.CreatedByUserId)
                .HasConstraintName("FK_provider_account_creator")
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureSecrets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SecretReferenceRecord>(entity =>
        {
            entity.ToTable("secret_references");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Purpose).HasMaxLength(200).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.Purpose });
            entity.HasIndex(item => item.BackendIdentityId);
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BackendIdentityRecord>().WithMany().HasForeignKey(item => item.BackendIdentityId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SecretVersionRecord>(entity =>
        {
            entity.ToTable("secret_versions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.KeyId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Nonce).IsRequired();
            entity.Property(item => item.Ciphertext).IsRequired();
            entity.Property(item => item.AuthenticationTag).IsRequired();
            entity.HasIndex(item => new { item.SecretReferenceId, item.Version }).IsUnique();
            entity.HasOne<SecretReferenceRecord>().WithMany()
                .HasForeignKey(item => item.SecretReferenceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderAccountRecord>()
            .HasOne<SecretReferenceRecord>()
            .WithMany()
            .HasForeignKey(item => item.SecretReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DurableJobRecord>(entity =>
        {
            entity.ToTable("durable_jobs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ScopeKey).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Type).HasMaxLength(200).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.PolicySnapshotJson).IsRequired();
            entity.Property(item => item.RequestFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300);
            entity.Property(item => item.ProviderCapability).HasMaxLength(100);
            entity.Property(item => item.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(300).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LeaseOwner).HasMaxLength(200);
            entity.Property(item => item.LastErrorCode).HasMaxLength(100);
            entity.Property(item => item.LastErrorMessage).HasMaxLength(1000);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ScopeKey, item.Type, item.IdempotencyKey }).IsUnique();
            // These unique indexes are the principal side of database-native lineage constraints.
            // They remain indexes (rather than EF alternate keys) because legacy/global jobs may
            // legitimately have nullable tenant and owner values.
            entity.HasIndex(item => new { item.Id, item.TenantId }).IsUnique()
                .HasDatabaseName("UX_durable_job_tenant_lineage");
            entity.HasIndex(item => new { item.Id, item.TenantId, item.OwnerUserId }).IsUnique()
                .HasDatabaseName("UX_durable_job_owner_lineage");
            entity.HasIndex(item => new { item.State, item.AvailableAt, item.Priority });
            entity.HasIndex(item => new { item.TenantId, item.UpdatedAt, item.Id })
                .HasDatabaseName("IX_durable_job_updates");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.OwnerUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .HasConstraintName("FK_durable_job_tenant_owner")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderAccountRecord>().WithMany().HasForeignKey(item => item.ProviderAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JobAttemptRecord>(entity =>
        {
            entity.ToTable("job_attempts");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.WorkerId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Outcome).HasMaxLength(50);
            entity.Property(item => item.ErrorCode).HasMaxLength(100);
            entity.Property(item => item.ErrorMessage).HasMaxLength(1000);
            entity.HasIndex(item => new { item.JobId, item.AttemptNumber }).IsUnique();
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutboxMessageRecord>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Type).HasMaxLength(200).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LeaseOwner).HasMaxLength(200);
            entity.Property(item => item.LastErrorCode).HasMaxLength(100);
            entity.Property(item => item.LastErrorMessage).HasMaxLength(1000);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.State, item.AvailableAt });
            entity.HasIndex(item => new { item.TenantId, item.UpdatedAt, item.Id })
                .HasDatabaseName("IX_outbox_updates");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureProviderHealth(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderHealthSampleRecord>(entity =>
        {
            entity.ToTable("provider_health_samples");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Capability).HasMaxLength(100).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.FailureCode).HasMaxLength(100);
            entity.HasIndex(item => new
            {
                item.ProviderAccountId,
                item.Capability,
                item.ObservedAt
            }).HasDatabaseName("IX_provider_health_account_capability_observed");
            entity.HasIndex(item => new { item.TenantId, item.ObservedAt, item.Id })
                .HasDatabaseName("IX_provider_health_updates");
            entity.HasOne<ProviderAccountRecord>().WithMany()
                .HasForeignKey(item => item.ProviderAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderCircuitRecord>(entity =>
        {
            entity.ToTable("provider_circuits");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Capability).HasMaxLength(100).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ProviderAccountId, item.Capability }).IsUnique();
            entity.HasOne<ProviderAccountRecord>().WithMany()
                .HasForeignKey(item => item.ProviderAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderHealthRollupRecord>(entity =>
        {
            entity.ToTable("provider_health_rollups");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Capability).HasMaxLength(100).IsRequired();
            entity.Property(item => item.LastState).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LastFailureCode).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new
            {
                item.ProviderAccountId,
                item.Capability,
                item.WindowStart
            }).IsUnique().HasDatabaseName("IX_provider_health_rollup_account_capability_window");
            entity.HasIndex(item => item.WindowEnd)
                .HasDatabaseName("IX_provider_health_rollup_window_end");
            entity.HasOne<ProviderAccountRecord>().WithMany()
                .HasForeignKey(item => item.ProviderAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureTrackIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CanonicalRecordingRecord>(entity =>
        {
            entity.ToTable("canonical_recordings");
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Isrc).HasMaxLength(32);
            entity.Property(item => item.MusicBrainzRecordingId).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.Isrc }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.MusicBrainzRecordingId }).IsUnique();
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CreatedByUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProviderTrackIdentityRecord>(entity =>
        {
            entity.ToTable("provider_track_identities", table =>
            {
                table.HasCheckConstraint(
                    "CK_provider_track_identities_scope_shape",
                    "(\"Scope\" = 'Catalog' AND \"ProviderAccountId\" IS NULL) OR " +
                    "(\"Scope\" = 'Account' AND \"ProviderAccountId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_provider_track_identities_track_only",
                    "\"ResourceKind\" = 'Track'");
                table.HasCheckConstraint(
                    "CK_provider_track_identities_verification",
                    "\"Verification\" IN ('Verified', 'Pinned')");
                table.HasCheckConstraint(
                    "CK_provider_track_identities_decision_version",
                    "\"DecisionVersion\" > 0");
                table.HasCheckConstraint(
                    "CK_provider_track_identities_external_hash",
                    "length(\"ExternalIdHash\") = 64");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ProviderId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.ResourceKind).HasConversion<string>().HasMaxLength(50);
            entity.Property(item => item.CatalogNamespace).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Scope).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ExternalId).HasMaxLength(500).IsRequired();
            entity.Property(item => item.ExternalIdHash).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Verification).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.VerificationMethod).HasMaxLength(50).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.CanonicalRecordingId });
            entity.HasIndex(item => new
            {
                item.TenantId,
                item.ProviderId,
                item.ResourceKind,
                item.CatalogNamespace,
                item.ExternalIdHash
            }).IsUnique()
                .HasFilter("\"Scope\" = 'Catalog'")
                .HasDatabaseName("IX_provider_track_identity_catalog_exact");
            entity.HasIndex(item => new
            {
                item.TenantId,
                item.ProviderId,
                item.ResourceKind,
                item.CatalogNamespace,
                item.ProviderAccountId,
                item.ExternalIdHash
            }).IsUnique()
                .HasFilter("\"Scope\" = 'Account'")
                .HasDatabaseName("IX_provider_track_identity_account_exact");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CanonicalRecordingRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalRecordingId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .HasConstraintName("FK_track_identity_canonical_recording")
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProviderAccountRecord>().WithMany()
                .HasForeignKey(item => new
                {
                    item.ProviderAccountId,
                    item.ProviderId
                })
                .HasPrincipalKey(item => new
                {
                    item.Id,
                    item.ProviderId
                })
                .HasConstraintName("FK_track_identity_provider_account")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureOperations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEventRecord>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Category).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Action).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Outcome).HasMaxLength(100).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.DetailsJson).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.CreatedAt, item.Id })
                .HasDatabaseName("IX_audit_event_updates");
            entity.HasIndex(item => item.CorrelationId);
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(item => item.ActorUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegacyEnvImportRecord>(entity =>
        {
            entity.ToTable("legacy_env_imports");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.SourceSha256).HasMaxLength(64).IsRequired();
            entity.Property(item => item.SchemaVersion).HasMaxLength(100).IsRequired();
            entity.Property(item => item.ResultJson).IsRequired();
            entity.Property(item => item.ProvenanceJson).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.SourceSha256, item.SchemaVersion }).IsUnique();
            entity.HasIndex(item => item.AuditEventId).IsUnique();
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.ActorUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AuditEventRecord>().WithMany().HasForeignKey(item => item.AuditEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BackupRecord>(entity =>
        {
            entity.ToTable("backups");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.StorageProvider).HasMaxLength(32).IsRequired();
            entity.Property(item => item.ArtifactPath).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(item => item.SchemaVersion).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ApplicationVersion).HasMaxLength(50).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(50).IsRequired();
            entity.Property(item => item.RestoreStatus).HasMaxLength(50);
            entity.HasIndex(item => item.CreatedAt);
        });
    }

    private static void ConfigurePortableDateTimeOffsets(ModelBuilder modelBuilder)
    {
        // UTC ticks keep lease/order semantics portable for PostgreSQL and offline state transfer.
        var required = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));
        var optional = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.UtcTicks : null,
            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(required);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(optional);
                }
            }
        }
    }

    private void EnforceImmutableSnapshots()
    {
        foreach (var entry in ChangeTracker.Entries<ExternalMetadataSnapshotRecord>()
                     .Where(entry => entry.State == EntityState.Added))
        {
            Playlists.PersistenceGuard.ValidateSafeJson(entry.Entity.PayloadJson, nameof(ExternalMetadataSnapshotRecord.PayloadJson));
        }
        foreach (var entry in ChangeTracker.Entries<TrackMatchRecord>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            Playlists.PersistenceGuard.ValidateSafeJson(entry.Entity.CandidateResultsJson, nameof(TrackMatchRecord.CandidateResultsJson));
            Playlists.PersistenceGuard.ValidateSafeJson(entry.Entity.ReasonsJson, nameof(TrackMatchRecord.ReasonsJson));
            Playlists.PersistenceGuard.ValidateSafeJson(entry.Entity.WarningsJson, nameof(TrackMatchRecord.WarningsJson));
        }
        foreach (var entry in ChangeTracker.Entries<PlaylistSyncEntryResultRecord>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            Playlists.PersistenceGuard.ValidateSafeJson(entry.Entity.DetailsJson, nameof(PlaylistSyncEntryResultRecord.DetailsJson));
        }
        var changed = ChangeTracker.Entries()
            .FirstOrDefault(entry =>
                (entry.State is EntityState.Modified or EntityState.Deleted) &&
                (entry.Entity is ExternalMetadataSnapshotRecord or
                    PlaylistSourceSnapshotRecord or
                    PlaylistSourceEntryRecord) &&
                !(entry.State == EntityState.Modified &&
                  entry.Properties.Where(property => property.IsModified).All(property =>
                      entry.Entity is PlaylistSourceSnapshotRecord &&
                      property.Metadata.Name == nameof(PlaylistSourceSnapshotRecord.PublishedAt) ||
                      entry.Entity is PlaylistSourceEntryRecord &&
                      property.Metadata.Name == nameof(PlaylistSourceEntryRecord.PublishedTrackMatchId))));
        if (changed != null)
        {
            throw new InvalidOperationException(
                $"{changed.Metadata.ClrType.Name} is immutable; create a new snapshot version instead.");
        }
    }
}
