using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class DurableBackupServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private string _databasePath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private DurableStorageOptions _options = null!;
    private DurableStorageState _state = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "source.db");
        _options = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={_databasePath}",
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(_options.ConnectionString)
            .Options;
        _factory = new TestDbContextFactory(dbOptions);
        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
        context.Jobs.Add(Job("before-backup"));
        await context.SaveChangesAsync();
        _state = new DurableStorageState(_options);
        _state.Set(DurableStorageReadiness.Ready, "InitialDurableFoundation");
    }

    [Fact]
    public async Task SqliteBackup_IsConsistentVerifiedAndRestorableToIsolatedDatabase()
    {
        var service = Service(new StorageProcessRunner());

        var artifact = await service.CreateAsync();
        await using (var context = await _factory.CreateDbContextAsync())
        {
            context.Jobs.Add(Job("after-backup"));
            await context.SaveChangesAsync();
        }

        var restoredPath = Path.Combine(_root, "restored", "allstarr.db");
        await service.RestoreSqliteToAsync(
            artifact,
            $"Data Source={restoredPath}",
            overwrite: false);

        var restoredOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={restoredPath}")
            .Options;
        await using var restored = new AllstarrDbContext(restoredOptions);
        var jobs = await restored.Jobs.AsNoTracking().ToListAsync();
        Assert.Single(jobs);
        Assert.Equal("before-backup", jobs[0].IdempotencyKey);
        Assert.True(File.Exists(artifact.ManifestPath));
        Assert.False(File.Exists(artifact.ArtifactPath + "-wal"));
        Assert.False(File.Exists(artifact.ArtifactPath + "-shm"));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(artifact.ManifestPath));
        Assert.False(manifest.RootElement.GetProperty("SecretKeyMaterialIncluded").GetBoolean());
        await using var current = await _factory.CreateDbContextAsync();
        var record = await current.Backups.SingleAsync();
        Assert.Equal("verified", record.Status);
        Assert.NotNull(record.VerifiedAt);
        Assert.Equal("verified", record.RestoreStatus);
        Assert.NotNull(record.RestoreVerifiedAt);
    }

    [Fact]
    public async Task BackupVerification_RejectsTamperedArtifact()
    {
        var service = Service(new StorageProcessRunner());
        var artifact = await service.CreateAsync();
        await File.AppendAllTextAsync(artifact.ArtifactPath, "tampered");

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            service.VerifyAsync(artifact));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestLoading_RejectsUnknownFieldsSecretsAndSchemaMismatch()
    {
        var service = Service(new StorageProcessRunner());
        var artifactPath = Path.Combine(_root, "strict-manifest.sqlite");
        await File.WriteAllBytesAsync(artifactPath, [1, 2, 3, 4]);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifactPath)))
            .ToLowerInvariant();
        await using var context = await _factory.CreateDbContextAsync();
        var currentSchema = context.Database.GetMigrations().Last();
        var id = Guid.CreateVersion7();
        var createdAt = DateTimeOffset.UtcNow;
        var manifestPath = artifactPath + ".manifest.json";

        await WriteManifest(manifestPath, id, "Sqlite", Path.GetFileName(artifactPath), hash,
            currentSchema, createdAt);
        var loaded = await service.LoadArtifactAsync(
            artifactPath,
            manifestPath,
            DurableStorageProvider.Sqlite,
            hash);
        Assert.Equal(id, loaded.Id);
        Assert.Equal(currentSchema, loaded.SchemaVersion);

        await File.WriteAllTextAsync(
            manifestPath,
            (await File.ReadAllTextAsync(manifestPath)).Replace(
                "\"SecretKeyMaterialIncluded\":false",
                "\"SecretKeyMaterialIncluded\":false,\"Unexpected\":true",
                StringComparison.Ordinal));
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.LoadArtifactAsync(
            artifactPath,
            manifestPath,
            DurableStorageProvider.Sqlite,
            hash));

        await WriteManifest(manifestPath, id, "Sqlite", Path.GetFileName(artifactPath), hash,
            currentSchema, createdAt, secretKeyMaterialIncluded: true);
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.LoadArtifactAsync(
            artifactPath,
            manifestPath,
            DurableStorageProvider.Sqlite,
            hash));

        await WriteManifest(manifestPath, id, "Sqlite", Path.GetFileName(artifactPath), hash,
            "20200101000000_OldSchema", createdAt);
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.LoadArtifactAsync(
            artifactPath,
            manifestPath,
            DurableStorageProvider.Sqlite,
            hash));
    }

    [Fact]
    public async Task SqliteRestore_DoesNotCutOverWhenIndependentTargetVerificationFails()
    {
        await using var context = await _factory.CreateDbContextAsync();
        var schema = context.Database.GetMigrations().Last();
        var verifier = new SequencedRestoreVerifier(schema, failOnCall: 3);
        var service = Service(new StorageProcessRunner(), verifier);
        var artifact = await service.CreateAsync();
        var targetPath = Path.Combine(_root, "rejected", "allstarr.db");

        await Assert.ThrowsAsync<BackupVerificationException>(() =>
            service.RestoreSqliteToAsync(
                artifact,
                $"Data Source={targetPath}",
                overwrite: false));

        Assert.False(File.Exists(targetPath));
        await using var verification = await _factory.CreateDbContextAsync();
        var record = await verification.Backups.SingleAsync(item => item.Id == artifact.Id);
        Assert.Equal("verification_failed", record.RestoreStatus);
        Assert.Null(record.RestoreVerifiedAt);
    }

    [Fact]
    public async Task PostgresRestore_UsesEnvironmentForPasswordAndNeverCommandArguments()
    {
        var (artifact, currentSchema) = await PostgresArtifact("fixture.dump");
        var postgresOptions = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = "Host=db.internal;Port=5433;Database=allstarr_live;Username=operator;Password=fixture-password;SSL Mode=Require",
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var state = new DurableStorageState(postgresOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var runner = new RecordingProcessRunner();
        var verifier = new SequencedRestoreVerifier(currentSchema);
        var service = new DurableBackupService(_factory, postgresOptions, state, runner, verifier);

        await service.RestorePostgresAsync(
            artifact,
            "Host=db.internal;Port=5433;Database=allstarr_restore;Username=operator;Password=fixture-password;SSL Mode=Require",
            destructiveRestoreConfirmed: true,
            isolatedTargetDatabaseConfirmation: "allstarr_restore");

        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal("pg_restore", runner.Requests[0].FileName);
        Assert.Contains("--list", runner.Requests[0].Arguments);
        var restore = runner.Requests[1];
        Assert.Contains("allstarr_restore", restore.Arguments);
        Assert.DoesNotContain(restore.Arguments, argument =>
            argument.Contains("fixture-password", StringComparison.Ordinal));
        Assert.Equal("fixture-password", restore.Environment["PGPASSWORD"]);
        Assert.Equal("db.internal", restore.Environment["PGHOST"]);
        Assert.Equal(DurableStorageProvider.Postgres, verifier.Providers.Single());
        await using var verification = await _factory.CreateDbContextAsync();
        var record = await verification.Backups.SingleAsync(item => item.Id == artifact.Id);
        Assert.Equal("verified", record.RestoreStatus);
        Assert.NotNull(record.RestoreVerifiedAt);
    }

    [Fact]
    public async Task PostgresRestore_RejectsConfiguredCurrentDatabaseBeforeRunningRestore()
    {
        var (artifact, currentSchema) = await PostgresArtifact("live-target.dump");
        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = "Host=db.internal;Port=5432;Database=allstarr_live;Username=operator;Password=live-secret",
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, currentSchema);
        var runner = new RecordingProcessRunner();
        var service = new DurableBackupService(
            _factory,
            options,
            state,
            runner,
            new SequencedRestoreVerifier(currentSchema));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestorePostgresAsync(
                artifact,
                "Host=db.internal;Port=5432;Database=ALLSTARR_LIVE;Username=operator;Password=other-secret",
                destructiveRestoreConfirmed: true,
                isolatedTargetDatabaseConfirmation: "ALLSTARR_LIVE"));

        Assert.Contains("current database", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task PostgresRestore_RequiresExactIsolatedTargetDatabaseConfirmation()
    {
        var (artifact, currentSchema) = await PostgresArtifact("confirmation-target.dump");
        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = "Host=db.internal;Database=allstarr_live;Username=operator;Password=live-secret",
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, currentSchema);
        var runner = new RecordingProcessRunner();
        var service = new DurableBackupService(
            _factory,
            options,
            state,
            runner,
            new SequencedRestoreVerifier(currentSchema));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestorePostgresAsync(
                artifact,
                "Host=db.internal;Database=allstarr_restore;Username=operator;Password=fixture",
                destructiveRestoreConfirmed: true,
                isolatedTargetDatabaseConfirmation: "wrong_target"));

        Assert.Contains("confirmed exactly", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    private DurableBackupService Service(
        IStorageProcessRunner runner,
        IDurableRestoreTargetVerifier? verifier = null) =>
        new(_factory, _options, _state, runner, verifier);

    private async Task<(BackupArtifact Artifact, string SchemaVersion)> PostgresArtifact(string fileName)
    {
        var artifactPath = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(artifactPath, [1, 2, 3, 4]);
        var manifestPath = artifactPath + ".manifest.json";
        await using var context = await _factory.CreateDbContextAsync();
        var currentSchema = context.Database.GetMigrations().Last();
        var id = Guid.CreateVersion7();
        var createdAt = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifactPath)))
            .ToLowerInvariant();
        await WriteManifest(
            manifestPath,
            id,
            "Postgres",
            Path.GetFileName(artifactPath),
            hash,
            currentSchema,
            createdAt);
        return (new BackupArtifact(
            id,
            DurableStorageProvider.Postgres,
            artifactPath,
            manifestPath,
            hash,
            currentSchema,
            createdAt), currentSchema);
    }

    private static Task WriteManifest(
        string path,
        Guid id,
        string provider,
        string artifactFile,
        string sha256,
        string schemaVersion,
        DateTimeOffset createdAt,
        bool secretKeyMaterialIncluded = false) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            FormatVersion = 1,
            Id = id,
            Provider = provider,
            ArtifactFile = artifactFile,
            Sha256 = sha256,
            SchemaVersion = schemaVersion,
            ApplicationVersion = AppVersion.Version,
            CreatedAt = createdAt,
            SecretKeyMaterialIncluded = secretKeyMaterialIncluded
        }));

    private static DurableJobRecord Job(string idempotencyKey) => new()
    {
        Id = Guid.CreateVersion7(),
        ScopeKey = "global",
        Type = "fixture",
        PayloadJson = "{}",
        IdempotencyKey = idempotencyKey,
        State = DurableJobState.Pending,
        MaxAttempts = 3,
        AvailableAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class RecordingProcessRunner : IStorageProcessRunner
    {
        public List<StorageProcessRequest> Requests { get; } = [];

        public Task<StorageProcessResult> RunAsync(
            StorageProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new StorageProcessResult(0, null));
        }
    }

    private sealed class SequencedRestoreVerifier(string schemaVersion, int failOnCall = -1)
        : IDurableRestoreTargetVerifier
    {
        private int _calls;
        public List<DurableStorageProvider> Providers { get; } = [];

        public Task<DurableSchemaCompatibilitySnapshot> VerifyAsync(
            DurableStorageProvider provider,
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            Providers.Add(provider);
            if (++_calls == failOnCall)
            {
                throw new BackupVerificationException("Restored target schema verification failed.");
            }

            return Task.FromResult(new DurableSchemaCompatibilitySnapshot(
                DurableSchemaCompatibilityStatus.Current,
                schemaVersion,
                schemaVersion,
                [],
                []));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
