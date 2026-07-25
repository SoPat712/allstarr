using System.Diagnostics;
using Npgsql;

namespace allstarr.Core.Storage;

public sealed class MigrationLockException(string message) : InvalidOperationException(message);

public sealed class DurableMigrationLock
{
    private const int PostgresLockNamespace = 1097624691;
    private readonly DurableStorageOptions _options;

    public DurableMigrationLock(DurableStorageOptions options)
    {
        _options = options;
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(_options.MigrationLockTimeoutSeconds);
        return await AcquirePostgresAsync(timeout, cancellationToken);
    }

    private async Task<IAsyncDisposable> AcquirePostgresAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (stopwatch.Elapsed < timeout)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT pg_try_advisory_lock({PostgresLockNamespace}, hashtext(current_database()))";
                if (await command.ExecuteScalarAsync(cancellationToken) is true)
                {
                    return new PostgresLease(connection);
                }

                await Task.Delay(100, cancellationToken);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        throw new MigrationLockException("Timed out waiting for the durable database migration lock.");
    }

    private sealed class PostgresLease(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT pg_advisory_unlock({PostgresLockNamespace}, hashtext(current_database()))";
                await command.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
