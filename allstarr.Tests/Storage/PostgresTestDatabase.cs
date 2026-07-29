using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Sdk;

namespace allstarr.Tests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
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

    public static async Task<PostgresTestDatabase> CreateAsync()
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

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
            await command.ExecuteNonQueryAsync();
        }

        return new PostgresTestDatabase(
            databaseName,
            isolated.ConnectionString,
            admin.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(DatabaseName)} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
