using System.Text;
using System.Text.Json;
using allstarr.Core.Playback;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
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
