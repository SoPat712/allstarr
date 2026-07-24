using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public enum DurableSchemaCompatibilityStatus
{
    Current,
    MigrationRequired,
    UnsupportedVersion
}

public sealed record DurableSchemaCompatibilitySnapshot(
    DurableSchemaCompatibilityStatus Status,
    string CurrentSchemaVersion,
    string AppliedSchemaVersion,
    IReadOnlyList<string> MissingMigrations,
    IReadOnlyList<string> UnknownMigrations)
{
    public bool IsCurrent => Status == DurableSchemaCompatibilityStatus.Current;
}

public static class DurableSchemaCompatibility
{
    public const string MigrationRequiredErrorCode = "schema_migration_required";
    public const string UnsupportedVersionErrorCode = "schema_version_unsupported";

    public static async Task<DurableSchemaCompatibilitySnapshot> InspectAsync(
        AllstarrDbContext context,
        CancellationToken cancellationToken = default)
    {
        var known = context.Database.GetMigrations().ToArray();
        if (known.Length == 0)
        {
            throw new InvalidOperationException("No checked-in durable storage migrations were found.");
        }

        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        var knownSet = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var appliedSet = applied.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = applied.Where(migration => !knownSet.Contains(migration)).ToArray();
        var missing = known.Where(migration => !appliedSet.Contains(migration)).ToArray();
        var status = unknown.Length > 0
            ? DurableSchemaCompatibilityStatus.UnsupportedVersion
            : missing.Length > 0
                ? DurableSchemaCompatibilityStatus.MigrationRequired
                : DurableSchemaCompatibilityStatus.Current;

        return new DurableSchemaCompatibilitySnapshot(
            status,
            known[^1],
            applied.LastOrDefault() ?? "none",
            missing,
            unknown);
    }

    public static string ErrorCode(DurableSchemaCompatibilitySnapshot snapshot) =>
        snapshot.Status switch
        {
            DurableSchemaCompatibilityStatus.MigrationRequired => MigrationRequiredErrorCode,
            DurableSchemaCompatibilityStatus.UnsupportedVersion => UnsupportedVersionErrorCode,
            _ => throw new InvalidOperationException("The current schema does not have an error code.")
        };
}
