using System.Text.Json;
using System.Data;
using allstarr.Core.Capabilities;
using allstarr.Core.Configuration;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Health;

public sealed record HealthSmokeSaveRequest(bool Enabled, Guid ListenerUserId, int IntervalMinutes = 30,
    string SearchQuery = "Get Lucky Daft Punk", int FirstByteBudgetSeconds = 15, long ExpectedRevision = 0,
    string? Username = null, string? Password = null, string? WebhookUrl = null, bool ClearWebhook = false);
public sealed record HealthSmokeSchedule(int Version, long Revision, int IntervalMinutes, string SearchQuery,
    int FirstByteBudgetSeconds, Guid CredentialReferenceId, Guid? WebhookReferenceId = null);
public sealed record HealthSmokeJobPayload(Guid ScheduleId, long ConfigurationRevision);
public sealed record HealthSmokeCleanupPayload(Guid RunId);
public sealed record HealthSmokeAlertPayload(Guid RunId, HealthSmokeAlertChange[] Changes);
internal sealed record HealthSmokeRunBinding(Guid ScheduleId, Guid OwnerUserId, string Protocol,
    string BackendInstanceId, string PrincipalId, HealthSmokeSchedule Configuration, DateTimeOffset StartedAt);
internal sealed record HealthSmokeCompleted(string Protocol, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    long ConfigurationRevision, HealthSmokeStep[] Steps, string[] OpenAlertSteps, string AlertStatus);

public sealed class HealthSmokeConfigurationException(string code, string message) : InvalidOperationException(message)
{ public string Code { get; } = code; }

