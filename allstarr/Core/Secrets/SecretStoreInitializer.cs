using System.Security.Cryptography;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Secrets;

public sealed class SecretStoreInitializer(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableStorageState storage,
    FileSecretKeyRingProvider keyRingProvider,
    SecretStoreOptions options,
    ILogger<SecretStoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready) return;
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var hasSecrets = await database.SecretVersions.AnyAsync(cancellationToken);
            if (await keyRingProvider.CreateIfMissingAsync(hasSecrets, cancellationToken))
                logger.LogInformation("Created a new encryption key ring at {Path}; keep it with database backups",
                    Path.GetFullPath(options.KeyRingPath));
            var ring = await keyRingProvider.LoadAsync(cancellationToken);
            foreach (var key in ring.Keys.Values) CryptographicOperations.ZeroMemory(key);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FileNotFoundException)
        {
            logger.LogError("The encryption key ring is missing while encrypted secrets exist. Restore the original key ring to use saved credentials; native proxy access remains available");
        }
        catch (Exception exception)
        {
            logger.LogError("The encryption key ring could not be initialized ({ErrorType}); check its location and private file permissions. Native proxy access remains available", exception.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
