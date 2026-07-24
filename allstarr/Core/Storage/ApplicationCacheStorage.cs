using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class ApplicationCacheEntryRecord
{
    public string Key { get; set; } = string.Empty;
    public string Category { get; set; } = "ProviderResponse";
    public string Value { get; set; } = string.Empty;
    public int PayloadBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed partial class AllstarrDbContext
{
    private static void ConfigureApplicationCache(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationCacheEntryRecord>(entity =>
        {
            entity.ToTable("application_cache_entries", table =>
                table.HasCheckConstraint(
                    "CK_application_cache_payload_bytes",
                    "\"PayloadBytes\" >= 0 AND \"PayloadBytes\" <= 1048576"));
            entity.HasKey(item => item.Key);
            entity.Property(item => item.Key).HasMaxLength(512);
            entity.Property(item => item.Category).HasMaxLength(50).IsRequired();
            entity.Property(item => item.Value).IsRequired();
            entity.HasIndex(item => item.ExpiresAt)
                .HasDatabaseName("IX_application_cache_expires_at");
            entity.HasIndex(item => new { item.Category, item.UpdatedAt })
                .HasDatabaseName("IX_application_cache_category_updated");
        });
    }
}
