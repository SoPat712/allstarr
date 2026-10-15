using System.Data;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed record LyricsChoiceStamp(Guid Id, long Revision);

public sealed record LyricsChoiceRevision(
    LyricsChoiceStamp? Personal = null,
    LyricsChoiceStamp? Household = null);

public sealed record RecordingLyricsChoice(
    Guid Id,
    Guid CanonicalRecordingId,
    Guid? OwnerUserId,
    string SourceId,
    long Revision);

public sealed record RecordingLyricsChoiceLayers(
    RecordingLyricsChoice? Personal,
    RecordingLyricsChoice? Household)
{
    public RecordingLyricsChoice? Effective => Personal ?? Household;

    public LyricsChoiceRevision Revision => new(
        Personal == null ? null : new LyricsChoiceStamp(Personal.Id, Personal.Revision),
        Household == null ? null : new LyricsChoiceStamp(Household.Id, Household.Revision));
}

public interface IRecordingLyricsChoices
{
    Task<RecordingLyricsChoiceLayers> GetAsync(
        ProtocolExecutionContext context,
        Guid recordingId);

    Task<RecordingLyricsChoiceLayers> SetAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string sourceId,
        string scope,
        LyricsChoiceRevision expected);

    Task<RecordingLyricsChoiceLayers> ClearAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string scope,
        LyricsChoiceRevision expected);
}

public sealed class LyricsChoiceConflictException(string message) : InvalidOperationException(message);

public sealed class RecordingLyricsChoices : IRecordingLyricsChoices
{
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly ITrackIdentityService _identities;
    private readonly IPlatformClock _clock;

    public RecordingLyricsChoices(
        IDbContextFactory<AllstarrDbContext> factory,
        ITrackIdentityService identities,
        IPlatformClock clock)
    {
        _factory = factory;
        _identities = identities;
        _clock = clock;
    }

    public async Task<RecordingLyricsChoiceLayers> GetAsync(
        ProtocolExecutionContext context,
        Guid recordingId)
    {
        var token = RequireContext(context);
        var authorization = await AuthorizeAsync(context, recordingId, requireAdministrator: false, token);
        await using var db = await _factory.CreateDbContextAsync(token);
        var rows = await ReadRowsAsync(db, authorization.CanonicalRecordingId, authorization.UserId, token);
        return Project(rows.Personal, rows.Household);
    }

