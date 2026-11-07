using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed partial class AdminUiActivityTests : IClassFixture<AdminUiActivityTests.HomeProfileDiagnostics>
{
    private readonly ITestOutputHelper output;
    private readonly HomeProfileDiagnostics profile;

    public AdminUiActivityTests(ITestOutputHelper output, HomeProfileDiagnostics profile)
    {
        this.output = output;
        this.profile = profile;
    }

    private void WriteHomeProfile(string message)
    {
        output.WriteLine(message);
        profile.WriteLine(message);
    }

    public sealed class HomeProfileDiagnostics(IMessageSink sink)
    {
        public void WriteLine(string message) => sink.OnMessage(new Xunit.Sdk.DiagnosticMessage(message));
    }

    [Fact]
    public async Task HomeActivity_LargeRoutineFloodKeepsNotableResultsWithBoundedReads()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var reads = new HomeReads();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(reads).Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var downloadRoot = Path.GetDirectoryName(database.DatabasePath)!;
        Directory.CreateDirectory(Path.Combine(downloadRoot, "cache"));
        for (var index = 1; index <= 32; index++)
            await File.WriteAllBytesAsync(Path.Combine(downloadRoot, "cache", $"{index}.flac"), []);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            for (var index = 0; index < 30; index++)
            {
                db.Jobs.Add(HistoryJob(owner, "favorite.process", DurableJobState.Succeeded, "succeeded", now.AddDays(-2).AddSeconds(-index)));
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "playlist",
                    Action = "refreshed",
                    Outcome = "success",
                    CreatedAt = now.AddDays(-2).AddSeconds(-index)
                });
            }
            var rollout = HistoryJob(owner, "track-match.rematch", DurableJobState.RetryScheduled, "deferred", now);
            rollout.LastErrorCode = "track_rematch_batch_pending";
            db.Jobs.Add(rollout);
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "matching-rollout",
                Action = "checking-matches",
                Outcome = "running",
                SubjectType = "job",
                SubjectId = rollout.Id.ToString("D"),
                CreatedAt = now,
                DetailsJson = JsonSerializer.Serialize(new { message = "25 of 100 · Now plays from your library: 12 songs" })
            });
            await SeedHomePlaylistsAsync(db, owner);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH RECURSIVE flood(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM flood WHERE n < 50000)
                INSERT INTO audit_events (Id, ActorUserId, Category, Action, Outcome, CorrelationId, DetailsJson, CreatedAt)
                SELECT printf('%08x-0000-0000-0000-000000000001', n), {owner}, 'history',
                    'history-resolution', 'running', '', json_object(), {now.UtcTicks} + n FROM flood
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH RECURSIVE flood(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM flood WHERE n < 50000)
                INSERT INTO durable_jobs (Id, OwnerUserId, ScopeKey, Type, PayloadJson, PolicySnapshotJson,
                    RequestFingerprint, CorrelationId, IdempotencyKey, State, Priority, AttemptCount,
                    FailureCount, DeferralCount, MaxAttempts, MaxDeferrals, AvailableAt, CreatedAt, UpdatedAt, Revision, LastAttemptOutcome, LastErrorCode)
                SELECT printf('%08x-0000-0000-0000-000000000002', n), {owner}, 'flood',
                    CASE n % 3 WHEN 0 THEN 'listening-history.resolve' WHEN 1 THEN 'recording.odesli.enrich'
                        ELSE 'track-match.rematch' END,
                    json_object(), json_object(), '', '', printf('flood-%d', n),
                    CASE n % 3 WHEN 2 THEN 'RetryScheduled' ELSE 'Succeeded' END, 0, 1, 0, 0, 3, 3,
                    {now.UtcTicks}, {now.UtcTicks}, {now.UtcTicks} + n, 1, 'deferred',
                    CASE n % 3 WHEN 2 THEN 'track_rematch_batch_pending' ELSE NULL END FROM flood
                """);
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH RECURSIVE flood(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM flood WHERE n < 50000)
                INSERT INTO listening_events (Id, OwnerUserId, Protocol, BackendInstanceId, OccurrenceKey,
                    State, ListenedAt, UpdatedAt, SourceKind, TrackReference, Artist, MusicBrainzEnrichmentState, ChosenByUser, Revision)
                SELECT printf('%08x-0000-0000-0000-000000000003', n), {owner}, 'jellyfin', 'fixture',
                    printf('listen-%d', n), 'Completed', {now.UtcTicks} - n * 100000000, {now.UtcTicks},
                    'protocol', printf('track-%d', n), printf('Artist %d', n % 100), 'NotRequested', 1, 1 FROM flood
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH RECURSIVE flood(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM flood WHERE n < 50000)
                INSERT INTO playback_delivery_checkpoints (Id, OwnerUserId, SignalKey, TargetId, Kind, State,
                    DetailsJson, RequiresReauthentication, UpdatedAt, AutomaticResendCount, SendCount, ReplaySafe, Revision)
                SELECT printf('%08x-0000-0000-0000-000000000004', n), {owner}, printf('signal-%d', n),
                    'lastfm', 'Completed', 'Delivered', json_object(), 0, {now.UtcTicks} - n * 100000000, 0, 1, 1, 1 FROM flood
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH RECURSIVE flood(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM flood WHERE n < 10000)
                INSERT INTO downloaded_song_mappings (Id, ScopeKey, AudioQuality, ProviderId, ExternalId,
                    LocalPath, Title, Artist, Album, DownloadedAt, Revision)
                SELECT printf('%08x-0000-0000-0000-000000000005', n), 'fixture', 0, 'fixture',
                    printf('track-%d', n), {Path.GetDirectoryName(database.DatabasePath)} || '/cache/' || n || '.flac',
                    'Track', 'Artist', 'Album', {now.UtcTicks}, 1 FROM flood
                """);
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var timing = new HomeTimings(output, profile);
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory)
            .AddSingleton(new DurablePlaylistProjectionReader(factory, HomeLibraryAccess().Object))
            .AddSingleton(new RecentValues(TimeProvider.System))
            .AddSingleton<ILogger<AdminUiController>>(timing).BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Library:DownloadPath"] = downloadRoot
        }).Build();
        var status = new ProviderStatusManager(configuration, null!, NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()));
        var overview = new AdminUiController(configuration,
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, status,
            new ProviderAccountOptions(), matches)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        overview.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = owner.ToString("N"),
            UserName = "Listener",
            AllstarrUserId = owner,
            IsAdministrator = true,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        for (var run = 0; run < 3; run++)
        {
            reads.Commands.Clear();
            timing.Messages.Clear();
            var elapsed = Stopwatch.StartNew();
            var response = Assert.IsType<OkObjectResult>(await overview.GetHome());
            elapsed.Stop();
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
            Assert.Equal(200, json.RootElement.GetProperty("stats").GetProperty("unresolvedTracks").GetInt32());
            Assert.Equal(32, json.RootElement.GetProperty("stats").GetProperty("cacheTracks").GetInt32());
            Assert.Equal(50_000, json.RootElement.GetProperty("stats").GetProperty("currentWeekListens").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("stats").GetProperty("previousWeekListens").GetInt32());
            var rows = json.RootElement.GetProperty("activity").GetProperty("items").EnumerateArray().ToArray();
            WriteHomeProfile($"Home run {run}: {elapsed.Elapsed.TotalMilliseconds:0.0} ms; {reads.Commands.Count} reads");
            Assert.Equal(24, rows.Length);
            Assert.All(rows, row => Assert.False(row.GetProperty("IsRoutine").GetBoolean()));
            Assert.Contains(rows, row => row.GetProperty("Label").GetString() == "Favorite update");
            Assert.Single(rows, row => row.GetProperty("Label").GetString() == "Checking matches");
            if (run == 0) continue;
            Assert.True(reads.Commands.Count <= 40,
                $"Home performed {reads.Commands.Count} reads in {elapsed.Elapsed.TotalMilliseconds:0.0} ms. {string.Join("; ", timing.Messages)}");
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Warm Home took {elapsed.Elapsed}. {string.Join("; ", timing.Messages)}");
            foreach (var (table, index) in new[] { ("durable_jobs", "IX_home_notable_jobs"), ("audit_events", "IX_home_notable_audits") })
            {
                var source = Assert.Single(reads.Commands, query => query.Sql.Contains($"SELECT * FROM {table} WHERE", StringComparison.Ordinal));
                Assert.Contains("LIMIT", source.Sql);
                Assert.Contains(source.Parameters, parameter => Equals(parameter.Value, 25));
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.OpenConnectionAsync();
                await using var explain = db.Database.GetDbConnection().CreateCommand();
                explain.CommandText = "EXPLAIN QUERY PLAN " + source.Sql;
                foreach (var retained in source.Parameters)
                {
                    var parameter = explain.CreateParameter();
                    parameter.ParameterName = retained.Key;
                    parameter.Value = retained.Value;
                    explain.Parameters.Add(parameter);
                }
                var plan = new List<string>();
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
                WriteHomeProfile(string.Join("; ", plan));
                Assert.Contains(plan, step => step.Contains(index, StringComparison.Ordinal));
                Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
            }
        }
        var other = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = other, DisplayName = "Other listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = other.ToString("N") });
            await db.SaveChangesAsync();
        }
        overview.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "other",
            UserId = other.ToString("N"),
            UserName = "Other listener",
            AllstarrUserId = other,
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        using var scoped = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await overview.GetHome()).Value));
        Assert.Equal(0, scoped.RootElement.GetProperty("stats").GetProperty("currentWeekListens").GetInt32());
        Assert.Equal(0, scoped.RootElement.GetProperty("stats").GetProperty("scrobbleDeliveries").GetInt32());
        Assert.Equal(0, scoped.RootElement.GetProperty("stats").GetProperty("unresolvedTracks").GetInt32());
        Assert.Empty(scoped.RootElement.GetProperty("activity").GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task HomePlaylistCountsReadAllLinksInOneBatch()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var reads = new HomeReads();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(reads).Options);
        var owner = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "listener", DisplayName = "Listener" });
            await SeedHomePlaylistsAsync(db, owner);
        }
        var access = HomeLibraryAccess();
        var projections = new DurablePlaylistProjectionReader(factory, access.Object);
        await using var verify = await factory.CreateDbContextAsync();
        var ids = await verify.PlaylistLinks.Select(item => item.Id).ToArrayAsync();
        reads.Commands.Clear();

        Assert.Equal((0, 200), await projections.ReadHomeCountsAsync(owner, ids, owner));

        Assert.InRange(reads.Commands.Count, 1, 25);
        Assert.Single(reads.Commands, query => query.Sql.Contains("FROM \"playlist_source_entries\"", StringComparison.Ordinal));
        access.Verify(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IBackendLibraryAccessResolver> HomeLibraryAccess()
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(null, new(false, [])));
        return access;
    }

    private static async Task SeedHomePlaylistsAsync(AllstarrDbContext db, Guid owner)
    {
        for (var index = 0; index < 20; index++)
        {
            var link = new PlaylistLinkRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                SourceProviderId = "fixture",
                SourcePlaylistId = $"playlist-{index}",
                SourcePlaylistIdHash = index.ToString("x64"),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "fixture",
                Mode = PlaylistLinkMode.Virtual
            };
            db.PlaylistLinks.Add(link);
            var snapshot = new PlaylistSourceSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                PlaylistLinkId = link.Id,
                SnapshotVersion = 1,
                Name = $"Playlist {index}",
                PayloadSha256 = new string('a', 64),
                PublishedAt = DateTimeOffset.UtcNow,
                RetrievedAt = DateTimeOffset.UtcNow
            };
            db.PlaylistSourceSnapshots.Add(snapshot);
            for (var position = 0; position < 10; position++)
            {
                var external = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    ProviderId = "fixture",
                    Protocol = "jellyfin",
                    BackendInstanceId = "fixture",
                    ExternalIdHash = (index * 10 + position).ToString("x64"),
                    SnapshotVersion = 1,
                    PayloadSha256 = new string('a', 64),
                    PayloadJson = JsonSerializer.Serialize(new PlaylistSourceMetadata("Track", ["Artist"]))
                };
                db.ExternalMetadataSnapshots.Add(external);
                db.PlaylistSourceEntries.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    PlaylistSourceSnapshotId = snapshot.Id,
                    ExternalMetadataSnapshotId = external.Id,
                    SourcePosition = position,
                    SourceEntryIdHash = position.ToString("x64")
                });
            }
        }
        await db.SaveChangesAsync();
    }

    private sealed record HomeQuery(string Sql, KeyValuePair<string, object>[] Parameters);

    private sealed class HomeReads : DbCommandInterceptor
    {
        public List<HomeQuery> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(parameter => new KeyValuePair<string, object>(parameter.ParameterName, parameter.Value!)).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class HomeTimings(ITestOutputHelper output, HomeProfileDiagnostics profile) : ILogger<AdminUiController>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.StartsWith("Home timing:", StringComparison.Ordinal)) return;
            Messages.Add(message);
            output.WriteLine(message);
            profile.WriteLine(message);
        }
    }
}
