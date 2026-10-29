using Microsoft.EntityFrameworkCore;
using allstarr.Core.ManagedFiles;

namespace allstarr.Core.Storage;

public sealed class PromotionRunRecord
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid JobId { get; set; }
    public string RequestKey { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string PlanJson { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public long SourceLength { get; set; }
    public string Step { get; set; } = "Copy";
    public string Outcome { get; set; } = "Queued";
    public string Reason { get; set; } = "Waiting to copy the complete original.";
    public Guid? ManagedFileId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovalReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class PromotionStepRecord
{
    public Guid RunId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? ResultJson { get; set; }
}

public sealed partial class AllstarrDbContext
{
    private static void ConfigurePromotions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PromotionRunRecord>(entity =>
        {
            entity.ToTable("promotion_runs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.OwnerUserId, item.RequestKey }).IsUnique();
            entity.HasIndex(item => item.JobId).IsUnique();
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.JobId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ManagedFileOwnershipEntity>().WithMany().HasForeignKey(item => item.ManagedFileId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PromotionStepRecord>(entity =>
        {
            entity.ToTable("promotion_steps");
            entity.HasKey(item => new { item.RunId, item.Name });
            entity.HasOne<PromotionRunRecord>().WithMany().HasForeignKey(item => item.RunId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
