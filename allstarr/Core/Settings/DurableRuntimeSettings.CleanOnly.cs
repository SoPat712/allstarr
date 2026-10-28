using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Settings;

public sealed partial class DurableRuntimeSettingsService
{
    public async Task<PersonalListeningPreferences> GetManagedPreferencesAsync(
        Guid userId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await RequirePreferenceAdministratorAsync(db, actorUserId, cancellationToken);
        await RequirePreferenceScopeAsync(db, userId, cancellationToken);
        return await ReadPreferencesAsync(db, userId, cancellationToken);
    }

    public async Task<PersonalListeningPreferences> UpdateCleanOnlyLockAsync(
        Guid userId, bool locked, string expectedRevision, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(expectedRevision))
            throw new ArgumentException("An expected preferences revision is required.", nameof(expectedRevision));

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequirePreferenceAdministratorAsync(db, actorUserId, cancellationToken);
        await RequirePreferenceScopeAsync(db, userId, cancellationToken);
        var current = await ReadPreferencesAsync(db, userId, cancellationToken);
        if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            throw new RuntimeSettingConflictException("Listening preferences changed during the update.");

        var key = CleanOnlyLockKeys.ForUser(userId);
        var row = await db.RuntimeSettings.SingleOrDefaultAsync(
            item => item.OwnerUserId == null && item.Key == key, cancellationToken);
        if (row == null)
        {
            row = new RuntimeSettingRecord
            {
                Id = Guid.CreateVersion7(),
                Key = key,
                ValueType = RuntimeSettingValueType.Boolean,
                Revision = 1,
                CreatedAt = _clock.UtcNow
            };
            db.RuntimeSettings.Add(row);
        }
        else row.Revision++;
        row.ValueJson = JsonSerializer.Serialize(locked, JsonOptions);
        row.Source = "clean-only-lock";
        row.UpdatedByUserId = actorUserId;
        row.UpdatedAt = _clock.UtcNow;
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actorUserId,
            Category = "runtime-settings",
            Action = "runtime-settings.clean-only-lock",
            Outcome = "succeeded",
            CorrelationId = $"runtime-settings:{Guid.NewGuid():N}",
            DetailsJson = JsonSerializer.Serialize(new { userId, locked }, JsonOptions),
            CreatedAt = _clock.UtcNow
        });

        PersonalListeningPreferences updated;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            updated = await ReadPreferencesAsync(db, userId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsWriteConflict(exception))
        { throw new RuntimeSettingConflictException("Listening preferences changed during the update.", exception); }
        _signal.Publish();
        return updated;
    }

    private static async Task RequirePreferenceAdministratorAsync(
        AllstarrDbContext db, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(
                item => item.Id == actorUserId && item.Enabled && item.IsAdmin, cancellationToken))
            throw new UnauthorizedAccessException("Administrator permissions required.");
    }
}
