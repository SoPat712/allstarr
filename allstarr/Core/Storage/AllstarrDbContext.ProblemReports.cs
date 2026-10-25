using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class ProblemReportRecord
{
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? SubjectUserId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestFingerprint { get; set; } = "";
    public string Kind { get; set; } = "other";
    public string Note { get; set; } = "";
    public string ReporterName { get; set; } = "";
    public string ContextJson { get; set; } = "{}";
    public string Status { get; set; } = "new";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? StatusChangedByUserId { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed partial class AllstarrDbContext
{
    public DbSet<ProblemReportRecord> ProblemReports => Set<ProblemReportRecord>();

    private static void ConfigureProblemReports(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProblemReportRecord>(entity =>
        {
            entity.ToTable("problem_reports", table =>
            {
                table.HasCheckConstraint("CK_problem_report_status", "\"Status\" IN ('new','seen','fixed')");
                table.HasCheckConstraint("CK_problem_report_kind", "\"Kind\" IN ('playback','seek','wrong-match','wrong-lyrics','metadata','other')");
                table.HasCheckConstraint("CK_problem_report_bounds", "length(\"Note\") BETWEEN 1 AND 2000 AND length(\"ReporterName\") BETWEEN 1 AND 200 AND length(\"RequestFingerprint\")=64 AND length(CAST(\"ContextJson\" AS BLOB))<=32768 AND \"Revision\">0");
                table.HasCheckConstraint("CK_problem_report_context", "json_valid(\"ContextJson\") AND coalesce(json_extract(\"ContextJson\",'$.schemaVersion')=1,0)");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.RequestFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Kind).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(16).IsRequired();
            entity.Property(item => item.Note).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.ReporterName).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ContextJson).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ActorUserId, item.RequestId }).IsUnique();
            entity.HasIndex(item => new { item.CreatedAt, item.Id });
            entity.HasIndex(item => new { item.Status, item.CreatedAt, item.Id });
            entity.HasIndex(item => item.SubjectUserId);
            entity.HasIndex(item => item.StatusChangedByUserId);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.SubjectUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.StatusChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
