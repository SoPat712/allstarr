using Microsoft.Data.Sqlite;
using Npgsql;

namespace allstarr.Core.Storage;

public enum DurableStorageProvider
{
    Postgres,
    Sqlite
}

public sealed class DurableStorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = nameof(DurableStorageProvider.Postgres);

    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=allstarr;Username=allstarr";

    public string? PasswordFile { get; set; }

    public bool AutoMigrate { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 30;

    public int ConnectionRetryCount { get; set; } = 3;

    public int MigrationLockTimeoutSeconds { get; set; } = 120;

    public int RuntimeProbeIntervalSeconds { get; set; } = 5;

    public int RuntimeProbeTimeoutSeconds { get; set; } = 5;

    public bool EnforceMutationGuard { get; set; } = true;

    public string BackupDirectory { get; set; } = "/app/state/backups";

    public DurableStorageProvider ParseProvider()
    {
        if (!Enum.TryParse<DurableStorageProvider>(Provider, ignoreCase: true, out var parsed))
        {
            throw new InvalidOperationException(
                $"Storage:Provider must be '{nameof(DurableStorageProvider.Postgres)}' or " +
                $"'{nameof(DurableStorageProvider.Sqlite)}'.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("Storage:ConnectionString is required.");
        }

        if (CommandTimeoutSeconds is < 1 or > 600)
        {
            throw new InvalidOperationException("Storage:CommandTimeoutSeconds must be between 1 and 600.");
        }

        if (ConnectionRetryCount is < 0 or > 10)
        {
            throw new InvalidOperationException("Storage:ConnectionRetryCount must be between 0 and 10.");
        }

        if (MigrationLockTimeoutSeconds is < 5 or > 1800)
        {
            throw new InvalidOperationException(
                "Storage:MigrationLockTimeoutSeconds must be between 5 and 1800.");
        }

        if (RuntimeProbeIntervalSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException(
                "Storage:RuntimeProbeIntervalSeconds must be between 1 and 300.");
        }

        if (RuntimeProbeTimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException(
                "Storage:RuntimeProbeTimeoutSeconds must be between 1 and 60.");
        }

        return parsed;
    }

    public void ApplyPasswordFile(DurableStorageProvider provider)
    {
        if (string.IsNullOrWhiteSpace(PasswordFile))
        {
            return;
        }

        if (provider != DurableStorageProvider.Postgres)
        {
            throw new InvalidOperationException("Storage:PasswordFile is supported only with Postgres.");
        }

        var path = Path.GetFullPath(PasswordFile);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The configured database password file is missing.", path);
        }

        var password = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("The configured database password file is empty.");
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Password = password
        };
        ConnectionString = builder.ConnectionString;
    }

    public string? GetSqlitePath()
    {
        if (ParseProvider() != DurableStorageProvider.Sqlite)
        {
            return null;
        }

        return GetSqlitePath(new SqliteConnectionStringBuilder(ConnectionString));
    }

    private static string? GetSqlitePath(SqliteConnectionStringBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
        {
            return null;
        }

        return Path.GetFullPath(builder.DataSource);
    }
}
