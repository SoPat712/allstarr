using System.Data;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Jobs;

public sealed record OutboxClaim(
    Guid MessageId,
    string Type,
    JsonElement Payload,
    Guid? TenantId,
    int AttemptNumber,
    int MaxAttempts,
    string WorkerId,
    DateTimeOffset LeaseExpiresAt);

public sealed record OutboxFailureResult(
    bool Terminal,
    int AttemptCount,
    int MaxAttempts);

public sealed class DurableOutbox
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableJobOptions _options;
    private readonly IPlatformClock _clock;

    public DurableOutbox(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableJobOptions options,
        IPlatformClock clock)
    {
        _contextFactory = contextFactory;
        _options = options;
        _clock = clock;
    }

    public async Task<OutboxClaim?> ClaimNextAsync(
        string workerId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var exhausted = await context.OutboxMessages
            .Where(item =>
                item.AttemptCount >= item.MaxAttempts &&
                (item.State == OutboxMessageState.Pending ||
                 (item.State == OutboxMessageState.Delivering && item.LeaseExpiresAt <= now)))
            .ToListAsync(cancellationToken);
        foreach (var item in exhausted)
        {
            item.State = OutboxMessageState.Failed;
            item.FailedAt = now;
            item.LeaseOwner = null;
            item.LeaseExpiresAt = null;
            item.LastErrorCode = "outbox_attempts_exhausted";
            item.LastErrorMessage = "Outbox delivery exhausted its configured attempt budget.";
            item.UpdatedAt = now;
            item.Revision++;
        }

        var message = await context.OutboxMessages
            .Where(item =>
                item.AttemptCount < item.MaxAttempts &&
                ((item.State == OutboxMessageState.Pending && item.AvailableAt <= now) ||
                 (item.State == OutboxMessageState.Delivering && item.LeaseExpiresAt <= now)))
            .OrderBy(item => item.AvailableAt)
            .ThenBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (message == null)
        {
            if (exhausted.Count > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            if (exhausted.Count > 0)
            {
                PlatformDiagnostics.OutboxTerminalFailed.Add(
                    exhausted.Count,
                    new KeyValuePair<string, object?>("failure.reason", "attempts_exhausted"));
            }

            return null;
        }

        message.State = OutboxMessageState.Delivering;
        message.AttemptCount++;
        message.LeaseOwner = workerId;
        message.LeaseExpiresAt = now.AddSeconds(_options.LeaseSeconds);
        message.UpdatedAt = now;
        message.Revision++;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (exhausted.Count > 0)
        {
            PlatformDiagnostics.OutboxTerminalFailed.Add(
                exhausted.Count,
                new KeyValuePair<string, object?>("failure.reason", "attempts_exhausted"));
        }
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return new OutboxClaim(
            message.Id,
            message.Type,
            payload.RootElement.Clone(),
            message.TenantId,
            message.AttemptCount,
            message.MaxAttempts,
            workerId,
            message.LeaseExpiresAt.Value);
    }

    public async Task MarkDeliveredAsync(
        OutboxClaim claim,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var message = await OwnedMessage(context, claim, cancellationToken);
        message.State = OutboxMessageState.Delivered;
        message.DeliveredAt = now;
        message.FailedAt = null;
        message.LeaseOwner = null;
        message.LeaseExpiresAt = null;
        message.LastErrorCode = null;
        message.LastErrorMessage = null;
        message.UpdatedAt = now;
        message.Revision++;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<OutboxFailureResult> MarkFailedAsync(
        OutboxClaim claim,
        string errorCode,
        string? safeMessage,
        bool terminal = false,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var message = await OwnedMessage(context, claim, cancellationToken);
        var isTerminal = terminal || message.AttemptCount >= message.MaxAttempts;
        message.State = isTerminal ? OutboxMessageState.Failed : OutboxMessageState.Pending;
        message.AvailableAt = isTerminal
            ? now
            : now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(message.AttemptCount, 8))));
        message.FailedAt = isTerminal ? now : null;
        message.LeaseOwner = null;
        message.LeaseExpiresAt = null;
        message.LastErrorCode = SafeOperationalText.Sanitize(errorCode, 100);
        message.LastErrorMessage = SafeOperationalText.Sanitize(safeMessage);
        message.UpdatedAt = now;
        message.Revision++;
        await context.SaveChangesAsync(cancellationToken);
        PlatformDiagnostics.OutboxDeliveryFailed.Add(
            1,
            new KeyValuePair<string, object?>("event.type", message.Type),
            new KeyValuePair<string, object?>("failure.terminal", isTerminal));
        if (isTerminal)
        {
            PlatformDiagnostics.OutboxTerminalFailed.Add(
                1,
                new KeyValuePair<string, object?>("event.type", message.Type));
        }

        return new OutboxFailureResult(isTerminal, message.AttemptCount, message.MaxAttempts);
    }

    private static async Task<OutboxMessageRecord> OwnedMessage(
        AllstarrDbContext context,
        OutboxClaim claim,
        CancellationToken cancellationToken)
    {
        var message = await context.OutboxMessages.SingleAsync(
            item => item.Id == claim.MessageId,
            cancellationToken);
        if (message.State != OutboxMessageState.Delivering ||
            message.LeaseOwner != claim.WorkerId ||
            message.AttemptCount != claim.AttemptNumber)
        {
            throw new InvalidOperationException("The worker no longer owns this outbox lease.");
        }

        return message;
    }
}
