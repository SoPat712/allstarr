using System.Text.Json;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class StorageOperatorCommandTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
    private readonly List<PostgresTestDatabase> _databases = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PostgresRestore_CliRequiresExactIsolatedTargetProof()
    {
        var database = await CreateDatabase();
        var services = Services(database, Path.Combine(_root, "operator-proof-backups"));
        var error = new StringWriter();

        var exit = await StorageOperatorCommand.RunAsync(
            services,
            ["storage", "restore-postgres", "--confirm-destructive-restore"],
            TextWriter.Null,
            error);

        Assert.Equal(1, exit);
        Assert.Contains("invalid_arguments", error.ToString(), StringComparison.Ordinal);

        var help = new StringWriter();
        Assert.Equal(0, await StorageOperatorCommand.RunAsync(
            services,
            ["storage", "help"],
            help,
            TextWriter.Null));
        Assert.Contains(
            "--confirm-isolated-target-database <name>",
            help.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("restore-sqlite", help.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task StateTransfer_RequiresConfirmationsAndImportsIntoEmptyPostgresTarget()
    {
        var sourceDatabase = await CreateDatabase();
        var sourceServices = Services(
            sourceDatabase,
            Path.Combine(_root, "source-backups"));
        var tenantId = Guid.CreateVersion7();
        await using (var provider = sourceServices.BuildServiceProvider())
        {
            await provider.GetRequiredService<DurableStorageInitializer>()
                .StartAsync(CancellationToken.None);
            var factory = provider.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
            await using var source = await factory.CreateDbContextAsync();
            source.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = "transfer-fixture",
                Name = "Transfer fixture",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await source.SaveChangesAsync();
        }

        var rejectedError = new StringWriter();
        var rejected = await StorageOperatorCommand.RunAsync(
            sourceServices,
            ["storage", "export", "--output", Path.Combine(_root, "exports")],
            TextWriter.Null,
            rejectedError);
        Assert.Equal(1, rejected);

        var exportOutput = new StringWriter();
        Assert.Equal(0, await StorageOperatorCommand.RunAsync(
            sourceServices,
            [
                "storage", "export",
                "--output", Path.Combine(_root, "exports"),
                "--confirm-writes-stopped"
            ],
            exportOutput,
            TextWriter.Null));
        using var exportJson = JsonDocument.Parse(exportOutput.ToString());
        var artifact = exportJson.RootElement.GetProperty("artifactPath").GetString()!;
        var hash = exportJson.RootElement.GetProperty("sha256").GetString()!;

        var targetDatabase = await CreateDatabase();
        var targetServices = Services(
            targetDatabase,
            Path.Combine(_root, "target-backups"));
        await using (var provider = targetServices.BuildServiceProvider())
        {
            await provider.GetRequiredService<DurableStorageInitializer>()
                .StartAsync(CancellationToken.None);
        }
        var importOutput = new StringWriter();

        Assert.Equal(0, await StorageOperatorCommand.RunAsync(
            targetServices,
            [
                "storage", "import",
                "--artifact", artifact,
                "--sha256", hash,
                "--confirm-empty-target"
            ],
            importOutput,
            TextWriter.Null));

        await using var target = new AllstarrDbContext(targetDatabase.Options);
        Assert.Equal(tenantId, (await target.Tenants.SingleAsync()).Id);
    }

    [Fact]
    public async Task RotateSecrets_ReencryptsActiveReferencesOnlyAfterExplicitConfirmation()
    {
        var database = await CreateDatabase();
        var keyRingPath = Path.Combine(_root, "operator-keyring.json");
        var key1 = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await WriteKeyRing(keyRingPath, "key-1", new Dictionary<string, byte[]>
        {
            ["key-1"] = key1
        });
        var services = Services(database, Path.Combine(_root, "secret-backups"), keyRingPath);
        await using (var provider = services.BuildServiceProvider())
        {
            await provider.GetRequiredService<DurableStorageInitializer>()
                .StartAsync(CancellationToken.None);
            await provider.GetRequiredService<EncryptedSecretStore>().StoreAsync(
                tenantId: null,
                purpose: "operator.fixture",
                plaintext: System.Text.Encoding.UTF8.GetBytes("operator-fixture-secret"));
        }

        await WriteKeyRing(keyRingPath, "key-2", new Dictionary<string, byte[]>
        {
            ["key-1"] = key1,
            ["key-2"] = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)
        });
        var rejected = await StorageOperatorCommand.RunAsync(
            services,
            ["storage", "rotate-secrets"],
            TextWriter.Null,
            TextWriter.Null);
        var output = new StringWriter();
        var rotated = await StorageOperatorCommand.RunAsync(
            services,
            ["storage", "rotate-secrets", "--confirm-writes-stopped"],
            output,
            TextWriter.Null);

        Assert.Equal(1, rejected);
        Assert.Equal(0, rotated);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("key-2", result.RootElement.GetProperty("activeKeyId").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("Rotated").GetInt32());
    }

    private async Task<PostgresTestDatabase> CreateDatabase()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        _databases.Add(database);
        return database;
    }

    private static ServiceCollection Services(
        PostgresTestDatabase database,
        string backupDirectory,
        string? keyRingPath = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Postgres",
            ["Storage:ConnectionString"] = database.ConnectionString,
            ["Storage:BackupDirectory"] = backupDirectory,
            ["Storage:ConnectionRetryCount"] = "0"
        };
        if (keyRingPath != null)
        {
            values["Secrets:KeyRingPath"] = keyRingPath;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddDurableStorage(configuration, new TestEnvironment());
        if (keyRingPath != null)
        {
            services.AddEncryptedSecretStore(configuration);
        }
        return services;
    }

    private static async Task WriteKeyRing(
        string path,
        string activeKeyId,
        IReadOnlyDictionary<string, byte[]> keys)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            activeKeyId,
            keys = keys.ToDictionary(item => item.Key, item => Convert.ToBase64String(item.Value))
        }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await database.DisposeAsync();
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "allstarr.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
