using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Playback;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.Extensions.Configuration;
using Moq;
using allstarr.Core.Storage;
using allstarr.Middleware;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class PlaybackStartTimingTests
{
    [Fact]
    public void MeasuresParallelPhasesAndBoundsPerRequestMemory()
    {
        var clock = new ManualTimeProvider();
        using var trace = new PlaybackStartTimings(clock);
        trace.Enter(); trace.Enable();
        using (trace.Measure(PlaybackStartPhase.Route))
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            using (trace.Measure(PlaybackStartPhase.Account, "deezer")) clock.Advance(TimeSpan.FromMilliseconds(10));
        }
        trace.Headers();
        clock.Advance(TimeSpan.FromMilliseconds(5));
        Assert.True(trace.FirstByte());
        Assert.False(trace.FirstByte());
        trace.Select("deezer", false, "Shared");
        trace.Catalog(new Song { Title = "Song\nname", Artist = new string('x', 250) });
        for (var index = 0; index < 200; index++) using (trace.Measure(PlaybackStartPhase.Retry, "deezer")) { }
        var sample = trace.Snapshot("completed", complete: true);
        Assert.Equal(30, sample.HeadersMs);
        Assert.Equal(35, sample.FirstByteMs);
        Assert.Equal(PlaybackStartTimings.MaximumPhases, sample.Phases.Count);
        Assert.Equal(106, sample.OmittedPhases);
        Assert.Equal("Songname", sample.Title);
        Assert.Equal(200, sample.Artist!.Length);
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Route && phase.ElapsedMs == 30);
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Account && phase.ElapsedMs == 10);
        trace.Select("apple-musickit", true, "Personal");
        Assert.Equal("deezer", trace.Snapshot("completed").Provider);
    }

    [Fact]
    public void FailedSourcesRetainCachedCatalogLabelsAndAttemptedProvider()
    {
        var clock = new ManualTimeProvider();
        using var timing = new PlaybackStartTimings(clock);
        timing.Enter(); timing.Enable();
        timing.Catalog(new Song { Title = "Known recording", Artist = "Known artist" });
        using (timing.Measure(PlaybackStartPhase.Lease, "qobuz")) clock.Advance(TimeSpan.FromSeconds(15));
        var sample = timing.Snapshot("failed");
        Assert.Equal("qobuz", sample.Provider);
        Assert.Equal("Known recording", sample.Title);
        Assert.Equal("Known artist", sample.Artist);
        Assert.Null(sample.FirstByteMs);
        Assert.Equal(15000, Assert.Single(sample.Phases).ElapsedMs);
    }

    [Theory]
    [InlineData("6000", "700", true)]
    [InlineData("0", "0", false)]
    [InlineData("-1", "700", false)]
    [InlineData("6000,7000", "700", false)]
    [InlineData("private-payload", "700", false)]
    [InlineData("9000", "700", false)]
    [InlineData("999999999999999", "700", false)]
    public void RetainsOnlyBoundedNumericSidecarPreparation(string prepare, string process, bool retained)
    {
        var clock = new ManualTimeProvider();
        using var timing = new PlaybackStartTimings(clock);
        timing.Enable();
        clock.Advance(TimeSpan.FromSeconds(8));
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-Allstarr-Prepare-Download-Ms", prepare);
        response.Headers.TryAddWithoutValidation("X-Allstarr-Prepare-Process-Ms", process);
        timing.Prepared(response, "apple-musickit");
        var phases = timing.Snapshot("started").Phases;
        if (retained)
        {
            Assert.Contains(phases, phase => phase.Phase == PlaybackStartPhase.Preparation && phase.ElapsedMs == 6000 && phase.StartedMs == 1300);
            Assert.Contains(phases, phase => phase.Phase == PlaybackStartPhase.Process && phase.ElapsedMs == 700 && phase.StartedMs == 7300);
        }
        else Assert.Empty(phases);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(phases), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetainsFirstByteBeforeCompletionAndSurvivesAStoreRestartWithoutRequestSecrets()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider();
        var store = Store(factory, clock);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?api_key=private-query-token");
        context.TraceIdentifier = "private-correlation";
        context.Request.Headers.Authorization = "Bearer private-header-token";
        context.Response.Body = new MemoryStream();
        var pipeline = new PlaybackStartTimingMiddleware(async http =>
        {
            var trace = PlaybackStartTimings.Current!;
            trace.Enable();
            await store.RecordAsync(trace.Snapshot("opening"));
            trace.Select("apple-musickit", true, "Personal");
            trace.Headers();
            clock.Advance(TimeSpan.FromMilliseconds(123));
            await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("audio"));
            await trace.PendingRecords;
            var started = Assert.Single(await Store(factory, clock).ReadAsync(10, default));
            Assert.Equal("started", started.Outcome);
            Assert.Equal(123, started.FirstByteMs);
            Assert.Equal("audio", Encoding.UTF8.GetString(((MemoryStream)context.Items["body"]!).ToArray()));
            throw new OperationCanceledException("private-upstream-error");
        }, store, clock);
        context.Items["body"] = context.Response.Body;
        await Assert.ThrowsAsync<OperationCanceledException>(() => pipeline.InvokeAsync(context));
        var saved = Assert.Single(await Store(factory, clock).ReadAsync(10, default));
        Assert.Equal("cancelled", saved.Outcome);
        Assert.Equal("apple-musickit", saved.Provider);
        Assert.True(saved.Cached);
        Assert.Equal(123, saved.FirstByteMs);
        await using var db = factory.CreateDbContext();
        var row = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(row), StringComparison.Ordinal);
        Assert.Null(PlaybackStartTimings.Current);
    }

    [Fact]
    public async Task PrunesOnlyTimingRowsByAgeAndCountAndBoundsReads()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        await using (var db = factory.CreateDbContext())
        {
            for (var index = 0; index < 520; index++)
            {
                var sample = Sample(clock.GetUtcNow().AddSeconds(-index));
                db.AuditEvents.Add(Row(sample));
            }
            db.AuditEvents.Add(Row(Sample(clock.GetUtcNow().AddDays(-8))));
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                Category = "other",
                SubjectType = "other",
                CreatedAt = clock.GetUtcNow().AddDays(-90),
                DetailsJson = "{}"
            });
            await db.SaveChangesAsync();
        }
        var store = Store(factory, clock);
        var newest = Sample(clock.GetUtcNow());
        await store.RecordAsync(newest);
        await using var check = factory.CreateDbContext();
        Assert.Equal(PlaybackStartTimingStore.MaximumSamples, await check.AuditEvents.CountAsync(row => row.Category == PlaybackStartTimingStore.Category));
        Assert.Equal(1, await check.AuditEvents.CountAsync(row => row.Category == "other"));
        Assert.Equal(100, (await store.ReadAsync(int.MaxValue, default)).Count);
        Assert.Single(await store.ReadAsync(-1, default));
        Assert.Contains(await store.ReadAsync(100, default), sample => sample.Id == newest.Id);
        clock.Advance(TimeSpan.FromDays(8));
        Assert.Empty(await store.ReadAsync(100, default));
    }

    [Fact]
    public async Task UnrelatedResponsesProduceNoTimingRows()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new PlaybackStartTimingMiddleware(http => http.Response.WriteAsync("status"), Store(factory, clock), clock).InvokeAsync(context);
        Assert.Empty(await Store(factory, clock).ReadAsync(100, default));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task RejectedStreamAdmissionDoesNotQueueDiagnosticWrites(ProtocolKind kind)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider();
        var store = Store(factory, clock);
        var principal = new AllstarrPrincipal(Guid.NewGuid(), kind.ToString().ToLowerInvariant(), "primary", "listener", "Listener", false);
        var execution = new ProtocolExecutionContext(kind, "primary", "listener", principal, "fixture", clock.GetUtcNow().AddMinutes(1), default);
        var limits = new MediaCapacityLimits(new ConfigurationManager { [MediaCapacityLimits.StreamsKey] = "1" });
        var admission = new ProviderStreamAdmission(limits);
        using var capacity = admission.TryEnter(execution);
        Assert.NotNull(capacity);
        using var timing = new PlaybackStartTimings(clock);
        timing.Enter();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var opener = new ProviderStreamOpener(gateway.Object, new(), admission: admission, timings: store);
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        await Assert.ThrowsAsync<LocalCapacityException>(() => opener.OpenAsync(http, execution, "deezer", "fixture", ProviderAudioQuality.Any));
        Assert.False(timing.Enabled);
        Assert.Empty(await store.ReadAsync(100, default));
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public void NamedPhasesCoverTheStretchesBetweenMeasuredSteps()
    {
        var clock = new ManualTimeProvider();
        using var trace = new PlaybackStartTimings(clock);
        trace.Enter(); trace.Enable();
        clock.Advance(TimeSpan.FromMilliseconds(40));
        trace.FromStart(PlaybackStartPhase.Request);
        using (trace.Selecting())
        {
            clock.Advance(TimeSpan.FromMilliseconds(15));
            trace.Selected();
            clock.Advance(TimeSpan.FromMilliseconds(5));
        }
        clock.Advance(TimeSpan.FromMilliseconds(10));
        trace.Sending();
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.True(trace.FirstByte());
        var sample = trace.Snapshot("completed", complete: true);
        Assert.Equal(new PlaybackStartPhaseTiming(PlaybackStartPhase.Request, null, 0, 40), sample.Phases[0]);
        Assert.Equal(new PlaybackStartPhaseTiming(PlaybackStartPhase.Selection, null, 40, 15), sample.Phases[1]);
        Assert.Equal(new PlaybackStartPhaseTiming(PlaybackStartPhase.Send, null, 70, 20), sample.Phases[2]);
        Assert.Equal(3, sample.Phases.Count);
        Assert.Equal(90, sample.FirstByteMs);
    }

    [Fact]
    public async Task ARefusedStartRecordsItsStatusAndTheCodeItsAnswerCarried()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider();
        var store = Store(factory, clock);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new PlaybackStartTimingMiddleware(async http =>
        {
            var trace = PlaybackStartTimings.Current!;
            trace.Enable();
            trace.Refuse("rate-limited");
            http.Response.StatusCode = 503;
            await http.Response.WriteAsync("""{"error":"Allstarr is at capacity.","code":"local-capacity-unavailable"}""");
        }, store, clock).InvokeAsync(context);
        var saved = Assert.Single(await store.ReadAsync(10, default));
        Assert.Equal("failed", saved.Outcome);
        Assert.Equal(503, saved.Status);
        Assert.Equal("local-capacity-unavailable", saved.Code);
    }

    [Fact]
    public async Task ARefusedOpenWithoutACodeInItsAnswerRecordsWhyItWasRefused()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider();
        var store = Store(factory, clock);
        var (execution, gateway) = await OpeningAsync(factory, clock);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "fixture", ProviderAudioQuality.Any, null, false))
            .ThrowsAsync(new LocalCapacityException());
        var opener = new ProviderStreamOpener(gateway.Object, new(), timings: store);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        await new PlaybackStartTimingMiddleware(async http =>
        {
            try { await opener.OpenAsync(http, execution, "deezer", "fixture", ProviderAudioQuality.Any); }
            catch (LocalCapacityException)
            {
                http.Response.StatusCode = 503;
                await http.Response.WriteAsync("""{"error":"Allstarr is at capacity."}""");
            }
        }, store, clock).InvokeAsync(context);
        var saved = Assert.Single(await store.ReadAsync(10, default));
        Assert.Equal("failed", saved.Outcome);
        Assert.Equal(503, saved.Status);
        Assert.Equal(LocalCapacityException.Code, saved.Code);
    }

    [Fact]
    public async Task ACompletedStartRecordsItsStatusAndNoCode()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider();
        var store = Store(factory, clock);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new PlaybackStartTimingMiddleware(async http =>
        {
            var trace = PlaybackStartTimings.Current!;
            trace.Enable();
            trace.Refuse("rate-limited");
            http.Response.StatusCode = 206;
            await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("audio"));
        }, store, clock).InvokeAsync(context);
        var saved = Assert.Single(await store.ReadAsync(10, default));
        Assert.Equal("completed", saved.Outcome);
        Assert.Equal(206, saved.Status);
        Assert.Null(saved.Code);
    }

    [Fact]
    public async Task RecordsWrittenBeforeStatusesWereKeptStillRead()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        var sample = Sample(clock.GetUtcNow());
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(sample))!.AsObject();
        legacy.Remove(nameof(PlaybackStartSample.Status));
        legacy.Remove(nameof(PlaybackStartSample.Code));
        var row = Row(sample);
        row.DetailsJson = legacy.ToJsonString();
        await using (var db = factory.CreateDbContext())
        {
            db.AuditEvents.Add(row);
            await db.SaveChangesAsync();
        }
        var saved = Assert.Single(await Store(factory, clock).ReadAsync(10, default));
        Assert.Equal(sample.Id, saved.Id);
        Assert.Null(saved.Status);
        Assert.Null(saved.Code);
    }

    [Fact]
    public async Task AnEarlierRecordLandingLateNeverReplacesTheFinalRecordOfTheSameStart()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        var store = Store(factory, clock);
        using var timing = new PlaybackStartTimings(clock);
        timing.Enable();
        var opening = timing.Snapshot(PlaybackStartTimingStore.OpeningOutcome);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(timing.FirstByte());
        var started = timing.Snapshot(PlaybackStartTimingStore.StartedOutcome);
        timing.Answer(200, null);
        await store.RecordAsync(timing.Snapshot("completed", complete: true));
        await store.RecordAsync(started);
        await store.RecordAsync(opening);
        var saved = Assert.Single(await store.ReadAsync(10, default));
        Assert.Equal("completed", saved.Outcome);
        Assert.Equal(200, saved.Status);

        // A signed playback link opened again is a new start under the same id, and its records replace the old one.
        clock.Advance(TimeSpan.FromSeconds(5));
        using var again = new PlaybackStartTimings(clock);
        again.Correlate(timing.Id);
        again.Enable();
        await store.RecordAsync(again.Snapshot(PlaybackStartTimingStore.OpeningOutcome));
        Assert.Equal(PlaybackStartTimingStore.OpeningOutcome, Assert.Single(await store.ReadAsync(10, default)).Outcome);
    }

    [Fact]
    public async Task OnlyTheFinalRecordPrunesOldRows()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        var old = Sample(clock.GetUtcNow().AddDays(-8));
        await using (var db = factory.CreateDbContext())
        {
            db.AuditEvents.Add(Row(old));
            await db.SaveChangesAsync();
        }
        var store = Store(factory, clock);
        using var timing = new PlaybackStartTimings(clock);
        timing.Enable();
        await store.RecordAsync(timing.Snapshot(PlaybackStartTimingStore.OpeningOutcome));
        await store.RecordAsync(timing.Snapshot(PlaybackStartTimingStore.StartedOutcome));
        await using (var db = factory.CreateDbContext()) Assert.True(await db.AuditEvents.AnyAsync(row => row.Id == old.Id));
        await store.RecordAsync(timing.Snapshot("completed", complete: true));
        await using (var db = factory.CreateDbContext()) Assert.False(await db.AuditEvents.AnyAsync(row => row.Id == old.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASlowOrFailingOpeningRecordNeitherHoldsUpNorFailsTheOpen(bool failing)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FirstWriteFactory(new ProviderCheckServiceTests.Factory(database.Options), gate.Task, failing);
        var clock = new ManualTimeProvider();
        var store = Store(factory, clock);
        var (execution, gateway) = await OpeningAsync(factory, clock);
        var reachedWhileWriting = false;
        using var upstream = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        var media = new ProviderMediaFormat("audio/flac", "flac", "flac", 900000, 44100, 16, 2);
        var routed = new ProtocolProviderStream(upstream, new("fixture", new("https://media.example.test/audio"),
            clock.GetUtcNow().AddHours(1), true, true, media, ProviderStreamRetryBehavior.DoNotRetry), "deezer", "fixture", IsCached: true)
        { FileFacts = new() { Measured = new(media, DurationSeconds: 220) } };
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "fixture", ProviderAudioQuality.Any, null, false))
            .Callback(() => reachedWhileWriting = failing || SpinWait.SpinUntil(() => factory.Waiting, 1000)).ReturnsAsync(routed);
        var opener = new ProviderStreamOpener(gateway.Object, new(), timings: store);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        var body = context.Response.Body;
        await new PlaybackStartTimingMiddleware(async http =>
        {
            var result = Assert.IsType<Microsoft.AspNetCore.Mvc.FileStreamResult>(
                await opener.OpenAsync(http, execution, "deezer", "fixture", ProviderAudioQuality.Any));
            await result.FileStream.CopyToAsync(http.Response.Body);
            await result.FileStream.DisposeAsync();
            gate.SetResult();
        }, store, clock).InvokeAsync(context);
        Assert.True(reachedWhileWriting);
        var saved = Assert.Single(await store.ReadAsync(10, default));
        Assert.Equal("completed", saved.Outcome);
        Assert.Equal(200, saved.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, ((MemoryStream)body).ToArray());
    }

    private static async Task<(ProtocolExecutionContext Execution, Mock<IProtocolProviderGateway> Gateway)> OpeningAsync(
        IDbContextFactory<AllstarrDbContext> factory, TimeProvider clock)
    {
        var principal = new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "primary", "listener", "Listener", false);
        // Each record names its listener, so the listener must exist.
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = principal.UserId,
                Enabled = true,
                DisplayName = "Listener",
                BackendType = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "listener"
            });
            await db.SaveChangesAsync();
        }
        return (new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal, "fixture", clock.GetUtcNow().AddMinutes(1), default),
            new Mock<IProtocolProviderGateway>(MockBehavior.Strict));
    }

    /// <summary>Holds up or fails only the first write, which is the opening record.</summary>
    private sealed class FirstWriteFactory(IDbContextFactory<AllstarrDbContext> inner, Task gate, bool failing) : IDbContextFactory<AllstarrDbContext>
    {
        private int calls;
        public volatile bool Waiting;
        public AllstarrDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                if (failing) throw new InvalidOperationException("store unavailable");
                Waiting = true;
                try { await gate.WaitAsync(cancellationToken); }
                finally { Waiting = false; }
            }
            return inner.CreateDbContext();
        }
    }

    private static PlaybackStartTimingStore Store(IDbContextFactory<AllstarrDbContext> factory, TimeProvider clock) =>
        new(factory, clock, NullLogger<PlaybackStartTimingStore>.Instance);
    private static PlaybackStartSample Sample(DateTimeOffset time) =>
        new(Guid.CreateVersion7(), time, "completed", "deezer", "Song", "Artist", "Shared", false, 100, 90, 100, [], 0);
    private static AuditEventRecord Row(PlaybackStartSample sample) => new()
    {
        Id = sample.Id,
        Category = PlaybackStartTimingStore.Category,
        SubjectType = PlaybackStartTimingStore.Category,
        SubjectId = "recent",
        CreatedAt = sample.StartedAt,
        Outcome = sample.Outcome,
        DetailsJson = JsonSerializer.Serialize(sample)
    };
}
