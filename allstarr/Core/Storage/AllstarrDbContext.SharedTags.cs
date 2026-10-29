using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class SharedTagProfileRecord
{
    public string Key { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset RefreshedAt { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<SharedTagProfileRecord> SharedTagProfiles => Set<SharedTagProfileRecord>();

    private static void ConfigureSharedTags(ModelBuilder modelBuilder) => modelBuilder.Entity<SharedTagProfileRecord>(entity =>
    {
        entity.ToTable("shared_tag_profiles");
        entity.HasKey(item => item.Key);
        entity.Property(item => item.Key).HasMaxLength(400);
        entity.Property(item => item.PayloadJson).IsRequired();
    });
}
