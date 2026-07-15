using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
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

public sealed record ProviderRouteDecisionHandle(Guid Id, Guid TenantId);

public sealed record ProviderRouteExecutionOutcome(
    string OutcomeKey,
    int Sequence,
    string Stage,
    string? ProviderId,
    Guid? ProviderAccountId,
    ProviderRouteOutcomeStatus Status,
    string ReasonCode,
    string? NextProviderId = null);

public interface IProviderRouteDecisionStore
{
    Task<ProviderRouteDecisionHandle> RecordPlanAsync(
        ProviderRouteRequest request,
        ProviderRouteDecisionRecord decision,
        string routeKey,
        CancellationToken cancellationToken = default);

    Task RecordOutcomeAsync(
        ProviderRouteDecisionHandle decision,
        ProviderRouteExecutionOutcome outcome,
        CancellationToken cancellationToken = default);
}

public sealed class DurableProviderRouteDecisionStore(
    IDbContextFactory<AllstarrDbContext> factory,
    IPlatformClock clock) : IProviderRouteDecisionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProviderRouteDecisionHandle> RecordPlanAsync(
        ProviderRouteRequest request,
        ProviderRouteDecisionRecord decision,
        string routeKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        if (string.IsNullOrWhiteSpace(routeKey) || routeKey.Length > 500)
            throw new ArgumentException("A bounded durable route key is required.", nameof(routeKey));
        if (decision.CorrelationId != request.CorrelationId || decision.Capability != request.Capability)
            throw new ArgumentException("The route decision does not belong to the request.", nameof(decision));
        if (request.Library != null && request.Library.TenantId != request.Actor.TenantId)
            throw new ArgumentException("The route library does not belong to the actor tenant.", nameof(request));
        ValidateDecision(decision);

        var candidateDecisionsJson = JsonSerializer.Serialize(decision.Candidates, JsonOptions);
        var hashedRouteKey = ScopedRouteKey(request, routeKey);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<ProviderRouteDecisionEntity>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == request.Actor.TenantId && item.RouteKey == hashedRouteKey, cancellationToken);
        if (existing != null)
            return ExactHandle(existing, request, decision, candidateDecisionsJson);

        var now = clock.UtcNow;
        var record = new ProviderRouteDecisionEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.Actor.TenantId,
            ActorUserId = request.Actor.EffectiveUserId,
            DurableJobId = request.Actor.DurableJobId,
            RouteKey = hashedRouteKey,
            OperationId = request.OperationId,
            CorrelationId = request.CorrelationId,
            Capability = request.Capability,
            LibraryScopeId = request.Library?.ScopeId,
            SelectedProviderId = decision.SelectedProviderId,
            SelectedProviderAccountId = decision.SelectedProviderAccountId,
            CandidateDecisionsJson = candidateDecisionsJson,
            CreatedAt = now
        };
        db.Add(record);
        db.AuditEvents.Add(Audit(
            record.TenantId,
            record.ActorUserId,
            "plan",
            decision.SelectedProviderId == null ? "no-route" : "selected",
            record.CorrelationId,
            new
            {
                routeDecisionId = record.Id,
                record.DurableJobId,
                record.OperationId,
                capability = record.Capability.ToString(),
                record.LibraryScopeId,
                record.SelectedProviderId,
                record.SelectedProviderAccountId,
                candidateCount = decision.Candidates.Count
            },
            now));
        db.OutboxMessages.Add(Outbox(
            record.TenantId,
            "provider-route.planned",
            new
            {
                routeDecisionId = record.Id,
                record.CorrelationId,
                capability = record.Capability.ToString(),
                record.SelectedProviderId,
                outcome = decision.SelectedProviderId == null ? "no-route" : "selected"
            },
            now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new ProviderRouteDecisionHandle(record.Id, record.TenantId);
        }
        catch (DbUpdateException)
        {
            // A retried durable action may race another worker. Return the winner without emitting duplicate audit/outbox rows.
            await using var winnerDb = await factory.CreateDbContextAsync(cancellationToken);
            var winner = await winnerDb.Set<ProviderRouteDecisionEntity>().AsNoTracking().SingleOrDefaultAsync(item =>
                item.TenantId == request.Actor.TenantId && item.RouteKey == hashedRouteKey, cancellationToken);
            if (winner == null) throw;
            return ExactHandle(winner, request, decision, candidateDecisionsJson);
        }
    }

    public async Task RecordOutcomeAsync(
        ProviderRouteDecisionHandle decision,
        ProviderRouteExecutionOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        if (decision.Id == Guid.Empty || decision.TenantId == Guid.Empty)
            throw new ArgumentException("A tenant-scoped route decision is required.", nameof(decision));
        Validate(outcome);
        var hashedOutcomeKey = Hash($"{decision.Id:N}|{outcome.OutcomeKey}");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var route = await db.Set<ProviderRouteDecisionEntity>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == decision.Id && item.TenantId == decision.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("The tenant-scoped route decision does not exist.");
        if (await db.Set<ProviderRouteOutcomeEntity>().AsNoTracking().AnyAsync(item =>
                item.RouteDecisionId == decision.Id && item.OutcomeKey == hashedOutcomeKey, cancellationToken))
            return;

        var now = clock.UtcNow;
        var record = new ProviderRouteOutcomeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = decision.TenantId,
            RouteDecisionId = decision.Id,
            OutcomeKey = hashedOutcomeKey,
            Sequence = outcome.Sequence,
            Stage = NormalizeStage(outcome.Stage),
            ProviderId = NormalizeProvider(outcome.ProviderId),
            ProviderAccountId = outcome.ProviderAccountId,
            Status = outcome.Status,
            ReasonCode = NormalizeCode(outcome.ReasonCode),
            NextProviderId = NormalizeProvider(outcome.NextProviderId),
            CreatedAt = now
        };
        db.Add(record);
        db.AuditEvents.Add(Audit(
            route.TenantId,
            route.ActorUserId,
            "outcome",
            record.Status.ToString().ToLowerInvariant(),
            route.CorrelationId,
            new
            {
                routeDecisionId = route.Id,
                route.DurableJobId,
                record.Sequence,
                record.Stage,
                record.ProviderId,
                record.ProviderAccountId,
                status = record.Status.ToString(),
                record.ReasonCode,
                record.NextProviderId
            },
            now));
        db.OutboxMessages.Add(Outbox(
            route.TenantId,
            "provider-route.outcome-recorded",
            new
            {
                routeDecisionId = route.Id,
                route.CorrelationId,
                record.Sequence,
                record.Stage,
                record.ProviderId,
                status = record.Status.ToString(),
                record.ReasonCode,
                record.NextProviderId
            },
            now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await using var winnerDb = await factory.CreateDbContextAsync(cancellationToken);
            if (!await winnerDb.Set<ProviderRouteOutcomeEntity>().AsNoTracking().AnyAsync(item =>
                    item.RouteDecisionId == decision.Id && item.OutcomeKey == hashedOutcomeKey, cancellationToken))
                throw;
        }
    }

    private static void Validate(ProviderRouteExecutionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (string.IsNullOrWhiteSpace(outcome.OutcomeKey) || outcome.OutcomeKey.Length > 500)
            throw new ArgumentException("A bounded durable route outcome key is required.", nameof(outcome));
        if (outcome.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(outcome));
        _ = NormalizeStage(outcome.Stage);
        if (!Enum.IsDefined(outcome.Status))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        _ = NormalizeCode(outcome.ReasonCode);
        _ = NormalizeProvider(outcome.ProviderId);
        _ = NormalizeProvider(outcome.NextProviderId);
        if (outcome.ProviderAccountId != null && outcome.ProviderId == null)
            throw new ArgumentException("A route outcome account requires its provider.", nameof(outcome));
        var lifecycleValid = outcome.Status switch
        {
            ProviderRouteOutcomeStatus.FallbackAdvanced =>
                outcome.ProviderId != null && outcome.NextProviderId != null &&
                !outcome.ProviderId.Equals(outcome.NextProviderId, StringComparison.Ordinal),
            ProviderRouteOutcomeStatus.Stopped or ProviderRouteOutcomeStatus.Succeeded =>
                outcome.NextProviderId == null,
            _ => false
        };
        if (!lifecycleValid)
            throw new ArgumentException("The route outcome lifecycle is invalid.", nameof(outcome));
    }

    private static void ValidateDecision(ProviderRouteDecisionRecord decision)
    {
        if (decision.Candidates.Count > 256 ||
            decision.Candidates.Select(item => item.ProviderId).Distinct(StringComparer.Ordinal).Count() !=
            decision.Candidates.Count)
            throw new ArgumentException("A route decision has too many or repeated candidates.", nameof(decision));
        foreach (var candidate in decision.Candidates)
        {
            _ = NormalizeProvider(candidate.ProviderId);
            _ = NormalizeCode(candidate.ReasonCode);
            if (!Enum.IsDefined(candidate.Status) || candidate.Priority < 0 || candidate.ProviderAccountId == Guid.Empty)
                throw new ArgumentException("A route candidate decision is invalid.", nameof(decision));
        }
        _ = NormalizeProvider(decision.SelectedProviderId);
        if (decision.SelectedProviderAccountId == Guid.Empty ||
            decision.SelectedProviderAccountId != null && decision.SelectedProviderId == null)
            throw new ArgumentException("The selected provider account shape is invalid.", nameof(decision));
        var selectedValid = decision.SelectedProviderId == null
            ? decision.Candidates.All(item => item.Status == ProviderRouteDecisionStatus.Rejected)
            : decision.Candidates.Any(item =>
                item.ProviderId == decision.SelectedProviderId &&
                item.ProviderAccountId == decision.SelectedProviderAccountId &&
                item.Status == ProviderRouteDecisionStatus.Accepted);
        if (!selectedValid)
            throw new ArgumentException("The selected provider is not an accepted route candidate.", nameof(decision));
    }

    private static string NormalizeStage(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 50)
            throw new ArgumentException("A bounded route stage is required.", nameof(value));
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Any(ch => !(ch is >= 'a' and <= 'z' || char.IsAsciiDigit(ch) || ch == '-')))
            throw new ArgumentException("A normalized route stage is required.", nameof(value));
        return normalized;
    }

    private static string? NormalizeProvider(string? value) => value == null
        ? null
        : ProviderContractValidation.ProviderId(value, nameof(value));

    private static string NormalizeCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100 ||
            value.Trim().Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("A host-authored route reason code is required.", nameof(value));
        return value.Trim().ToLowerInvariant();
    }

    private static string ScopedRouteKey(ProviderRouteRequest request, string routeKey) => Hash(string.Join('|',
        request.Actor.TenantId.ToString("N"),
        request.Actor.EffectiveUserId?.ToString("N") ?? "-",
        request.Actor.DurableJobId?.ToString("N") ?? "-",
        request.Capability.ToString(),
        request.Library?.ScopeId ?? "-",
        request.OperationId,
        routeKey));

    private static ProviderRouteDecisionHandle ExactHandle(
        ProviderRouteDecisionEntity existing,
        ProviderRouteRequest request,
        ProviderRouteDecisionRecord decision,
        string candidateDecisionsJson)
    {
        if (existing.ActorUserId != request.Actor.EffectiveUserId ||
            existing.DurableJobId != request.Actor.DurableJobId ||
            existing.OperationId != request.OperationId ||
            existing.CorrelationId != request.CorrelationId ||
            existing.Capability != request.Capability ||
            existing.LibraryScopeId != request.Library?.ScopeId ||
            existing.SelectedProviderId != decision.SelectedProviderId ||
            existing.SelectedProviderAccountId != decision.SelectedProviderAccountId ||
            existing.CandidateDecisionsJson != candidateDecisionsJson)
            throw new InvalidOperationException(
                "The durable route key is already bound to a different actor, job, capability, library, or decision.");
        return new ProviderRouteDecisionHandle(existing.Id, existing.TenantId);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AuditEventRecord Audit(
        Guid tenantId,
        Guid? actorUserId,
        string action,
        string outcome,
        string correlationId,
        object details,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ActorUserId = actorUserId,
            Category = "provider-route",
            Action = action,
            Outcome = outcome,
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(details, JsonOptions),
            CreatedAt = now
        };

    private static OutboxMessageRecord Outbox(Guid tenantId, string type, object payload, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Type = type,
        PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
        State = OutboxMessageState.Pending,
        AvailableAt = now,
        CreatedAt = now,
        UpdatedAt = now,
        Revision = 1
    };
}
