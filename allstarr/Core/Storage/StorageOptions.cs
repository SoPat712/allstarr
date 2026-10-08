using Microsoft.Data.Sqlite;

namespace allstarr.Core.Storage;

public enum DurableStorageProvider
{
    Sqlite,
    Postgres
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string DataDirectory { get; set; } = "/app/data";
    public string DatabaseFileName { get; set; } = "allstarr.db";
    public bool AutoMigrate { get; set; } = true;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public string DatabasePath => Path.Combine(Path.GetFullPath(DataDirectory), DatabaseFileName);
    public string BackupDirectory => Path.Combine(Path.GetFullPath(DataDirectory), "backups");
    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = CommandTimeoutSeconds
    }.ToString();

    public DurableStorageProvider ParseProvider()
    {
        Validate();
        return DurableStorageProvider.Sqlite;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidOperationException("Storage:DataDirectory is required.");
        if (string.IsNullOrWhiteSpace(DatabaseFileName) || DatabaseFileName is "." or ".." ||
            DatabaseFileName.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(DatabaseFileName) ||
            DatabaseFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Storage:DatabaseFileName must be a file name inside the data directory.");
        if (CommandTimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("Storage:CommandTimeoutSeconds must be between 1 and 600.");
    }
}
