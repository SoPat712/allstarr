using System.Data;
using System.Text;
using System.Text.Json;
using allstarr.Core.Configuration;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Health;

public sealed record NotificationChannelRequest(string DisplayName, string Kind, bool Enabled, bool Household,
    string? Destination = null, long ExpectedRevision = 0);
public sealed record NotificationChannelView(Guid Id, string DisplayName, string Kind, bool Enabled, bool Household,
    bool HasDestination, long Revision);
public sealed record ProblemAlertThresholds(long WarningBytes = 5368709120, double WarningPercent = 5,
    long CriticalBytes = 1073741824, double CriticalPercent = 1, long HysteresisBytes = 1073741824,
    double HysteresisPercent = 1, int CooldownMinutes = 30);
public sealed record ProblemAlertEvidence(string Kind, string Resource, Guid? OwnerUserId, bool Active,
    string Severity, DateTimeOffset ObservedAt, int OpenAfter = 1, int RecoverAfter = 1, ProblemAlertDetail? Detail = null);
public sealed record ProblemAlertDetail(string[]? Roots = null, long? FreeBytes = null, long? TotalBytes = null,
    string[]? HealthSteps = null, string? Protocol = null);
public sealed record ProblemAlertDeliveryPayload(Guid DeliveryId);
public sealed record ProblemAlertMessage(int Version, Guid DeliveryId, Guid? IncidentId, string Event,
    string Kind, string Severity, string Message, string Action, DateTimeOffset OccurredAt, ProblemAlertDetail? Detail = null);

