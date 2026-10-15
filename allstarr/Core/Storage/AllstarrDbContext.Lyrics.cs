using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class RecordingLyricsChoiceRecord
{
    public Guid Id { get; set; }
    public Guid CanonicalRecordingId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long Revision { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<RecordingLyricsChoiceRecord> RecordingLyricsChoices =>
        Set<RecordingLyricsChoiceRecord>();

    private static void ConfigureLyricsChoices(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecordingLyricsChoiceRecord>(entity =>
        {
            entity.ToTable("recording_lyrics_choices", table =>
            {
                table.HasCheckConstraint(
                    "CK_recording_lyrics_choices_revision",
                    "\"Revision\" > 0");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.CanonicalRecordingId).IsRequired();
            entity.Property(item => item.OwnerUserId);
            entity.Property(item => item.SourceId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.CanonicalRecordingId, item.OwnerUserId })
                .IsUnique()
                .HasFilter("\"RevokedAt\" IS NULL AND \"OwnerUserId\" IS NOT NULL")
                .HasDatabaseName("IX_recording_lyrics_choices_personal_active");
            entity.HasIndex(item => item.CanonicalRecordingId)
                .IsUnique()
                .HasFilter("\"RevokedAt\" IS NULL AND \"OwnerUserId\" IS NULL")
                .HasDatabaseName("IX_recording_lyrics_choices_household_active");
            entity.HasIndex(item => new { item.CanonicalRecordingId, item.RevokedAt });
            entity.HasOne<CanonicalRecordingRecord>().WithMany()
                .HasForeignKey(item => item.CanonicalRecordingId)
                .HasPrincipalKey(item => item.Id)
                .HasConstraintName("FK_recording_lyrics_choices_recording")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRecord>().WithMany()
                .HasForeignKey(item => item.OwnerUserId)
                .HasPrincipalKey(item => item.Id)
                .HasConstraintName("FK_recording_lyrics_choices_owner")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
