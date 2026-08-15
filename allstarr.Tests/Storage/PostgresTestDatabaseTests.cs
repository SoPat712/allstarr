using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace allstarr.Tests;

public sealed class PostgresTestDatabaseTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task DefaultClone_IsCurrentTemplateBackedAndUsesBoundedPool()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);

        Assert.True(database.IsTemplateBacked);
        Assert.True(connection.Pooling);
        Assert.Equal(0, connection.MinPoolSize);
        Assert.Equal(4, connection.MaxPoolSize);

        await using var context = new AllstarrDbContext(database.Options);
        var expected = context.Database.GetMigrations().Last();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).Last();
        Assert.Equal(expected, applied);

        await using var untemplated = await PostgresTestDatabase.CreateAsync(useTemplate: false);
        Assert.False(untemplated.IsTemplateBacked);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Clones_AreIsolated()
    {
        await using var first = await PostgresTestDatabase.CreateAsync();
        await using var second = await PostgresTestDatabase.CreateAsync();

        var tenantId = Guid.CreateVersion7();
        await using (var context = new AllstarrDbContext(first.Options))
        {
            context.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = "postgres-fixture-isolation",
                Name = "Fixture isolation",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using var isolatedContext = new AllstarrDbContext(second.Options);
        Assert.False(await isolatedContext.Tenants.AnyAsync(item => item.Id == tenantId));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Disposal_ClearsClonePoolBeforeDroppingDatabase()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        var databaseName = database.DatabaseName;
        try
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
        finally
        {
            await database.DisposeAsync();
        }

        var admin = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES")!)
        {
            Database = "postgres",
            Pooling = false
        };
        await using var verify = new NpgsqlConnection(admin.ConnectionString);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT EXISTS (SELECT FROM pg_database WHERE datname = @name)";
        verifyCommand.Parameters.AddWithValue("name", databaseName);
        Assert.False((bool)(await verifyCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ParallelCreation_UsesBoundedClonePools()
    {
        var databases = await Task.WhenAll(
            Enumerable.Range(0, 4)
                .Select(_ => PostgresTestDatabase.CreateAsync()));
        try
        {
            Assert.Equal(databases.Length, databases.Select(item => item.DatabaseName).Distinct().Count());
            Assert.All(databases, item => Assert.Equal(
                4,
                new NpgsqlConnectionStringBuilder(item.ConnectionString).MaxPoolSize));
        }
        finally
        {
            foreach (var database in databases)
            {
                await database.DisposeAsync();
            }
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task PostCleanup_CreateAsyncRecovers()
    {
        await using (var first = await PostgresTestDatabase.CreateAsync())
        {
            await using var connection = new NpgsqlConnection(first.ConnectionString);
            await connection.OpenAsync();
        }

        await using var second = await PostgresTestDatabase.CreateAsync();
        Assert.True(second.IsTemplateBacked);
    }
}
