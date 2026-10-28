using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class NotificationChannelRecord
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Household { get; set; }
    public Guid DestinationReferenceId { get; set; }
    public Guid? HealthScheduleId { get; set; }
    public long Revision { get; set; }
}

public sealed class ProblemIncidentRecord
{
    public Guid Id { get; set; }
    public string ScopeKey { get; set; } = "";
    public Guid? OwnerUserId { get; set; }
    public string Resource { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DetailJson { get; set; } = "null";
    public string Severity { get; set; } = "warning";
    public string State { get; set; } = "watching";
    public int Transition { get; set; }
    public int Failures { get; set; }
    public int Successes { get; set; }
    public DateTimeOffset? WindowStartedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class NotificationDeliveryRecord
{
    public Guid Id { get; set; }
    public Guid? IncidentId { get; set; }
    public int Transition { get; set; }
    public Guid ChannelId { get; set; }
    public long ChannelRevision { get; set; }
    public Guid JobId { get; set; }
    public Guid? HealthRunId { get; set; }
    public string Event { get; set; } = "opened";
    public string Kind { get; set; } = "";
    public string DetailJson { get; set; } = "null";
    public string Severity { get; set; } = "warning";
    public string Status { get; set; } = "pending";
    public int Attempt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? SafeError { get; set; }
}

public sealed class ProblemAlertConfigurationRecord
{
    public int Id { get; set; }
    public string ThresholdsJson { get; set; } = "{}";
    public string AllowedAuthoritiesJson { get; set; } = "[]";
    public long Revision { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<NotificationChannelRecord> NotificationChannels => Set<NotificationChannelRecord>();
    public DbSet<ProblemIncidentRecord> ProblemIncidents => Set<ProblemIncidentRecord>();
    public DbSet<NotificationDeliveryRecord> NotificationDeliveries => Set<NotificationDeliveryRecord>();
    public DbSet<ProblemAlertConfigurationRecord> ProblemAlertConfigurations => Set<ProblemAlertConfigurationRecord>();

    private static void ConfigureProblemAlerts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationChannelRecord>(entity =>
        {
            entity.ToTable("notification_channels");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.HealthScheduleId).IsUnique();
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SecretReferenceRecord>().WithMany().HasForeignKey(item => item.DestinationReferenceId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProblemIncidentRecord>(entity =>
        {
            entity.ToTable("problem_incidents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ScopeKey, item.Resource, item.Kind }).IsUnique();
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<NotificationDeliveryRecord>(entity =>
        {
            entity.ToTable("notification_deliveries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.HasIndex(item => new { item.IncidentId, item.Transition, item.ChannelId }).IsUnique();
            entity.HasIndex(item => item.JobId).IsUnique();
            entity.HasOne<ProblemIncidentRecord>().WithMany().HasForeignKey(item => item.IncidentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<NotificationChannelRecord>().WithMany().HasForeignKey(item => item.ChannelId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProblemAlertConfigurationRecord>(entity =>
        {
            entity.ToTable("problem_alert_configuration");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Revision).IsConcurrencyToken();
        });
    }
}
