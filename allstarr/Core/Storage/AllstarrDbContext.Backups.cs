using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class BackupArtifactRecord
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public string Origin { get; set; } = "manual";
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Directory { get; set; } = "";
    public string PolicyJson { get; set; } = "{}";
    public string StorageBinding { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? DirectoryDeviceId { get; set; }
    public string? DirectoryFileId { get; set; }
    public string? FileDeviceId { get; set; }
    public string? FileId { get; set; }
    public long Bytes { get; set; }
    public long FileWriteTicks { get; set; }
    public string? Sha256 { get; set; }
    public string SchemaVersion { get; set; } = "";
    public string ApplicationVersion { get; set; } = "";
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public string? LastFailureCode { get; set; }
    public string? LastFailureMessage { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed partial class AllstarrDbContext
{
    public DbSet<BackupArtifactRecord> BackupArtifacts => Set<BackupArtifactRecord>();
    private static void ConfigureBackups(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackupArtifactRecord>(entity =>
        {
            entity.ToTable("backup_artifacts", table => table.HasCheckConstraint("CK_backup_origin", "\"Origin\" IN ('manual','scheduled')"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.JobId).IsUnique();
            entity.HasIndex(item => item.CreatedAt);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.JobId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
