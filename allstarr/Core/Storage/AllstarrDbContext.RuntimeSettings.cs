using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    public DbSet<RuntimeSettingRecord> RuntimeSettings => Set<RuntimeSettingRecord>();

    private static void ConfigureRuntimeSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RuntimeSettingRecord>(entity =>
        {
            entity.ToTable("runtime_settings", table => table.HasCheckConstraint(
                "CK_runtime_settings_personal_keys",
                "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel')"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Key).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ValueType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(item => item.ValueJson).HasMaxLength(4096).IsRequired();
            entity.Property(item => item.Source).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.Key).IsUnique()
                .HasFilter("\"OwnerUserId\" IS NULL")
                .HasDatabaseName("IX_runtime_settings_household_key");
            entity.HasIndex(item => new { item.OwnerUserId, item.Key }).IsUnique()
                .HasFilter("\"OwnerUserId\" IS NOT NULL")
                .HasDatabaseName("IX_runtime_settings_personal_key");
            entity.HasOne<UserRecord>().WithMany()
                .HasForeignKey(item => item.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRecord>().WithMany()
                .HasForeignKey(item => item.UpdatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
