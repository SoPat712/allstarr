using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class DurableBackupServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private StorageOptions _options = null!;
    private DurableStorageState _state = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using var context = await _factory.CreateDbContextAsync();
        context.Jobs.Add(Job("backup-fixture"));
        await context.SaveChangesAsync();
        _options = new StorageOptions
        {
            DataDirectory = _root
        };
        _state = new DurableStorageState(_options);
        _state.Set(DurableStorageReadiness.Ready, context.Database.GetMigrations().Last());
    }

    [Fact]
    public async Task BackupVerification_RejectsTamperedPostgresArtifact()
    {
        var service = Service(new RecordingProcessRunner());
        var (artifact, _) = await PostgresArtifact("tampered.dump");
        await File.AppendAllTextAsync(artifact.ArtifactPath, "tampered");

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            service.VerifyAsync(artifact));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestLoading_RejectsUnknownFieldsSecretsAndSchemaMismatch()
    {
        var service = Service(new RecordingProcessRunner());
        var (artifact, currentSchema) = await PostgresArtifact("strict-manifest.dump");
        var loaded = await service.LoadArtifactAsync(
            artifact.ArtifactPath,
            artifact.ManifestPath,
            DurableStorageProvider.Postgres,
            artifact.Sha256);
        Assert.Equal(artifact.Id, loaded.Id);

        await File.WriteAllTextAsync(
            artifact.ManifestPath,
            (await File.ReadAllTextAsync(artifact.ManifestPath)).Replace(
                "\"SecretKeyMaterialIncluded\":false",
                "\"SecretKeyMaterialIncluded\":false,\"Unexpected\":true",
                StringComparison.Ordinal));
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.LoadArtifactAsync(
            artifact.ArtifactPath,
            artifact.ManifestPath,
            DurableStorageProvider.Postgres,
            artifact.Sha256));

        await WriteManifest(
            artifact.ManifestPath,
            artifact.Id,
            "Postgres",
            Path.GetFileName(artifact.ArtifactPath),
            artifact.Sha256,
            currentSchema,
            artifact.CreatedAt,
            secretKeyMaterialIncluded: true);
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.LoadArtifactAsync(
            artifact.ArtifactPath,
            artifact.ManifestPath,
            DurableStorageProvider.Postgres,
            artifact.Sha256));

        await WriteManifest(
            artifact.ManifestPath,
            artifact.Id,
            "Postgres",
            Path.GetFileName(artifact.ArtifactPath),
            artifact.Sha256,
            "20200101000000_OldSchema",
            artifact.CreatedAt);
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.LoadArtifactAsync(
            artifact.ArtifactPath,
            artifact.ManifestPath,
            DurableStorageProvider.Postgres,
            artifact.Sha256));
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

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