    public async Task<RecordingLyricsChoiceLayers> SetAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string sourceId,
        string scope,
        LyricsChoiceRevision expected)
    {
        var token = RequireContext(context);
        var source = ProviderContractValidation.ProviderId(sourceId, nameof(sourceId));
        var targetScope = ParseScope(scope);
        ArgumentNullException.ThrowIfNull(expected);
        var authorization = await AuthorizeAsync(
            context,
            recordingId,
            requireAdministrator: targetScope == ChoiceScope.Household,
            token);

        try
        {
            await using var db = await _factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                token);
            await RevalidateForWriteAsync(db, context, authorization, targetScope == ChoiceScope.Household, token);
            var rows = await ReadRowsAsync(
                db,
                authorization.CanonicalRecordingId,
                authorization.UserId,
                token);
            EnsureExpected(rows, expected);

            var personal = rows.Personal;
            var household = rows.Household;
            var target = targetScope == ChoiceScope.Personal ? personal : household;
            if (target != null && target.SourceId.Equals(source, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(token);
                return Project(personal, household);
            }

            var now = _clock.UtcNow;
            if (target == null)
            {
                target = new RecordingLyricsChoiceRecord
                {
                    Id = Guid.CreateVersion7(),
                    CanonicalRecordingId = authorization.CanonicalRecordingId,
                    OwnerUserId = targetScope == ChoiceScope.Personal ? authorization.UserId : null,
                    SourceId = source,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Revision = 1
                };
                db.RecordingLyricsChoices.Add(target);
                if (targetScope == ChoiceScope.Personal) personal = target;
                else household = target;
            }
            else
            {
                target.SourceId = source;
                target.UpdatedAt = now;
                target.Revision = checked(target.Revision + 1);
            }

            AddAudit(db, context, authorization, "choice.set", source, targetScope, now);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Project(personal, household);
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            throw Conflict();
        }
    }

    public async Task<RecordingLyricsChoiceLayers> ClearAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string scope,
        LyricsChoiceRevision expected)
    {
        var token = RequireContext(context);
        var targetScope = ParseScope(scope);
        ArgumentNullException.ThrowIfNull(expected);
        var authorization = await AuthorizeAsync(
            context,
            recordingId,
            requireAdministrator: targetScope == ChoiceScope.Household,
            token);

        try
        {
            await using var db = await _factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                token);
            await RevalidateForWriteAsync(db, context, authorization, targetScope == ChoiceScope.Household, token);
            var rows = await ReadRowsAsync(
                db,
                authorization.CanonicalRecordingId,
                authorization.UserId,
                token);
            EnsureExpected(rows, expected);

            var personal = rows.Personal;
            var household = rows.Household;
            var target = targetScope == ChoiceScope.Personal ? personal : household;
            if (target == null)
            {
                await transaction.CommitAsync(token);
                return Project(personal, household);
            }

            var now = _clock.UtcNow;
            var source = target.SourceId;
            target.RevokedAt = now;
            target.UpdatedAt = now;
            target.Revision = checked(target.Revision + 1);
            if (targetScope == ChoiceScope.Personal) personal = null;
            else household = null;

            AddAudit(db, context, authorization, "choice.clear", source, targetScope, now);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Project(personal, household);
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            throw Conflict();
        }
    }

    private async Task<Authorization> AuthorizeAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        bool requireAdministrator,
        CancellationToken cancellationToken)
    {
        if (recordingId == Guid.Empty)
            throw new ArgumentException("A canonical recording ID is required.", nameof(recordingId));

        var actor = context.RequireActor();
        var principal = context.Principal
            ?? throw new UnauthorizedAccessException("A linked backend user is required.");
        var userId = actor.EffectiveUserId
            ?? throw new UnauthorizedAccessException("A linked backend user is required.");
        cancellationToken.ThrowIfCancellationRequested();

        var recording = await _identities.ResolveRecordingAsync(
            actor,
            recordingId.ToString("D"),
            cancellationToken);
        if (recording == null)
            throw new KeyNotFoundException("The canonical recording does not exist.");

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == userId && item.Enabled &&
                    item.BackendType == principal.BackendType &&
                    item.BackendInstanceId == principal.BackendInstanceId &&
                    item.BackendPrincipalId == principal.BackendPrincipalId,
            cancellationToken);
        if (user == null)
            throw new UnauthorizedAccessException("The linked backend user is unavailable.");
        if (requireAdministrator && (!principal.IsAdministrator || !user.IsAdmin))
            throw new UnauthorizedAccessException("Current administrator authorization is required.");

        return new Authorization(recording.Id, user.Id);
    }

    private static async Task RevalidateForWriteAsync(AllstarrDbContext db, ProtocolExecutionContext context,
        Authorization authorization, bool requireAdministrator, CancellationToken token)
    {
        var principal = context.Principal!;
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == authorization.UserId && user.Enabled &&
                user.BackendType == principal.BackendType && user.BackendInstanceId == principal.BackendInstanceId &&
                user.BackendPrincipalId == principal.BackendPrincipalId &&
                (!requireAdministrator || user.IsAdmin && principal.IsAdministrator), token))
            throw new UnauthorizedAccessException("The linked backend authorization changed.");
        if (!await db.CanonicalRecordings.AsNoTracking().AnyAsync(recording =>
                recording.Id == authorization.CanonicalRecordingId && recording.MergedIntoId == null, token))
            throw Conflict();
    }

    private static CancellationToken RequireContext(ProtocolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.CancellationToken;
    }

    private static ChoiceScope ParseScope(string scope) => scope switch
    {
        "personal" => ChoiceScope.Personal,
        "household" => ChoiceScope.Household,
        _ => throw new ArgumentException(
            "The lyrics choice scope must be 'personal' or 'household'.", nameof(scope))
    };

    private static async Task<ChoiceRows> ReadRowsAsync(
        AllstarrDbContext db,
        Guid canonicalRecordingId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var rows = await db.RecordingLyricsChoices
            .Where(item => item.CanonicalRecordingId == canonicalRecordingId &&
                           item.RevokedAt == null &&
                           (item.OwnerUserId == null || item.OwnerUserId == userId))
            .ToListAsync(cancellationToken);
        return new ChoiceRows(
            rows.SingleOrDefault(item => item.OwnerUserId == userId),
            rows.SingleOrDefault(item => item.OwnerUserId == null));
    }

    private static void EnsureExpected(ChoiceRows rows, LyricsChoiceRevision expected)
    {
        if (!Matches(expected.Personal, rows.Personal) ||
            !Matches(expected.Household, rows.Household))
        {
            throw Conflict();
        }
    }

    private static bool Matches(LyricsChoiceStamp? expected, RecordingLyricsChoiceRecord? actual) =>
        expected == null
            ? actual == null
            : actual != null && actual.Id == expected.Id && actual.Revision == expected.Revision;

    private static RecordingLyricsChoiceLayers Project(
        RecordingLyricsChoiceRecord? personal,
        RecordingLyricsChoiceRecord? household) =>
        new(ToPublic(personal), ToPublic(household));

    private static RecordingLyricsChoice? ToPublic(RecordingLyricsChoiceRecord? value) => value == null
        ? null
        : new RecordingLyricsChoice(
            value.Id,
            value.CanonicalRecordingId,
            value.OwnerUserId,
            value.SourceId,
            value.Revision);

    private static void AddAudit(
        AllstarrDbContext db,
        ProtocolExecutionContext context,
        Authorization authorization,
        string action,
        string source,
        ChoiceScope scope,
        DateTimeOffset now)
    {
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = authorization.UserId,
            Category = "lyrics",
            Action = action,
            Outcome = "succeeded",
            CorrelationId = context.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                canonicalRecordingId = authorization.CanonicalRecordingId,
                sourceId = source,
                scope = scope == ChoiceScope.Personal ? "personal" : "household"
            }),
            CreatedAt = now
        });
    }

    private static bool IsConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException ||
        DbErrors.IsUniqueViolation(exception) ||
        DbErrors.IsTransientConflict(exception);

    private static LyricsChoiceConflictException Conflict() =>
        new("The lyrics choice changed before this operation completed.");

    private enum ChoiceScope
    {
        Personal,
        Household
    }

    private sealed record Authorization(Guid CanonicalRecordingId, Guid UserId);

    private sealed record ChoiceRows(
        RecordingLyricsChoiceRecord? Personal,
        RecordingLyricsChoiceRecord? Household);
}
