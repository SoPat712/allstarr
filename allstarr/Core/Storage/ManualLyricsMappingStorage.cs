using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class ManualLyricsMappingRecord
{
    public Guid Id { get; set; }
    public string IdentityHash { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public int LyricsId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal static class ManualLyricsMappingModelConfiguration
{
    public static void ConfigureManualLyricsMappings(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManualLyricsMappingRecord>(entity =>
        {
            entity.ToTable("manual_lyrics_mappings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.IdentityHash).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Artist).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Album).HasMaxLength(500);
            entity.HasIndex(item => item.IdentityHash).IsUnique();
            entity.HasIndex(item => item.UpdatedAt);
        });
    }
}
