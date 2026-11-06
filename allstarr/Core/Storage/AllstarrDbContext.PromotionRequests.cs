using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class PromotionRequestRecord
{
    public Guid Id { get; set; }
    public Guid RequesterUserId { get; set; }
    public Guid RecordingId { get; set; }
    public string IdentityKey { get; set; } = string.Empty;
    public string? ReleaseIdentity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int SongCount { get; set; }
    public string? CoverReference { get; set; }
    public string Unit { get; set; } = "album";
    public string State { get; set; } = "pending";
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DeclineNote { get; set; }
    public Guid? RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string RequestJson { get; set; } = string.Empty;
    public string SongsJson { get; set; } = "[]";
    public long Revision { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<PromotionRequestRecord> PromotionRequests => Set<PromotionRequestRecord>();

    private static void ConfigurePromotionRequests(ModelBuilder modelBuilder) => modelBuilder.Entity<PromotionRequestRecord>(entity =>
    {
        entity.ToTable("promotion_requests");
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).ValueGeneratedNever();
        entity.Property(item => item.Revision).IsConcurrencyToken();
        entity.HasIndex(item => new { item.RequesterUserId, item.IdentityKey }).IsUnique()
            .HasFilter("\"State\" IN ('pending', 'approved')");
        entity.HasIndex(item => item.State);
        entity.HasIndex(item => item.RunId);
        entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PromotionRunRecord>().WithMany().HasForeignKey(item => item.RunId).OnDelete(DeleteBehavior.Restrict);
    });
}
