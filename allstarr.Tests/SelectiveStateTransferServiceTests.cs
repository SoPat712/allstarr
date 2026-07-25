using System.IO.Compression;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class SelectiveStateTransferServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-selective-tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<PostgresTestDatabase> _databases = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await database.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExportSelective_OnlyIncludesRequestedCategories()
    {
        var (factory, options, state) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(factory, options, state);

        var exportDir = Path.Combine(_root, "export-only-accounts");
        var request = new SelectiveExportRequest
        {
            IncludeSettings = false,
            IncludeAccounts = true,
            IncludePlaylists = false,
            IncludeIntelligence = false,
            IncludeExtensions = false
        };

        var (artifact, report) = await service.ExportAsync(exportDir, request, writesQuiesced: true);

        Assert.True(File.Exists(artifact.Path));
        Assert.Contains("Settings", report.ExcludedCategories);
        Assert.Contains("Playlists", report.ExcludedCategories);
        Assert.Contains("Intelligence", report.ExcludedCategories);
        Assert.Contains("Extensions", report.ExcludedCategories);
        Assert.DoesNotContain("Accounts", report.ExcludedCategories);

        Assert.True(report.RowsByEntry.ContainsKey("users"));
        Assert.True(report.RowsByEntry.ContainsKey("backend-identities"));
        Assert.True(report.RowsByEntry.ContainsKey("provider-accounts"));
        Assert.False(report.RowsByEntry.ContainsKey("tenants"));
        Assert.False(report.RowsByEntry.ContainsKey("library-tracks"));

        using var archive = ZipFile.OpenRead(artifact.Path);
        var entryNames = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("users.json", entryNames);
        Assert.Contains("provider-accounts.json", entryNames);
        Assert.DoesNotContain("tenants.json", entryNames);
        Assert.DoesNotContain("library-tracks.json", entryNames);

        var manifest = await ReadManifestAsync(archive);
        Assert.Equal(false, manifest["isFullExport"]);
        var includedCategories = (object[]?)manifest["includedCategories"];
        Assert.NotNull(includedCategories);
        Assert.Equal(new object[] { "Accounts" }, includedCategories!);
    }

    [Fact]
    public async Task ImportSelective_RejectsMissingDependency()
    {
        var (sourceFactory, sourceOptions, sourceState) = await CreateSeededContextAsync();
        var sourceService = new SelectiveStateTransferService(sourceFactory, sourceOptions, sourceState);

        var exportDir = Path.Combine(_root, "export-just-accounts");
        var (artifact, _) = await sourceService.ExportAsync(
            exportDir,
            new SelectiveExportRequest
            {
                IncludeSettings = false,
                IncludeAccounts = true,
                IncludePlaylists = false,
                IncludeIntelligence = false,
                IncludeExtensions = false
            },
            writesQuiesced: true);

        var zipBytes = await File.ReadAllBytesAsync(artifact.Path);
        var backupJson = JsonSerializer.Serialize(new
        {
            archiveBase64 = Convert.ToBase64String(zipBytes)
        });

        var (targetFactory, targetOptions, targetState) = await CreateEmptyContextAsync();
        var targetService = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);

        var ex = await Assert.ThrowsAsync<SelectiveTransferValidationException>(() =>
            targetService.ImportAsync(new SelectiveImportRequest
            {
                ImportSettings = false,
                ImportAccounts = false,
                ImportPlaylists = true,
                ImportIntelligence = false,
                ImportExtensions = false,
                BackupJson = backupJson
            }));

        Assert.Contains("requires 'Settings'", ex.Message);
    }

    [Fact]
    public async Task ImportSelective_RoundTripsSettingsAndAccountsIntoEmptyTarget()
    {
        var (sourceFactory, sourceOptions, sourceState) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(sourceFactory, sourceOptions, sourceState);

        var exportDir = Path.Combine(_root, "export-settings-accounts");
        var (artifact, exportReport) = await service.ExportAsync(
            exportDir,
            new SelectiveExportRequest
            {
                IncludeSettings = true,
                IncludeAccounts = true,
                IncludePlaylists = false,
                IncludeIntelligence = false,
                IncludeExtensions = false
            },
            writesQuiesced: true);

        Assert.Equal(2, exportReport.IncludedCategories.Count);
        Assert.Contains("Settings", exportReport.IncludedCategories);
        Assert.Contains("Accounts", exportReport.IncludedCategories);

        var zipBytes = await File.ReadAllBytesAsync(artifact.Path);
        var backupJson = JsonSerializer.Serialize(new
        {
            archiveBase64 = Convert.ToBase64String(zipBytes)
        });

        var (targetFactory, targetOptions, targetState) = await CreateEmptyContextAsync();
        var targetService = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);

        var report = await targetService.ImportAsync(new SelectiveImportRequest
        {
            ImportSettings = true,
            ImportAccounts = true,
            ImportPlaylists = false,
            ImportIntelligence = false,
            ImportExtensions = false,
            BackupJson = backupJson
        });

        Assert.Equal(2, report.IncludedCategories.Count);
        Assert.Equal(1, report.RowsByEntry.GetValueOrDefault("tenants"));
        Assert.Equal(1, report.RowsByEntry.GetValueOrDefault("users"));
        Assert.Equal(1, report.RowsByEntry.GetValueOrDefault("provider-accounts"));
        Assert.False(report.RowsByEntry.ContainsKey("library-tracks"));

        await using var verify = await targetFactory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Tenants.CountAsync());
        Assert.Equal(1, await verify.Users.CountAsync());
        Assert.Equal(1, await verify.ProviderAccounts.CountAsync());
    }

    [Fact]
    public async Task ExportSelective_EmptySelectionIsRejected()
    {
        var (factory, options, state) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(factory, options, state);

        var exportDir = Path.Combine(_root, "export-empty");
        await Assert.ThrowsAsync<SelectiveTransferValidationException>(() =>
            service.ExportAsync(
                exportDir,
                new SelectiveExportRequest
                {
                    IncludeSettings = false,
                    IncludeAccounts = false,
                    IncludePlaylists = false,
                    IncludeIntelligence = false,
                    IncludeExtensions = false
                },
                writesQuiesced: true));
    }

    [Fact]
    public async Task ImportSelective_RejectsEmptyBackupPayload()
    {
        var (factory, options, state) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(factory, options, state);

        await Assert.ThrowsAsync<SelectiveTransferValidationException>(() =>
            service.ImportAsync(new SelectiveImportRequest
            {
                BackupJson = "   "
            }));
    }

    [Fact]
    public async Task ImportSelective_RejectsWrongFormatVersion()
    {
        var (factory, options, state) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(factory, options, state);

        var fakeArchivePath = Path.Combine(_root, "wrong-version.zip");
        using (var archive = ZipFile.Open(fakeArchivePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            await using var stream = manifest.Open();
            await JsonSerializer.SerializeAsync(stream, new[]
            {
                new
                {
                    formatVersion = 1,
                    isFullExport = false,
                    sourceProvider = "Postgres",
                    schemaVersion = "test",
                    applicationVersion = "test",
                    createdAt = DateTimeOffset.UtcNow,
                    secretKeyMaterialIncluded = false,
                    includedCategories = new[] { "Settings" }
                }
            });
        }
        var bytes = await File.ReadAllBytesAsync(fakeArchivePath);
        var backupJson = JsonSerializer.Serialize(new { archiveBase64 = Convert.ToBase64String(bytes) });

        await Assert.ThrowsAsync<SelectiveTransferSchemaMismatchException>(() =>
            service.ImportAsync(new SelectiveImportRequest
            {
                ImportSettings = true,
                ImportAccounts = false,
                ImportPlaylists = false,
                ImportIntelligence = false,
                ImportExtensions = false,
                BackupJson = backupJson
            }));
    }

    [Fact]
    public void ResolveIncludedCategories_RespectsEachFlag()
    {
        var service = new SelectiveStateTransferService(
            new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>().Options),
            new DurableStorageOptions { Provider = "Postgres", ConnectionString = "Host=database;Database=allstarr" },
            new DurableStorageState(new DurableStorageOptions { Provider = "Postgres", ConnectionString = "Host=database;Database=allstarr" }));

        var included = service.ResolveIncludedCategories(new SelectiveExportRequest
        {
            IncludeSettings = true,
            IncludeAccounts = false,
            IncludePlaylists = true,
            IncludeIntelligence = false,
            IncludeExtensions = true
        });

        Assert.Equal(new[] { "Settings", "Playlists", "Extensions" }, included.Select(c => c.ToString()));
    }

    private static async Task<Dictionary<string, object>> ReadManifestAsync(ZipArchive archive)
    {
        var entry = archive.Entries.First(e => e.FullName == "manifest.json");
        await using var stream = entry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement[0];
        var result = new Dictionary<string, object>();
        foreach (var prop in root.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Array => prop.Value.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())
                    .ToArray(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.ToString()
            };
        }
        return result;
    }

    private async Task<(TestDbContextFactory Factory, DurableStorageOptions Options, DurableStorageState State)>
        CreateSeededContextAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        _databases.Add(database);
        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = database.ConnectionString,
            BackupDirectory = Path.Combine(_root, "backups"),
            AutoMigrate = true
        };
        var factory = new TestDbContextFactory(database.Options);
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var backendId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        context.Tenants.Add(new TenantRecord
        {
            Id = tenantId,
            Slug = $"tenant-{Guid.NewGuid():N}",
            Name = "Selective tenant",
            CreatedAt = now
        });
        context.Users.Add(new PlatformUserRecord
        {
            Id = userId,
            TenantId = tenantId,
            DisplayName = "selective",
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.BackendIdentities.Add(new BackendIdentityRecord
        {
            Id = backendId,
            TenantId = tenantId,
            UserId = userId,
            BackendType = "jellyfin",
            BackendInstanceId = "inst",
            PrincipalId = "princ",
            CreatedAt = now,
            LastSeenAt = now
        });
        context.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = accountId,
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderId = "spotify",
            DisplayName = "Spotify selective",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "test");

        return (factory, options, state);
    }

    private async Task<(TestDbContextFactory Factory, DurableStorageOptions Options, DurableStorageState State)>
        CreateEmptyContextAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        _databases.Add(database);
        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = database.ConnectionString,
            BackupDirectory = Path.Combine(_root, "backups"),
            AutoMigrate = true
        };
        var factory = new TestDbContextFactory(database.Options);
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "test");

        return (factory, options, state);
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
