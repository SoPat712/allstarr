using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class AdminUiActivityTests
{
    [Fact]
    public async Task ActivityAssemblyPreparesLibraryOnceForAllMatchRows()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(new RequestTimingDbInterceptor()).Options);
        var owner = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            await db.SaveChangesAsync();
        }
        var tracks = Enumerable.Range(0, 300).Select(index => new LibraryTrackRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            BackendInstanceId = "fixture",
            BackendLibraryId = "music",
            BackendItemId = $"track-{index}",
            Title = $"Song {index}",
            Artist = "Artist",
            FilePath = $"/music/{index}.flac"
        }).ToArray();
        var now = DateTimeOffset.UtcNow;
        var snapshots = Enumerable.Range(0, 150).Select(index => new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            BackendInstanceId = "fixture",
            ProviderId = "fixture",
            PayloadJson = $"{{\"title\":\"Song {index}\",\"artist\":\"Artist\"}}"
        }).ToArray();
        var decisions = snapshots.Select((snapshot, index) => new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ExternalSnapshotId = snapshot.Id,
            LibraryTrackId = tracks[index].Id,
            State = TrackMatchState.Accepted,
            DecidedAt = now.AddSeconds(-index)
        }).ToArray();
        var original = Stopwatch.StartNew();
        var expected = decisions.Select((decision, index) => AdminUiController.MatchActivityItem(decision, snapshots[index], null, [], tracks)
            with
        { ActorDisplayName = "Listener" }).ToArray();
        WriteHomeProfile($"Per-row library preparation for 150 matches and 300 tracks: {original.Elapsed.TotalMilliseconds:0.0} ms");
        var counted = new ActivityLibraryReads(tracks);
        var matches = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        matches.Setup(item => item.GetActivityDataAsync(It.IsAny<TrackMatchActor>(), null, null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackMatchActivityData(decisions, snapshots, [], counted));
        var policy = new Mock<IEffectiveProviderPolicyResolver>();
        policy.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0));
        var timings = new AssemblyTimings(WriteHomeProfile);
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory)
            .AddSingleton<ILogger<AdminUiController>>(timings).BuildServiceProvider();
        var controller = AssemblyController(factory, services, owner, policy.Object, matches.Object);
        Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(limit: 100));
        counted.Reads = 0;
        using var timing = RequestTiming.Begin();

        var page = Assert.IsType<AdminUiActivityPage>(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(limit: 100)).Value);

        var measurement = timing.Snapshot("admin");
        WriteHomeProfile($"Activity with 150 matches and 300 library tracks: {measurement.Header}; library enumerations {counted.Reads}");
        Assert.Equal(JsonSerializer.Serialize(expected.Take(100)), JsonSerializer.Serialize(page.Items));
        Assert.Equal(1, counted.Reads);
        Assert.True(measurement.TotalMs <= 300, $"Activity exceeded 300 ms: {measurement.Header}");
    }

    [Fact]
    public async Task ActivityAssemblySeparatesPolicyWaitFromDatabaseTime()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(new RequestTimingDbInterceptor()).Options);
        var policy = new Mock<IEffectiveProviderPolicyResolver>();
        policy.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await Task.Delay(80);
            return new(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0);
        });
        var timings = new AssemblyTimings(WriteHomeProfile);
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory)
            .AddSingleton<ILogger<AdminUiController>>(timings).BuildServiceProvider();
        var controller = AssemblyController(factory, services, Guid.CreateVersion7(), policy.Object);
        using var timing = RequestTiming.Begin();

        Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity());

        var message = Assert.Single(timings.Messages);
        var policyMs = int.Parse(Regex.Match(message, @"policy (\d+) ms").Groups[1].Value);
        Assert.True(policyMs >= 60, message);
        Assert.Contains("download mappings", message);
        Assert.Contains("actors", message);
        var result = timing.Snapshot("admin");
        Assert.True(result.TotalMs >= policyMs, message);
        Assert.True(result.DbMs < result.TotalMs - 60, result.Header);
    }

    [Fact]
    public async Task ActivityAssemblyReusesPolicyWhileSettingsGateAndDatabaseWriterAreHeld()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .UseSqlite(database.ConnectionString, sqlite => sqlite.UseParameterizedCollectionMode(ParameterTranslationMode.Parameter))
            .AddInterceptors(new RequestTimingDbInterceptor()).Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            for (var index = 0; index < 2500; index++)
            {
                var at = now.AddSeconds(-index);
                var job = HistoryJob(owner, "favorite.process", DurableJobState.Succeeded, "succeeded", at);
                job.IdempotencyKey = $"work-{index}";
                db.Jobs.Add(job);
                db.AuditEvents.Add(new() { Id = Guid.CreateVersion7(), ActorUserId = owner, Category = "playlist", Action = "updated", Outcome = "success", CreatedAt = at });
                var snapshot = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    ProviderId = "fixture",
                    Protocol = "jellyfin",
                    BackendInstanceId = "fixture",
                    ExternalIdHash = index.ToString("x64"),
                    SnapshotVersion = 1,
                    PayloadSha256 = new string('a', 64),
                    PayloadJson = "{\"title\":\"Track\",\"artist\":\"Artist\"}",
                    RetrievedAt = at
                };
                db.ExternalMetadataSnapshots.Add(snapshot);
                var local = new LibraryTrackRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "fixture",
                    BackendLibraryId = "music",
                    BackendItemId = $"track-{index}",
                    Title = $"Song {index}",
                    Artist = "Artist",
                    FilePath = $"/music/{index}.flac"
                };
                db.LibraryTracks.Add(local);
                db.TrackMatches.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    ExternalSnapshotId = snapshot.Id,
                    LibraryTrackId = local.Id,
                    State = TrackMatchState.Accepted,
                    DecisionVersion = 1,
                    SourceSnapshotVersion = 1,
                    PolicyVersion = "fixture",
                    DecidedAt = at
                });
            }
            await db.SaveChangesAsync();
        }
        var signal = new RuntimeSettingsChangeSignal();
        using var durable = new DurableRuntimeSettingsService(factory, new ConfigurationBuilder().Build(), new SystemPlatformClock(), signal);
        using var gate = new SemaphoreSlim(0, 1);
        var held = false;
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.GetManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> keys, CancellationToken token) =>
            {
                if (held) await gate.WaitAsync(token);
                return await durable.GetManyAsync(keys, token);
            });
        settings.Setup(item => item.GetPreferencesAsync(null, It.IsAny<CancellationToken>()))
            .Returns(async (Guid? user, CancellationToken token) =>
            {
                if (held) await gate.WaitAsync(token);
                return await durable.GetPreferencesAsync(user, token);
            });
        var timings = new AssemblyTimings(WriteHomeProfile);
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory)
            .AddSingleton<IRuntimeSettingsChangeSignal>(signal).AddSingleton<ILogger<AdminUiController>>(timings).BuildServiceProvider();
        var resolver = ActivatorUtilities.CreateInstance<EffectiveProviderPolicyResolver>(services, settings.Object);
        var uncached = new EffectiveProviderPolicyResolver(durable);
        var baselines = new Dictionary<int, string>();
        foreach (var limit in new[] { 20, 100 })
            baselines[limit] = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await AssemblyController(factory, services, owner, uncached)
                .GetDashboardActivity(limit: limit)).Value);
        await using var writer = await factory.CreateDbContextAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("UPDATE users SET DisplayName = 'Uncommitted' WHERE DisplayName = 'Listener'");
        // A cold policy read must also stay independent of SQLite's writer in WAL mode.
        var controller = AssemblyController(factory, services, owner, resolver);
        using (var timing = RequestTiming.Begin())
        {
            Assert.Equal(baselines[100], JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(limit: 100)).Value));
            var measurement = timing.Snapshot("admin");
            WriteHomeProfile($"Activity cold policy: {measurement.Header}");
            Assert.True(measurement.TotalMs <= 300, $"Activity exceeded 300 ms: {measurement.Header}");
        }
        held = true;
        foreach (var limit in new[] { 20, 100 })
        {
            for (var run = 0; run < 5; run++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var timing = RequestTiming.Begin();
                var response = Assert.IsType<OkObjectResult>(await AssemblyController(factory, services, owner, resolver)
                    .GetDashboardActivity(limit: limit, cancellationToken: timeout.Token));
                var measurement = timing.Snapshot("admin");
                WriteHomeProfile($"Activity limit {limit}, run {run}: {measurement.Header}");
                Assert.Equal(baselines[limit], JsonSerializer.Serialize(response.Value));
                Assert.True(measurement.TotalMs <= 300, $"Activity exceeded 300 ms: {measurement.Header}");
                Assert.Equal("hit", measurement.Cache);
            }
        }
        settings.Verify(item => item.GetManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        settings.Verify(item => item.GetPreferencesAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        await transaction.RollbackAsync();
        held = false;
        var previous = await resolver.ResolveAsync();
        await durable.ApplyBatchAsync([new("Providers:StreamingOrder", "qobuz,deezer")], "fixture");
        var changed = await resolver.ResolveAsync();
        Assert.NotSame(previous, changed);
        Assert.Equal(new[] { "qobuz", "deezer" }, changed.GetProviderOrder(ProviderCapabilityKind.Streaming));
    }

    private static AdminUiController AssemblyController(Factory factory, IServiceProvider services, Guid owner,
        IEffectiveProviderPolicyResolver policy, ITrackMatchRepository? repository = null)
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        var principal = new AllstarrPrincipal(owner, "jellyfin", "fixture", owner.ToString("N"), "Listener", false);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", owner.ToString("N"), principal,
            "fixture", DateTimeOffset.UtcNow.AddMinutes(5), default);
        access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(context, new(true, ["music"])));
        var matches = repository ?? new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory),
            new SystemPlatformClock(), access.Object);
        var controller = new AdminUiController(new ConfigurationBuilder().Build(),
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, null!,
            new ProviderAccountOptions(), matches, effectivePolicies: policy)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = owner.ToString("N"),
            UserName = "Listener",
            AllstarrUserId = owner,
            IsAdministrator = true,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return controller;
    }

    private sealed class ActivityLibraryReads(IReadOnlyList<LibraryTrackRecord> tracks) : IReadOnlyList<LibraryTrackRecord>
    {
        public int Reads { get; set; }
        public int Count => tracks.Count;
        public LibraryTrackRecord this[int index] => tracks[index];
        public IEnumerator<LibraryTrackRecord> GetEnumerator() { Reads++; return tracks.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class AssemblyTimings(Action<string> write) : ILogger<AdminUiController>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.StartsWith("Activity timing:", StringComparison.Ordinal)) return;
            Messages.Add(message);
            write(message);
        }
    }
}
