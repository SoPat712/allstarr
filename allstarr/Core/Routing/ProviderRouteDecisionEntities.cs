using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Routing;

public enum ProviderRouteOutcomeStatus
{
    FallbackAdvanced,
    Stopped,
    Succeeded
}

public sealed class ProviderRouteDecisionEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? DurableJobId { get; set; }
    public string RouteKey { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public ProviderCapabilityKind Capability { get; set; }
    public string? LibraryScopeId { get; set; }
    public string? SelectedProviderId { get; set; }
    public Guid? SelectedProviderAccountId { get; set; }
    public string CandidateDecisionsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProviderRouteOutcomeEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RouteDecisionId { get; set; }
    public string OutcomeKey { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public ProviderRouteOutcomeStatus Status { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? NextProviderId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class ProviderRouteDecisionModelConfiguration
{
    public static void ConfigureProviderRouteDecisions(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderRouteDecisionEntity>(entity =>
        {
            entity.ToTable("provider_route_decisions");
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.Id, item.TenantId });
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.RouteKey).HasMaxLength(64).IsRequired();
            entity.Property(item => item.OperationId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Capability).HasConversion<string>().HasMaxLength(100);
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300);
            entity.Property(item => item.SelectedProviderId).HasMaxLength(100);
            entity.Property(item => item.CandidateDecisionsJson).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.RouteKey }).IsUnique()
                .HasDatabaseName("IX_provider_route_decision_key");
            entity.HasIndex(item => new { item.TenantId, item.CorrelationId, item.CreatedAt })
                .HasDatabaseName("IX_provider_route_decision_correlation");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(item => new { item.TenantId, item.ActorUserId })
                .HasPrincipalKey(item => new { item.TenantId, item.Id })
                .HasConstraintName("FK_provider_route_decision_actor")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.DurableJobId)
                .HasConstraintName("FK_provider_route_decision_job")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderAccountRecord>().WithMany()
                .HasForeignKey(item => new { item.SelectedProviderAccountId, item.SelectedProviderId })
                .HasPrincipalKey(item => new { item.Id, item.ProviderId })
                .HasConstraintName("FK_provider_route_decision_account")
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProviderRouteOutcomeEntity>(entity =>
        {
            entity.ToTable("provider_route_outcomes");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.OutcomeKey).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Stage).HasMaxLength(50).IsRequired();
            entity.Property(item => item.ProviderId).HasMaxLength(100);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ReasonCode).HasMaxLength(100).IsRequired();
            entity.Property(item => item.NextProviderId).HasMaxLength(100);
            entity.HasIndex(item => new { item.RouteDecisionId, item.OutcomeKey }).IsUnique()
                .HasDatabaseName("IX_provider_route_outcome_key");
            entity.HasIndex(item => new { item.TenantId, item.CreatedAt })
                .HasDatabaseName("IX_provider_route_outcome_tenant_created");
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderRouteDecisionEntity>().WithMany()
                .HasForeignKey(item => new { item.RouteDecisionId, item.TenantId })
                .HasPrincipalKey(item => new { item.Id, item.TenantId })
                .HasConstraintName("FK_provider_route_outcome_decision")
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProviderAccountRecord>().WithMany()
                .HasForeignKey(item => new { item.ProviderAccountId, item.ProviderId })
                .HasPrincipalKey(item => new { item.Id, item.ProviderId })
                .HasConstraintName("FK_provider_route_outcome_account")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
