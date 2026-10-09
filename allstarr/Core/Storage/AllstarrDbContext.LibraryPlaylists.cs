using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    private static void ConfigureLibraryAndPlaylists(ModelBuilder modelBuilder)
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
                table.HasCheckConstraint("CK_library_tracks_duration",
                    "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" > 0");
                table.HasCheckConstraint("CK_library_tracks_decision_version", "\"AcceptedDecisionVersion\" IS NULL OR \"AcceptedDecisionVersion\" > 0");
                table.HasCheckConstraint("CK_library_tracks_stable_artwork", "\"CoverArtReference\" IS NULL OR \"CoverArtReference\" NOT LIKE '%://%'");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.BackendLibraryId), 300);
            Required(entity.Property(item => item.Protocol), 32);
            Required(entity.Property(item => item.BackendInstanceId), 200);
            Required(entity.Property(item => item.BackendItemId), 500);
            Required(entity.Property(item => item.FilePath), 2000);
            Required(entity.Property(item => item.Title), 500);
            Required(entity.Property(item => item.Artist), 500);
            entity.Property(item => item.Album).HasMaxLength(500);
            entity.Property(item => item.AlbumArtist).HasMaxLength(500);
            entity.Property(item => item.DurationProvenance).HasMaxLength(100);
            entity.Property(item => item.Isrc).HasMaxLength(32);
            entity.Property(item => item.MusicBrainzRecordingId).HasMaxLength(100);
            entity.Property(item => item.MusicBrainzReleaseId).HasMaxLength(100);
            entity.Property(item => item.MusicBrainzArtistId).HasMaxLength(100);
            Required(entity.Property(item => item.ProviderIdsJson));
            entity.Property(item => item.CoverArtReference).HasMaxLength(1000);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.OwnerUserId, item.BackendLibraryId, item.BackendInstanceId, item.BackendItemId }).IsUnique().HasDatabaseName("IX_library_track_backend_item");
            entity.HasIndex(item => new { item.OwnerUserId, item.BackendLibraryId, item.Isrc }).HasDatabaseName("IX_library_track_scoped_isrc");
            entity.HasIndex(item => new { item.OwnerUserId, item.BackendLibraryId, item.MusicBrainzRecordingId }).HasDatabaseName("IX_library_track_scoped_musicbrainz");
            entity.HasIndex(item => item.CanonicalRecordingId);
            OwnedByUser(entity, item => item.OwnerUserId);
            entity.HasOne<CanonicalRecordingRecord>().WithMany()
                .HasForeignKey(item => item.CanonicalRecordingId)
                .HasPrincipalKey(item => item.Id)
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
            entity.Property(item => item.Id).ValueGeneratedNever();
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
            entity.HasIndex(item => new { item.OwnerUserId, item.ProviderAccountId, item.ResourceKind, item.ExternalIdHash, item.SnapshotVersion }).IsUnique().HasDatabaseName("IX_external_snapshot_version");
            OwnedByUser(entity, item => item.OwnerUserId);
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
                table.HasCheckConstraint("CK_track_matches_selected_shape", "(\"State\" IN ('Accepted', 'Suggested') AND (\"LibraryTrackId\" IS NOT NULL OR \"CanonicalRecordingId\" IS NOT NULL)) OR (\"State\" = 'Pinned' AND \"LibraryTrackId\" IS NOT NULL) OR (\"State\" IN ('Unresolved', 'Rejected', 'Ambiguous') AND \"LibraryTrackId\" IS NULL)");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            Required(entity.Property(item => item.MatcherVersion), 100);
            Required(entity.Property(item => item.PolicyVersion), 100);
            Required(entity.Property(item => item.CandidateResultsJson));
            Required(entity.Property(item => item.ReasonsJson));
            Required(entity.Property(item => item.WarningsJson));
            Required(entity.Property(item => item.CorrelationId), 100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.OwnerUserId, item.ExternalSnapshotId, item.DecisionVersion }).IsUnique().HasDatabaseName("IX_track_match_scoped_decision");
            entity.HasIndex(item => new { item.DecidedAt, item.Id })
                .HasDatabaseName("IX_track_match_updates");
            OwnedByUser(entity, item => item.OwnerUserId);
            Reference<TrackMatchRecord, ExternalMetadataSnapshotRecord>(entity, item => item.ExternalSnapshotId);
            Reference<TrackMatchRecord, LibraryTrackRecord>(entity, item => item.LibraryTrackId);
            Reference<TrackMatchRecord, CanonicalRecordingRecord>(entity, item => item.CanonicalRecordingId);
        });

        modelBuilder.Entity<ManualTrackOverrideRecord>(entity =>
        {
            entity.ToTable("manual_track_overrides", table =>
            {
                table.HasCheckConstraint("CK_manual_overrides_version", "\"DecisionVersion\" > 0");
                table.HasCheckConstraint("CK_manual_overrides_source", "length(\"SourceExternalIdHash\") = 64 AND length(\"SourceProviderId\") > 0");
                table.HasCheckConstraint("CK_manual_overrides_shape", """
                    ("Decision" = 'Pin' AND (
                        ("LibraryTrackId" IS NOT NULL AND "TargetProviderId" IS NULL AND "TargetExternalId" IS NULL) OR
                        ("LibraryTrackId" IS NULL AND length("TargetProviderId") > 0 AND length("TargetExternalId") > 0 AND
                         "TargetProviderId" IS NOT NULL AND "TargetExternalId" IS NOT NULL))) OR
                    ("Decision" = 'Reject' AND "TargetProviderId" IS NULL AND "TargetExternalId" IS NULL)
                    """);
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.SourceProviderId), 100);
            Required(entity.Property(item => item.SourceExternalIdHash), 64);
            entity.Property(item => item.TargetProviderId).HasMaxLength(100);
            entity.Property(item => item.TargetExternalId).HasMaxLength(500);
            entity.Property(item => item.Decision).HasConversion<string>().HasMaxLength(32);
            Required(entity.Property(item => item.Reason), 1000);
            Required(entity.Property(item => item.MatcherVersion), 100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.OwnerUserId, item.SourceProviderId, item.SourceExternalIdHash })
                .IsUnique().HasFilter("\"RevokedAt\" IS NULL AND \"OwnerUserId\" IS NOT NULL")
                .HasDatabaseName("IX_manual_track_override_personal_active");
            entity.HasIndex(item => new { item.SourceProviderId, item.SourceExternalIdHash })
                .IsUnique().HasFilter("\"RevokedAt\" IS NULL AND \"OwnerUserId\" IS NULL")
                .HasDatabaseName("IX_manual_track_override_household_active");
            OwnedByUser(entity, item => item.OwnerUserId);
            Reference<ManualTrackOverrideRecord, ExternalMetadataSnapshotRecord>(entity, item => item.ExternalSnapshotId);
            Reference<ManualTrackOverrideRecord, LibraryTrackRecord>(entity, item => item.LibraryTrackId);
        });
    }

    private static void ConfigurePlaylistSchedules(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobScheduleRecord>(entity =>
        {
            entity.ToTable("job_schedules");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
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
            OwnedByUser(entity, item => item.OwnerUserId);
        });

        modelBuilder.Entity<PlaylistLinkRecord>(entity =>
        {
            entity.ToTable("playlist_links", table =>
            {
                table.HasCheckConstraint("CK_playlist_links_source_hash", "length(\"SourcePlaylistIdHash\") = 64");
                table.HasCheckConstraint("CK_playlist_links_import_mode", "\"ImportMode\" IN ('Linked', 'OneTime')");
                table.HasCheckConstraint("CK_playlist_links_track_retention", "\"TrackRetention\" IN ('OnDemand', 'KeepAll')");
                table.HasCheckConstraint("CK_playlist_links_one_time_schedule", "\"ImportMode\" <> 'OneTime' OR \"ScheduleId\" IS NULL");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Enabled).HasDefaultValue(true);
            Required(entity.Property(item => item.SourceProviderId), 100);
            Required(entity.Property(item => item.SourcePlaylistId), 500);
            Required(entity.Property(item => item.SourcePlaylistIdHash), 64);
            Required(entity.Property(item => item.TargetProtocol), 32);
            Required(entity.Property(item => item.TargetBackendInstanceId), 200);
            entity.Property(item => item.TargetPlaylistId).HasMaxLength(500);
            entity.Property(item => item.Mode).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ProjectionMode).HasConversion<string>().HasMaxLength(32)
                .HasDefaultValue(PlaylistProjectionMode.Resolved);
            entity.Property(item => item.MaterializationMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ImportMode).HasConversion<string>().HasMaxLength(32)
                .HasDefaultValue(PlaylistImportMode.Linked);
            entity.Property(item => item.TrackRetention).HasConversion<string>().HasMaxLength(32)
                .HasDefaultValue(PlaylistTrackRetention.OnDemand);
            Required(entity.Property(item => item.RuleVersion), 100);
            Required(entity.Property(item => item.PolicyVersion), 100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.OwnerUserId, item.SourceProviderId, item.ProviderAccountId, item.SourcePlaylistIdHash, item.TargetProtocol, item.TargetBackendInstanceId }).IsUnique().HasDatabaseName("IX_playlist_link_source_target");
            OwnedByUser(entity, item => item.OwnerUserId);
            entity.HasOne<ProviderAccountRecord>().WithMany().HasForeignKey(item => new { item.ProviderAccountId, item.SourceProviderId })
                .HasPrincipalKey(item => new { item.Id, item.ProviderId }).HasConstraintName("FK_playlist_link_provider_account").OnDelete(DeleteBehavior.Restrict);
            Reference<PlaylistLinkRecord, JobScheduleRecord>(entity, item => item.ScheduleId);
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
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.ProviderRevision), 300);
            entity.Property(item => item.ETag).HasMaxLength(500);
            Required(entity.Property(item => item.Name), 500);
            entity.Property(item => item.Description).HasMaxLength(4000);
            entity.Property(item => item.ArtworkReferenceKey).HasMaxLength(1000);
            Required(entity.Property(item => item.PayloadSha256), 64);
            Required(entity.Property(item => item.CorrelationId), 100);
            entity.HasIndex(item => new { item.PlaylistLinkId, item.SnapshotVersion }).IsUnique().HasDatabaseName("IX_playlist_snapshot_version");
            entity.HasIndex(item => new { item.PlaylistLinkId, item.PublishedAt, item.SnapshotVersion })
                .HasDatabaseName("IX_playlist_snapshot_published");
            entity.HasIndex(item => new { item.RetrievedAt, item.Id })
                .HasDatabaseName("IX_playlist_snapshot_updates");
            OwnedByUser(entity, item => item.OwnerUserId);
            Reference<PlaylistSourceSnapshotRecord, PlaylistLinkRecord>(entity, item => item.PlaylistLinkId);
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
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.SourceEntryIdHash), 64);
            entity.HasIndex(item => new { item.PlaylistSourceSnapshotId, item.SourcePosition }).IsUnique().HasDatabaseName("IX_playlist_source_entry_position");
            Reference<PlaylistSourceEntryRecord, PlaylistSourceSnapshotRecord>(entity, item => item.PlaylistSourceSnapshotId);
            Reference<PlaylistSourceEntryRecord, ExternalMetadataSnapshotRecord>(entity, item => item.ExternalMetadataSnapshotId);
            Reference<PlaylistSourceEntryRecord, TrackMatchRecord>(entity, item => item.PublishedTrackMatchId);
        });

        modelBuilder.Entity<PlaylistSyncRunRecord>(entity =>
        {
            entity.ToTable("playlist_sync_runs", table =>
            {
                table.HasCheckConstraint("CK_playlist_sync_generation", "\"Generation\" > 0");
                table.HasCheckConstraint("CK_playlist_sync_verification_counts",
                    "(\"PlannedTargetTrackCount\" IS NULL OR \"PlannedTargetTrackCount\" >= 0) AND " +
                    "(\"VerifiedTargetTrackCount\" IS NULL OR \"VerifiedTargetTrackCount\" >= 0)");
                table.HasCheckConstraint("CK_playlist_sync_verification_durations",
                    "(\"PlannedTargetDurationMilliseconds\" IS NULL OR \"PlannedTargetDurationMilliseconds\" >= 0) AND " +
                    "(\"VerifiedTargetDurationMilliseconds\" IS NULL OR \"VerifiedTargetDurationMilliseconds\" >= 0)");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.IdempotencyKey), 300);
            Required(entity.Property(item => item.RuleVersion), 100);
            entity.Property(item => item.MaterializationMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.TargetRevisionBefore).HasMaxLength(500);
            entity.Property(item => item.TargetRevisionAfter).HasMaxLength(500);
            entity.Property(item => item.ConflictCode).HasMaxLength(100);
            entity.Property(item => item.VerificationCode).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.PlaylistLinkId, item.IdempotencyKey }).IsUnique();
            OwnedByUser(entity, item => item.OwnerUserId);
            Reference<PlaylistSyncRunRecord, PlaylistLinkRecord>(entity, item => item.PlaylistLinkId);
            Reference<PlaylistSyncRunRecord, PlaylistSourceSnapshotRecord>(entity, item => item.PlaylistSourceSnapshotId);
            Reference<PlaylistSyncRunRecord, JobScheduleRecord>(entity, item => item.ScheduleId);
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
            entity.HasIndex(item => new { item.PlaylistSyncRunId, item.SourcePosition }).IsUnique().HasDatabaseName("IX_playlist_result_run_position");
            Reference<PlaylistSyncEntryResultRecord, PlaylistSyncRunRecord>(entity, item => item.PlaylistSyncRunId);
            Reference<PlaylistSyncEntryResultRecord, PlaylistSourceEntryRecord>(entity, item => item.PlaylistSourceEntryId);
            Reference<PlaylistSyncEntryResultRecord, TrackMatchRecord>(entity, item => item.TrackMatchId);
            Reference<PlaylistSyncEntryResultRecord, LibraryTrackRecord>(entity, item => item.LibraryTrackId);
        });

        modelBuilder.Entity<PlaylistTargetMembershipRecord>(entity =>
        {
            entity.ToTable("playlist_target_memberships", table => table.HasCheckConstraint("CK_playlist_membership_position", "\"LastKnownPosition\" >= 0"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            Required(entity.Property(item => item.TargetEntryId), 500);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.PlaylistLinkId, item.TargetEntryId }).IsUnique().HasDatabaseName("IX_playlist_membership_target_entry");
            entity.HasIndex(item => new { item.PlaylistLinkId, item.LibraryTrackId, item.Active }).HasDatabaseName("IX_playlist_membership_track_active");
            Reference<PlaylistTargetMembershipRecord, PlaylistLinkRecord>(entity, item => item.PlaylistLinkId);
            Reference<PlaylistTargetMembershipRecord, LibraryTrackRecord>(entity, item => item.LibraryTrackId);
            Reference<PlaylistTargetMembershipRecord, PlaylistSyncRunRecord>(entity, item => item.CreatedBySyncRunId);
        });
    }

    private static void Required<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> property, int? maxLength = null)
    {
        property.IsRequired();
        if (maxLength.HasValue) property.HasMaxLength(maxLength.Value);
    }

    private static void OwnedByUser<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, System.Linq.Expressions.Expression<Func<TEntity, object?>> foreignKey)
        where TEntity : class
        => entity.HasOne<UserRecord>().WithMany().HasForeignKey(foreignKey)
            .HasPrincipalKey(item => item.Id)
            .HasConstraintName(PortableConstraintName<TEntity, UserRecord>()).OnDelete(DeleteBehavior.Restrict);

    private static void Reference<TEntity, TPrincipal>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, System.Linq.Expressions.Expression<Func<TEntity, object?>> foreignKey)
        where TEntity : class where TPrincipal : class
        => entity.HasOne<TPrincipal>().WithMany().HasForeignKey(foreignKey)
            .HasPrincipalKey("Id")
            .HasConstraintName(PortableConstraintName<TEntity, TPrincipal>()).OnDelete(DeleteBehavior.Restrict);

    private static string PortableConstraintName<TEntity, TPrincipal>()
    {
        var name = $"FK_{typeof(TEntity).Name.Replace("Record", "")}_{typeof(TPrincipal).Name.Replace("Record", "")}";
        return name[..Math.Min(name.Length, 60)];
    }
}
