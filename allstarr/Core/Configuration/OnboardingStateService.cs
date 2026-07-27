using System.Data;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Configuration;

public sealed record OnboardingStateSnapshot(
    bool Completed,
    bool SetupOpen,
    bool ShouldRedirectToSetup,
    string SchemaVersion,
    IReadOnlyList<string> CompletedSteps,
    string CompletionSource,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ReopenedAt,
    long Revision,
    IReadOnlyList<string> RecoveryNotices);

public sealed class OnboardingStateException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class OnboardingStateService(
    IDbContextFactory<AllstarrDbContext> factory,
    IPlatformClock clock)
{
    public const string SchemaVersion = "onboarding-v1";
    public const string BackendIdentityStep = "backend-identity";
    public const string LegacyEnvironmentStep = "legacy-environment";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OnboardingStateSnapshot> GetAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var state = await db.OnboardingStates.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.UserId == userId,
            cancellationToken);
        var identityPresent = await HasIdentityAsync(db, tenantId, userId, cancellationToken);
        return Snapshot(state, identityPresent);
    }

    public async Task<OnboardingStateSnapshot> CompleteAsync(
        Guid tenantId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await HasIdentityAsync(db, tenantId, userId, cancellationToken))
        {
            throw new OnboardingStateException(
                "backend_identity_required",
                "Connect and verify a Jellyfin or Subsonic identity before completing setup.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var state = await GetOrCreateAsync(
            db, tenantId, userId, "setup-guide", clock.UtcNow, cancellationToken);
        var alreadyCompleted = state.CompletedAt.HasValue && !state.ReopenedAt.HasValue;
        SetSteps(state, ReadSteps(state).Append(BackendIdentityStep));
        state.CompletionSource = "setup-guide";
        state.CompletedAt ??= clock.UtcNow;
        state.ReopenedAt = null;
        state.UpdatedAt = clock.UtcNow;
        if (!alreadyCompleted)
        {
            state.Revision++;
            AddAudit(db, state, userId, correlationId, "onboarding.complete");
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        return Snapshot(state, identityPresent: true);
    }

    public async Task<OnboardingStateSnapshot> ReopenAsync(
        Guid tenantId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var state = await GetOrCreateAsync(
            db, tenantId, userId, "administrator", clock.UtcNow, cancellationToken);
        if (!state.ReopenedAt.HasValue)
        {
            state.ReopenedAt = clock.UtcNow;
            state.UpdatedAt = clock.UtcNow;
            state.Revision++;
            AddAudit(db, state, userId, correlationId, "onboarding.reopen");
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        return Snapshot(
            state,
            await HasIdentityAsync(db, tenantId, userId, cancellationToken));
    }

    internal static async Task<OnboardingStateRecord> MarkLegacyImportAsync(
        AllstarrDbContext db,
        Guid tenantId,
        Guid userId,
        bool backendIdentityReady,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await db.OnboardingStates.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.UserId == userId,
            cancellationToken);
        if (state == null)
        {
            state = Create(tenantId, userId, "legacy-env-import", now);
            db.OnboardingStates.Add(state);
        }

        var steps = ReadSteps(state).Append(LegacyEnvironmentStep);
        if (backendIdentityReady)
        {
            steps = steps.Append(BackendIdentityStep);
            state.CompletedAt ??= now;
        }
        SetSteps(state, steps);
        state.CompletionSource = "legacy-env-import";
        state.UpdatedAt = now;
        state.Revision++;
        return state;
    }

    private static async Task<OnboardingStateRecord> GetOrCreateAsync(
        AllstarrDbContext db,
        Guid tenantId,
        Guid userId,
        string source,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(
                item => item.TenantId == tenantId && item.Id == userId,
                cancellationToken))
        {
            throw new OnboardingStateException(
                "user_required",
                "The administrator session is not linked to an Allstarr user.");
        }

        var state = await db.OnboardingStates.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.UserId == userId,
            cancellationToken);
        if (state != null)
        {
            return state;
        }

        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO onboarding_states
                 ("Id", "TenantId", "UserId", "SchemaVersion", "CompletedStepsJson",
                  "CompletionSource", "CompletedAt", "ReopenedAt", "CreatedAt", "UpdatedAt", "Revision")
             VALUES
                 ({id}, {tenantId}, {userId}, {SchemaVersion}, '[]',
                  {source}, NULL, NULL, {now.UtcTicks}, {now.UtcTicks}, 1)
             ON CONFLICT ("TenantId", "UserId") DO NOTHING
             """,
            cancellationToken);
        return await db.OnboardingStates.SingleAsync(
            item => item.TenantId == tenantId && item.UserId == userId,
            cancellationToken);
    }

    private static OnboardingStateRecord Create(
        Guid tenantId,
        Guid userId,
        string source,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            SchemaVersion = SchemaVersion,
            CompletionSource = source,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        };

    private static string[] ReadSteps(OnboardingStateRecord state)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(state.CompletedStepsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SetSteps(OnboardingStateRecord state, IEnumerable<string> steps) =>
        state.CompletedStepsJson = JsonSerializer.Serialize(
            steps.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            JsonOptions);

    private static Task<bool> HasIdentityAsync(
        AllstarrDbContext db,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken) =>
        db.BackendIdentities.AnyAsync(
            item => item.TenantId == tenantId && item.UserId == userId,
            cancellationToken);

    private static OnboardingStateSnapshot Snapshot(
        OnboardingStateRecord? state,
        bool identityPresent)
    {
        var steps = state == null ? [] : ReadSteps(state);
        var identityCompleted = steps.Contains(BackendIdentityStep, StringComparer.Ordinal);
        var setupOpen = state?.ReopenedAt.HasValue == true;
        return new(
            state?.CompletedAt.HasValue == true && !setupOpen,
            setupOpen,
            !identityCompleted,
            state?.SchemaVersion ?? SchemaVersion,
            steps,
            state?.CompletionSource ?? "none",
            state?.CompletedAt,
            state?.ReopenedAt,
            state?.Revision ?? 0,
            identityCompleted && !identityPresent ? ["backend_identity_missing"] : []);
    }

    private static void AddAudit(
        AllstarrDbContext db,
        OnboardingStateRecord state,
        Guid userId,
        string correlationId,
        string action) =>
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = state.TenantId,
            ActorUserId = userId,
            Category = "onboarding",
            Action = action,
            Outcome = "succeeded",
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                state.SchemaVersion,
                state.Revision
            }, JsonOptions),
            CreatedAt = state.UpdatedAt
        });
}
