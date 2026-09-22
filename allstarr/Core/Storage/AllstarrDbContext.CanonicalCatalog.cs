using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    public DbSet<CanonicalArtistRecord> CanonicalArtists => Set<CanonicalArtistRecord>();
    public DbSet<CanonicalReleaseGroupRecord> CanonicalReleaseGroups => Set<CanonicalReleaseGroupRecord>();
    public DbSet<CanonicalReleaseRecord> CanonicalReleases => Set<CanonicalReleaseRecord>();
    public DbSet<CanonicalReleaseTrackRecord> CanonicalReleaseTracks => Set<CanonicalReleaseTrackRecord>();
    public DbSet<CanonicalRecordingArtistRecord> CanonicalRecordingArtists => Set<CanonicalRecordingArtistRecord>();
    public DbSet<CanonicalReleaseGroupArtistRecord> CanonicalReleaseGroupArtists => Set<CanonicalReleaseGroupArtistRecord>();
    public DbSet<CanonicalCatalogAliasRecord> CanonicalCatalogAliases => Set<CanonicalCatalogAliasRecord>();
    public DbSet<CatalogFactRecord> CatalogFacts => Set<CatalogFactRecord>();

    private static void ConfigureCanonicalCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CanonicalArtistRecord>(entity =>
        {
            entity.ToTable("canonical_artists");
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Name).HasMaxLength(500).IsRequired();
            entity.Property(item => item.SortName).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Disambiguation).HasMaxLength(500);
            entity.Property(item => item.MusicBrainzArtistId).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.MusicBrainzArtistId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.SortName, item.Id });
            TenantOwned(entity);
        });

        modelBuilder.Entity<CanonicalReleaseGroupRecord>(entity =>
        {
            entity.ToTable("canonical_release_groups");
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Title).HasMaxLength(500).IsRequired();
            entity.Property(item => item.PrimaryType).HasMaxLength(100);
            entity.Property(item => item.SecondaryTypesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(item => item.FirstReleaseDate).HasMaxLength(10);
            entity.Property(item => item.MusicBrainzReleaseGroupId).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.MusicBrainzReleaseGroupId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.Title, item.Id });
            TenantOwned(entity);
        });

        modelBuilder.Entity<CanonicalReleaseRecord>(entity =>
        {
            entity.ToTable("canonical_releases");
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Title).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Disambiguation).HasMaxLength(500);
            entity.Property(item => item.Status).HasMaxLength(100);
            entity.Property(item => item.CountryCode).HasMaxLength(2);
            entity.Property(item => item.ReleaseDate).HasMaxLength(10);
            entity.Property(item => item.Barcode).HasMaxLength(100);
            entity.Property(item => item.MusicBrainzReleaseId).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.MusicBrainzReleaseId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.CanonicalReleaseGroupId, item.ReleaseDate });
            TenantOwned(entity);
            entity.HasOne<CanonicalReleaseGroupRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalReleaseGroupId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CanonicalReleaseTrackRecord>(entity =>
        {
            entity.ToTable("canonical_release_tracks", table =>
                table.HasCheckConstraint("CK_canonical_release_track_position", "\"MediumPosition\" > 0 AND \"TrackPosition\" > 0"));
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.TenantId, item.Id });
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Title).HasMaxLength(500).IsRequired();
            entity.Property(item => item.MusicBrainzTrackId).HasMaxLength(100);
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.MusicBrainzTrackId }).IsUnique();
            entity.HasIndex(item => new
            {
                item.TenantId,
                item.CanonicalReleaseId,
                item.MediumPosition,
                item.TrackPosition
            }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.CanonicalRecordingId });
            TenantOwned(entity);
            entity.HasOne<CanonicalReleaseRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalReleaseId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CanonicalRecordingRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalRecordingId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CanonicalRecordingArtistRecord>(entity =>
        {
            entity.ToTable("canonical_recording_artists", table =>
                table.HasCheckConstraint("CK_canonical_recording_artist_position", "\"Position\" >= 0"));
            entity.HasKey(item => new { item.TenantId, item.CanonicalRecordingId, item.Position });
            entity.Property(item => item.CreditName).HasMaxLength(500);
            entity.Property(item => item.JoinPhrase).HasMaxLength(50).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.CanonicalArtistId, item.CanonicalRecordingId });
            entity.HasOne<CanonicalRecordingRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalRecordingId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CanonicalArtistRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalArtistId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CanonicalReleaseGroupArtistRecord>(entity =>
        {
            entity.ToTable("canonical_release_group_artists", table =>
                table.HasCheckConstraint("CK_canonical_release_group_artist_position", "\"Position\" >= 0"));
            entity.HasKey(item => new { item.TenantId, item.CanonicalReleaseGroupId, item.Position });
            entity.Property(item => item.CreditName).HasMaxLength(500);
            entity.Property(item => item.JoinPhrase).HasMaxLength(50).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.CanonicalArtistId, item.CanonicalReleaseGroupId });
            entity.HasOne<CanonicalReleaseGroupRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalReleaseGroupId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CanonicalArtistRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.CanonicalArtistId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CanonicalCatalogAliasRecord>(entity =>
        {
            entity.ToTable("canonical_catalog_aliases", table =>
                table.HasCheckConstraint("CK_canonical_catalog_alias_hash", "length(\"ExternalIdHash\") = 64"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.EntityKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Namespace).HasMaxLength(100).IsRequired();
            entity.Property(item => item.ExternalId).HasMaxLength(500).IsRequired();
            entity.Property(item => item.ExternalIdHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.Namespace, item.EntityKind, item.ExternalIdHash }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.EntityKind, item.CanonicalEntityId });
            TenantOwned(entity);
        });

        modelBuilder.Entity<CatalogFactRecord>(entity =>
        {
            entity.ToTable("catalog_facts", table =>
            {
                table.HasCheckConstraint("CK_catalog_fact_confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1");
                table.HasCheckConstraint("CK_catalog_fact_payload_hash", "length(\"PayloadSha256\") = 64");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.EntityKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.FieldName).HasMaxLength(100).IsRequired();
            entity.Property(item => item.ValueJson).HasColumnType("jsonb").IsRequired();
            entity.Property(item => item.SourceId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.SourceRevision).HasMaxLength(500);
            entity.Property(item => item.PayloadSha256).HasMaxLength(64).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.EntityKind, item.CanonicalEntityId, item.FieldName });
            entity.HasIndex(item => new { item.TenantId, item.SourceId, item.RefreshAfter });
            TenantOwned(entity);
        });
    }

    private static void TenantOwned<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class
    {
        entity.HasOne<TenantRecord>().WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict);
    }
}
