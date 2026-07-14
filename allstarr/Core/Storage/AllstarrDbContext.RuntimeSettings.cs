using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    public DbSet<TenantRuntimeSettingRecord> TenantRuntimeSettings => Set<TenantRuntimeSettingRecord>();

    private static void ConfigureRuntimeSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantRuntimeSettingRecord>(entity =>
        {
            entity.ToTable("tenant_runtime_settings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Key).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ValueType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(item => item.ValueJson).HasMaxLength(4096).IsRequired();
            entity.Property(item => item.Source).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.Key }).IsUnique();
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.UpdatedByUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
