using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class HealthSmokeRunRecord
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string BindingJson { get; set; } = "{}";
    public string ArtifactsJson { get; set; } = "{}";
    public string? CompletedJson { get; set; }
    public string AlertStatus { get; set; } = "none";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<HealthSmokeRunRecord> HealthSmokeRuns => Set<HealthSmokeRunRecord>();

    private static void ConfigureHealth(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HealthSmokeRunRecord>(entity =>
        {
            entity.ToTable("health_smoke_runs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.BindingJson).IsRequired();
            entity.Property(item => item.ArtifactsJson).IsRequired();
            entity.Property(item => item.AlertStatus).HasMaxLength(32).IsRequired();
            entity.HasIndex(item => item.CreatedAt);
            entity.HasIndex(item => item.OwnerUserId);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
