using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Favorites;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task VirtualPlaylistLastFmReadsReusePublishedSongsAndVisibility()
    {
        var reads = new WarmReadCommands();
        using var factory = WarmReadFactory(reads);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, link) = await SeedWarmFeedAsync(factory);
        var id = PlaylistVirtualizationService.CreateProtocolId(link);
        var paths = new[] { $"/Playlists/{id}/Items?Limit=200&Fields=MediaSources,DateCreated", $"/Items/{id}" };
        foreach (var path in paths)
        {
            reads.Reset();
            using var first = await client.GetAsync(path);
            first.EnsureSuccessStatusCode();
            var expected = await first.Content.ReadAsStringAsync();
            _lyricsSearchOutput.WriteLine($"{path} first: {reads.Commands.Count} reads; " + Assert.Single(first.Headers.GetValues("Server-Timing")));
            reads.Reset();
            using var second = await client.GetAsync(path);
            second.EnsureSuccessStatusCode();
            Assert.Equal(expected, await second.Content.ReadAsStringAsync());
            var measurement = $"{path} warm: {reads.Commands.Count} reads; " + Assert.Single(second.Headers.GetValues("Server-Timing"));
            _lyricsSearchOutput.WriteLine(measurement);
            Assert.DoesNotContain(reads.Commands, sql => sql.Contains("playlist_source_entries", StringComparison.Ordinal));
        }
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal, "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var mixes = factory.Services.GetRequiredService<PersonalMixService>();
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var record = await db.PlaylistLinks.SingleAsync(item => item.Id == link);
        await mixes.RepairExternalAppsAsync(execution, default);
        Assert.True(await mixes.IsExternalVisibleAsync(execution, record, default));
        reads.Reset();
        using var timing = RequestTiming.Begin();
        await mixes.RepairExternalAppsAsync(execution, default);
        Assert.True(await mixes.IsExternalVisibleAsync(execution, record, default));
        var repeated = $"Repair and visibility repeat: {reads.Commands.Count} reads; {timing.Snapshot("jellyfin").Header}";
        _lyricsSearchOutput.WriteLine(repeated);
        Assert.True(reads.Commands.IsEmpty, repeated);
        record.Enabled = false;
        await db.SaveChangesAsync();
        Assert.False(await mixes.IsExternalVisibleAsync(execution, record, default));
        using var hidden = await client.GetAsync(paths[1]);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task VirtualPlaylistFeedCoverReusesThePublishedSongProjection()
    {
        var reads = new WarmReadCommands();
        using var factory = WarmReadFactory(reads);
        using var client = CreateClientApiClient(factory);
        var (owner, link) = await SeedWarmFeedAsync(factory);
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal, "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var expected = MixCoverInput.From(await factory.Services.GetRequiredService<PersonalMixService>().GetCoverAsync(owner, link));
        reads.Reset();
        using var timing = RequestTiming.Begin();
        var playlist = await factory.Services.GetRequiredService<IPlaylistVirtualizationService>()
            .ReadAsync(execution, PlaylistVirtualizationService.CreateProtocolId(link));
        Assert.NotNull(playlist);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(playlist.MixCover));
        var songReads = reads.Commands.Count(sql => sql.Contains("FROM \"playlist_source_entries\"", StringComparison.Ordinal));
        var measurement = $"Published playlist view: {reads.Commands.Count} reads, {songReads} song projections; {timing.Snapshot("jellyfin").Header}";
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.True(songReads == 1, measurement);
    }

    [Fact]
    public async Task MixCoverPublishedFeedHitChecksAccessWithoutResolvingSongs()
    {
        var reads = new WarmReadCommands();
        using var factory = WarmReadFactory(reads);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, link) = await SeedWarmFeedAsync(factory);
        var path = $"/allstarr/v1/mixes/{link}/cover";
        using var first = await client.GetAsync(path);
        first.EnsureSuccessStatusCode();
        var input = await factory.Services.GetRequiredService<allstarr.Core.ClientApi.MixReader>().CoverAsync(
            new(ProtocolKind.Jellyfin, "primary", "listener", (await factory.Services.GetRequiredService<BackendIdentityResolver>()
                .ResolveAsync(new("Jellyfin", "listener")))!, "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default), link);
        var versioned = path + "?v=" + MixCoverImageService.Version(input);
        // A different feed's refresh invalidates access entries, but the requested image is still on disk.
        await factory.Services.GetRequiredService<IApplicationCache>().DeleteByPatternAsync("feed:read:v1:*");
        reads.Reset();
        using var hit = await client.GetAsync(versioned);
        hit.EnsureSuccessStatusCode();
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await hit.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Headers.ETag, hit.Headers.ETag);
        var measurement = $"Published feed cover hit: {reads.Commands.Count} reads; " + Assert.Single(hit.Headers.GetValues("Server-Timing"));
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.True(!reads.Commands.Any(sql => sql.Contains("track_matches", StringComparison.Ordinal) ||
            sql.Contains("playlist_source_entries", StringComparison.Ordinal) || sql.Contains("library_tracks", StringComparison.Ordinal)), measurement);
        reads.Reset();
        using var warm = await client.GetAsync(versioned);
        warm.EnsureSuccessStatusCode();
        Assert.Equal(await hit.Content.ReadAsByteArrayAsync(), await warm.Content.ReadAsByteArrayAsync());
        Assert.Equal(hit.Headers.ETag, warm.Headers.ETag);
        Assert.Contains("FROM \"runtime_settings\"", Assert.Single(reads.Commands), StringComparison.Ordinal);
        _lyricsSearchOutput.WriteLine("Published feed cover warm: " + Assert.Single(warm.Headers.GetValues("Server-Timing")));
    }

    [Theory]
    [InlineData("deezer")]
    [InlineData("apple-download")]
    [InlineData("spotiflac-tidal-web")]
    public async Task ExternalAlbumMergeReadsOnlyRequestedUserData(string provider)
    {
        var reads = new WarmReadCommands();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), provider, "warm"))
            .ReturnsAsync(() => new Album { Id = $"ext-{provider}-album-warm", Title = "Album", Artist = "Artist", ExternalProvider = provider, ExternalId = "warm" });
        using var factory = WarmReadFactory(reads, gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        var path = $"/Items/ext-{provider}-album-warm?Fields=Genres,Overview";
        using var first = await client.GetAsync(path);
        first.EnsureSuccessStatusCode();
        reads.Reset();
        using var warm = await client.GetAsync(path);
        warm.EnsureSuccessStatusCode();
        Assert.Equal(await first.Content.ReadAsStringAsync(), await warm.Content.ReadAsStringAsync());
        var measurement = $"{provider} album warm: {reads.Commands.Count} reads; " + Assert.Single(warm.Headers.GetValues("Server-Timing"));
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.DoesNotContain(", saved;dur=", Assert.Single(warm.Headers.GetValues("Server-Timing")));
        Assert.DoesNotContain(", owned;dur=", Assert.Single(warm.Headers.GetValues("Server-Timing")));
        Assert.True(reads.Commands.Count <= 14, measurement);
    }

    [Fact]
    public async Task ExternalAlbumSavedDatesReuseUnchangedMembershipAndObserveNewHearts()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var reads = new WarmReadCommands();
        var storage = new FavoriteViewFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(reads).Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var reader = new SavedLibraryReader(storage, cache: cache, readCoalescer: new(new()));
        var execution = SavedMembershipContext();
        DefaultHttpContext Request()
        {
            var http = new DefaultHttpContext();
            http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = execution;
            return http;
        }
        async Task<IReadOnlyDictionary<string, DateTimeOffset>> FreshAsync() => (await reader.ReadAsync(execution))
            .ToDictionary(item => item.ItemId, item => item.FavoritedAt, StringComparer.Ordinal);
        var expected = await FreshAsync();
        Assert.Equal(expected, await reader.ReadDatesForRequestAsync(Request()));
        reads.Reset();
        using var timing = RequestTiming.Begin();
        var warm = await reader.ReadDatesForRequestAsync(Request());
        Assert.Equal(expected, warm);
        var measurement = $"Saved dates warm: {reads.Commands.Count} reads; {timing.Snapshot("jellyfin").Header}";
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.True(reads.Commands.Count <= 2, measurement);
        await using (var db = storage.CreateDbContext())
        {
            db.ExternalMetadataSnapshots.Add(FeedReadsTests.Snapshot(FavoriteViewer, "service-feed-prepared"));
            await db.SaveChangesAsync();
        }
        reads.Reset();
        Assert.Equal(expected, await reader.ReadDatesForRequestAsync(Request()));
        Assert.InRange(reads.Commands.Count, 0, 2);
        await using (var db = storage.CreateDbContext())
        {
            var state = await db.FavoriteStates.SingleAsync(item => item.ItemId == "ext-deezer-song-saved");
            state.IsFavorite = false;
            var accepted = await db.FavoriteEvents.SingleAsync(item => item.Id == state.LastEventId);
            accepted.Operation = FavoriteOperation.Unfavorite;
            await db.SaveChangesAsync();
        }
        Assert.Equal(await FreshAsync(), await reader.ReadDatesForRequestAsync(Request()));
    }

    [Fact]
    public async Task ExternalAlbumSavedDatesRecheckNativeVisibilityAndSharedMembership()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
        {
            var foreign = await db.Users.SingleAsync(user => user.Id != FavoriteViewer);
            await AddMembershipSnapshotAsync(db, "shared", 1, DateTimeOffset.UnixEpoch, true);
            var row = db.ChangeTracker.Entries<ExternalMetadataSnapshotRecord>().Single().Entity;
            row.OwnerUserId = foreign.Id;
            row.BackendPrincipalId = foreign.BackendPrincipalId;
            row.ExternalIdHash = WritablePlaylistMembership.Key(SavedMembershipContext(), "shared-playlist");
            var state = JsonSerializer.Deserialize<WritablePlaylistState>(row.PayloadJson)!;
            var member = state.Members[0];
            member.Song!.Id = "ext-deezer-song-shared";
            row.PayloadJson = JsonSerializer.Serialize(new WritablePlaylistState([member with { ItemId = member.Song.Id }]));
            await db.SaveChangesAsync();
        }
        var visible = true;
        var target = new Mock<IBackendPlaylistTarget>(MockBehavior.Strict);
        target.Setup(item => item.ListPageAsync(It.IsAny<BackendPlaylistTargetContext>(), null, 0, 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>(BackendPlaylistTargetStatus.Success,
                visible ? [new("shared-playlist", "Shared")] : []));
        var targets = new Mock<IBackendPlaylistTargetResolver>(MockBehavior.Strict);
        targets.Setup(item => item.Resolve("jellyfin")).Returns(target.Object);
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var reader = new SavedLibraryReader(storage, targets.Object, cache: cache, readCoalescer: new(new()));
        async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadAsync()
        {
            var http = new DefaultHttpContext();
            http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = SavedMembershipContext();
            return await reader.ReadDatesForRequestAsync(http);
        }
        Assert.Contains("ext-deezer-song-shared", await ReadAsync());
        Assert.Contains("ext-deezer-song-shared", await ReadAsync());
        visible = false;
        Assert.DoesNotContain("ext-deezer-song-shared", await ReadAsync());
        visible = true;
        await using (var db = storage.CreateDbContext())
        {
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "playlist-membership");
            row.Id = Guid.NewGuid();
            row.SnapshotVersion++;
            row.PayloadJson = JsonSerializer.Serialize(new WritablePlaylistState([]));
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain("ext-deezer-song-shared", await ReadAsync());
        await using (var db = storage.CreateDbContext())
        {
            var row = await db.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "playlist-membership" && item.SnapshotVersion == 2);
            await db.ExternalMetadataSnapshots.Where(item => item.Id == row.Id).ExecuteDeleteAsync();
            db.InvalidateFeedReads(row.OwnerUserId);
        }
        Assert.Contains("ext-deezer-song-shared", await ReadAsync());
    }

    [Fact]
    public async Task ItemDetailsNativeReadAvoidsQueriesForAbsentRecordings()
    {
        var reads = new WarmReadCommands();
        using var factory = WarmReadFactory(reads);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        using var first = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native-mix-song" } });
        first.EnsureSuccessStatusCode();
        reads.Reset();
        using var second = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native-mix-song" } });
        second.EnsureSuccessStatusCode();
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        var measurement = $"Native details: {reads.Commands.Count} reads; " + Assert.Single(second.Headers.GetValues("Server-Timing"));
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.True(!reads.Commands.Any(sql => sql.Contains("provider_track_identities", StringComparison.Ordinal) ||
            sql.Contains("external_metadata_snapshots", StringComparison.Ordinal) || sql.Contains("recording_identifiers", StringComparison.Ordinal)), measurement);
    }

    [Fact]
    public async Task AppearanceReadsOverlapItemVisibilityAndMixMembershipWithoutChangingContent()
    {
        var reads = new WarmReadCommands();
        var savedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlapped = false;
        using var factory = WarmReadFactory(reads, configure: services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton<IBackendLibraryAccessResolver>(provider =>
            {
                var fallback = new TestBackendLibraryAccess(provider.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(), "library");
                var access = new Mock<IBackendLibraryAccessResolver>();
                access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
                    .Returns(async () =>
                    {
                        savedStarted.TrySetResult();
                        await nativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
                        return new BackendLibraryAccess(true, ["library"]);
                    });
                access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .Returns((Guid owner, CancellationToken token) => fallback.ResolveUserAsync(owner, token));
                return access.Object;
            });
        }, observe: request =>
        {
            if (request.RequestUri!.AbsolutePath != "/Users/listener/Items/native-mix-song") return;
            overlapped = savedStarted.Task.IsCompleted;
            nativeStarted.TrySetResult();
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        const string path = "/allstarr/v1/items/native-mix-song/appearances";
        reads.Reset();
        using var first = await client.GetAsync(path);
        first.EnsureSuccessStatusCode();
        var expected = await first.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(expected);
        Assert.Equal(mix.Id.ToString(), Assert.Single(json.RootElement.GetProperty("appearances").EnumerateArray()).GetProperty("id").GetString());
        var measurement = $"Appearances first: {reads.Commands.Count} reads; " + Assert.Single(first.Headers.GetValues("Server-Timing"));
        _lyricsSearchOutput.WriteLine(measurement);
        Assert.True(overlapped, measurement);
        reads.Reset();
        using var second = await client.GetAsync(path);
        second.EnsureSuccessStatusCode();
        Assert.Equal(expected, await second.Content.ReadAsStringAsync());
        _lyricsSearchOutput.WriteLine($"Appearances warm: {reads.Commands.Count} reads; " + Assert.Single(second.Headers.GetValues("Server-Timing")));
    }

    private static ProtocolFactory WarmReadFactory(WarmReadCommands reads, Mock<IProtocolProviderGateway>? gateway = null,
        Action<IServiceCollection>? configure = null, Action<HttpRequestMessage>? observe = null) =>
        ClientMixFactory(gateway ?? new(), configure: services =>
        {
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>()).AddInterceptors(reads).Options));
            services.RemoveAll<MixCoverImageService>();
            services.AddSingleton(provider => new MixCoverImageService(MixCoverRenderer.Render, (_, _) => Task.FromResult<byte[]?>(MixPicture()),
                cache: provider.GetRequiredService<IApplicationCache>()));
            configure?.Invoke(services);
        }, nativeResponse: request => request.RequestUri!.AbsolutePath is "/Items" or "/Users/listener/Items"
            ? Json(200, """{"Items":[{"Id":"native-0","Type":"Audio","Name":"Song 0","AlbumPrimaryImageTag":"art-0"},{"Id":"native-mix-song","Type":"Audio","Name":"Library song","AlbumPrimaryImageTag":"native-album-art"}],"TotalRecordCount":2}""") : null, observe: observe);

    private static async Task<(Guid Owner, Guid Link)> SeedWarmFeedAsync(ProtocolFactory factory)
    {
        await SeedClientMixAsync(factory);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var link = Guid.NewGuid();
        await SeedServiceMixCoverAsync(factory, owner, "jellyfin", "lastfm", "top:1month", link, Guid.NewGuid(), 1);
        await factory.Services.GetRequiredService<IDurableRuntimeSettings>().ApplyBatchAsync([new(PersonalMixService.PublishKey, "true")], "fixture");
        return (owner, link);
    }

    private sealed class WarmReadCommands : DbCommandInterceptor
    {
        internal ConcurrentQueue<string> Commands { get; } = new();
        private int delay;
        internal void Reset() { Commands.Clear(); delay = 2; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            if (delay > 0) await Task.Delay(delay, cancellationToken);
            return result;
        }
    }
}
