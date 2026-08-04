using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class ListeningHistoryImportIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "allstarr-history-import-tests", Guid.NewGuid().ToString("N"));
    private PostgresTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private FakeClock _clock = null!;
    private DurableJobQueue _jobs = null!;
    private ListeningHistoryImporterRegistry _importers = null!;
    private ListeningHistoryImportArtifactStore _artifacts = null!;
    private ListeningHistoryImportOptions _importOptions = null!;
    private IntelligenceScope _scope = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        _clock = new FakeClock(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        _scope = new(Guid.NewGuid(), Guid.NewGuid(), "jellyfin", "fixture-server", "music");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Tenants.Add(new TenantRecord
            {
                Id = _scope.TenantId,
                Slug = "history-import",
                Name = "History import",
                CreatedAt = _clock.UtcNow
            });
            db.Users.Add(new PlatformUserRecord
            {
                Id = _scope.OwnerUserId,
                TenantId = _scope.TenantId,
                DisplayName = "History owner",
                Status = PlatformUserStatus.Active,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var jobOptions = new DurableJobOptions
        {
            DefaultMaxAttempts = 3,
            LeaseSeconds = 30,
            PollIntervalMilliseconds = 100,
            MaxPayloadBytes = 64 * 1024
        };
        _jobs = new DurableJobQueue(_factory, jobOptions, new JobPayloadPolicy(jobOptions), _clock);
        _importOptions = new() { RootPath = _root, MaximumUploadBytes = 1024 * 1024 };
        _artifacts = new(_importOptions);
        _importers = new([new SpotifyListeningHistoryImporter()]);
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ApplyCheckpointsExactScopeWithoutOutboundReplay()
    {
        var service = new ListeningHistoryImportService(
            _factory, _importers, _artifacts, _importOptions, _clock, _jobs);
        var source = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            Row("2026-07-01T12:00:00Z", "One", "1111111111111111111111", 180_000, "trackdone", false),
            Row("2026-07-02T12:00:00Z", "Two", "2222222222222222222222", 10_000, "forwardbtn", true),
            Row("2026-07-03T12:00:00Z", "One again", "1111111111111111111111", 180_000, "trackdone", false)
        });
        await using var stream = new MemoryStream(source);
        var preview = await service.PreviewAsync(_scope, "history.json", stream, source.Length, CancellationToken.None);
        var queued = await service.ApplyAsync(_scope, preview.ImportId, preview.Revision, CancellationToken.None);

        Assert.NotNull(queued);
        var claim = await _jobs.ClaimNextAsync(
            "history-import-test", [ListeningHistoryImportJobHandler.JobTypeName], CancellationToken.None);
        Assert.NotNull(claim);
        var musicBrainz = new MusicBrainzListeningEnrichmentQueue(
            _jobs,
            Options.Create(new MusicBrainzSettings { Enabled = true }));
        var handler = new ListeningHistoryImportJobHandler(
            _factory, _importers, _artifacts, _importOptions, _clock, musicBrainz);
        using var services = new ServiceCollection().BuildServiceProvider();
        var completion = await handler.ExecuteAsync(new(claim!, services), CancellationToken.None);
        await _jobs.CompleteAsync(claim!, completion, CancellationToken.None);

        await using var reimportStream = new MemoryStream(source);
        var reimportPreview = await service.PreviewAsync(
            _scope, "history.json", reimportStream, source.Length, CancellationToken.None);
        var reimportQueued = await service.ApplyAsync(
            _scope, reimportPreview.ImportId, reimportPreview.Revision, CancellationToken.None);
        var reimportClaim = await _jobs.ClaimNextAsync(
            "history-reimport-test", [ListeningHistoryImportJobHandler.JobTypeName], CancellationToken.None);
        Assert.NotNull(reimportClaim);
        var reimportCompletion = await handler.ExecuteAsync(new(reimportClaim!, services), CancellationToken.None);
        await _jobs.CompleteAsync(reimportClaim!, reimportCompletion, CancellationToken.None);

        await using var db = await _factory.CreateDbContextAsync();
        var import = await db.ListeningHistoryImports.SingleAsync(item => item.Id == preview.ImportId);
        var reimport = await db.ListeningHistoryImports.SingleAsync(item => item.Id == reimportPreview.ImportId);
        var events = await db.ListeningEvents.OrderBy(item => item.StartedAt).ToListAsync();
        Assert.Equal(ListeningHistoryImportState.Completed, import.State);
        Assert.Equal(3, import.ImportedRows);
        Assert.Equal(3, import.NextSequence);
        Assert.Equal(ListeningHistoryImportState.Completed, reimport.State);
        Assert.Equal(0, reimport.ImportedRows);
        Assert.Equal(3, reimport.DuplicateRows);
        Assert.Equal(3, reimport.NextSequence);
        Assert.Collection(events,
            item =>
            {
                Assert.Equal(ListeningEventState.Completed, item.State);
                Assert.NotNull(item.ListenedAt);
            },
            item =>
            {
                Assert.Equal(ListeningEventState.Skipped, item.State);
                Assert.Null(item.ListenedAt);
            },
            item =>
            {
                Assert.Equal(ListeningEventState.Completed, item.State);
                Assert.NotNull(item.ListenedAt);
            });
        Assert.Single(await db.Jobs.Where(item => item.Type == MusicBrainzListeningEnrichmentQueue.JobType).ToListAsync());
        Assert.Empty(await db.PlaybackDeliveryCheckpoints.ToListAsync());
        var audits = await db.AuditEvents.AsNoTracking()
            .Where(item => item.Category == "listening-history-import")
            .OrderBy(item => item.CreatedAt)
            .ToListAsync();
        var firstAudits = audits.Where(item => item.CorrelationId == preview.ImportId.ToString("N")).ToList();
        var previewAudit = Assert.Single(firstAudits, item => item.Action == "previewed");
        var completedAudit = Assert.Single(firstAudits, item => item.Action == "completed");
        var spotifyFormat = new SpotifyListeningHistoryImporter().Format;
        Assert.All(firstAudits, item => Assert.Equal(preview.ImportId.ToString("N"), item.CorrelationId));
        using (var details = JsonDocument.Parse(previewAudit.DetailsJson))
        {
            Assert.Equal(spotifyFormat, details.RootElement.GetProperty("sourceProvider").GetString());
            Assert.Equal("history_import_previewed", details.RootElement.GetProperty("reasonCode").GetString());
            Assert.True(details.RootElement.GetProperty("durationMilliseconds").GetInt64() >= 0);
            Assert.False(details.RootElement.TryGetProperty("title", out _));
            Assert.False(details.RootElement.TryGetProperty("artist", out _));
        }
        using (var details = JsonDocument.Parse(completedAudit.DetailsJson))
        {
            Assert.Equal(spotifyFormat, details.RootElement.GetProperty("sourceProvider").GetString());
            Assert.Equal(queued!.JobId, details.RootElement.GetProperty("runId").GetGuid());
            Assert.Equal(import.ImportedRows, details.RootElement.GetProperty("ImportedRows").GetInt64());
            Assert.Equal(import.DuplicateRows, details.RootElement.GetProperty("DuplicateRows").GetInt64());
            Assert.Equal(import.ResolvedRows, details.RootElement.GetProperty("ResolvedRows").GetInt64());
            Assert.Equal(import.UnresolvedRows, details.RootElement.GetProperty("UnresolvedRows").GetInt64());
            Assert.Equal("history_import_completed", details.RootElement.GetProperty("reasonCode").GetString());
            Assert.True(details.RootElement.GetProperty("durationMilliseconds").GetInt64() >= 0);
        }
        var reimportAudit = Assert.Single(audits,
            item => item.Action == "completed" && item.CorrelationId == reimportPreview.ImportId.ToString("N"));
        using (var details = JsonDocument.Parse(reimportAudit.DetailsJson))
        {
            Assert.Equal(reimportQueued!.JobId, details.RootElement.GetProperty("runId").GetGuid());
            Assert.Equal(0, details.RootElement.GetProperty("ImportedRows").GetInt64());
            Assert.Equal(3, details.RootElement.GetProperty("DuplicateRows").GetInt64());
        }
        var operationalJson = string.Join('\n', audits.Select(item => item.DetailsJson));
        Assert.DoesNotContain("private-user", operationalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("master_metadata", operationalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spotify:track:", operationalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("history.json", operationalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("One again", operationalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", operationalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", operationalJson, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await service.GetAsync(
            _scope with { OwnerUserId = Guid.NewGuid() }, preview.ImportId, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, $"{preview.ImportId:N}.json")));
        Assert.False(File.Exists(Path.Combine(_root, $"{reimportPreview.ImportId:N}.json")));
    }

    [Fact]
    public async Task FailedApplyPersistsSafeOperationalMetadataWithoutImportContent()
    {
        var service = new ListeningHistoryImportService(
            _factory, _importers, _artifacts, _importOptions, _clock, _jobs);
        var preview = await PreviewAsync(service, "Private failure title", "5555555555555555555555");
        var queued = await service.ApplyAsync(
            _scope, preview.ImportId, preview.Revision, CancellationToken.None);
        var claim = await _jobs.ClaimNextAsync(
            "history-import-failure-test", [ListeningHistoryImportJobHandler.JobTypeName], CancellationToken.None);
        Assert.NotNull(claim);
        _artifacts.Delete(preview.ImportId);
        var musicBrainz = new MusicBrainzListeningEnrichmentQueue(
            _jobs,
            Options.Create(new MusicBrainzSettings { Enabled = false }));
        var handler = new ListeningHistoryImportJobHandler(
            _factory, _importers, _artifacts, _importOptions, _clock, musicBrainz);
        using var services = new ServiceCollection().BuildServiceProvider();

        var completion = await handler.ExecuteAsync(new(claim!, services), CancellationToken.None);
        await _jobs.CompleteAsync(claim!, completion, CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
        Assert.Equal("history_import_artifact_invalid", completion.ErrorCode);
        await using var db = await _factory.CreateDbContextAsync();
        var audit = Assert.Single(await db.AuditEvents.AsNoTracking()
            .Where(item => item.Category == "listening-history-import" && item.Action == "failed")
            .ToListAsync());
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal("history_import_artifact_invalid", audit.Outcome);
        Assert.Equal(preview.ImportId.ToString("N"), audit.CorrelationId);
        Assert.Equal(queued!.JobId, details.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal(new SpotifyListeningHistoryImporter().Format,
            details.RootElement.GetProperty("sourceProvider").GetString());
        Assert.Equal("history_import_artifact_invalid", details.RootElement.GetProperty("reasonCode").GetString());
        Assert.Equal(0, details.RootElement.GetProperty("ImportedRows").GetInt64());
        Assert.True(details.RootElement.GetProperty("durationMilliseconds").GetInt64() >= 0);
        Assert.DoesNotContain("Private failure title", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("5555555555555555555555", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PendingApplyCanCancelAndFailedApplyCanResumeFromItsCheckpoint()
    {
        var service = new ListeningHistoryImportService(
            _factory, _importers, _artifacts, _importOptions, _clock, _jobs);
        var cancelledPreview = await PreviewAsync(service, "Cancelled", "3333333333333333333333");
        await service.ApplyAsync(_scope, cancelledPreview.ImportId, cancelledPreview.Revision, CancellationToken.None);

        var cancelled = await service.CancelAsync(
            _scope, cancelledPreview.ImportId, cancelledPreview.Revision, CancellationToken.None);

        Assert.Equal(ListeningHistoryImportState.Cancelled, cancelled!.State);
        Assert.False(File.Exists(Path.Combine(_root, $"{cancelledPreview.ImportId:N}.json")));

        var failedPreview = await PreviewAsync(service, "Resume", "4444444444444444444444");
        var firstApply = await service.ApplyAsync(
            _scope, failedPreview.ImportId, failedPreview.Revision, CancellationToken.None);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var import = await db.ListeningHistoryImports.SingleAsync(item => item.Id == failedPreview.ImportId);
            var job = await db.Jobs.SingleAsync(item => item.Id == firstApply!.JobId);
            import.State = ListeningHistoryImportState.Failed;
            import.NextSequence = 1;
            import.UpdatedAt = _clock.UtcNow;
            import.Revision++;
            job.State = DurableJobState.Failed;
            job.CompletedAt = _clock.UtcNow;
            job.UpdatedAt = _clock.UtcNow;
            job.Revision++;
            await db.SaveChangesAsync();
        }

        var resumed = await service.ResumeAsync(
            _scope, failedPreview.ImportId, failedPreview.Revision, CancellationToken.None);

        Assert.Equal(ListeningHistoryImportState.Pending, resumed!.State);
        Assert.NotEqual(firstApply!.JobId, resumed.JobId);
        await using var verify = await _factory.CreateDbContextAsync();
        var record = await verify.ListeningHistoryImports.SingleAsync(item => item.Id == failedPreview.ImportId);
        Assert.Equal(2, record.ApplyGeneration);
        Assert.Equal(1, record.NextSequence);
    }

    private async Task<ListeningHistoryImportPreviewResult> PreviewAsync(
        ListeningHistoryImportService service,
        string title,
        string trackId)
    {
        var source = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            Row("2026-07-03T12:00:00Z", title, trackId, 180_000, "trackdone", false)
        });
        await using var stream = new MemoryStream(source);
        return await service.PreviewAsync(
            _scope, title + ".json", stream, source.Length, CancellationToken.None);
    }

    private static Dictionary<string, object?> Row(
        string timestamp,
        string title,
        string trackId,
        long milliseconds,
        string reasonEnd,
        bool skipped) => new()
        {
            ["ts"] = timestamp,
            ["username"] = "private-user",
            ["platform"] = "desktop",
            ["ms_played"] = milliseconds,
            ["master_metadata_track_name"] = title,
            ["master_metadata_album_artist_name"] = "Artist",
            ["master_metadata_album_album_name"] = "Album",
            ["spotify_track_uri"] = "spotify:track:" + trackId,
            ["reason_start"] = "trackdone",
            ["reason_end"] = reasonEnd,
            ["skipped"] = skipped,
            ["offline"] = false,
            ["incognito_mode"] = false
        };

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
