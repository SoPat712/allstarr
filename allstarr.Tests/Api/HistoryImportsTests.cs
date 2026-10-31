using System.IO.Compression;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Enrichment;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ListeningHistoryImportIntegrationTests
{
    private ListeningHistoryImportService ImportService() => new(_factory, _importers, _artifacts, _importOptions, _clock, _jobs);
    private TasteService Taste() => new(_factory, new SharedTagStore(_factory, _clock), _clock);

    [Theory]
    [InlineData("lastfm-recent-tracks")]
    [InlineData("listenbrainz-export")]
    [InlineData("koito-export-v1")]
    [InlineData("maloja-export")]
    public async Task CoreImport_AppliesTheOtherExistingFormats(string format)
    {
        _importers = new([new SpotifyListeningHistoryImporter(), new LastFmListeningHistoryImporter(),
            new ListenBrainzListeningHistoryImporter(), new KoitoListeningHistoryImporter(), new MalojaListeningHistoryImporter()]);
        var bytes = ListeningHistoryImporterMatrixTests.Fixture(format, 1, false);
        using var source = new MemoryStream(bytes);
        var service = ImportService();
        var preview = await service.PreviewAsync(_scope, "history.json", source, bytes.Length, default);
        Assert.Equal(format, preview.Preview.Format);
        Assert.Equal(1, preview.Preview.Songs);
        await service.ApplyAsync(_scope, preview.ImportId, preview.Revision, default);
        await RunImport();
        await using var db = _factory.CreateDbContext();
        Assert.Equal(ListeningEventState.Completed, (await db.ListeningEvents.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreImport_ScansAppliesAndKeepsStatsAndDeliverySeparate(bool zipped)
    {
        var rows = new[]
        {
            Row("2026-07-30T12:00:00Z", "Finished", "1111111111111111111111", 180_000, "trackdone", false),
            Row("2026-07-30T13:00:00Z", "Skipped", "2222222222222222222222", 10_000, "forwardbtn", true),
            Row("2026-07-30T14:00:00Z", "Short", "3333333333333333333333", 5_000, "trackdone", false),
            new Dictionary<string, object?> { ["ts"] = "2026-07-30T15:00:00Z", ["ms_played"] = 180_000, ["episode_name"] = "Podcast" },
            new Dictionary<string, object?> { ["unknown"] = true }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rows);
        using var upload = new MemoryStream();
        if (zipped)
        {
            using (var archive = new ZipArchive(upload, ZipArchiveMode.Create, true))
            {
                await using var entry = archive.CreateEntry("Streaming_History_Audio_0.json").Open();
                await entry.WriteAsync(bytes);
            }
        }
        else await upload.WriteAsync(bytes);
        upload.Position = 0;
        var service = ImportService();
        var preview = await service.PreviewAsync(_scope, zipped ? "history.zip" : "Streaming_History_Audio_0.json", upload, upload.Length, default);
        Assert.Equal(3, preview.Preview.MusicRows);
        Assert.Equal(3, preview.Preview.Songs);
        Assert.Equal(1, preview.Preview.Completed);
        Assert.Equal(1, preview.Preview.Skipped);
        Assert.Equal(1, preview.Preview.Partial);
        Assert.Equal(1, preview.Preview.Episodes);
        Assert.Equal(1, preview.Preview.Malformed);
        Assert.Equal(DateTimeOffset.Parse("2026-07-30T12:00:00Z"), preview.Preview.Earliest);
        Assert.Empty((await Taste().GetAsync(_scope.OwnerUserId)).Usually.Artists);
        await service.ApplyAsync(_scope, preview.ImportId, preview.Revision, default);
        await RunImport();
        var result = await service.GetAsync(_scope, preview.ImportId, default);
        Assert.Equal(ListeningHistoryImportState.Completed, result!.State);
        Assert.Equal(3, result.ImportedRows);
        await using var db = _factory.CreateDbContext();
        db.ListeningEvents.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = _scope.OwnerUserId,
            Protocol = _scope.Protocol,
            BackendInstanceId = _scope.BackendInstanceId,
            OccurrenceKey = new string('f', 64),
            State = ListeningEventState.Completed,
            ListenedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
            SourceKind = "protocol",
            TrackReference = "observed",
            Title = "Observed",
            Artist = "Other",
            Revision = 1
        });
        await db.SaveChangesAsync();
        var stats = await ListeningStatsService.ReadAsync(db, _scope, new(null, _clock.UtcNow.AddDays(1), "UTC"), TimeZoneInfo.Utc, default);
        Assert.Equal(1, stats.ImportedCount);
        Assert.Equal(2, stats.PlayCount);
        Assert.Contains(stats.TopSongs, item => item.Title == "Observed");
        Assert.Equal(2, stats.TopSongs.Count);
        Assert.Empty(await db.PlaybackDeliveryCheckpoints.ToListAsync());
        Assert.Empty(await db.ListeningSignals.ToListAsync());
        Assert.Empty(await db.FavoriteActions.ToListAsync());
        Assert.Empty(await db.ProviderDownloadWorkspaces.ToListAsync());
        Assert.All(await db.Jobs.ToListAsync(), job => Assert.Contains(job.Type,
            new[] { ListeningHistoryImportJobHandler.JobTypeName, ListeningHistoryResolution.JobTypeName }));
        Assert.All(await db.ListeningEvents.Where(item => item.SourceKind == "import").ToListAsync(), item => Assert.Equal(MusicBrainzEnrichmentState.NotRequested, item.MusicBrainzEnrichmentState));
    }

    [Fact]
    public async Task ImportedTaste_UsesDatesAndClassificationAndRespondsToSettingPauseAndDeletion()
    {
        var service = ImportService();
        var preview = await PreviewAsync(service, "Finished", "1111111111111111111111");
        var recording = Guid.NewGuid();
        var tags = new SharedTagStore(_factory, _clock);
        await using (var db = _factory.CreateDbContext())
        {
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = _scope.OwnerUserId, PublicId = CanonicalRecordingRecord.DefaultPublicId(recording), CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
            var completed = ImportedEvent(preview.ImportId, new string('a', 64));
            completed.CanonicalRecordingId = recording;
            completed.Artist = "Finished artist";
            completed.ListenedAt = completed.UpdatedAt = _clock.UtcNow.AddDays(-30);
            var skipped = ImportedEvent(preview.ImportId, new string('b', 64));
            skipped.Artist = "Skipped artist";
            skipped.State = ListeningEventState.Skipped;
            skipped.ListenedAt = null;
            skipped.PositionTicks = TimeSpan.FromSeconds(10).Ticks;
            var old = ImportedEvent(preview.ImportId, new string('c', 64));
            old.Artist = "Old artist";
            old.ListenedAt = old.UpdatedAt = _clock.UtcNow.AddYears(-5);
            var partial = ImportedEvent(preview.ImportId, new string('d', 64));
            partial.Artist = "Partial artist";
            partial.State = ListeningEventState.Abandoned;
            var otherBackend = ImportedEvent(preview.ImportId, new string('e', 64));
            otherBackend.BackendInstanceId = "another";
            otherBackend.Artist = "Other backend";
            db.ListeningEvents.AddRange(completed, skipped, old, partial, otherBackend);
            await db.SaveChangesAsync();
        }
        await tags.SaveAsync(SharedTagStore.RecordingKey(recording), [new("provider", [new("rock")], _clock.UtcNow)], [], false, default);
        var taste = Taste();
        var profile = await taste.GetAsync(_scope.OwnerUserId);
        Assert.True(profile.UseImportedHistory);
        Assert.Equal(Math.Pow(.5, 30d / 180), Assert.Single(profile.Usually.Tags).Value, 6);
        Assert.Equal(Math.Pow(.5, 30d / 180), profile.Usually.Artists.Single(item => item.Name == "Finished artist").Value, 6);
        Assert.Equal(-1, profile.Usually.Artists.Single(item => item.Name == "Skipped artist").Value);
        Assert.Equal(2, profile.Usually.Artists.Count);
        Assert.Empty(profile.Lately.Artists);
        Assert.Empty(profile.Lately.Tags);
        Assert.Equal(0, profile.LatelyWeight);
        var song = new Song { CanonicalRecordingId = recording, Artist = "Finished artist" };
        Assert.True((await taste.ScoreAsync(_scope.OwnerUserId, song)).Score > 0);
        await taste.SetUseImportedHistoryAsync(_scope.OwnerUserId, false);
        Assert.Empty((await Taste().GetAsync(_scope.OwnerUserId)).Usually.Artists);
        Assert.Equal(0, (await taste.ScoreAsync(_scope.OwnerUserId, song)).Score);
        await taste.SetUseImportedHistoryAsync(_scope.OwnerUserId, true);
        Assert.NotEmpty((await taste.GetAsync(_scope.OwnerUserId)).Usually.Artists);
        await taste.SetPausedAsync(_scope.OwnerUserId, true);
        Assert.Empty((await taste.GetAsync(_scope.OwnerUserId)).Usually.Artists);
        await taste.SetPausedAsync(_scope.OwnerUserId, false);
        Assert.NotEmpty((await taste.GetAsync(_scope.OwnerUserId)).Usually.Artists);
        Assert.Equal(4, (await service.RemoveAsync(_scope, preview.ImportId, preview.Revision, default))!.RemovedListens);
        Assert.Empty((await taste.GetAsync(_scope.OwnerUserId)).Usually.Artists);
    }

    [Fact]
    public async Task ShutdownBetweenBatches_PreservesArtifactAndResumesWithoutDuplicateEvents()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, 501).Select(index =>
            Row(_clock.UtcNow.AddMinutes(-index).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), "Song", "1111111111111111111111", 180_000, "trackdone", false)));
        var service = ImportService();
        using var source = new MemoryStream(bytes);
        var preview = await service.PreviewAsync(_scope, "history.json", source, bytes.Length, default);
        await service.ApplyAsync(_scope, preview.ImportId, preview.Revision, default);
        var claim = (await _jobs.ClaimNextAsync("before-restart", [ListeningHistoryImportJobHandler.JobTypeName], default))!;
        using var services = new ServiceCollection().BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        var progress = 0;
        var execution = new DurableJobExecutionContext(claim, services)
        {
            ReportProgressAsync = async (update, token) =>
            {
                progress++;
                Assert.True(await _jobs.ReportProgressAsync(claim, update, token));
                stop.Cancel();
                return true;
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler().ExecuteAsync(execution, stop.Token));
        Assert.True(progress > 0);
        await using (var db = _factory.CreateDbContext())
        {
            Assert.Equal(500, await db.ListeningEvents.CountAsync());
            Assert.Equal(500, (await db.ListeningHistoryImports.SingleAsync()).NextSequence);
            Assert.Contains(await db.AuditEvents.ToListAsync(), item => item.Category == "job-progress");
        }
        await using (var artifact = _artifacts.OpenRead(preview.ImportId)) Assert.True(artifact.CanRead);
        _clock.UtcNow += TimeSpan.FromDays(2);
        await new ListeningHistoryImportCleanupService(_factory, _artifacts, _jobs, new(_factory, _clock),
            new(_database.StorageOptions), _clock, NullLogger<ListeningHistoryImportCleanupService>.Instance).SweepAsync(default);
        _jobs = new(_factory, new(), new JobPayloadPolicy(new()), _clock);
        await _jobs.RecoverInterruptedAsync();
        await RunImport();
        await using var verify = _factory.CreateDbContext();
        Assert.Equal(501, await verify.ListeningEvents.CountAsync());
        Assert.Equal(ListeningHistoryImportState.Completed, (await verify.ListeningHistoryImports.SingleAsync()).State);
    }

    [Fact]
    public async Task ImportedTaste_LeavesObservedLearningAndLatelyUnchanged()
    {
        var taste = Taste();
        var song = new Song { Artist = "Observed" };
        await taste.ObserveHeartAsync(_scope.OwnerUserId, "heart", song, _clock.UtcNow, default);
        var before = await taste.GetAsync(_scope.OwnerUserId);
        var preview = await PreviewAsync(ImportService(), "Imported", "1111111111111111111111");
        await using (var db = _factory.CreateDbContext())
        {
            db.ListeningEvents.Add(ImportedEvent(preview.ImportId, new string('a', 64)));
            await db.SaveChangesAsync();
        }
        var after = await taste.GetAsync(_scope.OwnerUserId);
        Assert.Equal(before.Lately.Artists, after.Lately.Artists);
        Assert.Equal(before.Lately.Tags, after.Lately.Tags);
        Assert.Equal(before.LatelyWeight, after.LatelyWeight);
        Assert.Equal(2, after.Usually.Artists.Count);
        await taste.SetUseImportedHistoryAsync(_scope.OwnerUserId, false);
        Assert.Equal(before.Usually.Artists, (await taste.GetAsync(_scope.OwnerUserId)).Usually.Artists);
        await taste.SetUseImportedHistoryAsync(_scope.OwnerUserId, true);
        await taste.SetPausedAsync(_scope.OwnerUserId, true);
        Assert.Equal(before.Usually.Artists, (await taste.GetAsync(_scope.OwnerUserId)).Usually.Artists);
    }

    [Fact]
    public async Task ListenerApi_UsesVerifiedSessionAndBoundsStreamedUploads()
    {
        var controller = new HistoryImportsController(ImportService(), _factory, _clock, _importOptions)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>(await controller.List(default));
        var session = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "history-owner",
            UserName = "Listener",
            AllstarrUserId = _scope.OwnerUserId,
            IsAdministrator = false,
            JellyfinAccessToken = "",
            BackendType = _scope.Protocol,
            BackendInstanceId = _scope.BackendInstanceId,
            ExpiresAtUtc = _clock.UtcNow.AddHours(1).UtcDateTime
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { Row("2026-07-30T12:00:00Z", "Private", "1111111111111111111111", 180_000, "trackdone", false) });
        controller.Request.ContentLength = bytes.Length;
        controller.Request.Body = new MemoryStream(bytes);
        var preview = Assert.IsType<ListeningHistoryImportPreviewResult>(Assert.IsType<OkObjectResult>(await controller.Upload("history.json", default)).Value);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ListeningHistoryImportPreviewResult>>(Assert.IsType<OkObjectResult>(await controller.List(default)).Value));
        Assert.IsType<NotFoundResult>(await controller.Get(Guid.NewGuid(), default));
        Assert.IsType<OkObjectResult>(await controller.Get(preview.ImportId, default));
        controller.Request.ContentLength = _importOptions.MaximumUploadBytes + 1;
        Assert.Equal(413, Assert.IsType<ObjectResult>(await controller.Upload("large.zip", default)).StatusCode);
        controller.Request.ContentLength = 1;
        controller.Request.Body = new MemoryStream(new byte[_importOptions.MaximumUploadBytes + 1]);
        Assert.IsType<BadRequestObjectResult>(await controller.Upload("overflow.json", default));
        Assert.Single(Directory.GetFiles(_root));
        Assert.IsType<OkObjectResult>(await controller.Confirm(preview.ImportId, new(preview.Revision), default));
        Assert.IsType<OkObjectResult>(await controller.Delete(preview.ImportId, preview.Revision, default));
        await using (var db = _factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync();
            user.BackendPrincipalId = "changed";
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.List(default)).StatusCode);
    }

    [Fact]
    public async Task ImportsAndTaste_ArePrivateToEachListenerIncludingAdministrators()
    {
        var service = ImportService();
        var preview = await PreviewAsync(service, "Private", "1111111111111111111111");
        var other = Guid.NewGuid();
        await using (var db = _factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = other,
                DisplayName = "Other",
                Enabled = true,
                IsAdmin = true,
                BackendType = _scope.Protocol,
                BackendInstanceId = _scope.BackendInstanceId,
                BackendPrincipalId = "other",
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            db.ListeningEvents.Add(ImportedEvent(preview.ImportId, new string('a', 64)));
            await db.SaveChangesAsync();
        }
        var scope = _scope with { OwnerUserId = other };
        Assert.Empty(await service.ListAsync(scope, 50, default));
        Assert.Null(await service.GetAsync(scope, preview.ImportId, default));
        Assert.Null(await service.ApplyAsync(scope, preview.ImportId, preview.Revision, default));
        Assert.Null(await service.RemoveAsync(scope, preview.ImportId, preview.Revision, default));
        Assert.Empty((await Taste().GetAsync(other)).Usually.Artists);
        Assert.NotEmpty((await Taste().GetAsync(_scope.OwnerUserId)).Usually.Artists);
        var controller = new HistoryImportsController(service, _factory, _clock, _importOptions)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "other",
            UserId = "other",
            UserName = "Other",
            AllstarrUserId = other,
            IsAdministrator = true,
            JellyfinAccessToken = "",
            BackendType = scope.Protocol,
            BackendInstanceId = scope.BackendInstanceId,
            ExpiresAtUtc = _clock.UtcNow.AddHours(1).UtcDateTime
        };
        Assert.IsType<NotFoundResult>(await controller.Get(preview.ImportId, default));
        Assert.IsType<NotFoundResult>(await controller.Confirm(preview.ImportId, new(preview.Revision), default));
        Assert.IsType<NotFoundResult>(await controller.Delete(preview.ImportId, preview.Revision, default));
    }

    [Fact]
    public void CoreRegistration_ContainsOnlyImportWorkersAndAllExistingFormats()
    {
        var services = new ServiceCollection();
        services.AddListeningHistoryImport(new ConfigurationBuilder().Build());
        Assert.Equal(5, services.Count(item => item.ServiceType == typeof(IListeningHistoryImporter)));
        Assert.Equal(2, services.Count(item => item.ServiceType == typeof(IDurableJobHandler)));
        Assert.Contains(services, item => item.ImplementationType == typeof(ListeningHistoryImportCleanupService));
        Assert.Equal(500L * 1024 * 1024, new ListeningHistoryImportOptions().MaximumUploadBytes);
    }

    private ListeningHistoryImportJobHandler Handler() => new(_factory, _importers, _artifacts, _importOptions, _clock, _jobs);
    private async Task RunImport()
    {
        var claim = (await _jobs.ClaimNextAsync("after-restart", [ListeningHistoryImportJobHandler.JobTypeName], default))!;
        Assert.NotNull(claim);
        using var services = new ServiceCollection().BuildServiceProvider();
        var completion = await Handler().ExecuteAsync(new(claim, services), default);
        await _jobs.CompleteAsync(claim, completion, default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
    }
}
