using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class ProviderUserDataRecord
{
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public int? Rating { get; set; }
    public bool? Played { get; set; }
    public DateTimeOffset? HistoryResetAt { get; set; }
    public DateTimeOffset? PlayedAt { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<ProviderUserDataRecord> ProviderUserData => Set<ProviderUserDataRecord>();

    private static void ConfigureProviderUserData(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderUserDataRecord>(entity =>
        {
            entity.ToTable("provider_user_data", table =>
            {
                table.HasCheckConstraint("CK_provider_user_data_rating", "\"Rating\" IS NULL OR (\"Rating\" >= 0 AND \"Rating\" <= 5)");
            });
            entity.HasKey(item => new { item.OwnerUserId, item.Protocol, item.BackendInstanceId, item.ItemId });
            entity.Property(item => item.Protocol).HasMaxLength(32);
            entity.Property(item => item.BackendInstanceId).HasMaxLength(200);
            entity.Property(item => item.ItemId).HasMaxLength(500);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
