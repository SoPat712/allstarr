using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public interface IDurableRestoreTargetVerifier
{
    Task<DurableSchemaCompatibilitySnapshot> VerifyAsync(
        DurableStorageProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default);
}

public sealed class DurableRestoreTargetVerifier : IDurableRestoreTargetVerifier
{
    public async Task<DurableSchemaCompatibilitySnapshot> VerifyAsync(
        DurableStorageProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<AllstarrDbContext>();
        if (provider == DurableStorageProvider.Postgres)
        {
            options.UseNpgsql(connectionString);
        }
        else
        {
            options.UseSqlite(connectionString);
        }

        await using var context = new AllstarrDbContext(options.Options);
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            throw new BackupVerificationException("The restored target cannot be opened for verification.");
        }

        if (provider == DurableStorageProvider.Sqlite)
        {
            await VerifySqliteIntegrityAsync(context, cancellationToken);
        }

        var compatibility = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
        if (!compatibility.IsCurrent)
        {
            throw new BackupVerificationException(
                "The restored target schema does not exactly match this Allstarr build.");
        }

        return compatibility;
    }

    private static async Task VerifySqliteIntegrityAsync(
        AllstarrDbContext context,
        CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync(cancellationToken);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupVerificationException("SQLite integrity verification failed.");
        }
    }
}
