using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class RecordingLyricsRefreshRecord
{
    public Guid OwnerUserId { get; set; }
    public Guid CanonicalRecordingId { get; set; }
    public string SourceId { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string? PreviousJson { get; set; }
    public string Reason { get; set; } = "";
    public double Score { get; set; }
    public long? TargetDurationMilliseconds { get; set; }
    public bool WordSynced { get; set; }
    public int Misses { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public DateTimeOffset NextCheckAt { get; set; }
    public long Revision { get; set; }
}

public sealed class LyricsRefreshAvailabilityRecord
{
    public string Key { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public long Revision { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<RecordingLyricsRefreshRecord> RecordingLyricsRefreshes => Set<RecordingLyricsRefreshRecord>();
    public DbSet<LyricsRefreshAvailabilityRecord> LyricsRefreshAvailability => Set<LyricsRefreshAvailabilityRecord>();

    private static void ConfigureLyricsRefresh(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecordingLyricsRefreshRecord>(entity =>
        {
            entity.ToTable("recording_lyrics_refreshes");
            entity.HasKey(item => new { item.OwnerUserId, item.CanonicalRecordingId });
            entity.Property(item => item.SourceId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.Reason).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.NextCheckAt);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CanonicalRecordingRecord>().WithMany().HasForeignKey(item => item.CanonicalRecordingId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<LyricsRefreshAvailabilityRecord>(entity =>
        {
            entity.ToTable("lyrics_refresh_availability");
            entity.HasKey(item => item.Key);
            entity.Property(item => item.Key).HasMaxLength(100);
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
        });
    }
}
