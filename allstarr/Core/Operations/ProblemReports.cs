using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Operations;

public sealed partial class ProblemReportService(
    IDbContextFactory<AllstarrDbContext> factory,
    IPlatformClock clock,
    IBackendLibraryAccessResolver libraryAccess,
    IEnumerable<IPlaybackActivitySource> playbackSources,
    IPlaybackDeliveryActivitySource? playbackDeliveries = null,
    ProviderRuntimeHealth? health = null,
    IProviderRegistry? registry = null,
    ProviderAccountResolver? accounts = null,
    IConfiguration? configuration = null,
    IProtocolProviderGateway? providerGateway = null)
{
    private readonly ProviderAccountResolver reportAccounts = accounts ?? new(factory);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    { "playback", "seek", "wrong-match", "wrong-lyrics", "metadata", "other" };
    private static readonly HashSet<string> States = new(StringComparer.Ordinal) { "new", "seen", "fixed" };

    public async Task<ProblemReportContext> ContextAsync(AdminAuthSession session, ProblemReportSelector? selector, CancellationToken token)
    {
        selector = NormalizeSelector(selector);
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var actor = await RequireActorAsync(db, session, false, token);
        return await CaptureAsync(db, session, actor, selector, token);
    }

    public async Task<ProblemReportReceipt> CreateAsync(AdminAuthSession session, CreateProblemReportRequest request, CancellationToken token)
    {
        var normalized = NormalizeRequest(request);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions)));
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var actor = await RequireActorAsync(db, session, false, token);
                var existing = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(item =>
                    item.ActorUserId == actor.Id && item.RequestId == normalized.RequestId, token);
                if (existing != null)
                {
                    if (existing.RequestFingerprint != fingerprint)
                        throw new ProblemReportException(409, "report_request_conflict", "This request was already used for another problem report.");
                    return new(existing.Id, existing.Status, existing.CreatedAt, true);
                }
                var context = await CaptureAsync(db, session, actor, normalized.Selector, token);
                var contextJson = JsonSerializer.Serialize(context, JsonOptions);
                if (Encoding.UTF8.GetByteCount(contextJson) > 32768) throw ProblemReportException.Invalid();
                var record = new ProblemReportRecord
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = actor.Id,
                    SubjectUserId = context.SubjectUserId,
                    RequestId = normalized.RequestId,
                    RequestFingerprint = fingerprint,
                    Kind = normalized.Kind,
                    Note = ProblemReportText.Clean(normalized.Note, 2000)!,
                    ReporterName = ProblemReportText.Clean(actor.DisplayName, 200) ?? "Listener",
                    ContextJson = contextJson,
                    CreatedAt = context.CapturedAt,
                    UpdatedAt = context.CapturedAt
                };
                db.ProblemReports.Add(record);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return new(record.Id, record.Status, record.CreatedAt, false);
            }
            catch (Exception error) when (attempt < 2 && (DbErrors.IsUniqueViolation(error) || DbErrors.IsTransientConflict(error))) { }
            catch (Exception error) when (DbErrors.IsUniqueViolation(error) || DbErrors.IsTransientConflict(error))
            { throw new ProblemReportException(409, "report_changed", "The report could not be saved. Try again."); }
        }
    }

    public async Task<ProblemReportPage> ListAsync(AdminAuthSession session, string? status, string? cursor, int limit, CancellationToken token)
    {
        if (status != null && !States.Contains(status) || limit is < 1 or > 100) throw ProblemReportException.Invalid();
        var page = ReadCursor(cursor, status);
        await using var db = await factory.CreateDbContextAsync(token);
        await RequireActorAsync(db, session, true, token);
        var query = db.ProblemReports.AsNoTracking().Where(item => status == null || item.Status == status);
        if (page != null)
            query = query.Where(item => (item.CreatedAt < page.UpperAt || item.CreatedAt == page.UpperAt && item.Id.CompareTo(page.UpperId) <= 0) &&
                (item.CreatedAt < page.BeforeAt || item.CreatedAt == page.BeforeAt && item.Id.CompareTo(page.BeforeId) < 0));
        var rows = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(limit + 1).ToArrayAsync(token);
        var shown = rows.Take(limit).ToArray();
        var upperAt = page?.UpperAt ?? shown.FirstOrDefault()?.CreatedAt;
        var upperId = page?.UpperId ?? shown.FirstOrDefault()?.Id;
        var next = rows.Length <= limit || shown.Length == 0 ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new ReportCursor(1, status, upperAt!.Value, upperId!.Value, shown[^1].CreatedAt, shown[^1].Id), JsonOptions));
        return new(shown.Select(Summary).ToArray(), next);
    }

    public async Task<ProblemReportDetail> DetailAsync(AdminAuthSession session, Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await RequireActorAsync(db, session, true, token);
        var record = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw ProblemReportException.Missing();
        var summary = Summary(record);
        return new(summary.Id, summary.Kind, summary.Status, summary.NotePreview, summary.ReporterName, summary.SubjectName,
            summary.SongTitle, summary.Artist, summary.ProviderId, summary.ClientName, summary.CreatedAt, summary.UpdatedAt,
            summary.Revision, record.Note, Context(record));
    }

    public async Task<ProblemReportSummary> UpdateStatusAsync(AdminAuthSession session, Guid id, UpdateProblemReportStatusRequest request, CancellationToken token)
    {
        if (request == null || !States.Contains(request.Status ?? "") || request.ExpectedRevision < 1) throw ProblemReportException.Invalid();
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var actor = await RequireActorAsync(db, session, true, token);
        var record = await db.ProblemReports.SingleOrDefaultAsync(item => item.Id == id, token) ?? throw ProblemReportException.Missing();
        if (record.Revision != request.ExpectedRevision) throw ProblemReportException.Changed();
        if (record.Status == request.Status) return Summary(record);
        var previous = record.Status;
        record.Status = request.Status!;
        record.UpdatedAt = clock.UtcNow;
        record.StatusChangedByUserId = actor.Id;
        record.Revision++;
        db.AuditEvents.Add(new()
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actor.Id,
            Category = "problem-report",
            Action = "status-changed",
            Outcome = "updated",
            SubjectType = "problem-report",
            SubjectId = record.Id.ToString("D"),
            CorrelationId = record.Id.ToString("N"),
            CreatedAt = record.UpdatedAt,
            DetailsJson = JsonSerializer.Serialize(new { id = record.Id, oldStatus = previous, newStatus = record.Status }, JsonOptions)
        });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw ProblemReportException.Changed(); }
        await transaction.CommitAsync(token);
        return Summary(record);
    }

    public async Task RequireSessionAsync(AdminAuthSession session, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await RequireActorAsync(db, session, false, token);
    }

    private async Task<UserRecord> RequireActorAsync(AllstarrDbContext db, AdminAuthSession session, bool administrator, CancellationToken token)
    {
        if (session.AllstarrUserId == null || session.ExpiresAtUtc <= clock.UtcNow.UtcDateTime) throw ProblemReportException.Unauthorized();
        var backend = session.BackendType.ToLowerInvariant();
        if (backend is not ("jellyfin" or "subsonic")) throw ProblemReportException.Unauthorized();
        if (configuration?["Backend:Type"] is { } selectedBackend &&
            !selectedBackend.Trim().Equals(backend, StringComparison.OrdinalIgnoreCase)) throw ProblemReportException.Unauthorized();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == session.AllstarrUserId && item.Enabled &&
            item.BackendType == backend && item.BackendInstanceId == session.BackendInstanceId &&
            item.BackendPrincipalId == session.UserId && item.IsAdmin == session.IsAdministrator, token) ?? throw ProblemReportException.Unauthorized();
        if (administrator && !actor.IsAdmin) throw new ProblemReportException(403, "administrator_required", "Administrator access is required.");
        return actor;
    }

    private static CreateProblemReportRequest NormalizeRequest(CreateProblemReportRequest request)
    {
        if (request == null || request.RequestId == Guid.Empty || request.Note == null || request.Kind == null) throw ProblemReportException.Invalid();
        var kind = request.Kind.Trim().ToLowerInvariant();
        var note = request.Note.Trim();
        if (!Kinds.Contains(kind) || note.Length is < 1 or > 2000 || note.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw ProblemReportException.Invalid();
        return new(request.RequestId, kind, note, NormalizeSelector(request.Selector));
    }

    private static ProblemReportSelector? NormalizeSelector(ProblemReportSelector? selector)
    {
        if (selector == null) return null;
        var itemId = selector.ItemId?.Trim();
        if ((itemId == null) == !selector.ExternalSnapshotId.HasValue || selector.ExternalSnapshotId == Guid.Empty ||
            itemId != null && !ProblemReportText.IsReference(itemId) || selector.Playback != null &&
            (itemId == null || selector.Playback.SubjectUserId == Guid.Empty || string.IsNullOrWhiteSpace(selector.Playback.DeviceId) ||
             selector.Playback.DeviceId.Length > 200 || selector.Playback.DeviceId.Any(char.IsControl))) throw ProblemReportException.Invalid();
        return selector with { ItemId = itemId };
    }

    private static ProblemReportContext Context(ProblemReportRecord record) =>
        JsonSerializer.Deserialize<ProblemReportContext>(record.ContextJson, JsonOptions) ?? throw ProblemReportException.Missing();
    private static ProblemReportSummary Summary(ProblemReportRecord record)
    {
        var context = Context(record);
        return new(record.Id, record.Kind, record.Status, record.Note[..Math.Min(record.Note.Length, 160)], record.ReporterName,
            context.SubjectName, context.Song?.Title, context.Song?.Artist,
            context.Playback?.ProviderId ?? context.Song?.CatalogProviderId, context.Playback?.ClientName,
            record.CreatedAt, record.UpdatedAt, record.Revision);
    }

    private sealed record ReportCursor(int Version, string? Status, DateTimeOffset UpperAt, Guid UpperId, DateTimeOffset BeforeAt, Guid BeforeId);
    private static ReportCursor? ReadCursor(string? cursor, string? status)
    {
        if (cursor == null) return null;
        if (cursor.Length > 1024) throw ProblemReportException.Invalid();
        try
        {
            var value = JsonSerializer.Deserialize<ReportCursor>(Convert.FromBase64String(cursor), JsonOptions);
            if (value == null || value.Version != 1 || value.Status != status || value.UpperId == Guid.Empty || value.BeforeId == Guid.Empty ||
                value.BeforeAt > value.UpperAt || value.BeforeAt == value.UpperAt && value.BeforeId.CompareTo(value.UpperId) > 0)
                throw ProblemReportException.Invalid();
            return value;
        }
        catch (Exception error) when (error is JsonException or FormatException) { throw ProblemReportException.Invalid(); }
    }
}
