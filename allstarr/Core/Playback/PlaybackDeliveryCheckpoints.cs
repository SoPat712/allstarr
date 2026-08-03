using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed class PlaybackDeliveryCheckpointEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string? OccurrenceKey { get; set; }
    public string SignalKey { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public PlaybackScrobbleDeliveryKind Kind { get; set; } = PlaybackScrobbleDeliveryKind.Completed;
    public ScopedPlaybackScrobbleOutcome State { get; set; } = ScopedPlaybackScrobbleOutcome.Delivered;
    public string? ProviderCode { get; set; }
    public string? SafeMessage { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset? RetryAfter { get; set; }
    public bool RequiresReauthentication { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public interface IPlaybackDeliveryCheckpointStore
{
    Task<bool> IsCompletedAsync(Guid tenantId, Guid ownerUserId, string signalKey, string targetId, CancellationToken token);
    Task RecordAsync(Guid tenantId, Guid ownerUserId, string occurrenceKey, string signalKey,
        PlaybackScrobbleDeliveryKind kind, string targetId, ScopedPlaybackScrobbleResult result,
        CancellationToken token);
}
public sealed class EfPlaybackDeliveryCheckpointStore(IDbContextFactory<AllstarrDbContext> factory) : IPlaybackDeliveryCheckpointStore
{
    public async Task<bool> IsCompletedAsync(Guid tenantId, Guid ownerUserId, string signalKey, string targetId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Set<PlaybackDeliveryCheckpointEntity>().AsNoTracking().AnyAsync(x =>
            x.TenantId == tenantId && x.OwnerUserId == ownerUserId && x.SignalKey == signalKey &&
            x.TargetId == targetId && (x.State == ScopedPlaybackScrobbleOutcome.Delivered ||
                                      x.State == ScopedPlaybackScrobbleOutcome.Ignored), token);
    }
    public async Task RecordAsync(Guid tenantId, Guid ownerUserId, string occurrenceKey, string signalKey,
        PlaybackScrobbleDeliveryKind kind, string targetId, ScopedPlaybackScrobbleResult result,
        CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var checkpoint = await db.Set<PlaybackDeliveryCheckpointEntity>().SingleOrDefaultAsync(x =>
                x.TenantId == tenantId && x.OwnerUserId == ownerUserId && x.SignalKey == signalKey &&
                x.TargetId == targetId, token);
            var added = checkpoint == null;
            checkpoint ??= new PlaybackDeliveryCheckpointEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                SignalKey = signalKey,
                TargetId = targetId
            };
            if (added || checkpoint.State is not (ScopedPlaybackScrobbleOutcome.Delivered or ScopedPlaybackScrobbleOutcome.Ignored) ||
                result.Outcome is ScopedPlaybackScrobbleOutcome.Delivered or ScopedPlaybackScrobbleOutcome.Ignored)
            {
                var now = DateTimeOffset.UtcNow;
                checkpoint.OccurrenceKey = occurrenceKey;
                checkpoint.Kind = kind;
                checkpoint.State = result.Outcome;
                checkpoint.ProviderCode = result.ProviderCode;
                checkpoint.SafeMessage = result.SafeMessage;
                checkpoint.DetailsJson = result.DetailsJson;
                checkpoint.RetryAfter = result.RetryAfter is { } delay ? now.Add(delay) : null;
                checkpoint.RequiresReauthentication = result.RequiresReauthentication;
                checkpoint.UpdatedAt = now;
            }
            if (added) db.Add(checkpoint);
            try { await db.SaveChangesAsync(token); return; }
            catch (DbUpdateException) when (added && attempt == 0) { }
        }
    }
}
public static class PlaybackDeliveryCheckpointModelConfiguration
{
    public static void ConfigurePlaybackDeliveryCheckpoints(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlaybackDeliveryCheckpointEntity>(entity =>
        {
            entity.ToTable("playback_delivery_checkpoints", table => table.HasCheckConstraint(
                "CK_playback_delivery_checkpoint_state",
                "\"State\" IN ('Delivered', 'Ignored', 'Retrying', 'PermanentFailure')"));
            entity.HasKey(x => x.Id); entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.OccurrenceKey).HasMaxLength(64);
            entity.Property(x => x.SignalKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TargetId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ProviderCode).HasMaxLength(100);
            entity.Property(x => x.SafeMessage).HasMaxLength(500);
            entity.Property(x => x.DetailsJson).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.SignalKey, x.TargetId }).IsUnique().HasDatabaseName("IX_playback_delivery_idempotency");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.OccurrenceKey, x.Kind })
                .HasDatabaseName("IX_playback_delivery_occurrence_status");
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.OwnerUserId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
