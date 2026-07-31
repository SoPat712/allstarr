using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Sdk;

namespace allstarr.Tests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private static readonly SemaphoreSlim TemplateGate = new(1, 1);
    private static string? _templateName;
    private readonly string _adminConnectionString;

    private PostgresTestDatabase(
        string databaseName,
        string connectionString,
        string adminConnectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        _adminConnectionString = adminConnectionString;
        Options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public DbContextOptions<AllstarrDbContext> Options { get; }

    public static async Task<PostgresTestDatabase> CreateAsync(bool useTemplate = true)
    {
        var configured = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw SkipException.ForSkip(
                "PostgreSQL integration tests require ALLSTARR_TEST_POSTGRES.");
        }

        var source = new NpgsqlConnectionStringBuilder(configured);
        var databaseName = $"allstarr_test_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false
        };
        var isolated = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = databaseName,
            Pooling = false
        };
        var templateName = useTemplate
            ? await EnsureTemplateAsync(configured, admin.ConnectionString)
            : null;

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = templateName == null
                ? $"CREATE DATABASE {QuoteIdentifier(databaseName)}"
                : $"CREATE DATABASE {QuoteIdentifier(databaseName)} TEMPLATE {QuoteIdentifier(templateName)}";
            await command.ExecuteNonQueryAsync();
        }

        return new PostgresTestDatabase(
            databaseName,
            isolated.ConnectionString,
            admin.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(DatabaseName)} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> EnsureTemplateAsync(
        string configured,
        string adminConnectionString)
    {
        if (_templateName != null) return _templateName;

        await TemplateGate.WaitAsync();
        try
        {
            if (_templateName != null) return _templateName;

            var options = new DbContextOptionsBuilder<AllstarrDbContext>()
                .UseNpgsql(configured)
                .Options;
            await using var model = new AllstarrDbContext(options);
            var latestMigration = model.Database.GetMigrations().Last();
            var templateName = $"allstarr_test_template_{latestMigration.Split('_', 2)[0]}";

            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(hashtext('allstarr-test-template'))";
            await command.ExecuteNonQueryAsync();
            try
            {
                command.CommandText =
                    "SELECT EXISTS (SELECT FROM pg_database WHERE datname = @name)";
                command.Parameters.AddWithValue("name", templateName);
                var exists = (bool)(await command.ExecuteScalarAsync())!;
                command.Parameters.Clear();
                if (!exists)
                {
                    command.CommandText = $"CREATE DATABASE {QuoteIdentifier(templateName)}";
                    await command.ExecuteNonQueryAsync();
                }

                var templateConnection = new NpgsqlConnectionStringBuilder(configured)
                {
                    Database = templateName,
                    Pooling = false
                };
                var templateOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
                    .UseNpgsql(templateConnection.ConnectionString)
                    .Options;
                await using var template = new AllstarrDbContext(templateOptions);
                await template.Database.MigrateAsync();
                _templateName = templateName;
                return templateName;
            }
            finally
            {
                command.CommandText =
                    "SELECT pg_advisory_unlock(hashtext('allstarr-test-template'))";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            TemplateGate.Release();
        }
    }

    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
