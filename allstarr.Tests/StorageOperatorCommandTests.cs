using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Core.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class StorageOperatorCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));

    public StorageOperatorCommandTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task BackupAndOfflineSqliteRestore_ProduceVerifiedStandaloneArtifact()
    {
        var sourcePath = Path.Combine(_root, "source.db");
        var services = Services(sourcePath, Path.Combine(_root, "backups"));
        var output = new StringWriter();
        var error = new StringWriter();

        var backupExit = await StorageOperatorCommand.RunAsync(
            services,
            ["storage", "backup"],
            output,
            error);

        Assert.Equal(0, backupExit);
        Assert.Equal(string.Empty, error.ToString());
        using var backupJson = JsonDocument.Parse(output.ToString());
        var artifactPath = backupJson.RootElement.GetProperty("artifactPath").GetString()!;
        var manifestPath = backupJson.RootElement.GetProperty("manifestPath").GetString()!;
        var sha256 = backupJson.RootElement.GetProperty("sha256").GetString()!;
        Assert.True(File.Exists(artifactPath));
        Assert.True(File.Exists(manifestPath));
        Assert.False(File.Exists(artifactPath + "-wal"));
        Assert.False(File.Exists(artifactPath + "-shm"));

        var restoredPath = Path.Combine(_root, "restored", "allstarr.db");
        output.GetStringBuilder().Clear();
        var restoreExit = await StorageOperatorCommand.RunAsync(
            services,
            [
                "storage", "restore-sqlite",
                "--artifact", artifactPath,
                "--sha256", sha256,
                "--target", restoredPath,
                "--confirm-target-offline"
            ],
            output,
            error);

        Assert.Equal(0, restoreExit);
        Assert.True(File.Exists(restoredPath));
        var restoredOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={restoredPath}")
            .Options;
        await using var restored = new AllstarrDbContext(restoredOptions);
        Assert.Equal(
            restored.Database.GetMigrations().Count(),
            (await restored.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task PostgresRestore_CliRequiresExactIsolatedTargetProof()
    {
        var services = Services(
            Path.Combine(_root, "operator-proof.db"),
            Path.Combine(_root, "operator-proof-backups"));
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
    }

    [Fact]
    public async Task ProviderTransfer_RequiresConfirmationsAndImportsIntoEmptyTarget()
    {
        var sourcePath = Path.Combine(_root, "transfer-source.db");
        var sourceServices = Services(sourcePath, Path.Combine(_root, "source-backups"));
        await StorageOperatorCommand.RunAsync(
            sourceServices,
            ["storage", "backup"],
            TextWriter.Null,
            TextWriter.Null);
        var sourceOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={sourcePath}")
            .Options;
        var tenantId = Guid.CreateVersion7();
        await using (var source = new AllstarrDbContext(sourceOptions))
        {
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
        Assert.Contains("storage_command_failed", rejectedError.ToString(), StringComparison.Ordinal);

        var exportOutput = new StringWriter();
        var exported = await StorageOperatorCommand.RunAsync(
            sourceServices,
            [
                "storage", "export",
                "--output", Path.Combine(_root, "exports"),
                "--confirm-writes-stopped"
            ],
            exportOutput,
            TextWriter.Null);
        Assert.Equal(0, exported);
        using var exportJson = JsonDocument.Parse(exportOutput.ToString());
        var artifact = exportJson.RootElement.GetProperty("artifactPath").GetString()!;
        var hash = exportJson.RootElement.GetProperty("sha256").GetString()!;

        var targetPath = Path.Combine(_root, "transfer-target.db");
        var targetServices = Services(targetPath, Path.Combine(_root, "target-backups"));
        var importOutput = new StringWriter();
        var imported = await StorageOperatorCommand.RunAsync(
            targetServices,
            [
                "storage", "import",
                "--artifact", artifact,
                "--sha256", hash,
                "--confirm-empty-target"
            ],
            importOutput,
            TextWriter.Null);

        Assert.Equal(0, imported);
        var targetOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={targetPath}")
            .Options;
        await using var target = new AllstarrDbContext(targetOptions);
        Assert.Equal(tenantId, (await target.Tenants.SingleAsync()).Id);
    }

    [Fact]
    public async Task RotateSecrets_ReencryptsActiveReferencesOnlyAfterExplicitConfirmation()
    {
        var databasePath = Path.Combine(_root, "secret-rotation.db");
        var keyRingPath = Path.Combine(_root, "operator-keyring.json");
        var key1 = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await WriteKeyRing(keyRingPath, "key-1", new Dictionary<string, byte[]>
        {
            ["key-1"] = key1
        });
        var services = ServicesWithSecrets(databasePath, keyRingPath);
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

    private static ServiceCollection Services(string databasePath, string backupDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "Sqlite",
                ["Storage:ConnectionString"] = $"Data Source={databasePath}",
                ["Storage:BackupDirectory"] = backupDirectory,
                ["Storage:ConnectionRetryCount"] = "0"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddDurableStorage(configuration, new TestEnvironment(), allowOfflineSqlite: true);
        return services;
    }

    private static ServiceCollection ServicesWithSecrets(
        string databasePath,
        string keyRingPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "Sqlite",
                ["Storage:ConnectionString"] = $"Data Source={databasePath}",
                ["Storage:BackupDirectory"] = Path.Combine(Path.GetDirectoryName(databasePath)!, "backups"),
                ["Storage:ConnectionRetryCount"] = "0",
                ["Secrets:KeyRingPath"] = keyRingPath
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddDurableStorage(configuration, new TestEnvironment(), allowOfflineSqlite: true);
        services.AddEncryptedSecretStore(configuration);
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

    public void Dispose()
    {
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
