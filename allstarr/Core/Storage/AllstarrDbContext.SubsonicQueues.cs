using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class SubsonicPlayQueueRecord
{
    public string BackendInstanceId { get; set; } = "";
    public string BackendPrincipalId { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

public sealed partial class AllstarrDbContext
{
    public DbSet<SubsonicPlayQueueRecord> SubsonicPlayQueues => Set<SubsonicPlayQueueRecord>();

    private static void ConfigureSubsonicQueues(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SubsonicPlayQueueRecord>(entity =>
        {
            entity.ToTable("subsonic_play_queues");
            entity.HasKey(item => new { item.BackendInstanceId, item.BackendPrincipalId });
            entity.Property(item => item.BackendInstanceId).HasMaxLength(200);
            entity.Property(item => item.BackendPrincipalId).HasMaxLength(500);
            entity.Property(item => item.PayloadJson).IsRequired();
        });
    }
}
