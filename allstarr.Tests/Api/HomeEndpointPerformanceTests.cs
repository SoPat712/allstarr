using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Controllers;
using allstarr.Core.Enrichment;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class HomeEndpointPerformanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private readonly Probe probe = new();
    private readonly Guid owner = Guid.NewGuid();
    private readonly IPlatformClock clock = new SystemPlatformClock();
    private readonly Mock<IProtocolProviderGateway> gateway = new();
    private readonly Mock<IDurableRuntimeSettings> settings = new();
    private readonly Mock<IEffectiveProviderPolicyResolver> policies = new();
    private readonly Mock<IBackendLibraryAccessResolver> access = new();
    private readonly TestMemoryApplicationCache cache = new();

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(probe).Options);
        await using var db = factory.CreateDbContext();
        db.Users.Add(new() { Id = owner, Enabled = true, IsAdmin = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "listener" });
        await db.SaveChangesAsync();
        // Keep every stored mix full and two review versions per song.
        var recordings = Enumerable.Range(0, 520).Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < recordings.Length; index++)
        {
            db.CanonicalRecordings.Add(new() { Id = recordings[index], CreatedByUserId = owner });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recordings[index],
                ProviderId = "deezer",
                ExternalId = index.ToString(),
                ExternalIdHash = Hash(index.ToString()),
                ResourceKind = allstarr.Core.Capabilities.ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "fixture",
                DecisionVersion = 1,
                Revision = 1
            });
        }
        var songs = Enumerable.Range(0, 520).Select(index => new Song
        {
            Id = $"ext-deezer-song-{index}",
            CanonicalRecordingId = recordings[index],
            ExternalProvider = "deezer",
            ExternalId = index.ToString(),
            Title = $"Song {index}",
            Artist = $"Artist {index % 40}",
            Album = "Album",
            Duration = 180,
            ExplicitContentLyrics = 0
        }).ToArray();
        var mixes = PersonalMixRules.Kinds.Select((kind, index) => new PersonalMix(Guid.NewGuid(), kind, kind, "Stored mix", 1,
            clock.UtcNow, new(true, "Ready"), true, songs.Skip(index * 40).Take(40)
                .Select(song => new PersonalMixItem(Guid.Empty, 1, song, "history", "Stored pick", 1)).ToArray())).ToDictionary(mix => mix.Kind);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            ProviderId = "allstarr",
            ResourceKind = "personal-mixes",
            ExternalIdHash = Hash($"{owner}:jellyfin:fixture:listener"),
            SnapshotVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new { AppsOptInVersion = 3, TimeZoneId = "America/New_York", Mixes = mixes }),
            PayloadSha256 = Hash("mixes"),
            RetrievedAt = clock.UtcNow
        });
        for (var index = 0; index < 520; index++) db.LibraryTracks.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "fixture",
            BackendLibraryId = "music",
            BackendItemId = $"local-{index}",
            FilePath = $"/fixture/{index}.flac",
            Title = $"Song {index}",
            Artist = $"Artist {index % 40}",
            Album = "Album",
            DurationMilliseconds = 180000,
            Revision = 1
        });
        for (var index = 0; index < 520; index++) db.ListeningEvents.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "fixture",
            OccurrenceKey = $"listen-{index}",
            State = ListeningEventState.Completed,
            TrackReference = songs[index % songs.Length].Id,
            SourceKind = "protocol",
            Title = songs[index % songs.Length].Title,
            Artist = songs[index % songs.Length].Artist,
            ListenedAt = clock.UtcNow.AddMinutes(-index),
            UpdatedAt = clock.UtcNow
        });
        for (var index = 0; index < 1040; index++)
        {
            var id = Guid.NewGuid();
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = id,
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = "listener",
                ProviderId = "deezer",
                ResourceKind = "track",
                ExternalIdHash = Hash((index % 520).ToString()),
                SnapshotVersion = index / 520 + 1,
                PayloadJson = JsonSerializer.Serialize(new { Title = $"Song {index % 520}", Artist = $"Artist {index % 40}", DurationSeconds = 180 }),
                PayloadSha256 = Hash(index.ToString()),
                RetrievedAt = clock.UtcNow.AddSeconds(index - 1040)
            });
            db.TrackMatches.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ExternalSnapshotId = id,
                State = TrackMatchState.Unresolved,
                DecisionVersion = 1,
                SourceSnapshotVersion = index / 520 + 1,
                Revision = 1,
                DecidedAt = clock.UtcNow
            });
            db.AuditEvents.Add(new() { Id = Guid.NewGuid(), ActorUserId = owner, Category = "matching", Action = "review", Outcome = "unresolved", CreatedAt = clock.UtcNow });
        }
        await db.SaveChangesAsync();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        policies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<allstarr.Core.Capabilities.ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "Lossless", .07)
        { Preferences = new("All") });
        settings.Setup(item => item.GetAsync(PersonalMixService.PublishKey, It.IsAny<CancellationToken>())).ReturnsAsync(new EffectiveRuntimeSetting(
            PersonalMixService.PublishKey, RuntimeSettingValueType.Boolean, true, "true", RuntimeSettingOrigin.Durable, 1, null, null, null));
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<allstarr.Core.Capabilities.ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["deezer"]);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener",
            new(owner, "jellyfin", "fixture", "listener", "Listener", true), "fixture", clock.UtcNow.AddMinutes(5), default);
        access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(context, new(true, ["music"])));
        probe.Reset();
    }

    [Fact]
    public async Task Mix_cards_read_stored_picks_without_library_scoring_or_history_replay()
    {
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var identities = new TrackIdentityService(factory, state, clock);
        var library = new LibraryIndexService(factory, state, clock, identities, access.Object);
        var tags = new SharedTagStore(factory, clock);
        var router = new Mock<IProviderRouter>();
        var service = new PersonalMixService(factory, clock, tags, new(factory, tags, clock), new(gateway.Object), gateway.Object,
            new NewReleasesService(factory, clock, router.Object, settings.Object), settings.Object, policies.Object, access.Object,
            libraryIndex: library, identities: identities);
        var controller = new MixesController(service, settings.Object, factory, clock, null!) { ControllerContext = Context() };
        await controller.List("America/New_York");
        probe.Reset();
        var watch = Stopwatch.StartNew();
        var cards = Json(await controller.List("America/New_York"));
        watch.Stop();
        output.WriteLine($"Mix cards warm: {watch.Elapsed.TotalMilliseconds:F1} ms; {probe.Reads} reads; library reads {probe.LibraryReads}; history reads {probe.HistoryReads}; SQL execution {probe.SqlMilliseconds:F1} ms.");
        Assert.Equal(PersonalMixRules.Kinds.Length, cards.GetArrayLength());
        Assert.All(cards.EnumerateArray(), card => Assert.True(card.GetProperty("readiness").GetProperty("ready").GetBoolean()));
        var savedLibraryReads = probe.LibraryReads;
        var savedHistoryReads = probe.HistoryReads;
        var savedReads = probe.Reads;
        var savedElapsed = watch.Elapsed;
        await AddLinkedMixesAsync();
        await controller.List("America/New_York");
        probe.Reset();
        watch.Restart();
        var linked = Json(await controller.List("America/New_York"));
        watch.Stop();
        output.WriteLine($"Mix cards with two linked services warm: {watch.Elapsed.TotalMilliseconds:F1} ms; {probe.Reads} reads; SQL execution {probe.SqlMilliseconds:F1} ms.");
        Assert.Equal(PersonalMixRules.Kinds.Length + 2, linked.GetArrayLength());
        Assert.All(linked.EnumerateArray().Where(card => card.GetProperty("kind").GetString() == "external"),
            card => Assert.True(card.GetProperty("readiness").GetProperty("ready").GetBoolean()));
        Assert.Equal(0, savedLibraryReads);
        Assert.Equal(0, savedHistoryReads);
        Assert.InRange(savedReads, 1, 20);
        Assert.True(savedElapsed < TimeSpan.FromSeconds(1), $"Warm mix cards took {savedElapsed.TotalMilliseconds:F1} ms.");
        Assert.InRange(probe.Reads, 1, 60);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Warm linked mix cards took {watch.Elapsed.TotalMilliseconds:F1} ms.");
        await service.ListAsync(owner, "America/New_York");
        probe.Reset();
        watch.Restart();
        var complete = await service.ListAsync(owner, "America/New_York");
        watch.Stop();
        output.WriteLine($"Complete mix list warm: {watch.Elapsed.TotalMilliseconds:F1} ms; {probe.Reads} reads; SQL execution {probe.SqlMilliseconds:F1} ms.");
        Assert.Equal(PersonalMixRules.Kinds.Length + 2, complete.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Warm complete mixes took {watch.Elapsed.TotalMilliseconds:F1} ms.");
        var selected = complete.First().Id;
        await service.GetAsync(owner, selected);
        probe.Reset();
        watch.Restart();
        await service.GetAsync(owner, selected);
        watch.Stop();
        output.WriteLine($"Mix detail warm: {watch.Elapsed.TotalMilliseconds:F1} ms; {probe.Reads} reads; SQL execution {probe.SqlMilliseconds:F1} ms.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Warm mix detail took {watch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private async Task AddLinkedMixesAsync()
    {
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener",
            new(owner, "jellyfin", "fixture", "listener", "Listener", true), "fixture", clock.UtcNow.AddMinutes(5), default);
        access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(context, new(true, ["music"])));
        await using var db = factory.CreateDbContext();
        foreach (var selection in new[] { "loved", "similar" })
        {
            var account = Guid.NewGuid();
            var link = Guid.NewGuid();
            var published = Guid.NewGuid();
            db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = owner, ProviderId = "lastfm", Enabled = true });
            db.PlaylistLinks.Add(new()
            {
                Id = link,
                OwnerUserId = owner,
                ProviderAccountId = account,
                SourceProviderId = "lastfm",
                SourcePlaylistId = selection,
                SourcePlaylistIdHash = Hash(selection),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "fixture"
            });
            db.PlaylistSourceSnapshots.Add(new()
            {
                Id = published,
                OwnerUserId = owner,
                ProviderAccountId = account,
                PlaylistLinkId = link,
                Name = selection,
                SnapshotVersion = 1,
                RetrievedAt = clock.UtcNow,
                PublishedAt = clock.UtcNow,
                PayloadSha256 = Hash(selection)
            });
            for (var index = 0; index < 40; index++)
            {
                var recording = Guid.NewGuid();
                var source = Guid.NewGuid();
                var match = Guid.NewGuid();
                var sourceIdentity = Guid.NewGuid();
                var targetId = (100000 + (selection == "loved" ? 0 : 40) + index).ToString();
                var hash = Hash(selection + index);
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
                db.ProviderTrackIdentities.Add(new()
                {
                    Id = Guid.NewGuid(),
                    ProviderId = "deezer",
                    CanonicalRecordingId = recording,
                    ExternalId = targetId,
                    ExternalIdHash = Hash(targetId),
                    ResourceKind = allstarr.Core.Capabilities.ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Catalog,
                    Verification = ProviderIdentityVerification.Verified,
                    DecisionVersion = 1
                });
                db.ProviderTrackIdentities.Add(new()
                {
                    Id = sourceIdentity,
                    ProviderId = "lastfm",
                    ProviderAccountId = account,
                    CanonicalRecordingId = recording,
                    ExternalId = selection + index,
                    ExternalIdHash = hash,
                    ResourceKind = allstarr.Core.Capabilities.ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Account,
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "fixture",
                    DecisionVersion = 1
                });
                db.ExternalMetadataSnapshots.Add(new()
                {
                    Id = source,
                    OwnerUserId = owner,
                    ProviderAccountId = account,
                    ProviderId = "lastfm",
                    ProviderTrackIdentityId = sourceIdentity,
                    Protocol = "jellyfin",
                    BackendInstanceId = "fixture",
                    BackendPrincipalId = "listener",
                    ResourceKind = "track",
                    SnapshotVersion = 1,
                    ExternalIdHash = hash,
                    PayloadSha256 = hash,
                    RetrievedAt = clock.UtcNow,
                    PayloadJson = JsonSerializer.Serialize(new { Title = $"Song {index}", Artist = $"Artist {index % 40}", DurationSeconds = 180 })
                });
                db.TrackMatches.Add(new()
                {
                    Id = match,
                    OwnerUserId = owner,
                    ExternalSnapshotId = source,
                    CanonicalRecordingId = recording,
                    State = TrackMatchState.Accepted,
                    Confidence = 1,
                    DecisionVersion = 1,
                    SourceSnapshotVersion = 1,
                    Revision = 1
                });
                db.PlaylistSourceEntries.Add(new()
                {
                    Id = Guid.NewGuid(),
                    PlaylistSourceSnapshotId = published,
                    ExternalMetadataSnapshotId = source,
                    SourceEntryIdHash = hash,
                    SourcePosition = index,
                    PublishedTrackMatchId = match
                });
            }
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Track_match_summary_reuses_projection_without_reloading_review_rows()
    {
        var repository = new TrackMatchCommandService(factory, new(), new ProviderAccountResolver(factory), clock, access.Object);
        TrackMatchesController Controller() => new(repository, gateway.Object, new(factory, clock), factory, null!, null!, new(), null!, applicationCache: cache)
        { ControllerContext = Context() };
        var cold = Stopwatch.StartNew();
        var initial = Raw(await Controller().List(pageSize: 1));
        cold.Stop();
        probe.Reset();
        var watch = Stopwatch.StartNew();
        var warm = Raw(await Controller().List(pageSize: 1));
        watch.Stop();
        output.WriteLine($"Match summary cold: {cold.Elapsed.TotalMilliseconds:F1} ms; warm: {watch.Elapsed.TotalMilliseconds:F1} ms; {probe.Reads} reads; library reads {probe.LibraryReads}; SQL execution {probe.SqlMilliseconds:F1} ms.");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(initial.GetRawText()), JsonNode.Parse(warm.GetRawText())));
        Assert.InRange(probe.Reads, 0, 4);
        Assert.Equal(0, probe.LibraryReads);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Warm match summary took {watch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private ControllerContext Context()
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "listener",
            UserName = "Listener",
            AllstarrUserId = owner,
            IsAdministrator = true,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new() { HttpContext = http };
    }

    private static JsonElement Raw(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public Task DisposeAsync() => database.DisposeAsync().AsTask();
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }

    private sealed class Probe : DbCommandInterceptor
    {
        public int Reads, LibraryReads, HistoryReads;
        public double SqlMilliseconds;
        public void Reset() { Reads = LibraryReads = HistoryReads = 0; SqlMilliseconds = 0; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            SqlMilliseconds += eventData.Duration.TotalMilliseconds;
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (command.CommandText.Contains("FROM \"library_tracks\"", StringComparison.Ordinal)) LibraryReads++;
            if (command.CommandText.Contains("FROM \"listening_events\"", StringComparison.Ordinal)) HistoryReads++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
