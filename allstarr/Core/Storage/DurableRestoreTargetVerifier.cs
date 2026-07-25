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
        if (provider != DurableStorageProvider.Postgres)
        {
            throw new BackupVerificationException("Only PostgreSQL restore targets are supported.");
        }

        var options = new DbContextOptionsBuilder<AllstarrDbContext>();
        options.UseNpgsql(connectionString);

        await using var context = new AllstarrDbContext(options.Options);
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            throw new BackupVerificationException("The restored target cannot be opened for verification.");
        }

        var compatibility = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
        if (!compatibility.IsCurrent)
        {
            throw new BackupVerificationException(
                "The restored target schema does not exactly match this Allstarr build.");
        }

        return compatibility;
    }
}
