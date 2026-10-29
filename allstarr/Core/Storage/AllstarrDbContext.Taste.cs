using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class TasteProfileRecord
{
    public Guid OwnerUserId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public long Revision { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<TasteProfileRecord> TasteProfiles => Set<TasteProfileRecord>();

    private static void ConfigureTaste(ModelBuilder modelBuilder) => modelBuilder.Entity<TasteProfileRecord>(entity =>
    {
        entity.ToTable("taste_profiles");
        entity.HasKey(item => item.OwnerUserId);
        entity.Property(item => item.OwnerUserId).ValueGeneratedNever();
        entity.Property(item => item.PayloadJson).IsRequired();
        entity.Property(item => item.Revision).IsConcurrencyToken();
        entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
    });
}
