using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed class PlaybackDeliveryCheckpointEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string SignalKey { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public DateTimeOffset CompletedAt { get; set; }
}
public interface IPlaybackDeliveryCheckpointStore
{
    Task<bool> IsCompletedAsync(Guid tenantId, Guid ownerUserId, string signalKey, string targetId, CancellationToken token);
    Task MarkCompletedAsync(Guid tenantId, Guid ownerUserId, string signalKey, string targetId, CancellationToken token);
}
public sealed class EfPlaybackDeliveryCheckpointStore(IDbContextFactory<AllstarrDbContext> factory) : IPlaybackDeliveryCheckpointStore
{
    public async Task<bool> IsCompletedAsync(Guid tenantId, Guid ownerUserId, string signalKey, string targetId, CancellationToken token)
    { await using var db = await factory.CreateDbContextAsync(token); return await db.Set<PlaybackDeliveryCheckpointEntity>().AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && x.SignalKey == signalKey && x.TargetId == targetId, token); }
    public async Task MarkCompletedAsync(Guid tenantId, Guid ownerUserId, string signalKey, string targetId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (await db.Set<PlaybackDeliveryCheckpointEntity>().AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && x.SignalKey == signalKey && x.TargetId == targetId, token)) return;
        db.Add(new PlaybackDeliveryCheckpointEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            SignalKey = signalKey,
            TargetId = targetId,
            CompletedAt = DateTimeOffset.UtcNow
        });
        try { await db.SaveChangesAsync(token); } catch (DbUpdateException) { }
    }
}
public static class PlaybackDeliveryCheckpointModelConfiguration
{
    public static void ConfigurePlaybackDeliveryCheckpoints(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlaybackDeliveryCheckpointEntity>(entity =>
        {
            entity.ToTable("playback_delivery_checkpoints"); entity.HasKey(x => x.Id); entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.SignalKey).HasMaxLength(64).IsRequired(); entity.Property(x => x.TargetId).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.SignalKey, x.TargetId }).IsUnique().HasDatabaseName("IX_playback_delivery_idempotency");
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.OwnerUserId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