public sealed partial class HealthSmokeService(
    IDbContextFactory<AllstarrDbContext> factory, EncryptedSecretStore secrets,
    IDurableRuntimeSettings settings, IBackendConnectionVerifier verifier, BackendSelectionAuthority backend,
    IdentityOptions identity, IPlatformClock clock, DurableJobQueue jobs,
    IEffectiveProviderPolicyResolver policies, HealthSmokeProtocolProbe probe)
{
    public const string JobType = "health.smoke";
    public const string CleanupJobType = "health.cleanup";
    public const string AlertJobType = "health.alert";
    public const string ListenerPurpose = "health-smoke-listener";
    public const string WebhookPurpose = "health-smoke-webhook";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _configurationGate = new(1, 1);

    public async Task<object> ReadAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var schedule = await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.JobType == JobType, token);
        var config = schedule == null ? null : Parse<HealthSmokeSchedule>(schedule.PayloadTemplateJson);
        var listeners = await db.Users.AsNoTracking().Where(user => user.Enabled && !user.IsAdmin &&
            user.BackendType == backend.EffectiveValue.ToLower() && user.BackendInstanceId == identity.BackendInstanceId)
            .OrderBy(user => user.DisplayName)
            .Select(user => new { user.Id, user.DisplayName, protocol = user.BackendType, user.IsSynthetic }).ToArrayAsync(token);
        var rows = await db.HealthSmokeRuns.AsNoTracking().Where(item => item.CompletedJson != null)
            .OrderByDescending(item => item.CreatedAt).Take(100).ToArrayAsync(token);
        var runs = rows.Select(row => new { row.Id, row.AlertStatus, Result = Parse<HealthSmokeCompleted>(row.CompletedJson!) })
            .Where(row => row.Result != null).Take(30).Select(row => new
            {
                row.Id,
                row.Result!.Protocol,
                row.Result.StartedAt,
                row.Result.CompletedAt,
                passed = row.Result.Steps.All(step => step.Status == "passed"),
                row.Result.Steps,
                row.AlertStatus
            }).ToArray();
        var active = await db.Jobs.AsNoTracking().Where(item => (item.Type == JobType || item.Type == CleanupJobType) &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled || item.State == DurableJobState.Running))
            .OrderBy(item => item.CreatedAt).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(token);
        return new
        {
            configuration = new
            {
                enabled = schedule?.Enabled == true,
                intervalMinutes = config?.IntervalMinutes ?? 30,
                listenerUserId = (Guid?)schedule?.OwnerUserId,
                searchQuery = config?.SearchQuery ?? "Get Lucky Daft Punk",
                firstByteBudgetSeconds = config?.FirstByteBudgetSeconds ?? 15,
                revision = config?.Revision ?? 0,
                hasListenerCredential = config != null,
                hasWebhook = config?.WebhookReferenceId != null,
                nextRunAt = schedule?.NextRunAt
            },
            protocol = backend.EffectiveValue,
            inactiveProtocol = backend.EffectiveValue == "Jellyfin" ? "Subsonic" : "Jellyfin",
            listeners,
            runs,
            runningJobId = active
        };
    }

    public async Task ConfigureAsync(HealthSmokeSaveRequest request, Guid administrator, CancellationToken token)
    {
        if (request.ListenerUserId == Guid.Empty || request.IntervalMinutes is < 1 or > 1440 ||
            request.FirstByteBudgetSeconds is < 1 or > 60 || string.IsNullOrWhiteSpace(request.SearchQuery) ||
            request.SearchQuery.Length > 200 || request.SearchQuery.Any(char.IsControl))
            throw new ArgumentException("Choose a dedicated listener, a song, an interval from 1 to 1440 minutes, and a playback budget from 1 to 60 seconds.");
        Uri? webhook = null;
        if (!string.IsNullOrWhiteSpace(request.WebhookUrl) &&
            (!Uri.TryCreate(request.WebhookUrl.Trim(), UriKind.Absolute, out webhook) ||
             webhook.Scheme is not ("http" or "https") || webhook.UserInfo.Length != 0 || webhook.Fragment.Length != 0 || request.WebhookUrl.Length > 2048))
            throw new ArgumentException("Enter an HTTP or HTTPS webhook address without a fragment or embedded username.");
        await _configurationGate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await db.Users.SingleOrDefaultAsync(user => user.Id == request.ListenerUserId && user.Enabled && !user.IsAdmin &&
                user.BackendType == backend.EffectiveValue.ToLower() && user.BackendInstanceId == identity.BackendInstanceId, token)
                ?? throw new ArgumentException("Choose an enabled, non-administrator listener from this deployment.");
            var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.JobType == JobType, token);
            var before = schedule == null ? null : Parse<HealthSmokeSchedule>(schedule.PayloadTemplateJson);
            if ((before?.Revision ?? 0) != request.ExpectedRevision)
                throw new HealthSmokeConfigurationException("revision_conflict", "Health settings changed. Reload before saving.");
            if (await HasActiveRunAsync(db, token))
                throw new HealthSmokeConfigurationException("check_running", "Wait for the current health check and cleanup before changing its listener or settings.");
            HealthSmokeCredentials? credentials = null;
            if (!string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrEmpty(request.Password))
            {
                var endpoint = (await settings.GetAsync("Backend:Url", token)).NormalizedValue.TrimEnd('/');
                var verified = await verifier.VerifyAsync(Enum.Parse<BackendType>(backend.EffectiveValue), endpoint,
                    request.Username, request.Password, token);
                if (verified == null || verified.IsAdministrator || verified.UserId != user.BackendPrincipalId)
                    throw new ArgumentException("The sign-in must belong to the selected dedicated listener.");
                credentials = new() { UserName = request.Username, Password = user.BackendType == "subsonic" ? request.Password : "", AccessToken = verified.AccessToken };
            }
            else if (before == null || schedule?.OwnerUserId != user.Id)
                throw new ArgumentException("Sign in as the selected dedicated listener to configure its check.");
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            if (await HasActiveRunAsync(db, token))
                throw new HealthSmokeConfigurationException("check_running", "Wait for the current health check and cleanup before changing its listener or settings.");
            var reference = credentials == null ? before!.CredentialReferenceId :
                (await secrets.StoreWithinTransactionAsync(db, user.Id, ListenerPurpose,
                    JsonSerializer.SerializeToUtf8Bytes(credentials, Json), cancellationToken: token)).Id;
            var webhookReference = request.ClearWebhook ? null : before?.WebhookReferenceId;
            if (webhook != null)
                webhookReference = (await secrets.StoreWithinTransactionAsync(db, null, WebhookPurpose,
                    System.Text.Encoding.UTF8.GetBytes(webhook.AbsoluteUri), cancellationToken: token)).Id;
            var config = new HealthSmokeSchedule(1, (before?.Revision ?? 0) + 1, request.IntervalMinutes,
                request.SearchQuery.Trim(), request.FirstByteBudgetSeconds, reference, webhookReference);
            var now = clock.UtcNow;
            if (schedule == null)
            {
                schedule = new() { Id = Guid.CreateVersion7(), JobType = JobType, CreatedAt = now };
                db.JobSchedules.Add(schedule);
            }
            user.IsSynthetic = true;
            schedule.OwnerUserId = user.Id;
            schedule.Enabled = request.Enabled;
            schedule.CronExpression = "* * * * *";
            schedule.TimeZoneId = "UTC";
            schedule.OverlapPolicy = ScheduleOverlapPolicy.Skip;
            schedule.MisfirePolicy = ScheduleMisfirePolicy.RunOnce;
            schedule.RetryPolicyJson = "{}";
            schedule.PayloadTemplateJson = JsonSerializer.Serialize(config, Json);
            schedule.NextRunAt = request.Enabled ? now.AddMinutes(config.IntervalMinutes) : null;
            schedule.UpdatedAt = now;
            schedule.Revision++;
            db.AuditEvents.Add(Event(Guid.CreateVersion7(), administrator, "configured", "success", "{}", now));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        finally { _configurationGate.Release(); }
    }

    public async Task<Guid> EnqueueAsync(CancellationToken token)
    {
        await _configurationGate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var schedule = await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.JobType == JobType, token)
                ?? throw new HealthSmokeConfigurationException("not_configured", "Configure a dedicated listener first.");
            if (await HasActiveRunAsync(db, token))
                throw new HealthSmokeConfigurationException("check_running", "A health check or its cleanup is already running.");
            var config = Parse<HealthSmokeSchedule>(schedule.PayloadTemplateJson) ?? throw new InvalidOperationException("Health configuration is unavailable.");
            var queued = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<HealthSmokeJobPayload>(JobType,
                $"health-manual:{Guid.NewGuid():N}", new(schedule.Id, config.Revision), schedule.OwnerUserId, MaxAttempts: 1), token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return queued.JobId;
        }
        finally { _configurationGate.Release(); }
    }

    internal async ValueTask<IDisposable> LockConfigurationAsync(CancellationToken token)
    {
        await _configurationGate.WaitAsync(token);
        return new ConfigurationLease(_configurationGate);
    }

    private sealed class ConfigurationLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    internal static Task<bool> HasActiveRunAsync(AllstarrDbContext db, CancellationToken token) => db.Jobs.AnyAsync(item =>
        (item.Type == JobType || item.Type == CleanupJobType) &&
        (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled || item.State == DurableJobState.Running), token);

    internal static T? Parse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return default; }
    }

    internal static AuditEventRecord Event(Guid runId, Guid owner, string action, string outcome, string details, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ActorUserId = owner,
        Category = "health-check",
        Action = action,
        Outcome = outcome,
        SubjectType = "health-run",
        SubjectId = runId.ToString("N"),
        DetailsJson = details,
        CreatedAt = now
    };

    private async Task SaveAsync(Guid runId, Guid owner, string action, string outcome, object details, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var json = JsonSerializer.Serialize(details, Json);
        var row = await db.HealthSmokeRuns.SingleOrDefaultAsync(item => item.Id == runId, token);
        if (action == "started" && row == null)
        {
            row = new() { Id = runId, OwnerUserId = owner, BindingJson = json, CreatedAt = clock.UtcNow };
            db.HealthSmokeRuns.Add(row);
        }
        if (row == null || row.OwnerUserId != owner) throw new UnauthorizedAccessException("The health run is unavailable.");
        if (action == "artifacts") row.ArtifactsJson = json;
        if (action == "completed")
        {
            row.CompletedJson = json;
            row.CompletedAt = clock.UtcNow;
            row.AlertStatus = ((HealthSmokeCompleted)details).AlertStatus;
        }
        db.AuditEvents.Add(Event(runId, owner, action, outcome, "{}", clock.UtcNow));
        await db.SaveChangesAsync(token);
    }

    private async Task<(HealthSmokeRunBinding Binding, HealthSmokeArtifacts Artifacts)?> ReadRunAsync(Guid runId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.HealthSmokeRuns.AsNoTracking().SingleOrDefaultAsync(item => item.Id == runId, token);
        var parsed = row == null ? null : Parse<HealthSmokeRunBinding>(row.BindingJson);
        return parsed == null ? null : (parsed, Parse<HealthSmokeArtifacts>(row!.ArtifactsJson) ?? new());
    }

    private async Task<HealthSmokeProbeContext> ContextAsync(Guid runId, HealthSmokeRunBinding binding, CancellationToken token)
    {
        if (!binding.Protocol.Equals(backend.EffectiveValue, StringComparison.OrdinalIgnoreCase) ||
            binding.BackendInstanceId != identity.BackendInstanceId)
            throw new UnauthorizedAccessException("The saved health run belongs to a different deployment.");
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == binding.OwnerUserId && user.Enabled && user.IsSynthetic && !user.IsAdmin &&
            user.BackendType == binding.Protocol.ToLower() && user.BackendInstanceId == binding.BackendInstanceId &&
            user.BackendPrincipalId == binding.PrincipalId, token))
            throw new UnauthorizedAccessException("The dedicated health listener is unavailable.");
        var configured = await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.Id == binding.ScheduleId &&
            item.OwnerUserId == binding.OwnerUserId && item.JobType == JobType, token);
        var refreshed = configured == null ? null : Parse<HealthSmokeSchedule>(configured.PayloadTemplateJson);
        var reference = refreshed?.CredentialReferenceId ?? binding.Configuration.CredentialReferenceId;
        using var lease = await secrets.OpenAsync(reference, new(binding.OwnerUserId, ListenerPurpose), token);
        var credentials = JsonSerializer.Deserialize<HealthSmokeCredentials>(lease.Value.Span, Json)
            ?? throw new InvalidOperationException("The health listener sign-in is unavailable.");
        var policy = await policies.ResolveForUserAsync(binding.OwnerUserId, token);
        return new(binding.Protocol, runId, binding.PrincipalId, binding.Configuration.SearchQuery,
            binding.Configuration.FirstByteBudgetSeconds, credentials,
            id => FavoriteDisplaySnapshot.Resource(id) is { ResourceKind: ProviderResourceKind.Track } track && policy.IsAutomatic(track.ProviderId),
            (artifacts, ct) => SaveAsync(runId, binding.OwnerUserId, "artifacts", "saved", artifacts, ct));
    }
}
