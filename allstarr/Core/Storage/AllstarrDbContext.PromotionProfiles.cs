using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class PromotionProfileRecord
{
    public string Key { get; set; } = string.Empty;
    public Guid? OwnerUserId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public long Revision { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<PromotionProfileRecord> PromotionProfiles => Set<PromotionProfileRecord>();

    private static void ConfigurePromotionProfiles(ModelBuilder modelBuilder) => modelBuilder.Entity<PromotionProfileRecord>(entity =>
    {
        entity.ToTable("promotion_profiles");
        entity.HasKey(item => item.Key);
        entity.Property(item => item.Key).HasMaxLength(40);
        entity.Property(item => item.PayloadJson).IsRequired();
        entity.Property(item => item.Revision).IsConcurrencyToken();
        entity.HasIndex(item => item.OwnerUserId).IsUnique();
        entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
    });
}

public static class PromotionProfileModel
{
    public static void ConfigureSnapshot(ModelBuilder modelBuilder) => modelBuilder.Entity("allstarr.Core.Storage.PromotionProfileRecord", entity =>
    {
        entity.Property<string>("Key").HasMaxLength(40).HasColumnType("TEXT");
        entity.Property<Guid?>("OwnerUserId").HasColumnType("TEXT");
        entity.Property<string>("PayloadJson").IsRequired().HasColumnType("TEXT");
        entity.Property<long>("Revision").IsConcurrencyToken().HasColumnType("INTEGER");
        entity.HasKey("Key");
        entity.HasIndex("OwnerUserId").IsUnique();
        entity.HasOne("allstarr.Core.Storage.UserRecord", null).WithMany().HasForeignKey("OwnerUserId").OnDelete(DeleteBehavior.Cascade);
        entity.ToTable("promotion_profiles");
    });
}
