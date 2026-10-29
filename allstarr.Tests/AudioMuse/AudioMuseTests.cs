using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.AudioMuse;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed class AudioMuseTests
{
    [Fact]
    public void Filtering_preserves_order_excludes_seed_proven_copies_and_caps_artists()
    {
        var recording = Guid.NewGuid();
        var seed = Track("seed", "one");
        seed.CanonicalRecordingId = recording;
        var copy = Track("copy", "another");
        copy.CanonicalRecordingId = recording;
        var tracks = new[] { seed, copy, Track("1", "artist"), Track("2", "artist"),
            Track("3", "artist"), Track("4", "artist"), Track("5", "other"), Track("5", "other") };
        Assert.Equal(new[] { "1", "2", "3", "5" }, AudioMuseDiscovery.Select(tracks, seed, 25).Select(t => t.BackendItemId));
    }

    [Fact]
    public void Recipes_require_measured_inputs_and_are_deterministic()
    {
        Assert.Null(AudioMuseDiscovery.RecipeScore("calm", new(null, .7, null, null)));
        Assert.Null(AudioMuseDiscovery.RecipeScore("workout", new(.8, null, null, null)));
        Assert.True(AudioMuseDiscovery.RecipeScore("calm", new(.2, .7, .3, .2)) >
            AudioMuseDiscovery.RecipeScore("calm", new(.6, .7, .3, .2)));
        Assert.True(AudioMuseDiscovery.RecipeScore("workout", new(.8, .2, .7, .6)) >
            AudioMuseDiscovery.RecipeScore("workout", new(.3, .2, .7, .6)));
    }

    [Fact]
    public async Task Connection_is_off_by_default_and_key_is_write_only_encrypted_and_rotatable()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal("not_enabled", (await f.Discovery.StatusAsync(f.Context, default)).Reason);
        Assert.Empty(f.Requests);
        Assert.Equal("not_enabled", (await f.Discovery.SimilarAsync(f.Context, "missing", 25, default)).Reason);
        Assert.Empty(f.Requests);
        await f.ConfigureAsync();
        var dto = await f.Settings.GetAsync(default);
        Assert.True(dto.KeyConfigured);
        Assert.DoesNotContain(Fixture.Key, JsonSerializer.Serialize(dto), StringComparison.Ordinal);
        await using (var db = f.Factory.CreateDbContext())
        {
            Assert.DoesNotContain(Fixture.Key, JsonSerializer.Serialize(await db.RuntimeSettings.ToListAsync()), StringComparison.Ordinal);
            Assert.DoesNotContain(Fixture.Key, JsonSerializer.Serialize(await db.SecretVersions.ToListAsync()), StringComparison.Ordinal);
            Assert.DoesNotContain(Fixture.Key, JsonSerializer.Serialize(await db.AuditEvents.ToListAsync()), StringComparison.Ordinal);
        }
        await f.Settings.ApplyAsync(f.Actor, "", null, dto.Revision, default);
        Assert.Equal("not_enabled", (await f.Discovery.StatusAsync(f.Context, default)).Reason);
    }

    [Fact]
    public async Task Key_rotation_is_atomic_and_disabled_administrators_cannot_change_it()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ConfigureAsync();
        var first = await f.Settings.GetAsync(default);
        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() =>
            f.Settings.ApplyAsync(f.Actor, first.BaseUrl, "stale-key", null, default));
        await f.Settings.ApplyAsync(f.Actor, first.BaseUrl, "rotated-key", first.Revision, default);
        await using var db = f.Factory.CreateDbContext();
        Assert.Single(await db.SecretReferences.ToListAsync());
        Assert.Equal(2, await db.SecretVersions.CountAsync());
        (await db.Users.SingleAsync()).Enabled = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Settings.ApplyAsync(f.Actor, "", "", first.Revision + 1, default));
    }

    internal static LibraryTrackRecord Track(string id, string artist = "artist") => new()
    {
        Id = Guid.NewGuid(),
        BackendItemId = id,
        BackendLibraryId = "allowed",
        Protocol = "jellyfin",
        BackendInstanceId = "primary",
        Title = "Library " + id,
        Artist = artist,
        IndexedAt = DateTimeOffset.UtcNow,
        SourceModifiedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    internal sealed class Fixture : IAsyncDisposable
    {
        internal const string Key = "private-fixture-key";
        private readonly SqliteTestDatabase _database;
        private readonly DurableRuntimeSettingsService _durable;
        internal Factory Factory { get; }
        internal AudioMuseSettings Settings { get; }
        internal AudioMuseDiscovery Discovery { get; }
        internal ProtocolExecutionContext Context { get; }
        internal AdminAuthSession Actor { get; }
        internal List<string> Requests { get; } = [];
        internal Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json("{}");
        internal Mock<IBackendPlaylistTarget> Target { get; } = new();
        internal Mock<IBackendLibraryAccessResolver> Access { get; } = new();
        internal ConfigurationManager Configuration { get; } = new()
        {
            ["Jellyfin:Url"] = "http://backend.example:8096",
            ["Identity:BackendInstanceId"] = "primary"
        };

        private Fixture(SqliteTestDatabase database, EncryptedSecretStore secrets, Factory factory,
            DurableRuntimeSettingsService durable, AdminAuthSession actor)
        {
            _database = database;
            _durable = durable;
            Factory = factory;
            Actor = actor;
            Context = new(ProtocolKind.Jellyfin, "primary", actor.UserId,
                new AllstarrPrincipal(actor.AllstarrUserId!.Value, "jellyfin", "primary", actor.UserId, "Listener", true),
                "fixture", DateTimeOffset.UtcNow.AddMinutes(5), default);
            Settings = new(factory, durable, secrets);
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(c => c.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new Handler(request =>
            {
                Requests.Add(request.RequestUri!.PathAndQuery);
                if (request.RequestUri.Host == "upstream.example")
                {
                    Assert.DoesNotContain(Key, request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
                    Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                    Assert.Equal(Key, request.Headers.Authorization?.Parameter);
                }
                return Respond(request);
            })));
            Access.Setup(a => a.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BackendLibraryAccess(true, ["allowed"]));
            var permission = new Mock<IBackendLibraryPermissionSource>();
            permission.Setup(p => p.CreateRequestAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://backend.example:8096/Items"));
            var targets = new Mock<IBackendPlaylistTargetResolver>();
            targets.Setup(t => t.Resolve("jellyfin")).Returns(Target.Object);
            var policy = new EffectiveProviderPolicyResolver(durable);
            Discovery = new(factory, Settings, new AudioMuseClient(clients.Object, secrets), Access.Object,
                policy, permission.Object, clients.Object, targets.Object, Configuration);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var database = await SqliteTestDatabase.CreateAsync();
            var factory = new Factory(database.Options);
            var options = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
            var ring = new FileSecretKeyRingProvider(options);
            await ring.CreateIfMissingAsync(false);
            var clock = new SystemPlatformClock();
            var durable = new DurableRuntimeSettingsService(factory, new ConfigurationManager(), clock, new RuntimeSettingsChangeSignal());
            var actor = new AdminAuthSession
            {
                SessionId = "fixture",
                UserId = "listener",
                UserName = "Listener",
                AllstarrUserId = Guid.NewGuid(),
                IsAdministrator = true,
                BackendType = "Jellyfin",
                BackendInstanceId = "primary",
                JellyfinAccessToken = "",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
            await using var db = factory.CreateDbContext();
            db.Users.Add(new UserRecord
            {
                Id = actor.AllstarrUserId.Value,
                BackendType = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "listener",
                Enabled = true,
                IsAdmin = true,
                DisplayName = "Listener",
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
            return new(database, new(factory, ring, options, clock), factory, durable, actor);
        }

        internal Task ConfigureAsync() => Settings.ApplyAsync(Actor, "http://upstream.example:8600", Key, null, default);
        internal Task SetCleanOnlyAsync() => _durable.ApplyBatchAsync([new("Library:ExplicitFilter", "CleanOnly")], "fixture");
        public ValueTask DisposeAsync() => _database.DisposeAsync();
        internal static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    internal sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
