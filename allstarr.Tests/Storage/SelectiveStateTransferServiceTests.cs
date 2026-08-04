using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Settings;
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

        var (artifact, report) = await service.ExportAsync(exportDir, request);

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

        var exportDir = Path.Combine(_root, "export-just-playlists");
        var (artifact, _) = await sourceService.ExportAsync(
            exportDir,
            new SelectiveExportRequest
            {
                IncludeSettings = false,
                IncludeAccounts = false,
                IncludePlaylists = true,
                IncludeIntelligence = false,
                IncludeExtensions = false
            });

        var (targetFactory, targetOptions, targetState) = await CreateEmptyContextAsync();
        var targetService = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);

        await using var archive = File.OpenRead(artifact.Path);
        var ex = await Assert.ThrowsAsync<SelectiveTransferValidationException>(() =>
            targetService.ImportAsync(archive, new SelectiveImportRequest
            {
                ImportSettings = false,
                ImportAccounts = false,
                ImportPlaylists = true,
                ImportIntelligence = false,
                ImportExtensions = false
            }, Guid.Empty, "missing-dependency"));

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
                IncludeExtensions = true
            });

        Assert.Equal(3, exportReport.IncludedCategories.Count);
        Assert.Contains("Settings", exportReport.IncludedCategories);
        Assert.Contains("Accounts", exportReport.IncludedCategories);
        Assert.Contains("Extensions", exportReport.IncludedCategories);

        var (targetFactory, targetOptions, targetState) = await CreateEmptyContextAsync();
        var targetService = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);

        await using var archive = File.OpenRead(artifact.Path);
        var report = await targetService.ImportAsync(archive, new SelectiveImportRequest
        {
            ImportSettings = true,
            ImportAccounts = true,
            ImportPlaylists = false,
            ImportIntelligence = false,
            ImportExtensions = true
        }, Guid.Empty, "round-trip");

        Assert.Equal(3, report.IncludedCategories.Count);
        Assert.Equal(1, report.RowsByEntry.GetValueOrDefault("tenants"));
        Assert.Equal(1, report.RowsByEntry.GetValueOrDefault("users"));
        Assert.Equal(1, report.RowsByEntry.GetValueOrDefault("provider-accounts"));
        Assert.False(report.RowsByEntry.ContainsKey("library-tracks"));

        await using var verify = await targetFactory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Tenants.CountAsync());
        Assert.Equal(1, await verify.Users.CountAsync());
        Assert.Equal(1, await verify.ProviderAccounts.CountAsync());
        Assert.Equal("\"HiResLossless\"", (await verify.TenantRuntimeSettings.SingleAsync()).ValueJson);
        Assert.Equal("spotiflac-selective", (await verify.ExtensionPackages.SingleAsync()).ExtensionId);
        Assert.Equal("spotiflac-selective", (await verify.ExtensionLogs.SingleAsync()).ExtensionId);
        Assert.Contains(await verify.AuditEvents.ToListAsync(), item =>
            item.Action == "selective-import.apply" &&
            item.CorrelationId == "round-trip");
    }

    [Fact]
    public async Task ImportSelective_InvalidForeignKeyRollsBackEveryRowAndAudit()
    {
        var (sourceFactory, sourceOptions, sourceState) = await CreateSeededContextAsync();
        var sourceService = new SelectiveStateTransferService(sourceFactory, sourceOptions, sourceState);
        var (artifact, _) = await sourceService.ExportAsync(
            Path.Combine(_root, "invalid-foreign-key"),
            new SelectiveExportRequest
            {
                IncludeSettings = true,
                IncludeAccounts = true,
                IncludePlaylists = false,
                IncludeIntelligence = false,
                IncludeExtensions = false
            });
        await RewriteEntryAsync(artifact.Path, "provider-accounts.json", rows =>
        {
            rows[0]!["ownerUserId"] = Guid.NewGuid().ToString();
        });

        var (targetFactory, targetOptions, targetState) = await CreateEmptyContextAsync();
        var targetService = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);
        await using var archive = File.OpenRead(artifact.Path);
        var ex = await Assert.ThrowsAsync<SelectiveTransferConflictException>(() =>
            targetService.ImportAsync(
                archive,
                new SelectiveImportRequest
                {
                    ImportSettings = true,
                    ImportAccounts = true,
                    ImportPlaylists = false,
                    ImportIntelligence = false,
                    ImportExtensions = false
                },
                Guid.Empty,
                "must-roll-back"));

        Assert.Contains("database key or dependency", ex.Message);
        await using var verify = await targetFactory.CreateDbContextAsync();
        Assert.Empty(await verify.Tenants.ToListAsync());
        Assert.Empty(await verify.Users.ToListAsync());
        Assert.Empty(await verify.ProviderAccounts.ToListAsync());
        Assert.Empty(await verify.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task PreviewSelective_RejectsTamperedEntryChecksum()
    {
        var (factory, options, state) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(factory, options, state);
        var (artifact, _) = await service.ExportAsync(
            Path.Combine(_root, "tampered"),
            new SelectiveExportRequest
            {
                IncludeSettings = true,
                IncludeAccounts = false,
                IncludePlaylists = false,
                IncludeIntelligence = false,
                IncludeExtensions = false
            });

        using (var archive = ZipFile.Open(artifact.Path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("tenants.json")!;
            entry.Delete();
            await using var stream = archive.CreateEntry("tenants.json").Open();
            await JsonSerializer.SerializeAsync(stream, Array.Empty<object>());
        }

        await using var upload = File.OpenRead(artifact.Path);
        var ex = await Assert.ThrowsAsync<SelectiveTransferValidationException>(() =>
            service.PreviewAsync(
                upload,
                new SelectiveImportRequest
                {
                    ImportSettings = true,
                    ImportAccounts = false,
                    ImportPlaylists = false,
                    ImportIntelligence = false,
                    ImportExtensions = false
                }));
        Assert.Contains("checksum or bounds", ex.Message);
    }

    [Fact]
    public async Task PreviewSelective_ConflictModeReportsNonEmptyTarget()
    {
        var (factory, options, state) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(factory, options, state);
        var (artifact, _) = await service.ExportAsync(
            Path.Combine(_root, "conflict-preview"),
            new SelectiveExportRequest
            {
                IncludeSettings = true,
                IncludeAccounts = false,
                IncludePlaylists = false,
                IncludeIntelligence = false,
                IncludeExtensions = false
            });

        await using var archive = File.OpenRead(artifact.Path);
        var preview = await service.PreviewAsync(
            archive,
            new SelectiveImportRequest
            {
                Mode = SelectiveImportMode.Conflict,
                ImportSettings = true,
                ImportAccounts = false,
                ImportPlaylists = false,
                ImportIntelligence = false,
                ImportExtensions = false
            });

        Assert.False(preview.CanImport);
        Assert.Contains(preview.Conflicts, conflict => conflict.Contains("empty target"));
    }

    [Fact]
    public async Task ImportSelective_MergeAddsRowsWithoutReplacingDependencies()
    {
        var (sourceFactory, sourceOptions, sourceState) = await CreateSeededContextAsync();
        await using var sourceContext = await sourceFactory.CreateDbContextAsync();
        var sourceTenant = await sourceContext.Tenants.AsNoTracking().SingleAsync();
        var sourceService = new SelectiveStateTransferService(sourceFactory, sourceOptions, sourceState);
        var (artifact, _) = await sourceService.ExportAsync(
            Path.Combine(_root, "merge"),
            new SelectiveExportRequest
            {
                IncludeSettings = false,
                IncludeAccounts = true,
                IncludePlaylists = false,
                IncludeIntelligence = false,
                IncludeExtensions = false
            });

        var (targetFactory, targetOptions, targetState) = await CreateEmptyContextAsync();
        await using (var targetContext = await targetFactory.CreateDbContextAsync())
        {
            targetContext.Tenants.Add(new TenantRecord
            {
                Id = sourceTenant.Id,
                Slug = sourceTenant.Slug,
                Name = "Existing target tenant",
                CreatedAt = sourceTenant.CreatedAt
            });
            await targetContext.SaveChangesAsync();
        }
        var targetService = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);
        await using var archive = File.OpenRead(artifact.Path);
        await targetService.ImportAsync(
            archive,
            new SelectiveImportRequest
            {
                Mode = SelectiveImportMode.Merge,
                ImportSettings = false,
                ImportAccounts = true,
                ImportPlaylists = false,
                ImportIntelligence = false,
                ImportExtensions = false
            },
            Guid.Empty,
            "merge");

        await using var verify = await targetFactory.CreateDbContextAsync();
        Assert.Equal("Existing target tenant", (await verify.Tenants.SingleAsync()).Name);
        Assert.Single(await verify.Users.ToListAsync());
        Assert.Single(await verify.ProviderAccounts.ToListAsync());
    }

    [Fact]
    public async Task ImportSelective_ReplaceReplacesEveryRowInSelectedCategory()
    {
        var (sourceFactory, sourceOptions, sourceState) = await CreateSeededContextAsync();
        await using var sourceContext = await sourceFactory.CreateDbContextAsync();
        var sourceTenantId = await sourceContext.Tenants.Select(item => item.Id).SingleAsync();
        var sourceService = new SelectiveStateTransferService(sourceFactory, sourceOptions, sourceState);
        var (artifact, _) = await sourceService.ExportAsync(
            Path.Combine(_root, "replace"),
            new SelectiveExportRequest
            {
                IncludeSettings = true,
                IncludeAccounts = false,
                IncludePlaylists = false,
                IncludeIntelligence = false,
                IncludeExtensions = false
            });

        var (targetFactory, targetOptions, targetState) = await CreateEmptyContextAsync();
        var oldTenantId = Guid.NewGuid();
        await using (var targetContext = await targetFactory.CreateDbContextAsync())
        {
            targetContext.Tenants.Add(new TenantRecord
            {
                Id = oldTenantId,
                Slug = $"old-{oldTenantId:N}",
                Name = "Old tenant",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await targetContext.SaveChangesAsync();
        }
        var targetService = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);
        await using var archive = File.OpenRead(artifact.Path);
        await targetService.ImportAsync(
            archive,
            new SelectiveImportRequest
            {
                Mode = SelectiveImportMode.Replace,
                ImportSettings = true,
                ImportAccounts = false,
                ImportPlaylists = false,
                ImportIntelligence = false,
                ImportExtensions = false
            },
            Guid.Empty,
            "replace");

        await using var verify = await targetFactory.CreateDbContextAsync();
        Assert.False(await verify.Tenants.AnyAsync(item => item.Id == oldTenantId));
        Assert.True(await verify.Tenants.AnyAsync(item => item.Id == sourceTenantId));
        Assert.Contains(await verify.AuditEvents.ToListAsync(), item =>
            item.CorrelationId == "replace");
    }

    [Fact]
    public async Task IntelligenceRoundTripPreservesScopeExpiresUploadsAndRejectsConflict()
    {
        const string temporaryUploadCanary = "private-history-upload-must-not-transfer";
        const string remoteTokenCanary = "remote-history-token-must-not-transfer";
        var (sourceFactory, sourceOptions, sourceState) = await CreateSeededContextAsync();
        Guid tenantId;
        Guid userId;
        await using (var source = await sourceFactory.CreateDbContextAsync())
        {
            tenantId = await source.Tenants.Select(item => item.Id).SingleAsync();
            userId = await source.Users.Select(item => item.Id).SingleAsync();
            var now = DateTimeOffset.UtcNow;
            source.ListeningEvents.Add(new ListeningEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = userId,
                Protocol = "jellyfin",
                BackendInstanceId = "inst",
                LibraryScopeId = "music",
                OccurrenceKey = new string('e', 64),
                State = ListeningEventState.Completed,
                StartedAt = now,
                ListenedAt = now,
                UpdatedAt = now,
                SourceKind = "history-import",
                TrackReference = "spotify:track:fixture"
            });
            source.ListeningHistoryImports.Add(new ListeningHistoryImportRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = userId,
                Protocol = "jellyfin",
                BackendInstanceId = "inst",
                LibraryScopeId = "music",
                DisplayFileName = "StreamingHistory_music_0.json",
                Format = "spotify-extended-streaming-history",
                ContentSha256 = new string('a', 64),
                SizeBytes = 1234,
                PreviewJson = "{}",
                PreviewRevision = new string('b', 64),
                State = ListeningHistoryImportState.Running,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.AddHours(24),
                Revision = 1
            });
            await source.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(
            Path.Combine(_root, "history-import.upload"),
            $"{temporaryUploadCanary}|{remoteTokenCanary}");

        var sourceService = new SelectiveStateTransferService(sourceFactory, sourceOptions, sourceState);
        var exportRequest = new SelectiveExportRequest
        {
            IncludeSettings = true,
            IncludeAccounts = true,
            IncludePlaylists = false,
            IncludeIntelligence = true,
            IncludeExtensions = false
        };
        var importRequest = new SelectiveImportRequest
        {
            Mode = SelectiveImportMode.Conflict,
            ImportSettings = true,
            ImportAccounts = true,
            ImportPlaylists = false,
            ImportIntelligence = true,
            ImportExtensions = false
        };
        var (artifact, report) = await sourceService.ExportAsync(
            Path.Combine(_root, "intelligence-round-trip"),
            exportRequest);
        Assert.Equal(1, report.RowsByEntry["listening-events"]);
        Assert.Equal(1, report.RowsByEntry["listening-history-imports"]);
        using (var archive = ZipFile.OpenRead(artifact.Path))
        {
            var manifest = await ReadManifestAsync(archive);
            Assert.Equal(SelectiveStateTransferService.CurrentFormatVersion.ToString(), manifest["formatVersion"]);
            Assert.Equal(artifact.SchemaVersion, manifest["schemaVersion"]);
            var contents = new List<string>();
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                contents.Add(await reader.ReadToEndAsync());
            }
            var archiveText = string.Join('\n', contents);
            Assert.DoesNotContain(temporaryUploadCanary, archiveText, StringComparison.Ordinal);
            Assert.DoesNotContain(remoteTokenCanary, archiveText, StringComparison.Ordinal);
        }

        var invalidPath = Path.Combine(_root, "invalid-history-scope.zip");
        File.Copy(artifact.Path, invalidPath);
        await RewriteEntryAsync(invalidPath, "listening-events.json", rows =>
            rows[0]!["ownerUserId"] = Guid.CreateVersion7().ToString());
        var (targetFactory, targetOptions, targetState) = await CreateEmptyContextAsync();
        var targetService = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);
        await using (var invalidArchive = File.OpenRead(invalidPath))
        {
            var invalidPreview = await targetService.PreviewAsync(invalidArchive, importRequest);
            Assert.False(invalidPreview.CanImport);
            Assert.Contains(invalidPreview.Conflicts, item =>
                item.Contains("database key or dependency", StringComparison.OrdinalIgnoreCase));
        }

        await using (var archive = File.OpenRead(artifact.Path))
        {
            await targetService.ImportAsync(archive, importRequest, userId, "history-round-trip");
        }
        var restarted = new SelectiveStateTransferService(targetFactory, targetOptions, targetState);
        await using (var restored = await targetFactory.CreateDbContextAsync())
        {
            var occurrence = await restored.ListeningEvents.SingleAsync();
            var historyImport = await restored.ListeningHistoryImports.SingleAsync();
            Assert.Equal((tenantId, userId, "jellyfin", "inst", "music"),
                (occurrence.TenantId, occurrence.OwnerUserId, occurrence.Protocol,
                    occurrence.BackendInstanceId, occurrence.LibraryScopeId));
            Assert.Equal((tenantId, userId, "jellyfin", "inst", "music"),
                (historyImport.TenantId, historyImport.OwnerUserId, historyImport.Protocol,
                    historyImport.BackendInstanceId, historyImport.LibraryScopeId));
            Assert.Equal(ListeningHistoryImportState.Expired, historyImport.State);
            Assert.Null(historyImport.JobId);
        }
        await using (var archive = File.OpenRead(artifact.Path))
        {
            var conflict = await restarted.PreviewAsync(archive, importRequest);
            Assert.False(conflict.CanImport);
            Assert.Contains(conflict.Conflicts, item => item.Contains("empty target", StringComparison.Ordinal));
        }
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
                }));
    }

    [Fact]
    public async Task ImportSelective_RejectsEmptyArchive()
    {
        var (factory, options, state) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(factory, options, state);

        await using var archive = new MemoryStream();
        await Assert.ThrowsAsync<SelectiveTransferValidationException>(() =>
            service.ImportAsync(archive, new SelectiveImportRequest(), Guid.Empty, "empty"));
    }

    [Fact]
    public async Task ImportSelective_RejectsWrongFormatVersion()
    {
        var (factory, options, state) = await CreateSeededContextAsync();
        var service = new SelectiveStateTransferService(factory, options, state);

        var fakeArchivePath = Path.Combine(_root, "wrong-version.zip");
        using (var archive = ZipFile.Open(fakeArchivePath, ZipArchiveMode.Create))
        {
            var data = archive.CreateEntry("tenants.json");
            await using (var dataStream = data.Open())
            {
                await JsonSerializer.SerializeAsync(dataStream, Array.Empty<object>());
            }
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
        await using var archiveStream = File.OpenRead(fakeArchivePath);
        await Assert.ThrowsAsync<SelectiveTransferSchemaMismatchException>(() =>
            service.ImportAsync(archiveStream, new SelectiveImportRequest
            {
                ImportSettings = true,
                ImportAccounts = false,
                ImportPlaylists = false,
                ImportIntelligence = false,
                ImportExtensions = false
            }, Guid.Empty, "wrong-version"));
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

    [Fact]
    public async Task PreviewSelective_RejectsUnknownImportModeBeforeReadingUpload()
    {
        var service = new SelectiveStateTransferService(
            new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>().Options),
            new DurableStorageOptions { Provider = "Postgres", ConnectionString = "Host=database;Database=allstarr" },
            new DurableStorageState(new DurableStorageOptions { Provider = "Postgres", ConnectionString = "Host=database;Database=allstarr" }));

        await Assert.ThrowsAsync<SelectiveTransferValidationException>(() =>
            service.PreviewAsync(
                Stream.Null,
                new SelectiveImportRequest { Mode = (SelectiveImportMode)999 }));
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

    private static async Task RewriteEntryAsync(
        string archivePath,
        string entryName,
        Action<JsonArray> mutate)
    {
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(entryName)!;
            JsonArray rows;
            await using (var stream = entry.Open())
            {
                rows = (await JsonNode.ParseAsync(stream))!.AsArray();
            }
            mutate(rows);
            entry.Delete();
            await using var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            await JsonSerializer.SerializeAsync(replacement, rows);
        }

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            var manifestEntry = archive.GetEntry("manifest.json")!;
            JsonArray manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = (await JsonNode.ParseAsync(stream))!.AsArray();
            }
            var entry = archive.GetEntry(entryName)!;
            var descriptor = manifest[0]!["entries"]!.AsArray()
                .Single(item => item!["name"]!.GetValue<string>() == entryName)!;
            descriptor["expandedBytes"] = entry.Length;
            descriptor["compressedBytes"] = entry.CompressedLength;
            await using (var stream = entry.Open())
            {
                descriptor["sha256"] = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            }
            manifestEntry.Delete();
            await using var replacement = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
            await JsonSerializer.SerializeAsync(replacement, manifest);
        }
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
        context.TenantRuntimeSettings.Add(new TenantRuntimeSettingRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Key = AudioQualityPolicy.SettingKey,
            ValueType = RuntimeSettingValueType.String,
            ValueJson = "\"HiResLossless\"",
            Source = "v3-compatibility-migration",
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        var packageId = Guid.CreateVersion7();
        context.ExtensionPackages.Add(new ExtensionPackageRecord
        {
            Id = packageId,
            ExtensionId = "spotiflac-selective",
            DisplayName = "Selective extension",
            Version = "1.0.0",
            SdkVersion = "1",
            Sha256 = new string('e', 64),
            ContentSha256 = new string('f', 64),
            PackagePath = "/extensions/spotiflac-selective",
            ManifestJson = """{"id":"spotiflac-selective","compatibility":"spotiflac-v1"}""",
            State = ExtensionPackageState.Active,
            StagedAt = now,
            ActivatedAt = now,
            Revision = 1
        });
        context.ExtensionLogs.Add(new ExtensionLogRecord
        {
            Id = Guid.CreateVersion7(),
            ExtensionPackageId = packageId,
            ExtensionId = "spotiflac-selective",
            Level = "Info",
            EventCode = "selective",
            Message = "Selective fixture",
            CorrelationId = "selective-extension",
            CreatedAt = now
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