public sealed class ProblemAlertService(IDbContextFactory<AllstarrDbContext> factory, EncryptedSecretStore secrets,
    DurableJobQueue jobs, IPlatformClock clock, BackendSelectionAuthority backend, IdentityOptions identity)
{
    public const string DeliveryJobType = "problem-alert.delivery";
    public const string DestinationPurpose = "problem-alert-destination";
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string Deployment => $"{backend.EffectiveValue.ToLowerInvariant()}:{identity.BackendInstanceId}";

    internal async Task<UserRecord> RequireSessionAsync(AllstarrDbContext db, AdminAuthSession session, CancellationToken token)
    {
        if (!session.BackendType.Equals(backend.EffectiveValue, StringComparison.OrdinalIgnoreCase) ||
            session.BackendInstanceId != identity.BackendInstanceId || session.AllstarrUserId == null)
            throw new UnauthorizedAccessException();
        return await db.Users.SingleOrDefaultAsync(user => user.Id == session.AllstarrUserId && user.Enabled &&
            user.BackendType == backend.EffectiveValue.ToLower() && user.BackendInstanceId == identity.BackendInstanceId &&
            user.IsAdmin == session.IsAdministrator, token) ?? throw new UnauthorizedAccessException();
    }

    internal async Task<bool> ChannelAuthorizedAsync(AllstarrDbContext db, NotificationChannelRecord channel, CancellationToken token)
    {
        if (!channel.Enabled) return false;
        return await db.Users.AnyAsync(user => user.Id == channel.OwnerUserId && user.Enabled &&
            (!channel.Household || user.IsAdmin) && user.BackendType == backend.EffectiveValue.ToLower() &&
            user.BackendInstanceId == identity.BackendInstanceId, token);
    }

    public async Task<object> ReadAsync(AdminAuthSession session, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await RequireSessionAsync(db, session, token);
        var channels = await db.NotificationChannels.AsNoTracking().Where(item => item.OwnerUserId == user.Id && item.HealthScheduleId == null)
            .OrderBy(item => item.DisplayName).ToArrayAsync(token);
        var ids = channels.Select(item => item.Id).ToArray();
        var rows = await db.NotificationDeliveries.AsNoTracking().Where(item => ids.Contains(item.ChannelId))
            .OrderByDescending(item => item.CreatedAt).Take(100).ToArrayAsync(token);
        var jobIds = rows.Select(item => item.JobId).ToArray();
        var states = await db.Jobs.AsNoTracking().Where(item => jobIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.State, token);
        var deliveries = rows.Select(item =>
        {
            var status = states.GetValueOrDefault(item.JobId) switch
            {
                DurableJobState.Failed => "failed",
                DurableJobState.Cancelled => "cancelled",
                _ => item.Status
            };
            return new
            {
                item.Id,
                item.IncidentId,
                item.ChannelId,
                item.Event,
                item.Kind,
                item.Severity,
                status,
                item.Attempt,
                nextAttemptAt = status is "failed" or "cancelled" ? null : item.NextAttemptAt,
                safeError = status == "failed" ? item.SafeError ?? "The notification work could not complete." : item.SafeError,
                item.CreatedAt
            };
        }).ToArray();
        var config = user.IsAdmin ? await db.ProblemAlertConfigurations.AsNoTracking().SingleOrDefaultAsync(token) : null;
        return new
        {
            channels = channels.Select(View),
            deliveries,
            thresholds = user.IsAdmin ? ParseThresholds(config) : null,
            permittedDestinations = user.IsAdmin ? ParseAuthorities(config) : null,
            revision = config?.Revision ?? 0
        };
    }

    public async Task<NotificationChannelView> SaveChannelAsync(AdminAuthSession session, Guid? id,
        NotificationChannelRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 80 || request.DisplayName.Any(char.IsControl) ||
            request.Kind is not ("ntfy" or "discord" or "webhook")) throw new ArgumentException("Enter a channel name and supported channel kind.");
        if (request.Destination != null) ValidateDestination(request.Destination);
        await _gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await RequireSessionAsync(db, session, token);
            if (request.Household && !user.IsAdmin) throw new UnauthorizedAccessException();
            if (request.Destination != null && !await DestinationPermittedAsync(db, user.Id, ValidateDestination(request.Destination), token))
                throw new ArgumentException("An administrator must permit this destination before it can be used.");
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var channel = id == null ? null : await db.NotificationChannels.SingleOrDefaultAsync(item => item.Id == id &&
                item.OwnerUserId == user.Id && item.HealthScheduleId == null, token) ?? throw new UnauthorizedAccessException();
            if ((channel?.Revision ?? 0) != request.ExpectedRevision) throw new InvalidOperationException("Channel settings changed. Reload before saving.");
            if (channel == null && request.Destination == null) throw new ArgumentException("Enter a destination.");
            if (channel == null && await db.NotificationChannels.CountAsync(item => item.OwnerUserId == user.Id, token) >= 12)
                throw new ArgumentException("The channel limit has been reached.");
            var reference = channel?.DestinationReferenceId ?? Guid.Empty;
            if (request.Destination != null)
            {
                var stored = await secrets.StoreWithinTransactionAsync(db, user.Id, DestinationPurpose,
                    Encoding.UTF8.GetBytes(request.Destination.Trim()), cancellationToken: token);
                if (reference != Guid.Empty)
                    (await db.SecretReferences.SingleAsync(item => item.Id == reference, token)).RevokedAt = clock.UtcNow;
                reference = stored.Id;
            }
            if (channel == null)
            {
                channel = new() { Id = Guid.CreateVersion7(), OwnerUserId = user.Id };
                db.NotificationChannels.Add(channel);
            }
            channel.DisplayName = request.DisplayName.Trim();
            channel.Kind = request.Kind;
            channel.Enabled = request.Enabled;
            channel.Household = request.Household;
            channel.DestinationReferenceId = reference;
            channel.Revision++;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return View(channel);
        }
        finally { _gate.Release(); }
    }

    internal static Uri ValidateDestination(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Fragment.Length != 0 || !OutboundRequestGuard.TryCreateConfiguredServiceUri(uri.GetLeftPart(UriPartial.Path), out _, out _))
            throw new ArgumentException("Enter an HTTP or HTTPS destination without a fragment or embedded username.");
        return uri;
    }

    internal async Task<bool> DestinationPermittedAsync(AllstarrDbContext db, Guid owner, Uri uri, CancellationToken token)
    {
        if (await db.Users.AnyAsync(user => user.Id == owner && user.Enabled && user.IsAdmin, token)) return true;
        var row = await db.ProblemAlertConfigurations.AsNoTracking().SingleOrDefaultAsync(token);
        return ParseAuthorities(row).Contains(uri.Authority, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ParseAuthorities(ProblemAlertConfigurationRecord? row) => row == null ? [] :
        JsonSerializer.Deserialize<string[]>(row.AllowedAuthoritiesJson, Json) ?? [];

    public async Task SaveNetworkAsync(AdminAuthSession session, string[] authorities, long expectedRevision, CancellationToken token)
    {
        if (authorities.Length > 32 || authorities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 255 ||
            value.Any(character => character is '/' or '?' or '#' or '@' || char.IsWhiteSpace(character)) ||
            !Uri.TryCreate("https://" + value, UriKind.Absolute, out _)))
            throw new ArgumentException("Enter up to 32 permitted host names with optional ports.");
        await _gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            if (!(await RequireSessionAsync(db, session, token)).IsAdmin) throw new UnauthorizedAccessException();
            var row = await db.ProblemAlertConfigurations.SingleOrDefaultAsync(token);
            if ((row?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("Alert settings changed. Reload before saving.");
            if (row == null) { row = new() { Id = 1 }; db.ProblemAlertConfigurations.Add(row); }
            row.AllowedAuthoritiesJson = JsonSerializer.Serialize(authorities.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Json);
            row.Revision++;
            await db.SaveChangesAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveThresholdsAsync(AdminAuthSession session, ProblemAlertThresholds thresholds, long expectedRevision, CancellationToken token)
    {
        if (thresholds.CriticalBytes < 0 || thresholds.WarningBytes < thresholds.CriticalBytes || thresholds.WarningBytes > 1L << 50 ||
            !double.IsFinite(thresholds.WarningPercent) || !double.IsFinite(thresholds.CriticalPercent) ||
            thresholds.CriticalPercent < 0 || thresholds.WarningPercent < thresholds.CriticalPercent || thresholds.WarningPercent > 99 ||
            thresholds.HysteresisBytes < 0 || thresholds.HysteresisBytes > 1L << 50 || !double.IsFinite(thresholds.HysteresisPercent) ||
            thresholds.HysteresisPercent is < 0 or > 50 || thresholds.CooldownMinutes is < 1 or > 1440)
            throw new ArgumentException("Enter valid disk thresholds, hysteresis and cooldown.");
        await _gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            if (!(await RequireSessionAsync(db, session, token)).IsAdmin) throw new UnauthorizedAccessException();
            var row = await db.ProblemAlertConfigurations.SingleOrDefaultAsync(token);
            if ((row?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("Alert settings changed. Reload before saving.");
            if (row == null) { row = new() { Id = 1 }; db.ProblemAlertConfigurations.Add(row); }
            row.ThresholdsJson = JsonSerializer.Serialize(thresholds, Json);
            row.Revision++;
            await db.SaveChangesAsync(token);
        }
        finally { _gate.Release(); }
    }

    internal static ProblemAlertThresholds ParseThresholds(ProblemAlertConfigurationRecord? row) =>
        row == null ? new() : JsonSerializer.Deserialize<ProblemAlertThresholds>(row.ThresholdsJson, Json) ?? new();

    public async Task<IReadOnlyList<object>> ReadIncidentsAsync(AdminAuthSession session, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await RequireSessionAsync(db, session, token);
        var scope = Deployment + ":";
        var rows = await db.ProblemIncidents.AsNoTracking().Where(item => item.ScopeKey.StartsWith(scope) && item.Transition > 0 &&
            (item.OwnerUserId == user.Id || item.OwnerUserId == null && user.IsAdmin))
            .OrderByDescending(item => item.LastObservedAt).Take(100).ToArrayAsync(token);
        return rows.Select(item => (object)new
        {
            item.Id,
            item.Kind,
            item.Severity,
            item.State,
            item.OpenedAt,
            item.ResolvedAt,
            item.LastObservedAt,
            item.Revision,
            resource = item.Resource,
            detail = JsonSerializer.Deserialize<ProblemAlertDetail>(item.DetailJson, Json),
            message = Message(item.Kind, item.State),
            action = Action(item.Kind)
        }).ToArray();
    }

    public async Task ObserveAsync(ProblemAlertEvidence evidence, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await ObserveWithinAsync(db, evidence, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        finally { _gate.Release(); }
    }

    internal async Task ObserveWithinAsync(AllstarrDbContext db, ProblemAlertEvidence evidence, CancellationToken token, Guid? healthRunId = null, Guid? healthScheduleId = null)
    {
        if (evidence.Kind is not ("provider-login" or "source-failure" or "disk-space" or "health-check") ||
            evidence.Resource.Length is < 1 or > 300 || evidence.Severity is not ("warning" or "critical"))
            throw new ArgumentException("Invalid problem observation.");
        var scope = Deployment + ":" + (evidence.OwnerUserId?.ToString("N") ?? "household");
        var row = await db.ProblemIncidents.SingleOrDefaultAsync(item => item.ScopeKey == scope &&
            item.Resource == evidence.Resource && item.Kind == evidence.Kind, token);
        if (row == null)
        {
            if (!evidence.Active) return;
            row = new()
            {
                Id = Guid.CreateVersion7(),
                ScopeKey = scope,
                OwnerUserId = evidence.OwnerUserId,
                Resource = evidence.Resource,
                Kind = evidence.Kind
            };
            db.ProblemIncidents.Add(row);
        }
        if (row.LastObservedAt >= evidence.ObservedAt) return;
        row.LastObservedAt = evidence.ObservedAt;
        row.DetailJson = JsonSerializer.Serialize(evidence.Detail, Json);
        row.Revision++;
        string? change = null;
        var lifecycleChanged = false;
        if (evidence.Active)
        {
            row.Successes = 0;
            if (row.WindowStartedAt?.AddMinutes(10) < evidence.ObservedAt) row.Failures = 0;
            if (row.Failures == 0) row.WindowStartedAt = evidence.ObservedAt;
            row.Failures = Math.Min(100, row.Failures + 1);
            if (row.State == "open")
            {
                if (row.Severity != "critical" && evidence.Severity == "critical") change = "escalated";
                else if (row.OpenedAt > row.LastNotifiedAt)
                {
                    var thresholds = ParseThresholds(await db.ProblemAlertConfigurations.AsNoTracking().SingleOrDefaultAsync(token));
                    if (row.LastNotifiedAt!.Value.AddMinutes(thresholds.CooldownMinutes) <= clock.UtcNow) change = "opened";
                }
            }
            else if (row.Failures >= evidence.OpenAfter)
            {
                var thresholds = ParseThresholds(await db.ProblemAlertConfigurations.AsNoTracking().SingleOrDefaultAsync(token));
                var notify = row.LastNotifiedAt == null || row.LastNotifiedAt.Value.AddMinutes(thresholds.CooldownMinutes) <= clock.UtcNow ||
                    evidence.Severity == "critical" && row.Severity != "critical";
                row.State = "open";
                row.OpenedAt = clock.UtcNow;
                row.ResolvedAt = null;
                row.Severity = evidence.Severity;
                lifecycleChanged = true;
                if (notify) change = "opened";
            }
            row.Severity = evidence.Severity == "critical" || row.State != "open" ? evidence.Severity : row.Severity;
        }
        else
        {
            row.Failures = 0;
            row.WindowStartedAt = null;
            row.Successes = Math.Min(100, row.Successes + 1);
            if (row.State == "open" && row.Successes >= evidence.RecoverAfter)
            {
                var notifiedOpening = row.OpenedAt <= row.LastNotifiedAt;
                row.State = "resolved";
                row.ResolvedAt = clock.UtcNow;
                lifecycleChanged = true;
                if (notifiedOpening) change = "resolved";
            }
        }
        if (change == null)
        {
            if (lifecycleChanged) row.Transition++;
            return;
        }
        row.Transition++;
        row.LastNotifiedAt = clock.UtcNow;
        var channels = await db.NotificationChannels.Where(item => item.Enabled &&
            (item.HealthScheduleId == null || evidence.Kind == "health-check" && item.HealthScheduleId == healthScheduleId) &&
            (evidence.OwnerUserId == null ? item.Household : !item.Household && item.OwnerUserId == evidence.OwnerUserId)).ToArrayAsync(token);
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in channels.OrderByDescending(item => item.HealthScheduleId.HasValue))
        {
            if (!await ChannelAuthorizedAsync(db, channel, token)) continue;
            var destinationKey = channel.Id.ToString("N");
            try
            {
                using var destination = await secrets.OpenAsync(channel.DestinationReferenceId,
                    channel.HealthScheduleId == null ? new(channel.OwnerUserId, DestinationPurpose) :
                        new(null, HealthSmokeService.WebhookPurpose, AllowShared: true), token);
                destinationKey = channel.Kind + ":" + ValidateDestination(destination.ReadUtf8()).AbsoluteUri;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { }
            if (destinations.Add(destinationKey)) await EnqueueWithinAsync(db, channel, row, change, token, healthRunId);
        }
    }

    private async Task<Guid> EnqueueWithinAsync(AllstarrDbContext db, NotificationChannelRecord channel,
        ProblemIncidentRecord? incident, string change, CancellationToken token, Guid? healthRunId = null)
    {
        var delivery = new NotificationDeliveryRecord
        {
            Id = Guid.CreateVersion7(),
            IncidentId = incident?.Id,
            ChannelId = channel.Id,
            ChannelRevision = channel.Revision,
            Transition = incident?.Transition ?? 0,
            Event = change,
            Kind = incident?.Kind ?? "test",
            Severity = incident?.Severity ?? "warning",
            CreatedAt = clock.UtcNow,
            HealthRunId = healthRunId,
            DetailJson = incident?.DetailJson ?? "null"
        };
        var queued = await jobs.EnqueueInExistingTransactionAsync(db,
            new DurableJobEnqueueRequest<ProblemAlertDeliveryPayload>(DeliveryJobType, $"problem-alert:{delivery.Id:N}",
                new(delivery.Id), channel.OwnerUserId, MaxAttempts: 5), token);
        delivery.JobId = queued.JobId;
        db.NotificationDeliveries.Add(delivery);
        return delivery.Id;
    }

    internal async Task ReconcileHealthChannelWithinAsync(AllstarrDbContext db, Guid scheduleId, Guid? reference, CancellationToken token)
    {
        var channel = await db.NotificationChannels.SingleOrDefaultAsync(item => item.HealthScheduleId == scheduleId, token);
        if (channel != null && channel.DestinationReferenceId != reference)
        {
            channel.Enabled = false;
            channel.Revision++;
        }
    }

    internal async Task ObserveHealthWithinAsync(AllstarrDbContext db, HealthSmokeRunBinding binding,
        HealthSmokeCompleted completed, HealthSmokeAlertDecision decision, Guid runId, CancellationToken token, HealthSmokeCompleted? previous = null)
    {
        await ReconcileHealthChannelWithinAsync(db, binding.ScheduleId, binding.Configuration.WebhookReferenceId, token);
        if (binding.Configuration.WebhookReferenceId is { } reference)
        {
            var channel = await db.NotificationChannels.SingleOrDefaultAsync(item => item.HealthScheduleId == binding.ScheduleId, token);
            var administrator = await db.Users.Where(user => user.Enabled && user.IsAdmin &&
                user.BackendType == backend.EffectiveValue.ToLower() && user.BackendInstanceId == identity.BackendInstanceId)
                .OrderBy(user => user.Id).Select(user => (Guid?)user.Id).FirstOrDefaultAsync(token);
            if (administrator != null)
            {
                if (channel == null)
                {
                    channel = new()
                    {
                        Id = Guid.CreateVersion7(),
                        OwnerUserId = administrator.Value,
                        Kind = "webhook",
                        DisplayName = "Health webhook",
                        Household = true,
                        HealthScheduleId = binding.ScheduleId
                    };
                    db.NotificationChannels.Add(channel);
                }
                if (channel.DestinationReferenceId != reference) channel.Revision++;
                channel.DestinationReferenceId = reference;
                channel.Enabled = true;
                await db.SaveChangesAsync(token);
            }
        }
        var scope = Deployment + ":household";
        if (previous?.OpenAlertSteps.Length > 0 && !await db.ProblemIncidents.AnyAsync(item => item.ScopeKey == scope &&
            item.Resource == "health" && item.Kind == "health-check", token))
        {
            db.ProblemIncidents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ScopeKey = scope,
                Resource = "health",
                Kind = "health-check",
                State = "open",
                Transition = 1,
                OpenedAt = previous.CompletedAt,
                LastObservedAt = previous.CompletedAt,
                LastNotifiedAt = previous.CompletedAt,
                Revision = 1,
                DetailJson = JsonSerializer.Serialize(new ProblemAlertDetail(HealthSteps: previous.OpenAlertSteps, Protocol: previous.Protocol), Json)
            });
            await db.SaveChangesAsync(token);
        }
        if (completed.OpenAlertSteps.Length == 0 && completed.Steps.Any(step => step.Status == "failed")) return;
        var steps = completed.OpenAlertSteps.Length > 0 ? completed.OpenAlertSteps : decision.Changes.Select(change => change.Step).ToArray();
        await ObserveWithinAsync(db, new("health-check", "health", null, completed.OpenAlertSteps.Length > 0,
            "warning", completed.CompletedAt, Detail: new(HealthSteps: steps, Protocol: binding.Protocol)), token, runId, binding.ScheduleId);
    }

    public async Task<Guid> TestAsync(AdminAuthSession session, Guid channelId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await RequireSessionAsync(db, session, token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var channel = await db.NotificationChannels.SingleOrDefaultAsync(item => item.Id == channelId &&
                item.OwnerUserId == user.Id && item.HealthScheduleId == null, token) ?? throw new UnauthorizedAccessException();
            if (!await ChannelAuthorizedAsync(db, channel, token)) throw new ArgumentException("Enable the channel before sending a test.");
            var cutoff = clock.UtcNow.AddMinutes(-1);
            if (await db.NotificationDeliveries.AnyAsync(item => item.ChannelId == channelId && item.Event == "test" && item.CreatedAt > cutoff, token))
                throw new InvalidOperationException("Wait one minute before sending another test.");
            var id = await EnqueueWithinAsync(db, channel, null, "test", token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return id;
        }
        finally { _gate.Release(); }
    }

    internal async Task<IDisposable> LockDeliveryAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        return new DeliveryLease(_gate);
    }

    private sealed class DeliveryLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    internal static NotificationChannelView View(NotificationChannelRecord channel) =>
        new(channel.Id, channel.DisplayName, channel.Kind, channel.Enabled, channel.Household, true, channel.Revision);
    internal static string Action(string kind) => kind switch
    {
        "provider-login" => "Reconnect the affected source in Sources.",
        "source-failure" => "Review source health in Sources.",
        "disk-space" => "Review available capacity in Storage settings.",
        "health-check" => "Review Health check results.",
        _ => "Review notification delivery in alert settings."
    };
    internal static string Message(string kind, string state) => state == "resolved" ? "The problem has resolved." : kind switch
    {
        "provider-login" => "A source login needs to be reconnected.",
        "source-failure" => "A source is repeatedly failing or automatically paused.",
        "disk-space" => "A writable filesystem is nearly full.",
        "health-check" => "An Allstarr health check is failing.",
        _ => "This is a test notification from Allstarr."
    };
}
