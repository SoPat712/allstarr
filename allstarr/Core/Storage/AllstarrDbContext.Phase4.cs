using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    private static void ConfigurePhase4LibraryAndPlaylists(ModelBuilder modelBuilder)
    {
        ConfigureLibraryAndMatching(modelBuilder);
        ConfigurePlaylistSchedules(modelBuilder);
        ConfigurePlaylistSnapshotsAndRuns(modelBuilder);
    }

    private static void ConfigureLibraryAndMatching(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LibraryTrackRecord>(entity =>
        {
            entity.ToTable("library_tracks", table =>
            {
                table.HasCheckConstraint("CK_library_tracks_duration", "\"DurationMilliseconds\" >= 0");
                table.HasCheckConstraint("CK_library_tracks_decision_version", "\"AcceptedDecisionVersion\" IS NULL OR \"AcceptedDecisionVersion\" > 0");
                table.HasCheckConstraint("CK_library_tracks_stable_artwork", "\"CoverArtReference\" IS NULL OR \"CoverArtReference\" NOT LIKE '%://%'");
            });
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.LibraryScopeId), 300);
            Required(entity.Property(item => item.Protocol), 32);
            Required(entity.Property(item => item.BackendInstanceId), 200);
            Required(entity.Property(item => item.BackendItemId), 500);
            Required(entity.Property(item => item.FilePath), 2000);
            Required(entity.Property(item => item.Title), 500);
            Required(entity.Property(item => item.Artist), 500);
            entity.Property(item => item.Album).HasMaxLength(500);
            entity.Property(item => item.AlbumArtist).HasMaxLength(500);
            entity.Property(item => item.Isrc).HasMaxLength(32);
            entity.Property(item => item.MusicBrainzRecordingId).HasMaxLength(100);
            entity.Property(item => item.MusicBrainzReleaseId).HasMaxLength(100);
            entity.Property(item => item.MusicBrainzArtistId).HasMaxLength(100);
            Required(entity.Property(item => item.ProviderIdsJson));
            entity.Property(item => item.CoverArtReference).HasMaxLength(1000);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.LibraryScopeId, item.BackendInstanceId, item.BackendItemId }).IsUnique().HasDatabaseName("IX_library_track_backend_item");
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.LibraryScopeId, item.Isrc }).HasDatabaseName("IX_library_track_scoped_isrc");
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.LibraryScopeId, item.MusicBrainzRecordingId }).HasDatabaseName("IX_library_track_scoped_musicbrainz");
            entity.HasIndex(item => new { item.TenantId, item.CanonicalRecordingId });
            TenantUser(entity, item => new { item.TenantId, item.OwnerUserId });
            entity.HasOne<BackendIdentityRecord>().WithMany().HasForeignKey(item => item.BackendIdentityId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CanonicalRecordingRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalRecordingId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .HasConstraintName("FK_library_track_canonical_recording").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExternalMetadataSnapshotRecord>(entity =>
        {
            entity.ToTable("external_metadata_snapshots", table =>
            {
                table.HasCheckConstraint("CK_external_snapshots_version", "\"SnapshotVersion\" > 0");
                table.HasCheckConstraint("CK_external_snapshots_external_hash", "length(\"ExternalIdHash\") = 64");
                table.HasCheckConstraint("CK_external_snapshots_payload_hash", "length(\"PayloadSha256\") = 64");
            });
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.LibraryScopeId), 300);
            Required(entity.Property(item => item.BackendInstanceId), 200);
            Required(entity.Property(item => item.BackendPrincipalId), 300);
            Required(entity.Property(item => item.Protocol), 32);
            Required(entity.Property(item => item.ProviderId), 100);
            Required(entity.Property(item => item.ResourceKind), 50);
            Required(entity.Property(item => item.ExternalIdHash), 64);
            Required(entity.Property(item => item.ProviderRevision), 300);
            Required(entity.Property(item => item.PayloadJson));
            Required(entity.Property(item => item.PayloadSha256), 64);
            Required(entity.Property(item => item.CorrelationId), 100);
            entity.HasIndex(item => new { item.TenantId, item.ProviderAccountId, item.ResourceKind, item.ExternalIdHash, item.SnapshotVersion }).IsUnique().HasDatabaseName("IX_external_snapshot_version");
            TenantUser(entity, item => new { item.TenantId, item.OwnerUserId });
            entity.HasOne<ProviderAccountRecord>().WithMany().HasForeignKey(item => new { item.ProviderAccountId, item.ProviderId })
                .HasPrincipalKey(item => new { item.Id, item.ProviderId }).HasConstraintName("FK_external_snapshot_provider_account").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderTrackIdentityRecord>().WithMany().HasForeignKey(item => item.ProviderTrackIdentityId).HasConstraintName("FK_external_snapshot_provider_identity").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.SourceJobId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TrackMatchRecord>(entity =>
        {
            entity.ToTable("track_matches", table =>
            {
                table.HasCheckConstraint("CK_track_matches_confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1 AND \"Threshold\" >= 0 AND \"Threshold\" <= 1");
                table.HasCheckConstraint("CK_track_matches_version", "\"DecisionVersion\" > 0");
                table.HasCheckConstraint("CK_track_matches_selected_shape", "(\"State\" IN ('Accepted', 'Pinned') AND \"LibraryTrackId\" IS NOT NULL) OR (\"State\" IN ('Unresolved', 'Suggested', 'Rejected', 'Ambiguous') AND \"LibraryTrackId\" IS NULL)");
            });
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.LibraryScopeId), 300);
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            Required(entity.Property(item => item.PolicyVersion), 100);
            Required(entity.Property(item => item.CandidateResultsJson));
            Required(entity.Property(item => item.ReasonsJson));
            Required(entity.Property(item => item.WarningsJson));
            Required(entity.Property(item => item.CorrelationId), 100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.LibraryScopeId, item.ExternalSnapshotId, item.DecisionVersion }).IsUnique().HasDatabaseName("IX_track_match_scoped_decision");
            TenantUser(entity, item => new { item.TenantId, item.OwnerUserId });
            TenantReference<TrackMatchRecord, ExternalMetadataSnapshotRecord>(entity, item => new { item.TenantId, item.ExternalSnapshotId });
            TenantReference<TrackMatchRecord, LibraryTrackRecord>(entity, item => new { item.TenantId, item.LibraryTrackId });
            TenantReference<TrackMatchRecord, CanonicalRecordingRecord>(entity, item => new { item.TenantId, item.CanonicalRecordingId });
        });

        modelBuilder.Entity<ManualTrackOverrideRecord>(entity =>
        {
            entity.ToTable("manual_track_overrides", table =>
            {
                table.HasCheckConstraint("CK_manual_overrides_version", "\"DecisionVersion\" > 0");
                table.HasCheckConstraint("CK_manual_overrides_shape", "(\"Decision\" = 'Pin' AND \"LibraryTrackId\" IS NOT NULL) OR (\"Decision\" = 'Reject' AND \"LibraryTrackId\" IS NULL)");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.LibraryScopeId), 300);
            entity.Property(item => item.Decision).HasConversion<string>().HasMaxLength(32);
            Required(entity.Property(item => item.Reason), 1000);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.LibraryScopeId, item.ExternalSnapshotId })
                .IsUnique().HasFilter("\"RevokedAt\" IS NULL").HasDatabaseName("IX_manual_track_override_active");
            TenantUser(entity, item => new { item.TenantId, item.OwnerUserId });
            TenantReference<ManualTrackOverrideRecord, ExternalMetadataSnapshotRecord>(entity, item => new { item.TenantId, item.ExternalSnapshotId });
            TenantReference<ManualTrackOverrideRecord, LibraryTrackRecord>(entity, item => new { item.TenantId, item.LibraryTrackId });
        });
    }

    private static void ConfigurePlaylistSchedules(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobScheduleRecord>(entity =>
        {
            entity.ToTable("job_schedules");
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.LibraryScopeId), 300);
            Required(entity.Property(item => item.JobType), 100);
            Required(entity.Property(item => item.CronExpression), 200);
            Required(entity.Property(item => item.TimeZoneId), 100);
            entity.Property(item => item.OverlapPolicy).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.MisfirePolicy).HasConversion<string>().HasMaxLength(32);
            Required(entity.Property(item => item.RetryPolicyJson));
            var payloadTemplate = entity.Property(item => item.PayloadTemplateJson);
            Required(payloadTemplate);
            payloadTemplate.HasDefaultValue("{}");
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.Enabled, item.NextRunAt });
            TenantUser(entity, item => new { item.TenantId, item.OwnerUserId });
        });

        modelBuilder.Entity<PlaylistLinkRecord>(entity =>
        {
            entity.ToTable("playlist_links", table => table.HasCheckConstraint("CK_playlist_links_source_hash", "length(\"SourcePlaylistIdHash\") = 64"));
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.LibraryScopeId), 300);
            Required(entity.Property(item => item.SourceProviderId), 100);
            Required(entity.Property(item => item.SourcePlaylistId), 500);
            Required(entity.Property(item => item.SourcePlaylistIdHash), 64);
            Required(entity.Property(item => item.TargetProtocol), 32);
            Required(entity.Property(item => item.TargetBackendInstanceId), 200);
            entity.Property(item => item.TargetPlaylistId).HasMaxLength(500);
            entity.Property(item => item.Mode).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.MaterializationMode).HasConversion<string>().HasMaxLength(32);
            Required(entity.Property(item => item.RuleVersion), 100);
            Required(entity.Property(item => item.PolicyVersion), 100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.LibraryScopeId, item.SourceProviderId, item.ProviderAccountId, item.SourcePlaylistIdHash, item.TargetProtocol, item.TargetBackendInstanceId }).IsUnique().HasDatabaseName("IX_playlist_link_source_target");
            TenantUser(entity, item => new { item.TenantId, item.OwnerUserId });
            entity.HasOne<ProviderAccountRecord>().WithMany().HasForeignKey(item => new { item.ProviderAccountId, item.SourceProviderId })
                .HasPrincipalKey(item => new { item.Id, item.ProviderId }).HasConstraintName("FK_playlist_link_provider_account").OnDelete(DeleteBehavior.Restrict);
            TenantReference<PlaylistLinkRecord, JobScheduleRecord>(entity, item => new { item.TenantId, item.ScheduleId });
        });
    }

    private static void ConfigurePlaylistSnapshotsAndRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlaylistSourceSnapshotRecord>(entity =>
        {
            entity.ToTable("playlist_source_snapshots", table =>
            {
                table.HasCheckConstraint("CK_playlist_snapshots_version", "\"SnapshotVersion\" > 0");
                table.HasCheckConstraint("CK_playlist_snapshots_payload_hash", "length(\"PayloadSha256\") = 64");
                table.HasCheckConstraint("CK_playlist_snapshots_stable_artwork", "\"ArtworkReferenceKey\" IS NULL OR \"ArtworkReferenceKey\" NOT LIKE '%://%'");
            });
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.ProviderRevision), 300);
            entity.Property(item => item.ETag).HasMaxLength(500);
            Required(entity.Property(item => item.Name), 500);
            entity.Property(item => item.Description).HasMaxLength(4000);
            entity.Property(item => item.ArtworkReferenceKey).HasMaxLength(1000);
            Required(entity.Property(item => item.PayloadSha256), 64);
            Required(entity.Property(item => item.CorrelationId), 100);
            entity.HasIndex(item => new { item.TenantId, item.PlaylistLinkId, item.SnapshotVersion }).IsUnique().HasDatabaseName("IX_playlist_snapshot_version");
            TenantUser(entity, item => new { item.TenantId, item.OwnerUserId });
            TenantReference<PlaylistSourceSnapshotRecord, PlaylistLinkRecord>(entity, item => new { item.TenantId, item.PlaylistLinkId });
            entity.HasOne<ProviderAccountRecord>().WithMany().HasForeignKey(item => item.ProviderAccountId).HasConstraintName("FK_playlist_snapshot_provider_account").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.SourceJobId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlaylistSourceEntryRecord>(entity =>
        {
            entity.ToTable("playlist_source_entries", table =>
            {
                table.HasCheckConstraint("CK_playlist_source_entry_position", "\"SourcePosition\" >= 0");
                table.HasCheckConstraint("CK_playlist_source_entry_hash", "length(\"SourceEntryIdHash\") = 64");
            });
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.SourceEntryIdHash), 64);
            entity.HasIndex(item => new { item.TenantId, item.PlaylistSourceSnapshotId, item.SourcePosition }).IsUnique().HasDatabaseName("IX_playlist_source_entry_position");
            TenantReference<PlaylistSourceEntryRecord, PlaylistSourceSnapshotRecord>(entity, item => new { item.TenantId, item.PlaylistSourceSnapshotId });
            TenantReference<PlaylistSourceEntryRecord, ExternalMetadataSnapshotRecord>(entity, item => new { item.TenantId, item.ExternalMetadataSnapshotId });
        });

        modelBuilder.Entity<PlaylistSyncRunRecord>(entity =>
        {
            entity.ToTable("playlist_sync_runs", table => table.HasCheckConstraint("CK_playlist_sync_generation", "\"Generation\" > 0"));
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.IdempotencyKey), 300);
            Required(entity.Property(item => item.RuleVersion), 100);
            entity.Property(item => item.MaterializationMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.TargetRevisionBefore).HasMaxLength(500);
            entity.Property(item => item.TargetRevisionAfter).HasMaxLength(500);
            entity.Property(item => item.ConflictCode).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.PlaylistLinkId, item.IdempotencyKey }).IsUnique();
            TenantUser(entity, item => new { item.TenantId, item.OwnerUserId });
            TenantReference<PlaylistSyncRunRecord, PlaylistLinkRecord>(entity, item => new { item.TenantId, item.PlaylistLinkId });
            TenantReference<PlaylistSyncRunRecord, PlaylistSourceSnapshotRecord>(entity, item => new { item.TenantId, item.PlaylistSourceSnapshotId });
            TenantReference<PlaylistSyncRunRecord, JobScheduleRecord>(entity, item => new { item.TenantId, item.ScheduleId });
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.JobId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlaylistSyncEntryResultRecord>(entity =>
        {
            entity.ToTable("playlist_sync_entry_results", table => table.HasCheckConstraint("CK_playlist_result_positions", "\"SourcePosition\" >= 0 AND (\"TargetPosition\" IS NULL OR \"TargetPosition\" >= 0)"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Outcome).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.OutcomeCode).HasMaxLength(100);
            Required(entity.Property(item => item.DetailsJson));
            entity.HasIndex(item => new { item.TenantId, item.PlaylistSyncRunId, item.SourcePosition }).IsUnique().HasDatabaseName("IX_playlist_result_run_position");
            TenantReference<PlaylistSyncEntryResultRecord, PlaylistSyncRunRecord>(entity, item => new { item.TenantId, item.PlaylistSyncRunId });
            TenantReference<PlaylistSyncEntryResultRecord, PlaylistSourceEntryRecord>(entity, item => new { item.TenantId, item.PlaylistSourceEntryId });
            TenantReference<PlaylistSyncEntryResultRecord, TrackMatchRecord>(entity, item => new { item.TenantId, item.TrackMatchId });
            TenantReference<PlaylistSyncEntryResultRecord, LibraryTrackRecord>(entity, item => new { item.TenantId, item.LibraryTrackId });
        });

        modelBuilder.Entity<PlaylistTargetMembershipRecord>(entity =>
        {
            entity.ToTable("playlist_target_memberships", table => table.HasCheckConstraint("CK_playlist_membership_position", "\"LastKnownPosition\" >= 0"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.TargetEntryId), 500);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.PlaylistLinkId, item.TargetEntryId }).IsUnique().HasDatabaseName("IX_playlist_membership_target_entry");
            entity.HasIndex(item => new { item.TenantId, item.PlaylistLinkId, item.LibraryTrackId, item.Active }).HasDatabaseName("IX_playlist_membership_track_active");
            TenantReference<PlaylistTargetMembershipRecord, PlaylistLinkRecord>(entity, item => new { item.TenantId, item.PlaylistLinkId });
            TenantReference<PlaylistTargetMembershipRecord, LibraryTrackRecord>(entity, item => new { item.TenantId, item.LibraryTrackId });
            TenantReference<PlaylistTargetMembershipRecord, PlaylistSyncRunRecord>(entity, item => new { item.TenantId, item.CreatedBySyncRunId });
        });
    }

    private static void Required<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> property, int? maxLength = null)
    {
        property.IsRequired();
        if (maxLength.HasValue) property.HasMaxLength(maxLength.Value);
    }

    private static void TenantUser<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, System.Linq.Expressions.Expression<Func<TEntity, object?>> foreignKey)
        where TEntity : class
        => entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(foreignKey)
            .HasPrincipalKey(item => new { item.TenantId, item.Id })
            .HasConstraintName(PortableConstraintName<TEntity, PlatformUserRecord>()).OnDelete(DeleteBehavior.Restrict);

    private static void TenantReference<TEntity, TPrincipal>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, System.Linq.Expressions.Expression<Func<TEntity, object?>> foreignKey)
        where TEntity : class where TPrincipal : class
        => entity.HasOne<TPrincipal>().WithMany().HasForeignKey(foreignKey)
            .HasPrincipalKey("TenantId", "Id")
            .HasConstraintName(PortableConstraintName<TEntity, TPrincipal>()).OnDelete(DeleteBehavior.Restrict);

    private static string PortableConstraintName<TEntity, TPrincipal>()
    {
        var name = $"FK_{typeof(TEntity).Name.Replace("Record", "")}_{typeof(TPrincipal).Name.Replace("Record", "")}";
        return name[..Math.Min(name.Length, 60)];
    }
}
