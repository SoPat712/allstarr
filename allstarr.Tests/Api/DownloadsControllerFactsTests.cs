using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DownloadsControllerFactsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KeptByUsesActiveInterestsAndRestrictsOtherListenerNames(bool administrator)
    {
        using var fixture = new Fixture(administrator);
        await fixture.InitializeAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var favorite = new FavoriteStateRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = fixture.Other.Id,
                Protocol = "jellyfin",
                BackendInstanceId = "home",
                ItemId = "song",
                IsFavorite = true
            };
            var job = new DurableJobRecord { Id = Guid.NewGuid(), OwnerUserId = fixture.Other.Id };
            var favoriteEvent = new FavoriteEventRecord { Id = Guid.NewGuid(), OwnerUserId = fixture.Other.Id, JobId = job.Id };
            favorite.LastEventId = favoriteEvent.Id;
            db.Jobs.Add(job);
            db.Set<FavoriteEventRecord>().Add(favoriteEvent);
            db.Set<FavoriteStateRecord>().Add(favorite);
            db.ManagedFileReferences.AddRange(
                fixture.Reference($"keep:user:{fixture.Viewer.Id:N}:{Guid.NewGuid():N}"),
                fixture.Reference($"heart:{favorite.Id:N}"),
                fixture.Reference($"keep:user:{fixture.Other.Id:N}:{Guid.NewGuid():N}"),
                fixture.Reference($"keep:user:{fixture.Foreign.Id:N}:{Guid.NewGuid():N}"),
                fixture.Reference($"keep:user:{fixture.Released.Id:N}:{Guid.NewGuid():N}"),
                fixture.Reference($"keep:user:{fixture.Ended.Id:N}:{Guid.NewGuid():N}", released: true));
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        var keepers = result.RootElement.GetProperty("keptBy").EnumerateArray().ToArray();
        Assert.Equal(2, keepers.Length);
        Assert.Equal("You", keepers[0].GetProperty("displayName").GetString());
        Assert.True(keepers[0].GetProperty("isViewer").GetBoolean());
        Assert.Equal(administrator ? "Robin" : "Someone else", keepers[1].GetProperty("displayName").GetString());
        Assert.Equal(new[] { "favorite", "keep" }, keepers[1].GetProperty("reasons").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("Foreign", result.RootElement.GetRawText());
        Assert.DoesNotContain("Released", result.RootElement.GetRawText());
        Assert.DoesNotContain("Ended", result.RootElement.GetRawText());
        Assert.DoesNotContain(fixture.Other.Id.ToString(), result.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastPlayedUsesRecordingHistoryAndViewerScope(bool administrator)
    {
        using var fixture = new Fixture(administrator);
        await fixture.InitializeAsync();
        var own = DateTimeOffset.UtcNow.AddDays(-3);
        var other = own.AddDays(1);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.ListeningEvents.AddRange(fixture.Play(fixture.Viewer, own.AddDays(-1)), fixture.Play(fixture.Viewer, own), fixture.Play(fixture.Other, other),
                fixture.Play(fixture.Foreign, other.AddHours(1)), fixture.Play(fixture.Released, other.AddHours(2)));
            var unrelated = fixture.Play(fixture.Viewer, other.AddHours(3));
            unrelated.ProviderTrackReference = "deezer:another";
            unrelated.TrackReference = "ext-deezer-song-another";
            db.ListeningEvents.Add(unrelated);
            var future = fixture.Play(fixture.Viewer, DateTimeOffset.UtcNow.AddDays(1));
            db.ListeningEvents.Add(future);
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(administrator ? other : own, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task CachedLastPlayedUsesListeningEvidenceInsteadOfCacheAccessTime()
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        var playedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var cacheUse = playedAt.AddDays(1);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var mapping = await db.DownloadedSongMappings.SingleAsync();
            var cache = Path.Combine(Path.GetDirectoryName(mapping.LocalPath)!, "cache");
            Directory.CreateDirectory(cache);
            var cachedPath = Path.Combine(cache, "song.flac");
            File.Move(mapping.LocalPath, cachedPath);
            mapping.LocalPath = cachedPath;
            mapping.CacheIdentityJson = JsonSerializer.Serialize(new { LastUsed = cacheUse });
            (await db.ManagedFiles.SingleAsync()).CanonicalPath = cachedPath;
            db.ListeningEvents.Add(fixture.Play(fixture.Viewer, playedAt));
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync("cache");
        Assert.Equal("cache", result.RootElement.GetProperty("storage").GetString());
        Assert.Equal(playedAt, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
        Assert.Equal(cacheUse.UtcDateTime, result.RootElement.GetProperty("lastAccessedAt").GetDateTime());
    }

    [Fact]
    public async Task KeptByResolvesTheOwnerOfAnActiveHouseholdPlaylistReference()
    {
        using var fixture = new Fixture(true);
        await fixture.InitializeAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var playlist = new PlaylistLinkRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = fixture.Other.Id,
                SourcePlaylistIdHash = CanonicalCatalogKeys.Hash("playlist"),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "home"
            };
            db.PlaylistLinks.Add(playlist);
            db.ManagedFileReferences.Add(fixture.Reference($"playlist:{playlist.Id:N}:entry"));
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        var keeper = Assert.Single(result.RootElement.GetProperty("keptBy").EnumerateArray());
        Assert.Equal("Robin", keeper.GetProperty("displayName").GetString());
        Assert.Equal("playlist", Assert.Single(keeper.GetProperty("reasons").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastPlayedIncludesDeliveredTracesAndRejectsMismatchedActorsAndAttempts(bool administrator)
    {
        using var fixture = new Fixture(administrator);
        await fixture.InitializeAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.AuditEvents.Add(fixture.Trace(fixture.Viewer, at));
            db.AuditEvents.Add(fixture.Trace(fixture.Other, at.AddMinutes(1)));
            db.AuditEvents.Add(fixture.Trace(fixture.Foreign, at.AddMinutes(2)));
            var mismatched = fixture.Trace(fixture.Other, at.AddMinutes(2));
            mismatched.ActorUserId = fixture.Viewer.Id;
            db.AuditEvents.Add(mismatched);
            db.AuditEvents.Add(fixture.Trace(fixture.Viewer, at.AddMinutes(3), delivered: false));
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(administrator ? at.AddMinutes(1) : at, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task LastPlayedResolvesMergedRecordingTraces()
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var recording = fixture.Recording("current-recording");
            var previous = fixture.Recording("previous-recording");
            previous.MergedIntoId = recording.Id;
            db.CanonicalRecordings.AddRange(recording, previous);
            db.ProviderTrackIdentities.Add(Identity(recording));
            var trace = fixture.Trace(fixture.Viewer, at, recordingId: previous.Id);
            var sample = JsonSerializer.Deserialize<PlaybackStartSample>(trace.DetailsJson)!;
            var play = sample.Trace!;
            play = play with
            {
                Request = play.Request with { ItemId = "ext-apple-song-original" },
                Resolution = play.Resolution with { Provider = "apple", ExternalId = "original" }
            };
            trace.DetailsJson = JsonSerializer.Serialize(sample with { Trace = play });
            db.AuditEvents.Add(trace);
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(at, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task LastPlayedMatchesTheSameRecordingAcrossVerifiedProviders()
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var recording = new CanonicalRecordingRecord { Id = Guid.NewGuid(), CreatedByUserId = fixture.Viewer.Id, PublicId = "recording" };
            db.CanonicalRecordings.Add(recording);
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recording.Id,
                ProviderId = "deezer",
                ExternalId = "recording",
                ExternalIdHash = CanonicalCatalogKeys.Hash("recording"),
                ResourceKind = ProviderResourceKind.Track,
                Verification = ProviderIdentityVerification.Verified,
                Scope = ProviderIdentityScope.Catalog,
                DecisionVersion = 1
            });
            var play = fixture.Play(fixture.Viewer, at);
            play.CanonicalRecordingId = recording.Id;
            play.ProviderTrackReference = "apple:another-source";
            play.TrackReference = "ext-apple-song-another-source";
            db.ListeningEvents.Add(play);
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(at, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastPlayedResolvesTheMappedAccountBeforeDefaultCatalogFallback(bool hasAccountIdentity)
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var account = fixture.Account("Mapped source");
            var otherAccount = fixture.Account("Other source");
            db.ProviderAccounts.AddRange(account, otherAccount);
            var mapping = await db.DownloadedSongMappings.SingleAsync();
            mapping.ProviderAccountId = account.Id;
            mapping.ScopeKey = new DownloadedSongMappingScope(account.Id, mapping.AudioQuality).Key;
            var mapped = fixture.Recording("mapped-recording");
            var catalog = fixture.Recording("catalog-recording");
            var other = fixture.Recording("other-account-recording");
            var otherCatalog = fixture.Recording("other-catalog-recording");
            var recordings = new[] { mapped, catalog, other, otherCatalog };
            db.CanonicalRecordings.AddRange(recordings);
            db.ProviderTrackIdentities.AddRange(Identity(catalog), Identity(other, otherAccount.Id),
                Identity(otherCatalog, account.Id, "another"));
            if (hasAccountIdentity) db.ProviderTrackIdentities.Add(Identity(mapped, account.Id));
            var expected = hasAccountIdentity ? mapped : catalog;
            foreach (var recording in recordings)
            {
                var play = fixture.Play(fixture.Viewer, recording == expected ? at : at.AddHours(1));
                play.CanonicalRecordingId = recording.Id;
                play.ProviderTrackReference = $"deezer:{recording.PublicId}";
                play.TrackReference = $"ext-deezer-song-{recording.PublicId}";
                db.ListeningEvents.Add(play);
            }
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(at, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastPlayedMatchesDirectReferencesOnlyForTheMappedAccount(bool accountScoped)
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var account = fixture.Account("Mapped source");
            var otherAccount = fixture.Account("Other source");
            db.ProviderAccounts.AddRange(account, otherAccount);
            var mapping = await db.DownloadedSongMappings.SingleAsync();
            mapping.ProviderAccountId = accountScoped ? account.Id : null;
            mapping.ScopeKey = new DownloadedSongMappingScope(mapping.ProviderAccountId, mapping.AudioQuality).Key;
            var matching = fixture.Play(fixture.Viewer, at);
            matching.ProviderAccountId = mapping.ProviderAccountId;
            var other = fixture.Play(fixture.Viewer, at.AddHours(1));
            other.ProviderAccountId = otherAccount.Id;
            db.ListeningEvents.AddRange(matching, other);
            if (accountScoped) db.ListeningEvents.Add(fixture.Play(fixture.Viewer, at.AddHours(2)));
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(at, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task LastPlayedDoesNotOverrideAConflictingRecordingWithASourceReference()
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var recording = fixture.Recording("mapped-recording");
            var other = fixture.Recording("another-catalog-recording");
            db.CanonicalRecordings.AddRange(recording, other);
            db.ProviderTrackIdentities.AddRange(Identity(recording), Identity(other, catalog: "another"));
            var matching = fixture.Play(fixture.Viewer, at);
            matching.CanonicalRecordingId = recording.Id;
            var conflicting = fixture.Play(fixture.Viewer, at.AddHours(1));
            conflicting.CanonicalRecordingId = other.Id;
            db.ListeningEvents.AddRange(matching, conflicting);
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(at, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task LastPlayedRequiresCanonicalEvidenceForAccountScopedTraces()
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var account = fixture.Account("Mapped source");
            db.ProviderAccounts.Add(account);
            var mapping = await db.DownloadedSongMappings.SingleAsync();
            mapping.ProviderAccountId = account.Id;
            mapping.ScopeKey = new DownloadedSongMappingScope(account.Id, mapping.AudioQuality).Key;
            var recording = fixture.Recording("mapped-recording");
            db.CanonicalRecordings.Add(recording);
            db.ProviderTrackIdentities.Add(Identity(recording, account.Id));
            db.AuditEvents.AddRange(fixture.Trace(fixture.Viewer, at, recordingId: recording.Id),
                fixture.Trace(fixture.Viewer, at.AddMinutes(1)));
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(at, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastPlayedIgnoresFailedStartsAndRetainsAbandonedPlaybackWithProgress(bool progressed)
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var abandoned = fixture.Play(fixture.Viewer, at.AddHours(1));
            abandoned.State = ListeningEventState.Abandoned;
            abandoned.ListenedAt = null;
            abandoned.StartedAt = at.AddHours(1);
            abandoned.PositionTicks = progressed ? TimeSpan.FromSeconds(30).Ticks : 0;
            var failedStart = fixture.Play(fixture.Viewer, at.AddHours(2));
            failedStart.State = ListeningEventState.Abandoned;
            failedStart.ListenedAt = null;
            failedStart.StartedAt = at.AddHours(2);
            failedStart.PositionTicks = 0;
            db.ListeningEvents.AddRange(fixture.Play(fixture.Viewer, at), abandoned, failedStart);
            await db.SaveChangesAsync();
        }
        using var result = await fixture.ListAsync();
        Assert.Equal(progressed ? at.AddHours(1) : at, result.RootElement.GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task UnknownHistoryAndUnverifiedViewerDoNotInventFacts()
    {
        using var fixture = new Fixture(false);
        await fixture.InitializeAsync();
        fixture.Controller.HttpContext.Items.Remove(AdminAuthSessionService.HttpContextSessionItemKey);
        using var result = await fixture.ListAsync();
        Assert.Empty(result.RootElement.GetProperty("keptBy").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("lastPlayedAt").ValueKind);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("role-changed")]
    [InlineData("principal-changed")]
    public async Task ListenerFactsRequireTheCurrentSessionIdentity(string change)
    {
        using var fixture = new Fixture(true);
        await fixture.InitializeAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.ListeningEvents.Add(fixture.Play(fixture.Other, DateTimeOffset.UtcNow.AddHours(-1)));
            db.ManagedFileReferences.Add(fixture.Reference($"keep:user:{fixture.Other.Id:N}:{Guid.NewGuid():N}"));
            var user = await db.Users.SingleAsync(item => item.Id == fixture.Viewer.Id);
            if (change == "role-changed") user.IsAdmin = false;
            if (change == "principal-changed") user.BackendPrincipalId = "changed";
            await db.SaveChangesAsync();
        }
        if (change == "expired")
            ((AdminAuthSession)fixture.Controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey]!).ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        using var result = await fixture.ListAsync();
        Assert.Empty(result.RootElement.GetProperty("keptBy").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("lastPlayedAt").ValueKind);
    }

    private static ProviderTrackIdentityRecord Identity(CanonicalRecordingRecord recording, Guid? accountId = null, string catalog = "default") => new()
    {
        Id = Guid.NewGuid(),
        CanonicalRecordingId = recording.Id,
        ProviderId = "deezer",
        ExternalId = "recording",
        ExternalIdHash = CanonicalCatalogKeys.Hash("recording"),
        ResourceKind = ProviderResourceKind.Track,
        Scope = accountId.HasValue ? ProviderIdentityScope.Account : ProviderIdentityScope.Catalog,
        ProviderAccountId = accountId,
        CatalogNamespace = catalog,
        Verification = ProviderIdentityVerification.Verified,
        DecisionVersion = 1
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "download-facts-" + Guid.NewGuid().ToString("N"));
        private readonly Guid fileId = Guid.NewGuid();
        public UserRecord Viewer { get; }
        public UserRecord Other { get; } = User("Robin");
        public UserRecord Foreign { get; } = User("Foreign", "elsewhere");
        public UserRecord Released { get; } = User("Released");
        public UserRecord Ended { get; } = User("Ended");
        public Factory Factory { get; }
        public DownloadsController Controller { get; }

        public Fixture(bool administrator)
        {
            Viewer = User("Viewer"); Viewer.IsAdmin = administrator;
            Released.Enabled = false;
            Directory.CreateDirectory(root);
            Factory = new(new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options);
            Controller = new(NullLogger<DownloadsController>.Instance,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Library:DownloadPath"] = root, ["Library:KeptPath"] = root }).Build(), contextFactory: Factory)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            Controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
            {
                SessionId = "fixture",
                UserId = Viewer.BackendPrincipalId,
                UserName = "Viewer",
                IsAdministrator = administrator,
                BackendType = "Jellyfin",
                BackendInstanceId = "home",
                AllstarrUserId = Viewer.Id,
                JellyfinAccessToken = "",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
        }

        public async Task InitializeAsync()
        {
            var path = Path.Combine(root, "song.flac");
            await File.WriteAllTextAsync(path, "fixture-audio");
            await using var db = Factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(Viewer, Other, Foreign, Released, Ended);
            db.ManagedFiles.Add(new()
            {
                Id = fileId,
                RootId = Guid.NewGuid(),
                CanonicalPath = path,
                ScopeKey = "household",
                IsManaged = true,
                ContentSha256 = new string('a', 64)
            });
            db.DownloadedSongMappings.Add(new() { Id = Guid.NewGuid(), ProviderId = "deezer", ExternalId = "recording", LocalPath = path });
            await db.SaveChangesAsync();
        }

        public ManagedFileReferenceEntity Reference(string key, bool released = false) => new()
        { Id = Guid.NewGuid(), ManagedFileId = fileId, ReferenceKey = key, ScopeKey = "household", ReleasedAt = released ? DateTimeOffset.UtcNow : null };

        public ListeningEventRecord Play(UserRecord user, DateTimeOffset at) => new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            Protocol = "jellyfin",
            BackendInstanceId = user.BackendInstanceId,
            OccurrenceKey = Guid.NewGuid().ToString(),
            State = ListeningEventState.Completed,
            ListenedAt = at,
            TrackReference = "ext-deezer-song-recording",
            ProviderId = "deezer",
            ProviderTrackReference = "deezer:recording"
        };

        public ProviderAccountRecord Account(string name) => new()
        { Id = Guid.NewGuid(), ProviderId = "deezer", OwnerUserId = Viewer.Id, DisplayName = name, Enabled = true };

        public CanonicalRecordingRecord Recording(string publicId) => new()
        { Id = Guid.NewGuid(), CreatedByUserId = Viewer.Id, PublicId = publicId };

        public AuditEventRecord Trace(UserRecord user, DateTimeOffset at, bool delivered = true, Guid? recordingId = null)
        {
            var sample = new PlaybackStartSample(Guid.NewGuid(), at, delivered ? "completed" : "failed", "deezer", null, null, null,
                true, 30, 10, delivered ? 15 : null, [], 0,
                new(user.Id, new("Jellyfin", "ext-deezer-song-recording", null, null, new()),
                    new(CanonicalRecordingId: recordingId, Provider: "deezer", ExternalId: "recording")));
            return new()
            {
                Id = sample.Id,
                ActorUserId = user.Id,
                Category = PlaybackStartTimingStore.Category,
                SubjectType = PlaybackStartTimingStore.Category,
                SubjectId = "recent",
                CreatedAt = at,
                DetailsJson = JsonSerializer.Serialize(sample)
            };
        }

        public async Task<JsonDocument> ListAsync(string storage = "kept")
        {
            var result = Assert.IsType<OkObjectResult>(await Controller.GetDownloads(storage));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return JsonDocument.Parse(Assert.Single(json.RootElement.GetProperty("files").EnumerateArray()).GetRawText());
        }

        private static UserRecord User(string name, string instance = "home") => new()
        { Id = Guid.NewGuid(), DisplayName = name, BackendType = "jellyfin", BackendInstanceId = instance, BackendPrincipalId = Guid.NewGuid().ToString() };
        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    public sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
}
