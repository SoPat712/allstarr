using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

internal sealed class SqliteTestDatabase : IAsyncDisposable
{
    private static readonly SemaphoreSlim TemplateGate = new(1, 1);
    private static string? _templatePath;

    private SqliteTestDatabase(string path, bool templateBacked)
    {
        DatabasePath = path;
        DatabaseName = Path.GetFileName(Path.GetDirectoryName(path)!);
        IsTemplateBacked = templateBacked;
        ConnectionString = Connection(path);
        Options = BuildOptions(path);
    }

    public string DatabasePath { get; }
    public string DatabaseName { get; }
    public string ConnectionString { get; }
    public bool IsTemplateBacked { get; }
    public DbContextOptions<AllstarrDbContext> Options { get; }
    public StorageOptions StorageOptions => new()
    {
        DataDirectory = Path.GetDirectoryName(DatabasePath)!,
        DatabaseFileName = Path.GetFileName(DatabasePath)
    };

    public static async Task<SqliteTestDatabase> CreateAsync(bool useTemplate = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "allstarr.db");
        if (useTemplate)
            File.Copy(await EnsureTemplateAsync(), path);
        return new SqliteTestDatabase(path, useTemplate);
    }

    private static string Connection(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false,
        DefaultTimeout = 30,
        ForeignKeys = true
    }.ToString();

    private static DbContextOptions<AllstarrDbContext> BuildOptions(string path) =>
        new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(Connection(path))
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

    private static async Task<string> EnsureTemplateAsync()
    {
        await TemplateGate.WaitAsync();
        try
        {
            if (_templatePath != null) return _templatePath;
            var directory = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "template.db");
            await using var context = new AllstarrDbContext(BuildOptions(path));
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await context.Database.MigrateAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            await context.Database.CloseConnectionAsync();
            _templatePath = path;
            return path;
        }
        finally
        {
            TemplateGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            File.Delete(DatabasePath + suffix);
        Directory.Delete(Path.GetDirectoryName(DatabasePath)!, recursive: true);
        return ValueTask.CompletedTask;
    }
}
